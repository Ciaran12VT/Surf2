using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;

public static partial class StorageRegressionSuite
{
    private sealed class SqlFixture : IAsyncDisposable
    {
        private const string Prefix = "Surf2_Regression_";
        private const string MasterConnectionString =
            "Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=True;TrustServerCertificate=True;Pooling=False;Connect Timeout=30";
        private const string SourcePayload =
            "{\"SchemaVersion\":2,\"Snapshots\":[{\"SnapshotId\":\"source-fixture\",\"DisplayName\":\"untouched evidence\",\"DatabaseName\":\"fixture only\",\"Objects\":[],\"Tables\":[],\"Columns\":[],\"PrimaryKeys\":[],\"TableDataSets\":[],\"FullDataTableNames\":[]}],\"Histories\":[]}";
        private readonly HashSet<string> _created = new(StringComparer.Ordinal);
        private readonly string _sourceName = Prefix + Guid.NewGuid().ToString("N");
        private readonly string _destinationName = Prefix + Guid.NewGuid().ToString("N");
        private readonly string _ownedDirectoryName = Prefix + Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);
        public Guid MigrationIdentity { get; } = Guid.NewGuid();
        public byte[] Fingerprint { get; } = SHA256.HashData(Encoding.Unicode.GetBytes(SourcePayload));
        public string SourceConnectionString => ConnectionString(_sourceName);
        public string DestinationConnectionString => ConnectionString(_destinationName);
        public string OwnedDirectory => Path.GetFullPath(Path.Combine(Path.GetTempPath(), _ownedDirectoryName));

        public static async Task<SqlFixture> CreateAsync(IReadOnlyDictionary<string, string>? sourceDocuments = null)
        {
            var fixture = new SqlFixture();
            try
            {
                await fixture.CreateDatabaseAsync(fixture._sourceName);
                await fixture.CreateDatabaseAsync(fixture._destinationName);
                await fixture.SourceSqlAsync("EXEC(N'CREATE SCHEMA app');");
                await fixture.SourceSqlAsync("""
                    CREATE TABLE app.Surf2Documents (
                        DocumentKey nvarchar(128) NOT NULL PRIMARY KEY,
                        PayloadJson nvarchar(max) NOT NULL,
                        UpdatedAtUtc datetime2(3) NOT NULL);
                    """);
                foreach (var document in sourceDocuments ?? new Dictionary<string, string> { ["database-snapshots"] = SourcePayload })
                {
                    fixture._documents.Add(document.Key, document.Value);
                    await fixture.SourceSqlAsync("""
                        INSERT app.Surf2Documents(DocumentKey, PayloadJson, UpdatedAtUtc)
                        VALUES(@Key, @Payload, '2024-02-03T04:05:06.123');
                        """, RelationalSession.Parameter("@Key", SqlDbType.NVarChar, document.Key, 128),
                        RelationalSession.Parameter("@Payload", SqlDbType.NVarChar, document.Value, -1));
                }
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private static string ConnectionString(string name)
        {
            ValidateName(name);
            var builder = new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = name };
            return builder.ConnectionString;
        }

        private static void ValidateName(string name)
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length != Prefix.Length + 32 ||
                !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Refusing a non-fixture database name.");
        }

        private async Task CreateDatabaseAsync(string name)
        {
            ValidateName(name);
            if (name != _sourceName && name != _destinationName)
                throw new InvalidOperationException("The database is not owned by this fixture.");
            await using var master = new SqlConnection(MasterConnectionString);
            await master.OpenAsync();
            await using var command = master.CreateCommand();
            command.CommandTimeout = 120;
            command.CommandText = $"CREATE DATABASE [{name}];";
            await command.ExecuteNonQueryAsync();
            _created.Add(name);
        }

        public Task<object?> DestinationSqlAsync(string sql, params SqlParameter[] parameters) =>
            SqlAsync(DestinationConnectionString, sql, parameters);
        private Task<object?> SourceSqlAsync(string sql, params SqlParameter[] parameters) =>
            SqlAsync(SourceConnectionString, sql, parameters);

