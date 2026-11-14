using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    public static async Task RunJsonCaptureUpgradeChecksAsync(Action<bool, string> check)
    {
        await VerifyJsonCaptureReaderAsync(check);
        await using var fixture = await SqlFixture.CreateAsync();
        foreach (string script in RelationalSchemaInstaller.ReadScripts(1))
            await fixture.DestinationSqlAsync(script);
        Guid identity = Guid.NewGuid();
        await fixture.DestinationSqlAsync("""
INSERT surf.StorageFormatInfo(Singleton,FormatIdentifier,SchemaVersion,MinimumReaderVersion,MinimumWriterVersion,State,DatabaseIdentity)
VALUES(1,@Format,1,1,1,'Ready',@Identity);
INSERT surf.SchemaMigration(SchemaVersion,ScriptChecksum) VALUES(1,@Checksum);
""", RelationalSession.Parameter("@Format", SqlDbType.NVarChar, PersistenceFormatProbe.FormatIdentifier, 64),
            RelationalSession.Parameter("@Identity", SqlDbType.UniqueIdentifier, identity),
            RelationalSession.Parameter("@Checksum", SqlDbType.Binary,
                RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts(1)), 32));

        var session = new RelationalSession(fixture.DestinationConnectionString);
        SnapshotFixture snapshots = await SeedSnapshotsAsync(session, new RelationalContentStore());
        CaptureColumnDefinition[] columns = [new("A"), new("text")];
        var layout = new CaptureLayout(columns);
        long layoutKey = await CreateLegacyCaptureLayoutAsync(fixture, layout);
        JsonElement[] evidence =
        [
            Json("{\"A\":1.2300,\"A\":null,\"a\":\"collision\",\"text\":\"C:\\\\Temp\",\"nested\":{\"n\":1e+02,\"n\":-0}}"),
            Json("{\"text\":\"\\\"quoted\\\" é😀 \\u00e9\\ud83d\\ude00\\nline\",\"A\":false}"),
            Json("{}"), Json("null"), Json("[1.000,{\"a\":2,\"a\":3},false]"),
            Json("\"root string\""), Json("123456789012345678901234567890.000001e-27"),
            .. Enumerable.Range(0, 260).Select(i => JsonSerializer.SerializeToElement(new { A = i, text = "row " + i }))
        ];
        long currentKey = await CreateLegacyCaptureAsync(fixture, session, layout, layoutKey,
            snapshots.CurrentDataRevision, evidence, evidence.Length, "Ready");
        JsonElement[] partial = [Json("{\"A\":-0,\"text\":\"previous é😀\"}"), Json("{\"text\":null}")];
        long partialKey = await CreateLegacyCaptureAsync(fixture, session, layout, layoutKey,
            snapshots.OldDataRevision, partial, 3, "Writing");
        long emptyRevision = 0;
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(connection, transaction, new RelationalContentStore());
            emptyRevision = await writer.InsertTableDataRevisionAsync(snapshots.DataResource);
        });
        long emptyKey = await CreateLegacyCaptureAsync(fixture, session, layout, layoutKey,
            emptyRevision, [], 0, "Ready");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => new RelationalCaptureSchemaUpgrade()
                .UpgradeAsync(fixture.DestinationConnectionString, cancellationToken: cancelled.Token), check,
                "Cancelled startup migration leaves the legacy capture available for retry");
        }
        await fixture.DestinationSqlAsync("""
UPDATE surf.DataSet SET ActualRowCount=ActualRowCount+1,ExpectedActualRowCount=ExpectedActualRowCount+1 WHERE DataSetKey=@Key;
""", RelationalSession.Parameter("@Key", SqlDbType.BigInt, partialKey));
        await ThrowsAsync<InvalidDataException>(() => new RelationalCaptureSchemaUpgrade()
            .UpgradeAsync(fixture.DestinationConnectionString), check,
            "Startup migration rejects a physical capture whose row count disagrees with its catalogue");
        check(Equals(await fixture.DestinationSqlAsync("SELECT SchemaVersion FROM surf.StorageFormatInfo;"), 1) &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'capture');")) == 1,
            "Failed conversion retains the old schema marker and original capture table");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.FullTableData;")) == 1,
            "A failed later capture preserves the completed payload for startup migration recovery");
        await fixture.DestinationSqlAsync("""
UPDATE surf.DataSet SET ActualRowCount=ActualRowCount-1,ExpectedActualRowCount=ExpectedActualRowCount-1 WHERE DataSetKey=@Key;
""", RelationalSession.Parameter("@Key", SqlDbType.BigInt, partialKey));

        await Task.WhenAll(new RelationalCaptureSchemaUpgrade().UpgradeAsync(fixture.DestinationConnectionString),
            new RelationalCaptureSchemaUpgrade().UpgradeAsync(fixture.DestinationConnectionString));
        var format = await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString);
        check(format.Format == PersistenceFormat.Relational && format.SchemaVersion == 2 &&
            format.DatabaseIdentity == identity, "Concurrent startup migrations publish the new format and retain database identity");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'capture');")) == 0 &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.DataValueException;")) == 0,
            "Successful conversion removes generated row tables and their superseded exceptional tokens");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.FullTableData;")) == 3,
            "Ready, empty and partially written captures each receive one JSON document");

        await using(var upgraded=await session.OpenAsync())
            await RelationalResourceSchemaUpgrade.ApplyAsync(upgraded,identity,false,CancellationToken.None);
        var capture = new RelationalCaptureStore(new RelationalSession(fixture.DestinationConnectionString));
        var descriptor = await capture.GetDescriptorAsync(currentKey);
        check(descriptor.Summary.RevisionKey == snapshots.CurrentDataRevision && descriptor.Summary.LayoutKey == layoutKey &&
            descriptor.Columns.SequenceEqual(columns) && descriptor.Summary.ReportedRowCount == -99 &&
            descriptor.Summary.ImportedAtUtc == EvidenceTime,
            "Capture migration retains revision keys, layout metadata, reported counts and timestamps");
        var restored = new List<JsonElement>();
        await foreach (var row in capture.StreamRowsAsync(currentKey)) restored.Add(row.Value);
        check(restored.Count == evidence.Length && restored.Select((row, i) => JsonEvidenceEquals(row, evidence[i])).All(x => x),
            "Startup conversion round-trips multiple legacy batches including duplicate properties and exact numeric tokens");
        check(await capture.CountRowsAsync(emptyKey) == 0 &&
            Equals(await fixture.DestinationSqlAsync("SELECT JsonPayload FROM surf.FullTableData WHERE DataSetKey=@Key;",
                RelationalSession.Parameter("@Key", SqlDbType.BigInt, emptyKey)), "[]"),
            "An empty legacy capture becomes a valid empty JSON array");
        var pending = Required(await capture.FindForRevisionForImportAsync(snapshots.OldDataRevision), "partially migrated capture");
        check(pending.Handle.DataSetKey == partialKey && pending.Progress == new CaptureWriteProgress(2, "Writing", 3),
            "A partial legacy capture retains its committed import checkpoint");
        await fixture.DestinationSqlAsync("ALTER TABLE surf.FullTableData ALTER COLUMN JsonPayload nvarchar(max) COLLATE Latin1_General_100_CI_AS_SC NOT NULL;");
        JsonElement finalRow = Json("{\"A\":true,\"text\":\"resumed\"}");
        await capture.AppendRowsAsync(pending.Handle, JsonRows([finalRow]), 2);
        await capture.CompleteDataSetAsync(pending.Handle);
        restored.Clear();
        await foreach (var row in capture.StreamRowsAsync(partialKey)) restored.Add(row.Value);
        check(restored.Count == 3 && JsonEvidenceEquals(restored[0], partial[0]) &&
            JsonEvidenceEquals(restored[1], partial[1]) && JsonEvidenceEquals(restored[2], finalRow),
            "A migrated partial capture with supplementary Unicode resumes correctly under an SC collation");
        check((await capture.FindMatchingColumnsAsync(currentKey, @"C:\Temp")).SequenceEqual([1]),
            "Captured content search uses decoded cell values after JSON migration");
        object? before = await fixture.DestinationSqlAsync("SELECT HASHBYTES('SHA2_256',JsonPayload) FROM surf.FullTableData WHERE DataSetKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, currentKey));
        await using(var upgraded=await session.OpenAsync())
            await RelationalResourceSchemaUpgrade.ApplyAsync(upgraded,identity,false,CancellationToken.None);
        var after = (byte[])(await fixture.DestinationSqlAsync("SELECT HASHBYTES('SHA2_256',JsonPayload) FROM surf.FullTableData WHERE DataSetKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, currentKey)))!;
        check(before is byte[] hash && hash.SequenceEqual(after) &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.FullTableData;")) == 3,
            "Subsequent startup checks preserve captured payloads without repeating conversion");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task VerifyJsonCaptureReaderAsync(Action<bool, string> check)
    {
        const string source = "[null,1.2300,-0,true,\"é😀\\\"\\\\\\n\",{\"A\":1,\"A\":2},[false,{}]]";
        using var expected = JsonDocument.Parse(source);
        foreach (int chunk in new[] { 1, 7, 4096 })
        {
            await using var stream = new ChunkedReadStream(Encoding.UTF8.GetBytes(source), chunk);
            var rows = new List<JsonElement>();
            await foreach (JsonElement row in CaptureJsonArray.ReadAsync(stream, 1024)) rows.Add(row);
            check(rows.Count == expected.RootElement.GetArrayLength() &&
                rows.Select((row, i) => JsonEvidenceEquals(row, expected.RootElement[i])).All(x => x),
                "JSON row reader preserves escaped strings, split UTF-8, duplicate fields and scalar rows at chunk size " + chunk);
        }
        foreach (string malformed in new[] { "", "{}", "[1", "[null", "[1,", "[1 2]", "[] true", "[][]" })
        {
            bool rejected = false;
            try
            {
                await using var stream = new ChunkedReadStream(Encoding.UTF8.GetBytes(malformed), 1);
                await foreach (JsonElement row in CaptureJsonArray.ReadAsync(stream, 1024)) { }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException) { rejected = true; }
            check(rejected, "JSON row reader rejects malformed or incomplete payload " + JsonSerializer.Serialize(malformed));
        }
        await ThrowsAsync<CaptureLimitException>(async () =>
        {
            await using var stream = new ChunkedReadStream(Encoding.UTF8.GetBytes("[\"" + new string('x', 2048) + "\"]"), 17);
            await foreach (JsonElement row in CaptureJsonArray.ReadAsync(stream, 128)) { }
        }, check, "JSON row reader rejects an oversized row without retaining the full document");
    }

    private static async Task<long> CreateLegacyCaptureLayoutAsync(SqlFixture fixture, CaptureLayout layout)
    {
        long layoutKey = (long)(await fixture.DestinationSqlAsync("""
INSERT surf.DataLayout(LayoutHash,EncodingVersion,ColumnCount) OUTPUT INSERTED.LayoutKey VALUES(@Hash,1,@Count);
""", RelationalSession.Parameter("@Hash", SqlDbType.Binary, layout.Hash, 32),
            RelationalSession.Parameter("@Count", SqlDbType.Int, layout.Columns.Length)))!;
        for (int ordinal = 0; ordinal < layout.Columns.Length; ordinal++)
            await fixture.DestinationSqlAsync("""
INSERT surf.DataColumn(LayoutKey,ColumnOrdinal,SourceName,EncodingPolicy) VALUES(@Layout,@Ordinal,@Name,'JsonScalarToken-v1');
""", RelationalSession.Parameter("@Layout", SqlDbType.BigInt, layoutKey),
                RelationalSession.Parameter("@Ordinal", SqlDbType.Int, ordinal),
                RelationalSession.Parameter("@Name", SqlDbType.NVarChar, layout.Columns[ordinal].SourceName, -1));
        await fixture.DestinationSqlAsync(layout.CreateTableSql(layoutKey));
        return layoutKey;
    }

    private static async Task<long> CreateLegacyCaptureAsync(SqlFixture fixture, RelationalSession session, CaptureLayout layout,
        long layoutKey, long revisionKey, JsonElement[] rows, long expected, string state)
    {
        long key = (long)(await fixture.DestinationSqlAsync("""
INSERT surf.DataSet(RevisionKey,LayoutKey,ReportedRowCount,ActualRowCount,ExpectedActualRowCount,ImportedAtUtc,State,DisplayFormatVersion)
OUTPUT INSERTED.DataSetKey VALUES(@Revision,@Layout,-99,@Count,@Expected,@Imported,@State,1);
""", RelationalSession.Parameter("@Revision", SqlDbType.BigInt, revisionKey),
            RelationalSession.Parameter("@Layout", SqlDbType.BigInt, layoutKey),
            RelationalSession.Parameter("@Count", SqlDbType.BigInt, rows.Length),
            RelationalSession.Parameter("@Expected", SqlDbType.BigInt, expected),
            RelationalSession.Parameter("@Imported", SqlDbType.DateTimeOffset, EvidenceTime),
            RelationalSession.Parameter("@State", SqlDbType.VarChar, state, 16)))!;
        var limits = new CaptureLimits();
        var handle = new CaptureWriteHandle(session.Epoch, key, layoutKey);
        await using var connection = await session.OpenAsync();
        long ordinal = 0;
        foreach (JsonElement[] values in rows.Chunk(64))
        {
            using var batch = new CaptureBulkBatch(handle, layout,
                values.Select(row => CaptureRowCodec.Encode(layout, row, limits)).ToArray(), ordinal, limits);
            foreach (var (table, destination) in new[] { (batch.Rows, CaptureLayout.QualifiedTable(layoutKey)), (batch.Exceptions, "surf.DataValueException") })
            {
                using var copy = new SqlBulkCopy(connection) { DestinationTableName = destination };
                foreach (DataColumn column in table.Columns) copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                await copy.WriteToServerAsync(table);
            }
            ordinal += values.Length;
        }
        return key;
    }
}
