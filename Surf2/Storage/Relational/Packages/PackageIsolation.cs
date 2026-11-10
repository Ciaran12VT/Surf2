using System.Data;

namespace Surf2.Storage.Relational.Packages;

internal static class PackageIsolation
{
    internal static IsolationLevel Select(int? snapshotState, bool allowBlocking)
    {
        if (!snapshotState.HasValue || snapshotState is < 0 or > 3)
            throw new InvalidOperationException("The source snapshot-isolation option could not be verified.");
        if (snapshotState == 1) return IsolationLevel.Snapshot;
        if (allowBlocking) return IsolationLevel.Serializable;
        throw new InvalidOperationException("Streaming export requires ALLOW_SNAPSHOT_ISOLATION already enabled on the source. The exporter never changes database options. A blocking SERIALIZABLE fallback requires explicit AllowBlockingConsistentFallback consent.");
    }
}
