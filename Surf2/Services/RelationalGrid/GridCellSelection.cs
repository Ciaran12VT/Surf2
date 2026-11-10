namespace Surf2.Services.RelationalGrid;

// Positions belong to one query generation. Selection never retains row objects.
public readonly record struct GridCellRange(long FirstRow, long LastRow, int FirstColumn, int LastColumn, bool AllRows = false)
{
    public bool Contains(long row, int displayColumn) => row >= 0 && displayColumn >= FirstColumn && displayColumn <= LastColumn &&
        (AllRows || row >= FirstRow && row <= LastRow);
}

public sealed class GridCellSelection
{
    public GridCellRange? Range { get; private set; }
    public long AnchorRow { get; private set; }
    public int AnchorColumn { get; private set; }
    public long FocusRow { get; private set; }
    public int FocusColumn { get; private set; }
    public void Clear() => Range = null;
    public void Select(long row, int displayColumn, bool extend = false)
    {
        if (row < 0 || displayColumn < 0) throw new ArgumentOutOfRangeException(nameof(row));
        if (!extend || Range == null) { AnchorRow = row; AnchorColumn = displayColumn; }
        FocusRow = row; FocusColumn = displayColumn;
        Range = new(Math.Min(row, AnchorRow), Math.Max(row, AnchorRow), Math.Min(displayColumn, AnchorColumn), Math.Max(displayColumn, AnchorColumn));
    }
    public void SelectAll(int firstColumn, int lastColumn)
    {
        if (firstColumn < 0 || lastColumn < firstColumn) throw new ArgumentOutOfRangeException(nameof(firstColumn));
        AnchorRow = FocusRow = 0; AnchorColumn = FocusColumn = firstColumn;
        Range = new(0, long.MaxValue, firstColumn, lastColumn, AllRows: true);
    }
}
