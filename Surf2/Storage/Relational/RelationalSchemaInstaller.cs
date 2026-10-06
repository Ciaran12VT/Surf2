using System.Data;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational;

public sealed class RelationalSchemaInstaller
{
    private static readonly string[] ScriptNames =
        ["001.Core.sql", "001.Snapshots.sql", "001.Capture.sql", "001.State.sql", "001.Indexes.sql"];

    public async Task InitializeDestinationAsync(string sourceConnectionString,
        string destinationConnectionString, Guid migrationIdentity, byte[] sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (sourceFingerprint is not { Length: 32 } || migrationIdentity == Guid.Empty)
        {
            throw new ArgumentException("A migration identity and exact source fingerprint are required.");
        }

        var probe = new PersistenceFormatProbe();
        var source = await probe.ProbeAsync(sourceConnectionString, cancellationToken);
        var destination = await probe.ProbeAsync(destinationConnectionString, cancellationToken);
        if (PersistenceFormatProbe.SameDatabase(source, destination))
        {
            throw new InvalidOperationException("Migration must use a different destination database.");
        }

        if (source.Format != PersistenceFormat.Legacy || destination.Format != PersistenceFormat.Empty)
        {
            throw new InvalidOperationException("Conversion requires a readable legacy source and a verified empty destination.");
        }

        await InstallAsync(destinationConnectionString,
            new(migrationIdentity, sourceFingerprint, source.DatabaseName), cancellationToken);
    }

    /// <summary>Explicit new-database action only; unavailable metadata is never treated as empty.</summary>
    public async Task InitializeNewDatabaseAsync(string destinationConnectionString,
        CancellationToken cancellationToken = default)
    {
        await RequireEmptyAsync(destinationConnectionString, cancellationToken);
        await InstallAsync(destinationConnectionString, null, cancellationToken);
    }

    /// <summary>Installs an unpublished import destination; the importer must validate before activation.</summary>
    public async Task InitializePackageDestinationAsync(string destinationConnectionString,
        Guid importIdentity, byte[] packageFingerprint, CancellationToken cancellationToken = default)
    {
        if (importIdentity == Guid.Empty || packageFingerprint is not { Length: 32 })
            throw new ArgumentException("An import identity and exact package fingerprint are required.");
        await RequireEmptyAsync(destinationConnectionString, cancellationToken);
        await InstallAsync(destinationConnectionString,
            new(importIdentity, packageFingerprint, "Relational package"), cancellationToken);
    }

    private static async Task RequireEmptyAsync(string connectionString, CancellationToken ct)
    {
        var destination = await new PersistenceFormatProbe().ProbeAsync(connectionString, ct);
        if (destination.Format != PersistenceFormat.Empty)
            throw new InvalidOperationException("Initialization requires a verified empty destination: " + destination.Reason);
    }

    public async Task InitializeLocalDestinationAsync(string destinationConnectionString,
        Guid migrationIdentity, byte[] sourceFingerprint, CancellationToken cancellationToken = default)
    {
        if (migrationIdentity == Guid.Empty || sourceFingerprint is not { Length: 32 })
            throw new ArgumentException("A local migration identity and exact source fingerprint are required.");
        await RequireEmptyAsync(destinationConnectionString, cancellationToken);
        await InstallAsync(destinationConnectionString,
            new(migrationIdentity, sourceFingerprint, "Legacy AppData files"), cancellationToken);
    }

    private sealed record MigrationSeed(Guid Identity, byte[] Fingerprint, string SourceName);

