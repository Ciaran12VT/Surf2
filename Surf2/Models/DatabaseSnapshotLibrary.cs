using System.Collections.ObjectModel;

namespace Surf2.Models;

public sealed class DatabaseSnapshotLibrary
{
    public int SchemaVersion { get; set; } = 2;

    public ObservableCollection<DatabaseMetadataSnapshot> Snapshots { get; set; } = [];

    public ObservableCollection<DatabaseSnapshotHistory> Histories { get; set; } = [];
}
