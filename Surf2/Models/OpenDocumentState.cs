using System.Text.Json.Serialization;

namespace Surf2.Models;

public enum SavedDocumentTargetState { External, Resolved, Missing, Ambiguous }

public sealed class OpenDocumentState
{
    [JsonIgnore] public long? BoundSnapshotKey { get; set; }
    [JsonIgnore] public long? BoundResourceKey { get; set; }
    [JsonIgnore] public SavedDocumentTargetState TargetState { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; } = 720;

    public double Height { get; set; } = 460;

    public double FontSize { get; set; } = 13;

    public double HorizontalOffset { get; set; }

    public double VerticalOffset { get; set; }

    public Dictionary<int, string> SpreadsheetFilters { get; set; } = [];
}