    private static async Task InstallAsync(string destinationConnectionString, MigrationSeed? seed,
        CancellationToken cancellationToken)
    {

        var scripts = ReadScripts();
        var session = new RelationalSession(destinationConnectionString);
        await using var connection = await session.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 120;
            using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
            command.CommandText = """
DECLARE @LockResult int;
EXEC @LockResult = sys.sp_getapplock @Resource=N'Surf2.Relational.Provision',
    @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
IF @LockResult < 0 THROW 51000, 'Could not acquire the destination provisioning lock.', 1;
IF EXISTS(SELECT 1 FROM sys.objects WHERE is_ms_shipped = 0)
   OR EXISTS(SELECT 1 FROM sys.types WHERE is_user_defined = 1)
   OR EXISTS(SELECT 1 FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383)
    THROW 51001, 'Destination is no longer empty.', 1;
""";
            await command.ExecuteNonQueryAsync(cancellationToken);
            foreach (string script in scripts)
            {
                command.CommandText = script;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            command.CommandText = """
INSERT surf.StorageFormatInfo(Singleton, FormatIdentifier, SchemaVersion, MinimumReaderVersion,
    MinimumWriterVersion, State, DatabaseIdentity)
VALUES (1, @Format, @Version, @Version, @Version, @State, NEWID());
INSERT surf.SchemaMigration(SchemaVersion, ScriptChecksum) VALUES (@Version, @Checksum);
""";
            command.Parameters.Add(RelationalSession.Parameter("@Format", SqlDbType.NVarChar, PersistenceFormatProbe.FormatIdentifier, 64));
            command.Parameters.Add(RelationalSession.Parameter("@Version", SqlDbType.Int, PersistenceFormatProbe.SupportedSchemaVersion));
            command.Parameters.Add(RelationalSession.Parameter("@Checksum", SqlDbType.Binary, ScriptChecksum(scripts), 32));
            command.Parameters.Add(RelationalSession.Parameter("@State", SqlDbType.VarChar, seed == null ? "Ready" : "Migrating", 24));
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (seed != null)
            {
                command.Parameters.Clear();
                command.CommandText = """
INSERT surf.MigrationRun(MigrationIdentity, SourceFingerprint, ConverterVersion, SourceDatabaseName, Status)
VALUES (@Migration, @Fingerprint, 1, @SourceName, 'Converting');
""";
                command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, seed.Identity));
                command.Parameters.Add(RelationalSession.Parameter("@Fingerprint", SqlDbType.Binary, seed.Fingerprint, 32));
                command.Parameters.Add(RelationalSession.Parameter("@SourceName", SqlDbType.NVarChar, seed.SourceName, -1));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                var settings = new AppSettings();
                settings.EnsureDefaults();
                var state = new RelationalStateStore(session);
                await state.ImportSettingsAsync(connection, transaction, settings, Guid.NewGuid(), cancellationToken);
                await state.ImportScopeSelectionAsync(connection, transaction, 1, null, Guid.NewGuid(), cancellationToken);
                await state.ImportWorkspaceAsync(connection, transaction, new WorkspaceState(), Guid.NewGuid(), cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                // Preserve the original failure; a lost connection requires probing the destination before retry.
            }
            throw;
        }
    }

    public async Task CreateEmptyDestinationAsync(string sourceConnectionString, string destinationConnectionString,
        CancellationToken cancellationToken = default)
    {
        var sourceOptions = SqlServerConnectionOptions.FromConnectionString(sourceConnectionString);
        var destinationOptions = SqlServerConnectionOptions.FromConnectionString(destinationConnectionString);
        var sourceBuilder = new SqlConnectionStringBuilder(sourceOptions.ConnectionString);
        var destinationBuilder = new SqlConnectionStringBuilder(destinationOptions.ConnectionString);
        if (string.Equals(sourceBuilder.DataSource, destinationBuilder.DataSource, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(sourceBuilder.InitialCatalog, destinationBuilder.InitialCatalog, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The destination cannot be the source database.");
        }

        string name = destinationBuilder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
        {
            throw new ArgumentException("The destination database name is invalid.");
        }

        await CreateDatabaseAsync(destinationBuilder, cancellationToken);
    }

    public Task CreateEmptyDatabaseAsync(string destinationConnectionString, CancellationToken cancellationToken = default) =>
        CreateDatabaseAsync(new SqlConnectionStringBuilder(
            SqlServerConnectionOptions.FromConnectionString(destinationConnectionString).ConnectionString), cancellationToken);

    private static async Task CreateDatabaseAsync(SqlConnectionStringBuilder destinationBuilder, CancellationToken cancellationToken)
    {
        string name = destinationBuilder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("The destination database name is invalid.");
        destinationBuilder.InitialCatalog = "master";
        await using var connection = new SqlConnection(destinationBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
IF DB_ID(@Name) IS NOT NULL
    THROW 51002, 'Destination already exists; select it explicitly as an empty destination.', 1;
DECLARE @Sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@Name);
EXEC sys.sp_executesql @Sql;
SET @Sql = N'ALTER DATABASE ' + QUOTENAME(@Name) + N' SET ALLOW_SNAPSHOT_ISOLATION ON';
EXEC sys.sp_executesql @Sql;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Name", SqlDbType.NVarChar, name, 128));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string[] ReadScripts()
    {
        Assembly assembly = typeof(RelationalSchemaInstaller).Assembly;
        string[] resources = assembly.GetManifestResourceNames();
        return ScriptNames.Select(name =>
        {
            string resource = resources.SingleOrDefault(resource => resource.EndsWith("." + name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The relational schema installation is incomplete.");
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("A schema script could not be read.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }).ToArray();
    }

    public static byte[] ScriptChecksum(IEnumerable<string> scripts) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n-- Surf schema boundary --\n",
            scripts.Select(script => script.ReplaceLineEndings("\n")))));
}