        private static async Task<object?> SqlAsync(string connectionString, string sql, SqlParameter[] parameters)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 120;
            command.CommandText = sql;
            command.Parameters.AddRange(parameters);
            return await command.ExecuteScalarAsync();
        }

        public async Task MarkTestFixtureReadyAsync()
        {
            if (!_created.Contains(_destinationName)) throw new InvalidOperationException("Fixture destination not owned.");
            object? changed = await DestinationSqlAsync("""
                UPDATE surf.StorageFormatInfo SET State='Ready', CompletedMigrationIdentity=@Migration
                WHERE Singleton=1 AND State='Migrating';
                SELECT @@ROWCOUNT;
                """, RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, MigrationIdentity));
            if (!Equals(changed, 1)) throw new InvalidOperationException("Fixture Ready transition did not update exactly one marker.");
        }

        public async Task VerifySourceUnchangedAsync(Action<bool, string> check)
        {
            await using var source = new SqlConnection(SourceConnectionString);
            await source.OpenAsync();
            await using var command = source.CreateCommand();
            command.CommandText = """
                SELECT DocumentKey, PayloadJson, UpdatedAtUtc FROM app.Surf2Documents ORDER BY DocumentKey;
                SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped=0;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            int count = 0;
            bool unchanged = true;
            while (await reader.ReadAsync())
            {
                count++;
                unchanged &= _documents.TryGetValue(reader.GetString(0), out string? payload) && reader.GetString(1) == payload &&
                    reader.GetDateTime(2) == new DateTime(2024, 2, 3, 4, 5, 6, 123);
            }
            check(unchanged && count == _documents.Count, "Every legacy SQL source payload and timestamp remains unchanged");
            check(count == _documents.Count, "Legacy source retains exactly its original fixture documents");
            await reader.NextResultAsync();
            check(await reader.ReadAsync() && reader.GetInt64(0) == 1, "Relational tests install no tables in the legacy source");
        }

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            foreach (string name in _created.ToArray())
            {
                try
                {
                    ValidateName(name);
                    if (name != _sourceName && name != _destinationName)
                        throw new InvalidOperationException("Refusing to drop a database not owned by this fixture.");
                    await using var master = new SqlConnection(MasterConnectionString);
                    await master.OpenAsync();
                    await using var command = master.CreateCommand();
                    command.CommandTimeout = 120;
                    command.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];";
                    await command.ExecuteNonQueryAsync();
                    _created.Remove(name);
                }
                catch (Exception ex) { failures.Add(new InvalidOperationException("Could not clean owned fixture " + name, ex)); }
            }
            try
            {
                string root = OwnedDirectory;
                ValidateName(Path.GetFileName(root));
                if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                    Path.GetFileName(root) != _ownedDirectoryName)
                    throw new InvalidOperationException("Refusing cleanup outside the fixture-owned temporary directory.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) { failures.Add(new InvalidOperationException("Could not clean owned fixture staging files.", ex)); }
            if (failures.Count != 0) throw new AggregateException("Owned LocalDB fixture cleanup failed.", failures);
        }
    }

    private static async Task VerifySchemaAsync(SqlFixture fixture, Action<bool, string> check)
    {
        var probe = new PersistenceFormatProbe();
        check((await probe.ProbeAsync(fixture.SourceConnectionString)).Format == PersistenceFormat.Legacy,
            "Populated fixture source has the exact legacy schema");
        check((await probe.ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Empty,
            "Fixture destination starts empty");
        var installer = new RelationalSchemaInstaller();
        await ThrowsAsync<InvalidOperationException>(() => installer.InitializeDestinationAsync(
            fixture.SourceConnectionString, fixture.SourceConnectionString, fixture.MigrationIdentity, fixture.Fingerprint), check,
            "Installer rejects source equal to destination");
        await ThrowsAsync<ArgumentException>(() => installer.InitializeDestinationAsync(
            fixture.SourceConnectionString, fixture.DestinationConnectionString, Guid.Empty, fixture.Fingerprint), check,
            "Installer rejects empty migration identity");
        await ThrowsAsync<ArgumentException>(() => installer.InitializeDestinationAsync(
            fixture.SourceConnectionString, fixture.DestinationConnectionString, fixture.MigrationIdentity, new byte[31]), check,
            "Installer rejects malformed fingerprint");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped=0;")) == 0,
            "Rejected installer requests leave the destination empty");
        await installer.InitializeDestinationAsync(fixture.SourceConnectionString, fixture.DestinationConnectionString,
            fixture.MigrationIdentity, fixture.Fingerprint);
        var installed = await probe.ProbeAsync(fixture.DestinationConnectionString);
        check(installed.Format == PersistenceFormat.Incomplete && installed.SchemaVersion == PersistenceFormatProbe.SupportedSchemaVersion &&
            installed.DatabaseIdentity is { } identity && identity != Guid.Empty, "Installed destination is Migrating with a format identity");
        check(Equals(await fixture.DestinationSqlAsync("SELECT State FROM surf.StorageFormatInfo WHERE Singleton=1;"), "Migrating"),
            "Schema installer never publishes Ready");
        byte[] checksum = (byte[])Required(await fixture.DestinationSqlAsync("SELECT ScriptChecksum FROM surf.SchemaMigration;"), "schema checksum");
        check(RelationalSchemaInstaller.ReadScripts().Length == 5 &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM sys.table_types WHERE schema_id=SCHEMA_ID(N'surf') AND name=N'IndexKeyBatch';")) == 1 &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.IndexCatalogueHead;")) == 1,
            "Fifth 001.Indexes script installs its table type and catalogue head after State");
        check(checksum.SequenceEqual(RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts())),
            "Installed migration checksum matches the embedded 001 schema and index scripts");
        byte[] fingerprint = (byte[])Required(await fixture.DestinationSqlAsync(
            "SELECT SourceFingerprint FROM surf.MigrationRun WHERE MigrationIdentity=@Migration;",
            RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, fixture.MigrationIdentity)), "source fingerprint");
        check(fingerprint.SequenceEqual(fixture.Fingerprint), "Installer preserves exact source fingerprint");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("""
            SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE s.name=N'surf' AND t.name IN
            (N'UserProfile',N'SnapshotResourceRevision',N'TableMetadataRevision',N'TableDataRevision',
             N'DataSet',N'DataLayout',N'Scope',N'DiagramRevision',N'ApplicationPreference',N'WorkspaceSession',N'Workbench');
            """)) == 11, "Core, snapshot, capture and state tables install together");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM sys.foreign_keys WHERE is_disabled=1 OR is_not_trusted=1;")) == 0,
            "All installed foreign keys are enabled and trusted");
        await ThrowsAsync<InvalidOperationException>(() => installer.InitializeDestinationAsync(
            fixture.SourceConnectionString, fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint), check,
            "Installer refuses reinstallation into its nonempty destination");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}
