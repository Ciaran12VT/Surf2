using System.Collections.ObjectModel;

namespace Surf2.Models;

public sealed class DatabaseSnapshotLibrary
{
    public int SchemaVersion { get; set; } = 1;

    public ObservableCollection<DatabaseMetadataSnapshot> Snapshots { get; set; } = [];
}
