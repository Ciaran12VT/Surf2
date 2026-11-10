using System.Data;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;

public static partial class StorageRegressionSuite
{
    public static async Task RunMigrationMappingChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            const string document = "doc:\u00e9\ud83d\ude00";
            string[] identities = MappingAccess.Identities(document, "histories/0/versions/2/changes/13/previous");
            check(identities.Length == 128 && identities[13] == MappingIdentity(document,
                "histories/0/versions/2/changes/13/previous") && identities[127] == MappingIdentity(document,
                "histories/0/versions/2/changes/127/previous"),
                "Mapping identity uses invariant UTF-16 lengths and varies only the final numeric segment");
            string[] seals = MappingAccess.Identities("database-snapshots", "snapshots/0/tables/9/seal");
            check(seals.Length == 128 && seals[9] == MappingIdentity("database-snapshots", "snapshots/0/tables/9/seal"),
                "Mapping batches preserve table seal suffixes");
            foreach (string path in new[] { "snapshots/2/publish", "snapshots/2/objects/7", "snapshots/2/columns/7",
                "snapshots/2/keys/7", "snapshots/2/selections/7", "snapshots/2/datasets/7/seal",
                "histories/2", "histories/2/versions/7", "histories/2/versions/7/changes/4/resource",
                "histories/2/versions/7/changes/4/previous/seal" })
                check(MappingAccess.Identities("d", path).Length == 128, "Known converter path batches: " + path);

