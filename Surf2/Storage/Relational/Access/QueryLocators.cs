using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Access;

/// <summary>Resolved identity, not a display alias, path parser or permission to write.</summary>
public abstract record SessionLocator
{
    protected SessionLocator(Guid epoch)
    {
        if (epoch == Guid.Empty) throw new ArgumentException("A connection epoch is required.", nameof(epoch));
        Epoch = epoch;
    }

    public Guid Epoch { get; }

    public void RequireEpoch(Guid epoch)
    {
        if (epoch != Epoch) throw new InvalidOperationException("The locator belongs to another connection epoch.");
    }

    public void RequireSession(RelationalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireEpoch(session.Epoch);
    }

    protected static long Key(long key) => key > 0 ? key : throw new ArgumentOutOfRangeException(nameof(key));
}

/// <summary>VersionKey remains necessary for effective historical table flags and root children.</summary>
public sealed record SnapshotResourceLocator : SessionLocator
{
    public SnapshotResourceLocator(Guid epoch, long snapshotKey, long resourceKey, long revisionKey,
        long? versionKey = null, TableRevisionSource tableSource = TableRevisionSource.Metadata) : base(epoch)
    {
        SnapshotKey = Key(snapshotKey);
        ResourceKey = Key(resourceKey);
        RevisionKey = Key(revisionKey);
        VersionKey = versionKey.HasValue ? Key(versionKey.Value) : null;
        if (!Enum.IsDefined(tableSource)) throw new ArgumentOutOfRangeException(nameof(tableSource));
        TableSource = tableSource;
    }

    public long SnapshotKey { get; }
    public long ResourceKey { get; }
    public long RevisionKey { get; }
    public long? VersionKey { get; }
    public TableRevisionSource TableSource { get; }

    public static SnapshotResourceLocator From(Guid epoch, SnapshotResourceSummary summary, long? versionKey = null) =>
        new(epoch, summary.SnapshotKey, summary.ResourceKey, summary.RevisionKey, versionKey, summary.TableSource);
}

public sealed record CapturedDataSetLocator : SessionLocator
{
    public CapturedDataSetLocator(Guid epoch, long dataSetKey, long revisionKey, int displayFormatVersion) : base(epoch)
    {
        DataSetKey = Key(dataSetKey);
        RevisionKey = Key(revisionKey);
        if (displayFormatVersion <= 0) throw new ArgumentOutOfRangeException(nameof(displayFormatVersion));
        DisplayFormatVersion = displayFormatVersion;
    }

    public long DataSetKey { get; }
    public long RevisionKey { get; }
    public int DisplayFormatVersion { get; }

    public static CapturedDataSetLocator From(CaptureDataSetDescriptor descriptor) =>
        new(descriptor.Epoch, descriptor.Summary.DataSetKey, descriptor.Summary.RevisionKey, descriptor.Summary.DisplayFormatVersion);
}

public sealed record IndexedDocumentLocator : SessionLocator
{
    public IndexedDocumentLocator(Guid epoch, long documentKey, long revisionKey) : base(epoch)
    {
        DocumentKey = Key(documentKey);
        RevisionKey = Key(revisionKey);
    }

    public long DocumentKey { get; }
    public long RevisionKey { get; }

    public static IndexedDocumentLocator From(Guid epoch, IndexedDocumentSummary summary) =>
        new(epoch, summary.DocumentKey, summary.RevisionKey ?? throw new InvalidOperationException("The document has no published index revision."));
}

public sealed record DiagramRevisionLocator : SessionLocator
{
    public DiagramRevisionLocator(Guid epoch, long diagramKey, long revisionKey) : base(epoch)
    {
        DiagramKey = Key(diagramKey);
        RevisionKey = Key(revisionKey);
    }

    public long DiagramKey { get; }
    public long RevisionKey { get; }

    public static DiagramRevisionLocator From(DiagramSummary summary) =>
        new(summary.Token.Epoch, summary.Token.Key, summary.RevisionKey);
}
