using System.Collections.Immutable;
using Surf2.Models;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalExplorer;

public enum ExplorerAvailability { Present, Missing, Inaccessible, Failed }
public enum ExplorerChildrenState { Unloaded, Loading, Loaded, Failed }
public enum ExplorerCategory { Procedures, Views, Functions, Triggers, Tables }
public enum ExplorerNodeRole { Resource, Category, PhysicalDirectory, PhysicalFile, DatabaseDocument, Diagram, DiagramRoot, VirtualFolder }

public sealed record ExplorerResource(long ScopeResourceKey, long SortOrdinal, string ResourceId,
    ResourceKind Kind, string Path, string Alias, bool IncludeChildren, bool IsLoaded,
    SnapshotSummary? Snapshot, long? DiagramKey, long? DiagramRevisionKey, string? DiagramName,
    bool HasUnresolvedQueries);
public sealed record ExplorerVirtualFolder(long Key, long SortOrdinal, string Id, string Name,
    string ParentNodeKey, ImmutableArray<string> ChildNodeKeys);
public sealed record ExplorerScope(IndexRequestContext Context, string ScopeId, string Name,
    ImmutableArray<ExplorerResource> Resources, ImmutableArray<ExplorerVirtualFolder> VirtualFolders,
    string SortCultureName);

// All fields needed by FileSystemNode, without constructing WPF geometry, collections, or brushes.
// OccurrenceKey identifies an alias occurrence; NodeKey intentionally retains legacy folder-move matching.
public sealed record ExplorerNodeSummary(string OccurrenceKey, string Name, string MatchName, string FullPath,
    string NodeKey, string ParentNodeKey, string NaturalParentKey, ExplorerNodeRole Role,
    ResourceKind ResourceKind, FileSystemNodeIconKind IconKind, ExplorerAvailability Availability,
    bool IsDirectory, bool IsVirtualDocument, bool IsScopeResourceRoot, bool IsScopeResourceLoaded,
    ExplorerChildrenState ChildrenState, long? ScopeResourceKey = null, string ScopeResourceId = "",
    long? SnapshotKey = null, long? SnapshotResourceKey = null, long? SourceRevisionKey = null,
    long? DiagramRevisionKey = null, ExplorerCategory? Category = null, string VirtualFolderId = "",
    bool HasUnresolvedQueries = false, string SchemaName = "", string ObjectName = "",
    long SourceOrdinal = 0, bool HasFullData = false, long ReportedRowCount = 0, bool IsReparsePoint = false,
    string? EffectiveParentOccurrenceKey = null, string? ToolTip = null)
{
    public bool Exists => Availability == ExplorerAvailability.Present;
    public bool IsVirtualFolder => Role == ExplorerNodeRole.VirtualFolder;
    public FileSystemNodeIconKind EffectiveIconKind => Exists ? IconKind : FileSystemNodeIconKind.Missing;
}

public sealed record ExplorerChildrenBatch(ImmutableArray<ExplorerNodeSummary> Nodes, ExplorerChildrenState State,
    bool Completed, string? FailureCode = null);
public sealed record ExplorerAddress(ExplorerNodeSummary Node, string CanonicalLocator, string ReadableLocator,
    ImmutableArray<string> Hierarchy, long? RevisionKey, bool IsTableData = false)
{
    public string? TextFileNameSeed => Hierarchy.IsEmpty ? null : string.Join("_", Hierarchy);
}
public sealed record ExplorerDatabaseItem(SnapshotResourceSummary Resource, SqlDatabaseObjectKind? ObjectKind,
    bool HasFullData = false, long ReportedRowCount = 0);
public sealed record PhysicalExplorerEntry(string Path, bool IsDirectory, ExplorerAvailability Availability,
    bool IsReparsePoint = false);

public sealed record ExplorerLimits(int PageSize = 64, int MaximumMetadataRows = 100_000,
    long MaximumMetadataCharacters = 4 * 1024 * 1024, int MaximumDepth = 128,
    int MaximumDocumentCharacters = 8 * 1024 * 1024)
{
    public void Validate()
    {
        if (PageSize is < 1 or > 128 || MaximumMetadataRows <= 0 || MaximumMetadataCharacters <= 0 ||
            MaximumDepth is < 1 or > 256 || MaximumDocumentCharacters is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(ExplorerLimits));
    }
}
public sealed class ExplorerLimitException(string message) : InvalidOperationException(message);

