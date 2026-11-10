using System.IO;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

/// <summary>A token belongs to one connection epoch and one mutable aggregate, not a historical revision.</summary>
public sealed class StateToken
{
    private readonly byte[] _version;

    public StateToken(long key, Guid epoch, byte[] version, Guid publicationId, IReadOnlyList<StateImportWarning>? importWarnings = null)
    {
        if (key <= 0 || version.Length != 8) throw new ArgumentException("Invalid state token.");
        Key = key;
        Epoch = epoch;
        _version = version.ToArray();
        PublicationId = publicationId;
        ImportWarnings = Array.AsReadOnly((importWarnings ?? []).ToArray());
    }

    public long Key { get; }
    public Guid Epoch { get; }
    public byte[] Version => _version.ToArray();
    public Guid PublicationId { get; }
    public IReadOnlyList<StateImportWarning> ImportWarnings { get; }
    internal StateToken WithWarnings(IReadOnlyList<StateImportWarning> warnings) => new(Key, Epoch, _version, PublicationId, warnings);
}

public sealed record StateImportWarning(string AggregateKind, int ObjectOrdinal, string Code);

public sealed record SelectedState<T>(T Value, StateToken Token);
public sealed record StateCursor(Guid Epoch, long SortOrdinal, long Key, byte[] Generation);
public sealed record StatePage<T>(IReadOnlyList<T> Items, StateCursor? Next);
public sealed record ScopeSummary(StateToken Token, long SortOrdinal, string ScopeId, string Name);
public sealed record DiagramSummary(StateToken Token, long SortOrdinal, string DiagramId, string Name,
    long RevisionKey, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record WorkbenchSummary(StateToken Token, long SortOrdinal, string WorkbenchId, string Name,
    string ScopeId, string ScopeName, bool IsDefaultForScope, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, DateTimeOffset SavedAtUtc, long? EmbeddedDiagramRevisionKey);
public sealed record ScopeSelection(int SchemaVersion, string? LastActiveScopeId);
public sealed record PreferenceSummary(StateToken Token, string Theme, bool LoadMostRecentWorkbenchOnStartup,
    bool IgnoreWhitespaceByDefault, bool IgnoreCaseByDefault, bool EnableInternalLogging,
    string DefaultBackcolor, KeyboardShortcutSettings KeyboardShortcuts);

/// <summary>Pasted bytes are indexed by collection ordinal, since legacy object IDs can repeat.</summary>
public enum PastedImageResolution { None, CapturedLocal, MissingUseInline, MissingUseDefinition }
public sealed record PastedImageFallback(PastedImageResolution Resolution, byte[] Bytes);
public sealed record DiagramState(DiagramDocument Document, IReadOnlyDictionary<int, byte[]> PastedImages,
    IReadOnlyDictionary<int, PastedImageFallback>? PastedImageFallbacks = null);
public sealed record WorkbenchAggregate(WorkbenchState Workbench, IReadOnlyDictionary<int, byte[]> PastedImages,
    IReadOnlyDictionary<int, PastedImageFallback>? PastedImageFallbacks = null);
public sealed record ScopeResourceTarget(int ResourceOrdinal, long? SnapshotKey, long? DiagramKey);

public enum StateLinkResolution { None, Resolved, Missing, Ambiguous, ContextMismatch }
public sealed record DiagramWorkflowBinding(long DiagramObjectKey, long? WorkflowKey, long? WorkflowItemKey,
    StateLinkResolution WorkflowResolution, StateLinkResolution ItemResolution);
public sealed record WorkflowItemMarker(long WorkflowKey, long WorkflowItemKey, long? MarkerDiagramObjectKey,
    StateLinkResolution Resolution);
public sealed record DiagramPortalTarget(long DiagramObjectKey, long? TargetDiagramKey, long? TargetRevisionKey,
    long? TargetObjectKey, StateLinkResolution Resolution);
public sealed record DiagramRelationships(long DiagramRevisionKey, IReadOnlyList<DiagramWorkflowBinding> WorkflowBindings,
    IReadOnlyList<WorkflowItemMarker> ItemMarkers, IReadOnlyList<DiagramPortalTarget> PortalTargets);
public sealed record VirtualFolderParent(long VirtualFolderKey, long? ParentVirtualFolderKey, long? ParentScopeResourceKey,
    StateLinkResolution Resolution);
public sealed record VirtualFolderChild(long VirtualFolderKey, long SortOrdinal, long? ChildVirtualFolderKey,
    long? ChildScopeResourceKey, StateLinkResolution Resolution);
public sealed record ScopeFolderRelationships(long ScopeKey, IReadOnlyList<VirtualFolderParent> Parents,
    IReadOnlyList<VirtualFolderChild> Members);
public sealed record WorkbenchScopeTarget(StateToken Token, long? ScopeKey, StateLinkResolution Resolution);

public sealed class StateConflictException(string aggregate)
    : InvalidOperationException($"The {aggregate} changed or is missing. Reload before publishing.");

/// <summary>Limits reject an entire operation; they never truncate or silently drop a child.</summary>
public sealed record StateLimits
{
    public int MaximumRows { get; init; } = 100_000;
    public long MaximumAggregateBytes { get; init; } = 64L * 1024 * 1024;
    public int MaximumTextBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumAssetBytes { get; init; } = 16 * 1024 * 1024;
    public long MaximumImagePixels { get; init; } = 16_000_000;
    public int MaximumPageSize { get; init; } = 500;

    internal void Validate()
    {
        if (MaximumRows <= 0 || MaximumAggregateBytes <= 0 || MaximumTextBytes <= 0 ||
            MaximumAssetBytes <= 0 || MaximumImagePixels <= 0 || MaximumPageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(StateLimits));
    }
}

internal sealed class StateBudget(StateLimits limits)
{
    private int _rows;
    private long _bytes;
    public void Row()
    {
        if (++_rows > limits.MaximumRows) throw new InvalidDataException("The selected state exceeds its row budget.");
    }
    public void Text(long bytes)
    {
        if (bytes > limits.MaximumTextBytes) throw new InvalidDataException("A state text value exceeds its byte budget.");
        Bytes(bytes);
    }
    public void Asset(long bytes)
    {
        if (bytes > limits.MaximumAssetBytes) throw new InvalidDataException("A state image exceeds its byte budget.");
        Bytes(bytes);
    }
    private void Bytes(long bytes)
    {
        if (bytes < 0 || bytes > limits.MaximumAggregateBytes - _bytes)
            throw new InvalidDataException("The selected state exceeds its byte budget.");
        _bytes += bytes;
    }
}
