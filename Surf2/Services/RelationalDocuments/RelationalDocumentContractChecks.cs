using System.Collections.Immutable;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalDocuments;

/// <summary>Pure selected-document checks. No SQL, UI, source-file reads or writes.</summary>
public static class RelationalDocumentContractChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var passed = new List<string>();
        Guid epoch = Guid.Parse("471a78eb-daca-4186-a54d-8855e195ef03");
        Guid other = Guid.Parse("471a78eb-daca-4186-a54d-8855e195ef04");
        var metadata = new SnapshotDocumentIdentity(epoch, 1, 2, false);
        Check(metadata == new SnapshotDocumentIdentity(epoch, 1, 2, false) && metadata != new SnapshotDocumentIdentity(other, 1, 2, false),
            "Logical identities namespace overlapping bigint keys by database epoch", passed);
        Check(metadata != new SnapshotDocumentIdentity(epoch, 1, 2, true) && metadata != new SnapshotDocumentIdentity(epoch, 1, 3, false),
            "Table code, table data and duplicate-name resources have distinct window identities", passed);
        var revision = new SnapshotResourceLocator(epoch, 1, 2, 3);
        Check(new DocumentTextKey(revision, "v1") != new DocumentTextKey(new SnapshotResourceLocator(epoch, 1, 2, 4), "v1") &&
            new DocumentTextKey(revision, "v1") != new DocumentTextKey(revision, "v2"), "Text keys bind exact immutable revision and renderer policy", passed);
        foreach (var state in new[] { SavedDocumentTargetState.Missing, SavedDocumentTargetState.Ambiguous })
        {
            try
            {
                RelationalDocumentService.RejectUnresolvedSavedTarget(new() { FilePath = "db://duplicate/table/dbo.T.sql", TargetState = state });
                throw new InvalidOperationException("An unresolved saved target was rebound by its raw path.");
            }
            catch (DocumentTargetException error) when (error.TargetState == state) { passed.Add(state + " saved targets fail before any locator query"); }
        }
        var resource = new SnapshotResourceSummary(2, Guid.Empty, 1, DatabaseVersionedResourceKind.TableMetadata,
            "dbo", "T", "original", 3, 0, new byte[8]);
        string path = RelationalDocumentService.IdentityPath(epoch, resource, ExplorerCategory.Tables, false);
        string renamed = RelationalDocumentService.IdentityPath(epoch, resource with { SchemaName = "changed", ObjectName = "renamed" }, ExplorerCategory.Tables, false);
        Check(path == renamed && path != RelationalDocumentService.IdentityPath(epoch, resource with { ResourceKey = 4 }, ExplorerCategory.Tables, false) &&
            DatabaseDocumentService.TryParseDocumentPath(path, out var parsed) && parsed.FullName == "2",
            "Internal identity paths survive renames without colliding with duplicate resource names", passed);
        var snapshot = new SnapshotSummary(1, Guid.Empty, "original", "Friendly", "Database", DateTimeOffset.MinValue, 0, null, new byte[8]);
        Check(RelationalDocumentService.SnapshotMatchRank(snapshot, "original") == 0 &&
            RelationalDocumentService.SnapshotMatchRank(snapshot, "Friendly") == 1 &&
            RelationalDocumentService.SnapshotMatchRank(snapshot, "Database") == 2 &&
            RelationalDocumentService.SnapshotMatchRank(snapshot, "missing") == int.MaxValue,
            "Unbound locator matching retains legacy preference tiers without first-owner selection", passed);

        var table = new SqlTable { SchemaName = "dbo", TableName = "T]", HasFullData = true, FullDataRowCount = 7 };
        var columns = new[]
        {
            new OrderedColumn(1, 0, new() { SchemaName = "dbo", TableName = "T]", ColumnName = "Second", DataType = "nvarchar", MaxLength = 20, Ordinal = 2, IsNullable = true }),
            new OrderedColumn(2, 1, new() { SchemaName = "dbo", TableName = "T]", ColumnName = "First]", DataType = "int", Ordinal = 1, IsIdentity = true }),
            new OrderedColumn(3, 2, new() { SchemaName = "dbo", TableName = "T]", ColumnName = "Tie", DataType = "decimal", NumericPrecision = 12, NumericScale = 3, Ordinal = 1 })
        };
        var keys = new[] { new OrderedPrimaryKey(1, 0, new() { SchemaName = "dbo", TableName = "T]", ColumnName = "First]", ConstraintName = "PK" }) };
        var details = new TableMetadataDetails(3, table, columns, keys);
        string expected = new DatabaseDocumentService().CreateTableDocument(new() { Columns = columns.Select(c => c.Column).ToList(), PrimaryKeys = keys.Select(k => k.Column).ToList() }, table);
        string rendered = RelationalDocumentService.RenderTable(details, 1024 * 1024);
        Check(rendered == expected && rendered.IndexOf("[First]]]") < rendered.IndexOf("[Tie]") && rendered.Contains(" PK IDENTITY", StringComparison.Ordinal),
            "Selected-table rendering preserves legacy format, source ordinal ties, keys and full-data header", passed);
        Check(RelationalDocumentService.EstimateTableTextBytes(details) >= (long)rendered.Length * 2,
            "Generated metadata is byte-estimated before its output string is allocated", passed);
        try { RelationalDocumentService.RenderTable(details, 1); throw new InvalidOperationException("Over-budget metadata was rendered."); }
        catch (DocumentTextLimitException) { passed.Add("Over-budget generated documents explicitly fail without truncation"); }

        var address = new RelationalDocumentAddress(metadata, path, "document.sql", "T.sql", null, revision, null);
        var plan = new DocumentTextPlan(address, new(revision, "v1"), 10);
        var indexed = new IndexedDocumentSummary(1, 1, IndexedDocumentKind.TableCode, "T", "SQL", "alias", "node", "parent", 1,
            IndexFreshness.Indexed, 1, 3, null, null, new("parser", "renderer", "policy"));
        RelationalDocumentService.ValidateReferenceSource(indexed, address, plan);
        passed.Add("Reference locations require the selected authoritative metadata revision");
        try { RelationalDocumentService.ValidateReferenceSource(indexed with { SourceRevisionKey = 4 }, address, plan); throw new InvalidOperationException("Stale locations were accepted."); }
        catch (DocumentReferenceChangedException) { passed.Add("An independent newer resource revision rejects old symbol locations"); }
        const string filePath = @"C:\fixtures\source.cs";
        var fileLocator = new PhysicalDocumentLocator(epoch, filePath, new string('A', 64));
        Check(typeof(PhysicalDocumentLocator).GetProperties().All(p => p.PropertyType.IsValueType), "Physical locators retain only fixed-size value fields, never paths or payload strings", passed);
        var fileAddress = new RelationalDocumentAddress(PhysicalDocumentIdentity.From(epoch, filePath), filePath, filePath, "source.cs", null, null, null);
        var filePlan = new DocumentTextPlan(fileAddress, new(fileLocator, "file-v1", filePath.ToUpperInvariant()), 10);
        RelationalDocumentService.ValidateReferenceSource(indexed with { Kind = IndexedDocumentKind.File, Fingerprint = new string('A', 64) }, fileAddress, filePlan);
        try { RelationalDocumentService.ValidateReferenceSource(indexed with { Kind = IndexedDocumentKind.File, Fingerprint = new string('B', 64) }, fileAddress, filePlan); throw new InvalidOperationException("Changed file locations were accepted."); }
        catch (DocumentReferenceChangedException) { passed.Add("Physical reference positions require exact authoritative file hash agreement"); }

        var limits = new DocumentOpenLimits { WarningBytes = 16, MaximumTextBytes = 64, CacheBytes = 2048, CacheEntries = 2 };
        await using var cache = new DocumentTextCache(limits);
        var key = new DocumentTextKey(revision, "v1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        async Task<string> Read(CancellationToken token) { Interlocked.Increment(ref reads); entered.TrySetResult(); return await finish.Task.WaitAsync(token); }
        var first = cache.ReadAsync(key, Read, ct);
        await entered.Task.WaitAsync(ct);
        var joined = cache.ReadAsync(key, Read, ct);
        finish.SetResult("selected text");
        var results = await Task.WhenAll(first, joined);
        Check(reads == 1 && ReferenceEquals(results[0], results[1]), "Simultaneous aliases share one typed revision text read", passed);
        await cache.ReadAsync(key, _ => throw new InvalidOperationException("Cache hit read its provider."), ct);
        Check(reads == 1 && cache.Usage.RetainedBytes <= limits.CacheBytes && cache.ActiveReads == 0,
            "Warm text reads use bounded charged cache entries and release in-flight capacity", passed);
        try { await cache.ReadAsync(new(revision, "oversize"), _ => Task.FromResult(new string('x', 33)), ct); throw new InvalidOperationException("Oversize text was cached."); }
        catch (DocumentTextLimitException) { passed.Add("Selected text limits reject complete oversized entries without caching partial text"); }
        await using var tiny = new DocumentTextCache(limits with { CacheBytes = 1 });
        var uncached = await tiny.ReadAsync(key, _ => Task.FromResult("ok"), ct);
        Check(uncached.Text == "ok" && tiny.Usage.RetainedBytes == 0, "Cache admission failure does not truncate or prohibit an otherwise bounded selected document", passed);
        passed.AddRange(await RelationalDocumentExplorerChecks.RunPureAsync(ct));
        passed.AddRange(await DocumentTextCacheDiagnosticChecks.RunAsync(ct));
        return passed.AsReadOnly();
    }
    private static void Check(bool success, string label, List<string> passed)
    {
        if (!success) throw new InvalidOperationException(label);
        passed.Add(label);
    }
}
