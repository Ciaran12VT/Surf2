using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Surf2.Services.RelationalGrid;

namespace Surf2.Controls.RelationalGrid;

public sealed record GridBindingLimits
{
    public int PageRows { get; init; } = 128;
    public long PageBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxItems { get; init; } = 512;
    public long MaxBytes { get; init; } = 32 * 1024 * 1024;

    internal void Validate()
    {
        if (PageRows is < 1 or > 1024 || PageBytes is < 1024 or > 128 * 1024 * 1024 ||
            MaxItems < Math.Max(16, PageRows) || MaxItems > 4096 || MaxBytes < PageBytes + MaxItems * 512L || MaxBytes > 512 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(GridBindingLimits));
    }
}

// This IList is positional, not a resident row list. Getters do no I/O and
// schedule no work. Only the viewport coordinator requests provider pages.
public sealed class GridViewportItems : IList, INotifyCollectionChanged
{
    private readonly Dictionary<int, GridViewportRow> _items = [];
    private readonly LinkedList<int> _lru = [];
    private readonly Dictionary<int, LinkedListNode<int>> _nodes = [];
    private readonly Dictionary<int, int> _pinned = [];
    private readonly HashSet<int> _editing = [];
    private readonly GridBindingLimits _limits;
    private readonly IDataGridSource _source;
    private readonly GridViewportRow _overflow;
    private long _bytes;
    private long _generation;
    private int _count;
    private bool _closed;
    private IGridQuerySession? _query;

    public GridViewportItems(IDataGridSource source, GridBindingLimits? limits = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _limits = limits ?? new();
        _limits.Validate();
        _overflow = new(this, -1, -1);
        _overflow.Fail("Viewport item budget exceeded. Reduce the visible range.");
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event EventHandler? CellCommitted;
    public GridBindingLimits Limits => _limits;
    public int ResidentItems => _items.Count;
    public long ResidentBytes => _bytes;
    public long Generation => _generation;
    public Exception? Error { get; private set; }
    public long? UnaddressableCount { get; private set; }
    public int Count => _count;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            if (_items.TryGetValue(index, out var existing)) { Touch(index); return existing; }
            if (!MakeRoom(512, 1))
            {
                Error = new GridLimitException(_overflow.Error);
                return _overflow;
            }
            var row = new GridViewportRow(this, index, _generation);
            _items.Add(index, row);
            _nodes.Add(index, _lru.AddLast(index));
            _bytes += row.EstimatedBytes;
            return row;
        }
        set => throw new NotSupportedException();
    }

    public void Bind(IGridQuerySession? query)
    {
        _generation = checked(_generation + 1);
        _query = query;
        _items.Clear(); _nodes.Clear(); _lru.Clear(); _pinned.Clear(); _editing.Clear();
        _bytes = 0; _count = 0; Error = null; UnaddressableCount = null;
        CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
    }

    public bool UpdateCount(GridCount count)
    {
        if (_closed || _query == null) return false;
        if (count.AvailableRows < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count.AvailableRows > int.MaxValue)
        {
            UnaddressableCount = count.AvailableRows;
            Error = new GridLimitException("This view exceeds WPF's Int32 row-addressing limit. Narrow filters or export all matching rows.");
            if (_count == 0) return false;
            _count = 0;
        }
        else
        {
            if (UnaddressableCount != null) return false;
            int next = checked((int)count.AvailableRows);
            if (next == _count) return false;
            _count = next;
        }
        CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
        return true;
    }

    public void Pin(GridViewportRow row, bool pinned)
    {
        if (!Owns(row)) return;
        _pinned.TryGetValue(row.Position, out int references);
        if (pinned) _pinned[row.Position] = checked(references + 1);
        else if (references > 1) _pinned[row.Position] = references - 1;
        else _pinned.Remove(row.Position);
    }

    internal void PinEditor(GridViewportRow row, bool pinned)
    {
        if (!Owns(row)) return;
        if (pinned) _editing.Add(row.Position); else _editing.Remove(row.Position);
    }

    public bool Publish(GridPage page, long bindingGeneration)
    {
        if (_closed || _query == null || bindingGeneration != _generation ||
            page.SourceId != _source.Descriptor.SourceId || page.QueryGeneration != _query.Generation ||
            page.OverlayGeneration != _query.OverlayGeneration) return false;
        foreach (var (data, offset) in page.Rows.Select((row, index) => (row, index)))
        {
            long position = page.Request.Start + offset;
            if (position > int.MaxValue || !_items.TryGetValue((int)position, out var row)) continue;
            if (row.IsLoaded) continue;
            long delta = data.EstimatedBytes + 512 - row.EstimatedBytes;
            if (delta > 0 && !MakeRoom(delta, 0, row.Position))
            {
                Error = new GridLimitException("Visible rows exceed the UI byte budget. Reduce the visible range or increase its explicit budget.");
                row.Fail(Error.Message);
                continue;
            }
            _bytes += delta;
            row.Load(data);
        }
        return true;
    }

    public void Fail(Exception error, long bindingGeneration)
    {
        if (_closed || bindingGeneration != _generation) return;
        Error = error;
        foreach (var row in _items.Values) if (!row.IsLoaded) row.Fail(error.Message);
    }

    public void ClearError()
    {
        if (UnaddressableCount != null) return;
        Error = null;
        foreach (var row in _items.Values) if (!row.IsLoaded) row.ClearError();
    }

    internal void Commit(GridViewportRow row, int column, string value)
    {
        if (_closed || !Owns(row) || row.Data == null || !_items.TryGetValue(row.Position, out var current) || !ReferenceEquals(current, row))
            throw new InvalidOperationException("This row is no longer editable.");
        long extra = 96L + value.Length * 4L;
        if (!MakeRoom(extra, 0, row.Position)) throw new GridLimitException("The edited visible row exceeds the UI byte budget. Existing edits were retained.");
        _source.SetCell(row.Data, column, value);
        long before = row.EstimatedBytes;
        row.ApplyEdit(column, value);
        _bytes += row.EstimatedBytes - before;
        CellCommitted?.Invoke(this, EventArgs.Empty);
    }

    internal bool Owns(GridViewportRow row) => ReferenceEquals(row.Owner, this) && row.Generation == _generation && row.Position >= 0;

    private bool MakeRoom(long extraBytes, int extraItems, int protectedPosition = -1)
    {
        var node = _lru.First;
        while (_bytes + extraBytes > _limits.MaxBytes || _items.Count + extraItems > _limits.MaxItems)
        {
            while (node != null && (_pinned.ContainsKey(node.Value) || _editing.Contains(node.Value) || node.Value == protectedPosition)) node = node.Next;
            if (node == null) return false;
            int position = node.Value;
            var next = node.Next;
            _bytes -= _items[position].EstimatedBytes;
            _items.Remove(position); _nodes.Remove(position); _lru.Remove(node);
            node = next;
        }
        return true;
    }

    private void Touch(int position)
    {
        var node = _nodes[position];
        _lru.Remove(node); _lru.AddLast(node);
    }

    public int IndexOf(object? value) => value is GridViewportRow row && Owns(row) && row.Position < _count ? row.Position : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator()
    {
        if (_count > _limits.MaxItems) throw new GridLimitException("Enumerating a virtual grid is not supported. Use the provider's bounded StreamAsync.");
        for (int i = 0; i < _count; i++) yield return this[i];
    }
    public void CopyTo(Array array, int index)
    {
        foreach (var row in this) array.SetValue(row, index++);
    }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void Close()
    {
        _closed = true; Bind(null);
        CellCommitted = null;
    }
}