public interface IExplorerMetadataQueries
{
    Task<ExplorerScope> ReadScopeAsync(long scopeKey, IReadOnlySet<string>? unloadedResourceIds,
        string? sortCultureName, CancellationToken ct);
    Task<bool> IsCurrentAsync(IndexRequestContext context, CancellationToken ct);
    IAsyncEnumerable<ExplorerDatabaseItem> ReadDatabaseCategoryAsync(ExplorerScope scope,
        ExplorerResource resource, ExplorerCategory category, CancellationToken ct);
}

public interface IPhysicalExplorerQueries
{
    Task<ExplorerAvailability> ProbeAsync(string path, bool isDirectory, CancellationToken ct);
    Task<ImmutableArray<PhysicalExplorerEntry>> ReadDirectoryAsync(string path, string sortCultureName,
        ExplorerLimits limits, CancellationToken ct);
}

public sealed record ExplorerSearchRequest(ExplorerScope Scope, string Query, bool UseRegex,
    IndexSearchTarget Target, Guid RequestIdentity, TimeSpan? RegexTimeout = null);
public sealed record ExplorerSearchHit(ExplorerNodeSummary Node, ImmutableArray<ExplorerNodeSummary> Ancestors,
    bool ContentMatched, ImmutableArray<int> MatchingColumnIndexes, string Query, bool UseRegex,
    IndexFreshness IndexFreshness, bool CurrentSourceEvaluated)
{
    public bool AncestorsAreNatural => true;
}
public sealed record ExplorerSearchOutcome(string OccurrenceKey, IndexScanStatus Status,
    IndexFreshness IndexFreshness, bool CurrentSourceEvaluated);
public sealed record ExplorerSearchCoverage(long Considered, long Matched, long Incomplete,
    long IndexStaleOrUnindexed, bool DiscoveryReconciled, bool Completed, bool GenerationChanged,
    bool IsSubset = false, bool PhysicalDiscoveryNotFrozen = false)
{
    public bool FullyCurrent => Completed && !GenerationChanged && !IsSubset && Incomplete == 0 && DiscoveryReconciled && !PhysicalDiscoveryNotFrozen;
    public bool IndexFullyPublished => FullyCurrent && IndexStaleOrUnindexed == 0;
}
public sealed record ExplorerSearchBatch(Guid RequestIdentity, ImmutableArray<ExplorerSearchHit> Hits,
    ImmutableArray<ExplorerSearchOutcome> Outcomes, ExplorerSearchCoverage Coverage);
public sealed record ExplorerIndexBinding(string OccurrenceKey, long DocumentKey, long ScopeResourceKey,
    long? SourceRevisionKey, IndexFreshness Freshness);
public sealed record ExplorerTableSearch(bool CodeMatched, ImmutableArray<int> MatchingColumns,
    IndexScanStatus Status, bool CurrentSourceEvaluated);

public interface IExplorerSearchSources
{
    Task<ImmutableArray<ExplorerIndexBinding>> BindIndexDocumentsAsync(ExplorerScope scope,
        ImmutableArray<ExplorerNodeSummary> nodes, CancellationToken ct);
    IAsyncEnumerable<IndexSearchBatch> SearchIndexAsync(IndexSearchRequest request, CancellationToken ct);
    Task<string> ReadCurrentContentAsync(ExplorerNodeSummary node, int maximumCharacters, CancellationToken ct);
    Task<ExplorerTableSearch> SearchTableAsync(ExplorerNodeSummary node, IndexTextMatcher matcher,
        int maximumCharacters, CancellationToken ct);
}

internal sealed class ExplorerMetadataBudget(ExplorerLimits limits)
{
    private long _characters;
    private int _rows;
    internal void Add(params string[] fields)
    {
        if (++_rows > limits.MaximumMetadataRows) throw new ExplorerLimitException("Selected metadata exceeds its row budget.");
        foreach (string field in fields) _characters = checked(_characters + field.Length);
        if (_characters > limits.MaximumMetadataCharacters) throw new ExplorerLimitException("Selected metadata exceeds its character budget.");
    }
}
