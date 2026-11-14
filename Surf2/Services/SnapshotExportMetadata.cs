using System.Text.Json;
using Surf2.Models;

namespace Surf2.Services;

public sealed record SnapshotExportMetadata(string Format, int Version, DatabaseOrigin? Origin, string DatabaseName,
    DateTimeOffset CapturedAtUtc, IReadOnlyList<string> RequestedTables)
{
    public const string FileName = "surf2-origin.json";
    public static SnapshotExportMetadata Create(DatabaseOrigin? origin, string database, DateTimeOffset captured,
        IReadOnlyList<string> tables) => new("Surf2.SnapshotOrigin", 1, origin, database, captured, tables);
    public static SnapshotExportMetadata Parse(byte[] bytes)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new FormatException("Snapshot metadata exceeds its limit.");
        var value = JsonSerializer.Deserialize<SnapshotExportMetadata>(bytes, new JsonSerializerOptions { MaxDepth = 10 }) ?? throw new FormatException("Invalid snapshot metadata.");
        if (value.Format != "Surf2.SnapshotOrigin" || value.Version != 1 || value.RequestedTables == null || value.RequestedTables.Count > 100000 ||
            value.RequestedTables.Any(x => x.Length > 8192 || FullTableDataSelection.TryParse(x) == null)) throw new FormatException("Unsupported or invalid snapshot metadata.");
        value.Origin?.Validate(); return value;
    }
}
