using Surf2.Models;
using Surf2.Services.RelationalExplorer;

namespace Surf2.Services.RelationalComparison;

// Immutable selected locators, not a reconstructed snapshot/library. Historical
// targets pin a selected version and its projected entry (including data companions).
public sealed record RelationalComparisonTarget(ComparisonResource Resource, Guid Epoch,
    long? SnapshotKey = null, long? ResourceKey = null, long? RevisionKey = null,
    long? VersionKey = null, long? HistoricalEntryKey = null, ExplorerCategory? Category = null,
    ExplorerNodeSummary? ExplorerNode = null, string? ExpectedTextDigest = null);

public sealed record ComparisonOptions(bool IgnoreWhitespace = false, bool IgnoreCase = false);
public sealed record ComparisonLimits
{
    public long MaximumDiskBytes { get; init; } = 1024L * 1024 * 1024;
    public long SortBufferBytes { get; init; } = 8 * 1024 * 1024;
    public long MaximumPageBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumRecordBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumRuns { get; init; } = 4096;
    public int MergeFanIn { get; init; } = 8;
    public int PageSize { get; init; } = 128;
    public int MaximumCandidates { get; init; } = 5000;
    public long MaximumCandidateBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumTextCharacters { get; init; } = 4 * 1024 * 1024;
    public string? StagingDirectory { get; init; }
    internal void Validate()
    {
        if (MaximumDiskBytes is < 1024 or > 16L * 1024 * 1024 * 1024 ||
            SortBufferBytes is < 1024 or > 64 * 1024 * 1024 ||
            MaximumPageBytes is < 1024 or > 32 * 1024 * 1024 ||
            MaximumRecordBytes is < 1024 or > 16 * 1024 * 1024 || MaximumRuns is < 2 or > 4096 ||
            MergeFanIn is < 2 or > 8 || PageSize is < 1 or > 256 ||
            MaximumCandidates is < 1 or > 5000 || MaximumCandidateBytes is < 1024 or > 16 * 1024 * 1024 ||
            MaximumTextCharacters is < 1 or > 8 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(ComparisonLimits));
    }
}
public sealed class ComparisonLimitException(string message) : InvalidOperationException(message);

public sealed record ComparisonResultRow(string Key, string Status, string ChangedColumns,
    string LeftPreview, string RightPreview, bool IsCollection = false,
    RelationalComparisonTarget? Left = null, RelationalComparisonTarget? Right = null);
public sealed record ComparisonResultPage(IReadOnlyList<ComparisonResultRow> Rows, long Offset,
    long MatchingCount, bool HasNext);
public sealed record ComparisonCandidates(IReadOnlyList<RelationalComparisonTarget> Items,
    bool Complete, string? LimitReason = null);
public sealed record ComparisonDocumentChoice(ResourceComparisonDocument Document, RelationalComparisonTarget Target);
public sealed record ComparisonDocumentChoices(IReadOnlyList<ComparisonDocumentChoice> Left,
    IReadOnlyList<ComparisonDocumentChoice> Right, bool Complete);
