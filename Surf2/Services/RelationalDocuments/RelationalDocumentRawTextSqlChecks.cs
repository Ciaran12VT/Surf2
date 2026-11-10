using Surf2.Models;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalDocuments;

/// <summary>Read-only SQL hook for an isolated parent-owned current/previous definition fixture.</summary>
public static class RelationalDocumentRawTextSqlChecks
{
    public const string CurrentDefinition = "-- current raw UTF-16\r\nSELECT N'\ud800 high \udc00 low \0\uffff \ud83d\udca7';  ";
    public const string HistoricalDefinition = "-- historical raw UTF-16\r\nSELECT N'\udc00 low \ud800 high \0\uffff \ud83d\udca7'; \r\n ";

    public static async Task<IReadOnlyList<string>> RunAsync(RelationalRuntime runtime, long resourceKey,
        long historicalVersionKey, string expectedCurrent = CurrentDefinition,
        string expectedHistorical = HistoricalDefinition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        RequireFixture(expectedCurrent);
        RequireFixture(expectedHistorical);
        if (expectedCurrent == expectedHistorical) throw new ArgumentException("Current and historical fixture definitions must differ.");
        var passed = new List<string>();
        var current = await runtime.Snapshots.ResolveResourceAsync(resourceKey, ct: ct).ConfigureAwait(false)
            ?? throw new ArgumentException("The fixture resource must be published and currently present.");
        if (current.Kind is null or DatabaseVersionedResourceKind.TableMetadata or DatabaseVersionedResourceKind.TableData)
            throw new ArgumentException("The fixture requires a typed code definition resource.");
        await using var context = await runtime.Snapshots.OpenHistoricalSnapshotAsync(current.SnapshotKey, historicalVersionKey, ct).ConfigureAwait(false);
        var previous = await context.FindResource(resourceKey, ct).ConfigureAwait(false)
            ?? throw new ArgumentException("The fixture resource must also exist in the selected historical version.");
        Check(previous.Collection == HistoricalCollection.Objects && previous.RevisionKey.HasValue && previous.RevisionKey != current.RevisionKey,
            "Current and historical SQL fixtures select independent immutable definition revisions", passed);
        long previousRevision = previous.RevisionKey ?? throw new InvalidOperationException("The historical object has no revision.");
        var currentValue = await runtime.Snapshots.ReadObjectAsync(current.RevisionKey, ct).ConfigureAwait(false);
        Check(currentValue.Definition == expectedCurrent,
            "SQL current definition preserves raw unpaired UTF-16, NUL, noncharacters, valid pairs and trailing whitespace", passed);
        var previousValue = await runtime.Snapshots.ReadObjectAsync(previousRevision, ct).ConfigureAwait(false);
        Check(previousValue.Definition == expectedHistorical,
            "SQL previous typed revision preserves every original code unit without replacement", passed);

        await using var documents = new RelationalDocumentService(runtime);
        var address = await documents.ResolveResourceAsync(current.SnapshotKey, resourceKey, null, ct).ConfigureAwait(false);
        var plan = await documents.DescribeTextAsync(address, ct).ConfigureAwait(false);
        var metricsBefore = runtime.QueryMetrics.Snapshot();
        var selected = await documents.ReadTextAsync(plan, ct).ConfigureAwait(false);
        Check(selected.Text == expectedCurrent, "Current selected-document preview preserves exact raw SQL definition", passed);
        var warm = await documents.ReadTextAsync(plan, ct).ConfigureAwait(false);
        Check(ReferenceEquals(selected, warm) && warm.Text == expectedCurrent,
            "Bounded warm document cache retains the same raw code units", passed);
        var metricsAfter = runtime.QueryMetrics.Snapshot();
        Check(metricsAfter.Completed >= metricsBefore.Completed + 2 && metricsAfter.RowsRead >= metricsBefore.RowsRead + 1 &&
            metricsAfter.BytesRead >= metricsBefore.BytesRead + expectedCurrent.Length * 2L && metricsAfter.CacheHits >= metricsBefore.CacheHits + 1 &&
            metricsAfter.Queries == metricsBefore.Queries,
            "Actual runtime selected-definition producer and warm hit publish redacted output metrics without estimating SQL queries", passed);
        var history = new RelationalSnapshotHistoryService(runtime);
        var preview = await history.ReadPreviewAsync(context, previous, ct).ConfigureAwait(false);
        Check(preview.Text == expectedHistorical && preview.CapturedData == null,
            "Actual selected-version history preview preserves raw UTF-16 instead of substituting current text", passed);

        await ExpectLimitAsync(current.RevisionKey, expectedCurrent.Length * 2L, "Current SQL definition limit rejects before consuming its binary LOB").ConfigureAwait(false);
        await ExpectLimitAsync(previousRevision, expectedHistorical.Length * 2L, "Historical SQL definition limit rejects before consuming its binary LOB").ConfigureAwait(false);
        return passed.AsReadOnly();

        async Task ExpectLimitAsync(long revisionKey, long bytes, string label)
        {
            var limited = new RelationalSnapshotStore(runtime.Session, new RelationalContentStore(),
                new SnapshotReadLimits { SelectedDefinitionBytes = bytes - 1 });
            try { _ = await limited.ReadObjectAsync(revisionKey, ct).ConfigureAwait(false); }
            catch (SnapshotReadLimitException error) when (error.LimitBytes == bytes - 1)
            { passed.Add(label); return; }
            throw new InvalidOperationException(label);
        }
    }

    private static void RequireFixture(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool high = false, low = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                else high = true;
            }
            else if (char.IsLowSurrogate(text[i])) low = true;
        }
        if (!high || !low || text.Length * 2L > SnapshotReadLimits.DefaultDefinitionBytes)
            throw new ArgumentException("Each SQL fixture definition must contain unpaired high and low code units within the selected-read budget.");
    }

    private static void Check(bool valid, string label, List<string> passed)
    { if (!valid) throw new InvalidOperationException(label); passed.Add(label); }
}