            string[] first = MappingAccess.Identities("d", "snapshots/127");
            string[] next = MappingAccess.Identities("d", "snapshots/128");
            check(first.SequenceEqual(MappingAccess.Identities("d", "snapshots/0")) &&
                next[0] == MappingIdentity("d", "snapshots/128") && next[127] == MappingIdentity("d", "snapshots/255"),
                "Sibling blocks align deterministically at 128-ordinal boundaries");
            string[] maximum = MappingAccess.Identities("d", "snapshots/" + long.MaxValue.ToString(CultureInfo.InvariantCulture));
            check(maximum.Length == 128 && maximum[0] == MappingIdentity("d", "snapshots/9223372036854775680") &&
                maximum[^1] == MappingIdentity("d", "snapshots/9223372036854775807"),
                "Sibling range includes the maximum long ordinal without overflow");
            foreach (string path in new[] { "", "$", "$.Scopes[2]", "header", "other/7", "snapshots", "snapshots/1/",
                "snapshots//1", "snapshots/01", "snapshots/+1", "snapshots/-1", "snapshots/ 1", "snapshots/\u0661",
                "snapshots/9223372036854775808", "histories/00/versions/2/changes/3", "snapshots/0/unknown/3",
                "histories/0/versions/2/changes/3/resource/seal", "snapshots/0/objects/1/seal" })
                check(MappingAccess.Identities("d", path).SequenceEqual(new[] { MappingIdentity("d", path) }),
                    "Unknown or noncanonical path retains exact single lookup: " + path);
            check(MappingIdentity("a1", "b") != MappingIdentity("a", "1b"),
                "Length-prefixed mapping identities cannot confuse document/path boundaries");
            check(MappingAccess.Identities(new string('d', 40_000), "snapshots/0").Length == 1,
                "Oversized sibling metadata degrades to a bounded single lookup");
        }
        finally { CultureInfo.CurrentCulture = saved; }

        string identity = MappingIdentity("d", "snapshots/0");
        byte[] hash = MappingHash(identity);
        object valid = MappingAccess.Validate(identity, hash, identity, "Snapshot", -1);
        check(MappingAccess.Result(valid) == new MappingResult(identity, "Snapshot", -1),
            "Mapping reader leaves entity-kind and destination-key validation to its caller");
        check(MappingAccess.Result(MappingAccess.Validate(identity, hash, identity, "UnexpectedKind", 0)) ==
            new MappingResult(identity, "UnexpectedKind", 0),
            "A valid journal identity does not make the reader enforce its caller's kind or positive-key checks");
        MappingAccess.GuardLengths(identity, identity.Length * 2L, 256);
        MappingRejects<InvalidDataException>(() => MappingAccess.GuardLengths(identity, identity.Length * 2L + 2, 16), check,
            "Mapping LOB length mismatch is rejected before string materialization");
        foreach (int kindBytes in new[] { -1, 1, 257, 258 })
            MappingRejects<InvalidDataException>(() => MappingAccess.GuardLengths(identity, identity.Length * 2L, kindBytes), check,
                "Mapping kind length is bounded before materialization: " + kindBytes);
        MappingRejects<InvalidDataException>(() => MappingAccess.Validate(identity, new byte[32], identity, "Snapshot", 1), check,
            "Returned migration hash must match the UTF-8 identity");
        MappingRejects<InvalidDataException>(() => MappingAccess.Validate(identity, hash, identity.ToUpperInvariant(), "Snapshot", 1), check,
            "Identity checks are ordinal rather than SQL-collation comparisons");
        MappingRejects<InvalidDataException>(() => MappingAccess.Validate(identity, hash, identity + "x", "Snapshot", 1), check,
            "A substituted identity under a requested hash is rejected as a collision");

        var access = new MappingAccess();
        object initial = MappingAccess.Block("d", "snapshots/0");
        MappingAccess.Add(initial, valid);
        MappingRejects<InvalidDataException>(() => MappingAccess.Add(initial, valid), check,
            "Duplicate returned migration mappings fail instead of overwriting");
        access.Remember(initial);
        for (int i = 1; i < 8; i++) access.Remember(MappingAccess.Block("d", "snapshots/" + (i * 128)));
        check(access.Cached(identity, out var cached) && cached == new MappingResult(identity, "Snapshot", -1),
            "Cached mapping retains the full ordinal source identity");
        check(access.Cached(MappingIdentity("d", "snapshots/1"), out var absent) && absent == null &&
            !access.Cached(identity.ToUpperInvariant(), out _),
            "Cached null slots distinguish missing mappings and reject case-folded identities");
        access.Remember(MappingAccess.Block("d", "snapshots/1024"));
        check(access.Count == 8 && !access.Cached(MappingIdentity("d", "snapshots/128"), out _) &&
            access.Cached(identity, out _) && access.Bytes <= 8L * 1024 * 1024,
            "Eight-block LRU evicts the least recently used block including its null slots");
        check(await access.FindAsync("d", "snapshots/0") == cached && await access.FindAsync("d", "snapshots/1") == null,
            "Both present and absent cached lookups perform no SQL");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => access.FindAsync("d", "snapshots/0", cancellation.Token), check,
                "Pre-cancelled cache hit is cancelled rather than returning a mapping");
            await ThrowsAsync<OperationCanceledException>(() => access.FindAsync("d", "snapshots/8192", cancellation.Token), check,
                "Pre-cancelled cache miss does not open SQL");
        }
        check(access.Session.Metrics.Snapshot().Started == 0, "Pure mapping cache cases never execute a database command");

        var budget = new MappingAccess();
        string largeDocument = new('d', 15_000);
        for (int i = 0; i < 3; i++) budget.Remember(MappingAccess.Block(largeDocument, "snapshots/" + (i * 128)));
        check(budget.Count == 2 && budget.Bytes <= 8L * 1024 * 1024 &&
            !budget.Cached(MappingIdentity(largeDocument, "snapshots/0"), out _) &&
            budget.Cached(MappingIdentity(largeDocument, "snapshots/256"), out _),
            "Metadata-byte LRU evicts before the eight-block limit is reached");
        MappingRejects<InvalidDataException>(() => MappingAccess.Block(new string('d', 4 * 1024 * 1024), "$"), check,
            "An individually oversized mapping request fails within the metadata limit");
    }

    public static async Task RunMigrationMappingSqlChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        const string document = "database-snapshots";
        var seeds = new List<MappingSeed>();
        for (int i = 0; i < 300; i++)
        {
            if (i != 13) seeds.Add(new(document, "snapshots/0/tables/" + i, "TableRevision", 1000 + i));
            seeds.Add(new(document, "snapshots/0/tables/" + i + "/seal", "SealedRevision", 1000 + i));
        }
        seeds.Add(new(document, "histories/0/versions/2/changes/13/previous", "PreviousTableRevision", 9013));
        seeds.Add(new(document, "histories/0/versions/2/changes/13/previous/seal", "SealedPreviousData", 9013));
        seeds.Add(new("app-settings", "$", "Settings", 1));
        seeds.Add(new(document, "snapshots/9223372036854775807", "Snapshot", 9001));
        seeds.Add(new(document, "SNAPSHOTS/0/TABLES/0", "CaseSensitiveFixture", 9002));
        seeds.Add(new(document, "snapshots/00", "UnexpectedKind", -2));
        await SeedMappingRowsAsync(fixture, fixture.MigrationIdentity, seeds);
        Guid otherMigration = Guid.NewGuid();
        await fixture.DestinationSqlAsync("""
INSERT surf.MigrationRun(MigrationIdentity,SourceFingerprint,ConverterVersion,SourceDatabaseName,Status)
VALUES(@Migration,@Fingerprint,1,N'fixture-other','Converting');
""", RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, otherMigration),
            RelationalSession.Parameter("@Fingerprint", SqlDbType.Binary, fixture.Fingerprint, 32));
        await SeedMappingRowsAsync(fixture, otherMigration, [new(document, "snapshots/0/tables/0", "OtherMigration", 9999)]);
        long mapCount = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationIdentityMap;"));
        long tableCount = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped=0;"));
        byte[] checksum = (byte[])Required(await fixture.DestinationSqlAsync("SELECT ScriptChecksum FROM surf.SchemaMigration;"), "mapping checksum");

        var access = new MappingAccess(fixture.DestinationConnectionString, fixture.MigrationIdentity);
        long before = access.Queries;
        bool exact = true;
        for (int i = 0; i < 300; i++)
        {
            MappingResult? result = await access.FindAsync(document, "snapshots/0/tables/" + i);
            exact &= i == 13 ? result == null : result == new MappingResult(
                MappingIdentity(document, "snapshots/0/tables/" + i), "TableRevision", 1000 + i);
        }
        check(exact && access.Queries - before == 3, "Three indexed map batches serve 300 consecutive table lookups including a missing row");
        before = access.Queries;
        for (int i = 299; i >= 0; i--) await access.FindAsync(document, "snapshots/0/tables/" + i);
        check(await access.FindAsync(document, "snapshots/0/tables/310") == null && access.Queries == before,
            "Repeated reads and prefetched absences execute no additional SQL");
        for (int i = 0; i < 300; i++)
            exact &= (await access.FindAsync(document, "snapshots/0/tables/" + i + "/seal"))?.DestinationKey == 1000 + i;
        check(exact && access.Queries - before == 3, "Seal suffixes use their own contiguous sibling hash batches");
        before = access.Queries;
        check((await access.FindAsync(document, "histories/0/versions/2/changes/13/previous"))?.DestinationKey == 9013 &&
            await access.FindAsync(document, "histories/0/versions/2/changes/14/previous") == null && access.Queries - before == 1,
            "Nested previous-payload paths batch the final change ordinal and retain missing neighbors");
        check((await access.FindAsync(document, "histories/0/versions/2/changes/13/previous/seal"))?.DestinationKey == 9013,
            "Nested previous seal is not confused with the previous mapping");
        before = access.Queries;
        check((await access.FindAsync("app-settings", "$"))?.EntityKind == "Settings" &&
            (await access.FindAsync("app-settings", "$"))?.DestinationKey == 1 && access.Queries - before == 1,
            "Non-numeric paths use a cached parameterized single lookup");
        check((await access.FindAsync(document, "SNAPSHOTS/0/TABLES/0"))?.DestinationKey == 9002 &&
            await access.FindAsync(document, "snapshots/00") == new MappingResult(MappingIdentity(document, "snapshots/00"), "UnexpectedKind", -2),
            "SQL collation does not collapse case-distinct or noncanonical identities and key checks stay with the caller");
        before = access.Queries;
        check((await access.FindAsync(document, "snapshots/9223372036854775807"))?.DestinationKey == 9001 &&
            await access.FindAsync(document, "snapshots/9223372036854775806") == null && access.Queries - before == 1,
            "Maximum-long SQL batch is bounded, non-overflowing and caches absent siblings");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); before = access.Queries;
            await ThrowsAsync<OperationCanceledException>(() => access.FindAsync(document, "snapshots/9223372036854775807", cancellation.Token), check,
                "SQL-backed cached hit observes cancellation without another command");
            check(access.Queries == before, "Cancelled mapping cache hit performs no SQL");
        }

        var eviction = new MappingAccess(fixture.DestinationConnectionString, fixture.MigrationIdentity);
        for (int i = 0; i < 9; i++) await eviction.FindAsync(document, "snapshots/0/tables/" + (i * 128));
        before = eviction.Queries;
        await eviction.FindAsync(document, "snapshots/0/tables/0");
        check(before == 9 && eviction.Queries == 10 && eviction.Count == 8 && eviction.Bytes <= 8L * 1024 * 1024,
            "SQL mapping cache bounds both populated and entirely absent blocks and reloads evicted metadata");

        const string collisionPath = "snapshots/7/tables/20";
        string collisionIdentity = MappingIdentity(document, collisionPath);
        await SeedMappingRowsAsync(fixture, fixture.MigrationIdentity,
            [new(document, collisionPath, "TableRevision", 7020, MappingIdentity(document, "snapshots/7/tables/21")),
             new(document, "snapshots/7/tables/19", "TableRevision", 7019)]);
        var corrupt = new MappingAccess(fixture.DestinationConnectionString, fixture.MigrationIdentity);
        await ThrowsAsync<InvalidDataException>(() => corrupt.FindAsync(document, collisionPath), check,
            "SQL lookup rejects a substituted same-length identity under a requested hash");
        check(corrupt.Count == 0, "Failed map batch retains neither partial mappings nor null slots");
        await RepairMappingIdentityAsync(fixture, collisionIdentity);
        before = corrupt.Queries;
        check((await corrupt.FindAsync(document, collisionPath))?.DestinationKey == 7020 &&
            (await corrupt.FindAsync(document, "snapshots/7/tables/19"))?.DestinationKey == 7019 && corrupt.Queries - before == 1,
            "A failed batch is queried afresh after fixture repair");

        const string oversizedPath = "snapshots/8/tables/0";
        string oversizedIdentity = MappingIdentity(document, oversizedPath);
        await SeedMappingRowsAsync(fixture, fixture.MigrationIdentity,
            [new(document, oversizedPath, "TableRevision", 8000, new string('x', 1024 * 1024))]);
        var oversized = new MappingAccess(fixture.DestinationConnectionString, fixture.MigrationIdentity);
        await ThrowsAsync<InvalidDataException>(() => oversized.FindAsync(document, oversizedPath), check,
            "SQL mapping LOB length guard rejects oversized identity metadata before buffering it");
        check(oversized.Count == 0, "Oversized map batch is never cached");
        await RepairMappingIdentityAsync(fixture, oversizedIdentity);
        check((await oversized.FindAsync(document, oversizedPath))?.DestinationKey == 8000,
            "Oversized fixture repair is visible because failed loads were not cached");

        var cancelled = new MappingAccess(fixture.DestinationConnectionString, fixture.MigrationIdentity);
        await using (var blocker = new SqlConnection(fixture.DestinationConnectionString))
        {
            await blocker.OpenAsync();
            await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
            await using var command = blocker.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
SELECT DestinationKey FROM surf.MigrationIdentityMap WITH (XLOCK,HOLDLOCK,ROWLOCK)
WHERE MigrationIdentity=@Migration AND SourceIdentityHash=@Hash;
""";
            command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, fixture.MigrationIdentity));
            command.Parameters.Add(RelationalSession.Parameter("@Hash", SqlDbType.Binary,
                MappingHash(MappingIdentity(document, "snapshots/0/tables/0")), 32));
            await command.ExecuteScalarAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<MappingResult?> pending = cancelled.FindAsync(document, "snapshots/0/tables/0", cancellation.Token);
            for (int i = 0; i < 100 && cancelled.Queries == 0 && !pending.IsCompleted; i++) await Task.Delay(10);
            check(cancelled.Queries == 1, "Cancellation fixture starts the indexed SQL command before requesting cancellation");
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => pending, check,
                "A blocked indexed map batch is cancelled through the provider");
            check(cancelled.Count == 0, "An in-flight cancelled batch caches no partial data");
            await transaction.RollbackAsync();
        }
        before = cancelled.Queries;
        check((await cancelled.FindAsync(document, "snapshots/0/tables/0"))?.DestinationKey == 1000 && cancelled.Queries - before == 1,
            "A cancelled batch is reloaded successfully after the fixture lock is released");
        check(new[] { access, eviction, corrupt, oversized, cancelled }.All(reader => reader.Session.Metrics.Snapshot().ActiveCommands == 0),
            "Mapping success, corruption and cancellation dispose every measured SQL command");
        byte[] finalChecksum = (byte[])Required(await fixture.DestinationSqlAsync("SELECT ScriptChecksum FROM surf.SchemaMigration;"), "mapping checksum");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationIdentityMap;")) == mapCount + 3 &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped=0;")) == tableCount &&
            Equals(await fixture.DestinationSqlAsync("SELECT State FROM surf.StorageFormatInfo WHERE Singleton=1;"), "Migrating") &&
            checksum.SequenceEqual(finalChecksum),
            "Mapping reads create no tables or journal writes and never alter schema checksum or publication state");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private sealed record MappingResult(string SourceIdentity, string EntityKind, long DestinationKey);
    private sealed record MappingSeed(string Document, string Path, string Kind, long Key, string? StoredIdentity = null);

    private static string MappingIdentity(string document, string path) =>
        FormattableString.Invariant($"{document.Length}:{document}{path.Length}:{path}");
    private static byte[] MappingHash(string identity) => SHA256.HashData(Encoding.UTF8.GetBytes(identity));

    private static async Task SeedMappingRowsAsync(SqlFixture fixture, Guid migration, IReadOnlyList<MappingSeed> rows)
    {
        foreach (MappingSeed[] chunk in rows.Chunk(128))
        {
            var parameters = new List<SqlParameter> { RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, migration) };
            var values = new List<string>();
            for (int i = 0; i < chunk.Length; i++)
            {
                MappingSeed row = chunk[i];
                string suffix = i.ToString(CultureInfo.InvariantCulture);
                values.Add($"(@Migration,@Hash{suffix},@Identity{suffix},@Kind{suffix},@Key{suffix})");
                string identity = MappingIdentity(row.Document, row.Path);
                parameters.Add(RelationalSession.Parameter("@Hash" + suffix, SqlDbType.Binary, MappingHash(identity), 32));
                parameters.Add(RelationalSession.Parameter("@Identity" + suffix, SqlDbType.NVarChar, row.StoredIdentity ?? identity, -1));
                parameters.Add(RelationalSession.Parameter("@Kind" + suffix, SqlDbType.NVarChar, row.Kind, 128));
                parameters.Add(RelationalSession.Parameter("@Key" + suffix, SqlDbType.BigInt, row.Key));
            }
            await fixture.DestinationSqlAsync("INSERT surf.MigrationIdentityMap(MigrationIdentity,SourceIdentityHash,SourceIdentity,EntityKind,DestinationKey) VALUES " +
                string.Join(",", values) + ";", parameters.ToArray());
        }
    }

    private static Task<object?> RepairMappingIdentityAsync(SqlFixture fixture, string identity) => fixture.DestinationSqlAsync("""
UPDATE surf.MigrationIdentityMap SET SourceIdentity=@Identity WHERE MigrationIdentity=@Migration AND SourceIdentityHash=@Hash;
""", RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, fixture.MigrationIdentity),
        RelationalSession.Parameter("@Hash", SqlDbType.Binary, MappingHash(identity), 32),
        RelationalSession.Parameter("@Identity", SqlDbType.NVarChar, identity, -1));

    private static void MappingRejects<T>(Action action, Action<bool, string> check, string name) where T : Exception
    {
        bool rejected = false;
        try { action(); }
        catch (T) { rejected = true; }
        check(rejected, name);
    }

    // Reflection keeps these tests callable without changing production visibility or assembly registration.
    private sealed class MappingAccess
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type ReaderType = typeof(RelationalSession).Assembly.GetType(
            "Surf2.Storage.Relational.Migration.MigrationMappingReader", throwOnError: true)!;
        private readonly object _reader;
        public RelationalSession Session { get; }
        public long Queries => Session.Metrics.Snapshot().Started;
        public int Count => (int)Field("_cache").GetType().GetProperty("Count")!.GetValue(Field("_cache"))!;
        public long Bytes => (long)Field("_metadataBytes");

        public MappingAccess(string connectionString = "Server=(localdb)\\MSSQLLocalDB;Database=Surf2_Regression_Unused;Integrated Security=True;TrustServerCertificate=True",
            Guid migration = default)
        {
            Session = new(connectionString);
            Session.Metrics.Enable();
            _reader = Activator.CreateInstance(ReaderType, Instance, null, [Session, migration], CultureInfo.InvariantCulture)!;
        }

        public async Task<MappingResult?> FindAsync(string document, string path, CancellationToken ct = default)
        {
            var task = (Task)Invoke(ReaderType.GetMethod("FindAsync", Instance)!, _reader, [document, path, ct])!;
            await task;
            object? mapping = task.GetType().GetProperty("Result")!.GetValue(task);
            return mapping == null ? null : Result(mapping);
        }

        public bool Cached(string identity, out MappingResult? result)
        {
            object?[] args = [identity, null];
            bool found = (bool)Invoke(ReaderType.GetMethod("TryCached", Instance)!, _reader, args)!;
            result = args[1] == null ? null : Result(args[1]!);
            return found;
        }

        public void Remember(object block) => Invoke(ReaderType.GetMethod("Remember", Instance)!, _reader, [block]);
        public static string[] Identities(string document, string path) => (string[])Call("CreateIdentities", [document, path])!;
        public static object Block(string document, string path) => Call("CreateBlock", [document, path])!;
        public static object Validate(string expected, byte[] hash, string identity, string kind, long key) =>
            Call("ValidateMapping", [expected, hash, identity, kind, key])!;
        public static void GuardLengths(string identity, long identityBytes, int kindBytes) => Call("GuardRowLengths", [identity, identityBytes, kindBytes]);
        public static void Add(object block, object mapping) => Invoke(block.GetType().GetMethod("Add", Instance)!, block, [mapping]);
        public static MappingResult Result(object mapping) => new((string)Property(mapping, "SourceIdentity"),
            (string)Property(mapping, "EntityKind"), (long)Property(mapping, "DestinationKey"));

        private object Field(string name) => ReaderType.GetField(name, Instance)!.GetValue(_reader)!;
        private static object Property(object owner, string name) => owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
        private static object? Call(string name, object?[] args) => Invoke(ReaderType.GetMethod(name, Static)!, null, args);
        private static object? Invoke(MethodInfo method, object? target, object?[] args)
        {
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
