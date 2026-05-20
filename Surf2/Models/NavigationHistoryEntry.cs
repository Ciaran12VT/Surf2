namespace Surf2.Models;

public sealed class NavigationHistoryEntry
{
    public string FilePath { get; set; } = string.Empty;

    public int LineNumber { get; set; } = 1;

    public int ColumnNumber { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