public sealed class GridViewportRow : INotifyPropertyChanged
{
    private readonly Dictionary<int, string> _edits = [];
    internal GridViewportItems Owner { get; }
    internal GridRow? Data { get; private set; }
    public int Position { get; }
    public long Generation { get; }
    public bool IsLoaded => Data != null;
    public bool IsLoading => Data == null && Error.Length == 0;
    public string Error { get; private set; } = string.Empty;
    public string Status => Error.Length != 0 ? Error : IsLoading ? "Loading row..." : string.Empty;
    public long EstimatedBytes => 512 + (Data?.EstimatedBytes ?? 0) + _edits.Sum(edit => 96L + edit.Value.Length * 4L);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal GridViewportRow(GridViewportItems owner, int position, long generation)
    { Owner = owner; Position = position; Generation = generation; }

    public string this[int column]
    {
        get => _edits.TryGetValue(column, out string? value) ? value : Data != null && column >= 0 && column < Data.Cells.Count ? Data.Cells[column] : string.Empty;
        set
        {
            value ??= string.Empty;
            if (string.Equals(this[column], value, StringComparison.Ordinal)) return;
            Owner.Commit(this, column, value);
        }
    }

    internal void Load(GridRow data)
    {
        Data = data; _edits.Clear(); Error = string.Empty; Notify();
    }
    internal void ApplyEdit(int column, string value) { _edits[column] = value; Notify(); }
    internal void Fail(string error) { Error = error; Notify(); }
    internal void ClearError() { Error = string.Empty; Notify(); }
    public override bool Equals(object? value) => value is GridViewportRow row && ReferenceEquals(Owner, row.Owner) &&
        Generation == row.Generation && Position == row.Position;
    public override int GetHashCode() => HashCode.Combine(Owner, Generation, Position);
    private void Notify()
    {
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(nameof(IsLoaded)));
        PropertyChanged?.Invoke(this, new(nameof(IsLoading)));
        PropertyChanged?.Invoke(this, new(nameof(Error)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }
}
