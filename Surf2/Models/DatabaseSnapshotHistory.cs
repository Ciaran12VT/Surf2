using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Surf2.Models;

public sealed class DatabaseSnapshotHistory
{
    public string SnapshotId { get; set; } = string.Empty;

    public int NextVersionNumber { get; set; } = 1;

    public ObservableCollection<DatabaseSnapshotVersion> Versions { get; set; } = [];
}

public sealed class DatabaseSnapshotVersion
{
    public string VersionId { get; set; } = Guid.NewGuid().ToString("N");

    public string VersionName { get; set; } = string.Empty;

    public int VersionNumber { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IsInitial { get; set; }

    public List<DatabaseSnapshotResourceChange> Changes { get; set; } = [];
}

public sealed class DatabaseSnapshotResourceChange
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DatabaseVersionedResourceKind Kind { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DatabaseSnapshotResourceChangeKind ChangeKind { get; set; }

    public string ResourceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public DatabaseSnapshotResourcePayload? PreviousPayload { get; set; }
}

public enum DatabaseVersionedResourceKind
{
    StoredProcedure,
    View,
    Function,
    Trigger,
    TableMetadata,
    TableData
}

public enum DatabaseSnapshotResourceChangeKind
{
    Added,
    Modified,
    Deleted
}

public sealed class DatabaseSnapshotResourcePayload
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DatabaseVersionedResourceKind Kind { get; set; }

    public SqlDatabaseObject? DatabaseObject { get; set; }

    public SqlTable? Table { get; set; }

    public List<SqlColumn> Columns { get; set; } = [];

    public List<SqlPrimaryKeyColumn> PrimaryKeys { get; set; } = [];

    public SqlTableDataSet? TableDataSet { get; set; }
}
