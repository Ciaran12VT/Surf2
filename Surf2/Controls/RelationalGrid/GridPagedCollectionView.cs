using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;

namespace Surf2.Controls.RelationalGrid;

// Do not use ListCollectionView: local filter/sort/refresh may enumerate the
// entire virtual IList. Provider queries own those operations instead.
internal sealed class GridPagedCollectionView : CollectionView, IEditableCollectionView
{
    private readonly GridViewportItems _items;
    private GridViewportRow? _editing;
    private readonly SortDescriptionCollection _sorts = new ProviderSortDescriptions();

    internal GridPagedCollectionView(GridViewportItems items) : base(Array.Empty<object>())
    {
        _items = items;
        _items.CollectionChanged += ItemsChanged;
    }

    public override int Count => _items?.Count ?? 0;
    public override bool IsEmpty => Count == 0;
    public override bool CanFilter => false;
    public override bool CanSort => true;
    public override SortDescriptionCollection SortDescriptions => _sorts;
    public override bool CanGroup => false;
    public override object GetItemAt(int index) => _items[index]!;
    public override int IndexOf(object item) => _items.IndexOf(item);
    public override bool Contains(object item) => _items.Contains(item);
    protected override IEnumerator GetEnumerator() => _items.GetEnumerator();
    protected override void RefreshOverride() => ResetView();

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ResetView();
    private void ResetView()
    {
        int position = CurrentPosition;
        OnCurrentChanging(new CurrentChangingEventArgs(false));
        if (position < 0 || position >= Count) SetCurrent(null, -1, Count);
        else SetCurrent(GetItemAt(position), position, Count);
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
        OnCurrentChanged();
        OnPropertyChanged(new(nameof(CurrentItem)));
        OnPropertyChanged(new(nameof(CurrentPosition)));
        OnPropertyChanged(new(nameof(IsCurrentBeforeFirst)));
        OnPropertyChanged(new(nameof(IsCurrentAfterLast)));
    }

    public override void DetachFromSourceCollection()
    {
        _items.CollectionChanged -= ItemsChanged;
        CancelEdit();
        base.DetachFromSourceCollection();
    }

    public NewItemPlaceholderPosition NewItemPlaceholderPosition
    {
        get => NewItemPlaceholderPosition.None;
        set { if (value != NewItemPlaceholderPosition.None) throw new NotSupportedException(); }
    }
    public bool CanAddNew => false;
    public object AddNew() => throw new NotSupportedException();
    public void CommitNew() => throw new NotSupportedException();
    public void CancelNew() => throw new NotSupportedException();
    public bool IsAddingNew => false;
    public object? CurrentAddItem => null;
    public bool CanRemove => false;
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void Remove(object item) => throw new NotSupportedException();
    public bool CanCancelEdit => true;
    public bool IsEditingItem => _editing != null;
    public object? CurrentEditItem => _editing;
    public void EditItem(object item)
    {
        if (item is not GridViewportRow { IsLoaded: true } row || _items.IndexOf(row) < 0)
            throw new InvalidOperationException("Wait for the row to load before editing.");
        if (ReferenceEquals(row, _editing)) return;
        CommitEdit();
        _editing = row;
        _items.PinEditor(row, true);
    }
    public void CommitEdit()
    {
        if (_editing != null) _items.PinEditor(_editing, false);
        _editing = null;
    }
    // Cell edits are committed individually, as in the legacy CsvGridRow.
    // Canceling the active editor does not roll back earlier committed cells.
    public void CancelEdit() => CommitEdit();

    private sealed class ProviderSortDescriptions : SortDescriptionCollection
    {
        protected override void InsertItem(int index, SortDescription item) =>
            throw new NotSupportedException("Sort through the provider's Sorting event, not a local collection scan.");
    }
}
