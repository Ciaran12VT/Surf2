using System.Data;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational;

public sealed class RelationalSession
{
    private readonly string _connectionString;
    private Guid? _validationMigration;
    private byte[]? _validationFingerprint;
    private readonly object _identityGate = new();
    private Guid? _databaseIdentity;
    private bool _identityChanged;
    private static readonly Lazy<byte[]> SupportedScriptChecksum = new(() =>
        RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts()));

    public RelationalSession(string connectionString)
    {
        _connectionString = SqlServerConnectionOptions.FromConnectionString(connectionString).ConnectionString;
        Metrics = new(Epoch);
    }

    public Guid Epoch { get; } = Guid.NewGuid();
    public RelationalSqlMetrics Metrics { get; }
    public Guid? DatabaseIdentity { get { lock (_identityGate) return _databaseIdentity; } }
    internal bool IsMigrationValidation => _validationMigration.HasValue;
    internal void RejectValidationWrite()
    {
        if (IsMigrationValidation) throw new InvalidOperationException("A migration validation session cannot publish application changes.");
    }

    internal static RelationalSession ForMigrationValidation(string connectionString, Guid migration, byte[] fingerprint)
    {
        if (migration == Guid.Empty || fingerprint.Length != 32) throw new ArgumentException("A pinned migration identity is required.");
        return new(connectionString) { _validationMigration = migration, _validationFingerprint = fingerprint.ToArray() };
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            Metrics.Track(connection);
            await connection.OpenAsync(cancellationToken);
            Guid? expected;
            lock (_identityGate)
            {
                if (_identityChanged) throw new RelationalSessionChangedException();
                expected = _databaseIdentity;
            }
            if (expected.HasValue)
            {
                // Check the actual borrowed connection, not only a probe on another pooled connection.
                await using var marker = connection.CreateCommand();
                marker.CommandText = "SELECT DatabaseIdentity FROM surf.StorageFormatInfo WHERE Singleton=1;";
                using var cancellation = CancelCommand(marker, cancellationToken);
                object? observed = await marker.ExecuteScalarAsync(cancellationToken);
                PinIdentity(observed is Guid identity ? identity : Guid.Empty);
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static SqlParameter Parameter(string name, SqlDbType type, object? value, int size = 0)
    {
        var parameter = new SqlParameter(name, type) { Value = value ?? DBNull.Value };
        if (size != 0)
        {
            parameter.Size = size;
        }

        return parameter;
    }

    public static CancellationTokenRegistration CancelCommand(SqlCommand command, CancellationToken cancellationToken) =>
        cancellationToken.Register(() =>
        {
            try
            {
                command.Cancel();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or SqlException)
            {
                // Cancellation can race command completion and disposal.
            }
        });

    public async Task RequireReadyAsync(CancellationToken cancellationToken = default)
    {
        Guid? pinned;
        lock (_identityGate)
        {
            if (_identityChanged) throw new RelationalSessionChangedException();
            pinned = _databaseIdentity;
        }
        if (pinned.HasValue && !_validationMigration.HasValue)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT COUNT_BIG(*) FROM surf.StorageFormatInfo f JOIN surf.SchemaMigration m ON m.SchemaVersion=f.SchemaVersion
WHERE f.Singleton=1 AND f.DatabaseIdentity=@Database AND f.FormatIdentifier=@Format
AND f.SchemaVersion=@Version AND f.MinimumReaderVersion BETWEEN 1 AND @Version
AND f.MinimumWriterVersion BETWEEN 1 AND @Version AND f.State='Ready'
AND m.ScriptChecksum=@Checksum AND (SELECT COUNT_BIG(*) FROM surf.StorageFormatInfo)=1;
""";
            command.Parameters.Add(Parameter("@Database", SqlDbType.UniqueIdentifier, pinned.Value));
            command.Parameters.Add(Parameter("@Format", SqlDbType.NVarChar, PersistenceFormatProbe.FormatIdentifier, 64));
            command.Parameters.Add(Parameter("@Version", SqlDbType.Int, PersistenceFormatProbe.SupportedSchemaVersion));
            command.Parameters.Add(Parameter("@Checksum", SqlDbType.Binary, SupportedScriptChecksum.Value, 32));
            using var cancellation = CancelCommand(command, cancellationToken);
            if (!Equals(await command.ExecuteScalarAsync(cancellationToken), 1L))
                throw new InvalidOperationException("The selected relational database is no longer a supported Ready source.");
            return;
        }
        var result = await new PersistenceFormatProbe().ProbeAsync(_connectionString, cancellationToken);
        if (result.Format != PersistenceFormat.Relational)
        {
            if (result.Format == PersistenceFormat.Incomplete && _validationMigration.HasValue)
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
SELECT COUNT_BIG(*) FROM surf.MigrationRun r JOIN surf.StorageFormatInfo f ON f.Singleton=1
WHERE r.MigrationIdentity=@Migration AND r.SourceFingerprint=@Fingerprint AND r.ConverterVersion=1
AND r.Status='Converting' AND f.State='Migrating' AND f.SchemaVersion=1 AND f.DatabaseIdentity=@Database;
""";
                command.Parameters.Add(Parameter("@Migration", SqlDbType.UniqueIdentifier, _validationMigration.Value));
                command.Parameters.Add(Parameter("@Fingerprint", SqlDbType.Binary, _validationFingerprint, 32));
                command.Parameters.Add(Parameter("@Database", SqlDbType.UniqueIdentifier, result.DatabaseIdentity));
                if ((long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) == 1)
                {
                    PinIdentity(result.DatabaseIdentity ?? Guid.Empty);
                    return;
                }
            }
            throw new InvalidOperationException("The relational database has not been validated and published.");
        }
        PinIdentity(result.DatabaseIdentity ?? Guid.Empty);
    }

    private void PinIdentity(Guid identity)
    {
        lock (_identityGate)
        {
            if (_identityChanged) throw new RelationalSessionChangedException();
            if (identity == Guid.Empty || _databaseIdentity.HasValue && _databaseIdentity.Value != identity)
            {
                _identityChanged = true;
                throw new RelationalSessionChangedException();
            }
            _databaseIdentity ??= identity;
        }
    }
}

public sealed class RelationalSessionChangedException() : InvalidOperationException(
    "The persistence database identity changed. Close this session and reconnect before using cached keys.");
