using System.Text.Json;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Services.RelationalGrid;

internal static class CapturedGridValues
{
    internal static GridColumn[] PrepareColumns(IReadOnlyList<GridColumn> columns) =>
        columns.Count == 0 ? [new(0, "Column 1", string.Empty, IsSynthetic: true)] : columns.ToArray();

    internal static string[]? Cells(JsonElement row, IReadOnlyList<GridColumn> columns) =>
        row.ValueKind == JsonValueKind.Object
            ? columns.Select(c => c.IsSynthetic ? string.Empty : CaptureDisplay.Cell(row, c.SourceName)).ToArray()
            : null;

    internal static bool ShouldDisplay(string[]? cells) =>
        cells != null && cells.Any(cell => !string.IsNullOrWhiteSpace(cell));
}
