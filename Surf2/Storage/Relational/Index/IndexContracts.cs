using System.Collections.Immutable;
using Surf2.Models;

namespace Surf2.Storage.Relational.Index;

public enum IndexedDocumentKind { File, Definition, TableCode, Diagram }
public enum IndexFreshness { Unindexed, Indexed, Stale, Missing, Inaccessible, Failed }
public enum SearchProjectionKind { File, Definition, Diagram }
public enum IndexSearchTarget { Name, Content }
public enum IndexScanStatus { Matched, NotMatched, TooLarge, TimedOut, Missing, Inaccessible, Changed, Unindexed, Delegated }

// Typed owner variants prevent callers from fabricating an unchecked polymorphic key.
public abstract record DocumentOwner;
public sealed record FileDocumentOwner(string Path) : DocumentOwner;
public sealed record SnapshotDocumentOwner(long SnapshotKey, long ResourceKey, bool IsTableCode) : DocumentOwner;
public sealed record DiagramDocumentOwner(long DiagramRevisionKey) : DocumentOwner;
public sealed record DocumentRegistration(DocumentOwner Owner, string DisplayName, string Language);
public sealed record DocumentHandle(long DocumentKey, string Version);
public sealed record DocumentMembership(long ScopeResourceKey, string DisplayName, string Locator,
    string NodeKey, string ParentNodeKey, long SortOrdinal);
public sealed record IndexLocator(long ScopeResourceKey, int Kind, string Locator);
public sealed record FileFingerprint(string Sha256, long ByteCount, DateTimeOffset LastWriteUtc);
public sealed record IndexPolicy(string ParserVersion, string RendererVersion, string PolicyVersion);
public sealed record IndexWorkLease(long WorkItemKey, long DocumentKey, Guid PublicationId, Guid LeaseId,
    string ExpectedDocumentVersion, string SourceFingerprint, IndexPolicy Policy);
public sealed record IndexPublication(IndexWorkLease Lease, string Text, string Language,
    ImmutableArray<SymbolInput> Symbols, long? SourceRevisionKey = null, FileFingerprint? FileFingerprint = null);
public sealed record PreparedIndexSource(string Text, string Fingerprint, string Language,
    ImmutableArray<SymbolInput> Symbols, long? SourceRevisionKey = null);
public sealed record SymbolInput(string Name, string QualifiedName, ReferenceEntityKind Kind, string Locator,
    int LineNumber, int ColumnNumber, int EndLineNumber, int EndColumnNumber, int? ParameterCount,
    int? MinimumArgumentCount, int? MaximumArgumentCount, string Language, string ContainerName)
{
    public static SymbolInput FromReference(ReferenceEntity entity) => new(entity.Name, entity.QualifiedName,
        entity.Kind, entity.FilePath, entity.LineNumber, entity.ColumnNumber, entity.EndLineNumber,
        entity.EndColumnNumber, entity.ParameterCount, entity.MinimumArgumentCount, entity.MaximumArgumentCount,
        entity.Language, entity.ContainerName);
}

// Summaries cannot contain TextContent.Text, search bodies or syntax trees.
public sealed record SymbolSummary(long SymbolKey, long DocumentKey, long RevisionKey,
    SymbolInput Definition, IndexFreshness Freshness);
public sealed record IndexedDocumentSummary(long DocumentKey, long? RevisionKey, IndexedDocumentKind Kind,
    string DisplayName, string Language, string Locator, string NodeKey, string ParentNodeKey,
    long ScopeResourceKey, IndexFreshness Freshness, long? ContentKey, long? SourceRevisionKey,
    string? Fingerprint, string? PhysicalPath, IndexPolicy? Policy);
public sealed record IndexRequestContext(Guid Epoch, long ScopeKey, long CatalogueGeneration, string ScopeVersion,
    string SnapshotCatalogueVersion, string DiagramCatalogueVersion, bool DiscoveryReconciled, ImmutableArray<long> UnloadedScopeResourceKeys,
    ImmutableArray<long> DocumentKeys, bool RestrictDocumentKeys);
public sealed record IndexPage<T>(ImmutableArray<T> Items, long AfterKey, bool Exhausted);
public sealed record SearchCursor(long AfterScopeResourceKey, long AfterDocumentKey);
public sealed record IndexDocumentPage(ImmutableArray<IndexedDocumentSummary> Items, SearchCursor Next, bool Exhausted);
public sealed record ResolvedIndexLocator(long LocatorKey, long DocumentKey, long ScopeResourceKey, int Kind, string Locator);
public sealed record SearchHit(long DocumentKey, long? RevisionKey, long ScopeResourceKey, string DisplayName,
    string Locator, string NodeKey, string ParentNodeKey, IndexFreshness Freshness,
    string? EvaluatedFingerprint = null, bool AuthoritativeFileRead = false);
public sealed record SearchSourceOutcome(long DocumentKey, long ScopeResourceKey, IndexScanStatus Status,
    IndexFreshness Freshness);
public sealed record SearchCoverage(long Considered, long Scanned, long Matched, long Incomplete, long Stale,
    long Delegated, bool DiscoveryReconciled, bool IsSubset, bool Completed, bool GenerationChanged)
{
    public bool FullyCurrentDocumentSet => Completed && !GenerationChanged && DiscoveryReconciled &&
        !IsSubset && Incomplete == 0 && Stale == 0 && Delegated == 0;
    public bool IncludesExplorerContainers => false;
}
public sealed record IndexSearchRequest(IndexRequestContext Context, string Query, bool UseRegex,
    IndexSearchTarget Target, int PageSize = 64, int MaximumDocumentCharacters = 8 * 1024 * 1024,
    TimeSpan? RegexTimeout = null, bool ReadAuthoritativeFiles = true);
public sealed record IndexSearchBatch(ImmutableArray<SearchHit> Hits,
    ImmutableArray<SearchSourceOutcome> Outcomes, SearchCoverage Coverage);
public sealed class IndexGenerationChangedException : InvalidOperationException
{
    public IndexGenerationChangedException() : base("The index request generation is no longer current.") { }
}
