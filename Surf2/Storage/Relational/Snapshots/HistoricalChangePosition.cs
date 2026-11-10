namespace Surf2.Storage.Relational.Snapshots;

// Equal version numbers retain complete versions in source order, not change
// ordinals interleaved across versions. Keys only break equal source ordinals.
internal readonly record struct HistoricalChangePosition(int VersionNumber, long VersionSortOrdinal,
    long VersionKey, long ChangeSortOrdinal, long ChangeKey) : IComparable<HistoricalChangePosition>
{
    public int CompareTo(HistoricalChangePosition other)
    {
        int result = other.VersionNumber.CompareTo(VersionNumber);
        if (result != 0) return result;
        result = VersionSortOrdinal.CompareTo(other.VersionSortOrdinal);
        if (result != 0) return result;
        result = VersionKey.CompareTo(other.VersionKey);
        if (result != 0) return result;
        result = ChangeSortOrdinal.CompareTo(other.ChangeSortOrdinal);
        return result != 0 ? result : ChangeKey.CompareTo(other.ChangeKey);
    }
}
