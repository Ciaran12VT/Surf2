# Provider-Backed Spreadsheet Binding

## Integration Contract

```csharp
var grid = new FloatingSpreadsheetWindow(state, source);
grid.GridReadFailed += OnGridReadFailed;
grid.GridReopenRequested += OnGridReopenRequested;
// Attach to canvas/tab as before. Do not dispose on reparenting or Unloaded.
// On actual document close, before dropping the logical owner:
await grid.DisposeGridAsync();
```

`source` is an `IDataGridSource`, normally `CapturedGridSource` or `CsvGridSource`.
The constructor transfers its exclusive lifetime ownership to this control only
after successful construction. Do not share that owned source between windows.
An optional third constructor argument, `GridBindingLimits`, pins UI cache/range
budgets. They are separate from provider budgets: page rows/bytes must be allowed
by the chosen provider. Defaults are 128 rows / 8 MiB per request, 512 requested
item objects / 32 MiB UI payload estimates. Provider caches remain independently
bounded. WPF retained visuals/selection/current-item and one clipboard result
also have transient allocations; these are not CLR private-byte measurements.

The existing `(OpenDocumentState, string)` constructor retains CSV parsing,
resident rows, legacy selection, local collection filtering/sorting, and its
Copy/Export behavior for the legacy persistence path. The relational MainWindow
presenter selects the provider constructor for captured data and physical CSVs.
The footer wraps its commands within narrow windows.

## Positional View and Requests

`GridViewportItems` exposes a genuine positional `IList.Count`; it creates objects
only for requested positions. Indexer, Count, IndexOf, cell getters and currency
lookup start **no tasks or I/O**. Loading placeholders have distinct positional
identities and cannot be edited. Item/byte LRU budgets cover both placeholders
and hydrated rows. Visible and active-edit rows are pinned; quota exhaustion is
an error, never silent eviction of the edit overlay. Recreated positions compare
equal within a binding generation, while a replacement query gets new identities.

`GridPagedCollectionView` delegates positional operations and supports DataGrid's
`IEditableCollectionView` edit lifecycle. It does not use `ListCollectionView` or
its local filtering/sorting machinery. Sorting is intercepted by the control's
`Sorting` event; direct insertion into local SortDescriptions is rejected.
Large whole-list enumeration throws an explicit error instead of scanning every
row or allocating all placeholders. WPF's positional override points are in
the [official CollectionView source](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Data/CollectionView.cs).

Loaded rows, viewport changes and resize events queue a 40 ms coalesced range
request. There is at most one UI range task; replacement waits for canceled work
to actually finish before starting the latest range. Requests seek provider query
positions, not raw ordinals. Byte-short pages advance by returned row count.
All publication checks source/query/overlay/binding identity and logical lifetime.
Provider materialization means repeated scrolling does not rerun SQL filters or
sort a capture for each page. Row height is fixed for stable logical item scrolling
and adjusts with the existing font zoom control; row resizing is disabled here.
An explicit item-budget-derived MaxHeight also keeps measurement finite if a
host panel supplies infinite height, so that host cannot force every row visual.
Docking hides only the window's own resize grips, not the DataGrid scrollbar thumb.

A 250 ms dispatcher timer publishes the query's growing result-prefix count.
The scrollbar addresses that published prefix until the count is complete, then
the full dataset extent. It never treats reported legacy counts as addressable
rows. Pending counts are labeled; source `DisplayRowCount`, query matching count,
and raw/reported descriptor counts remain separate. Count growth preserves the
single current cell and vertical offset and is deferred during an active row edit.
There is no `int` truncation: a result above `Int32.MaxValue` has an explicit
unaddressable error with no virtual list extent. Headers/filters remain accessible
to narrow it, and all-matching streaming output is still available.

**Selection gate:** the provider constructor deliberately uses single-cell
selection. Select All is intercepted, not expanded into millions of selected
objects. The legacy constructor keeps extended selection. Multi-cell/range
selection without unbounded WPF item retention is not implemented. This is an
explicit compatibility limitation of the relational workflow, not a limitation
on toolbar Copy/Export, which still include all matching rows and headers.

## Filters, Edits, Sort, and Output

Existing public hooks remain: `ApplyColumnFilters`, `ApplyColumnFiltersByName`,
`GetColumnFiltersByName`, and `FilterReferenceCopyRequested`. The existing header
boxes keep 250 ms debounce, ordinary AND, and search-originated active OR-group
precedence. Clear Filters clears predicates but preserves the current sort.

Column-header sorting uses culture-sensitive displayed strings and the provider's
raw-ordinal tie-breaker. Clicking cycles ascending/descending; Shift appends or
replaces a column in the multi-column specification. Sort indicators are set
explicitly. Filter/sort/edit generations replace only the interactive query; a
separate output query remains pinned to its operation snapshot.

Cell bindings use explicit source update. `CellEditEnding` commits the editor text
to `IDataGridSource.SetCell` before WPF finishes the cell. Failed quota/source
validation keeps the editor active and reports the error. Canceling an editor
does not change its cell; previously committed cells remain, as in legacy editing.
Late same-query pages never overwrite committed cell values. After row commit,
the next query incorporates sparse edits into filtering, sorting and all output.
Edits survive UI/provider page eviction and tab/canvas transitions. They never
write the CSV or captured dataset. Logical close discards the session overlay
and cancels an uncommitted active editor.

Toolbar Copy/Export commit cell **and row** before snapshotting filters, sorts,
overlay, source and visible column ordinals in DisplayIndex order. They create
an operation-owned query and use `GridOutput` over every matching row, not the
viewport/selected items. Copy uses the provider's explicit clipboard byte policy
and reports an actionable failure instead of truncating. Export uses the existing
Save dialog's overwrite confirmation, streams to an owned same-directory pending
file, and publishes atomically only after success. Cancel affects output only;
changing a filter does not alter or cancel the pinned operation. The parent
handles actual clipboard temporary-file policy as before.

## Error and Lifetime Hooks

- `GridReadFailed` carries `Error`, `SourceId`, `QueryGeneration`, and
  `RequiresReopen`. It is raised on the dispatcher, not from painting.
- `GridReopenRequested` asks the parent to replace an invalidated or failed pinned
  source. The control does not invent a replacement or silently discard edits.
- `GridReadError`, `UsesGridProvider`, and `IsGridDisposed` expose state only.
- Retry retries a page, or recreates a failed query. A failed source stage or
  changed physical-file fingerprint requires the parent's reopen hook.
- Error/loading overlays leave header filters accessible. Failures are never
  represented as a zero-row success or an editable empty cell.
- Existing Close button/context-menu `CloseRequested` remains a request. The
  logical parent must await `DisposeGridAsync`; the menu does not auto-dispose.
- `DisposeGridAsync` is idempotent, dispatcher-marshaled, rejects new work,
  cancels/awaits tracked reads/query/output/count tasks, detaches handlers/stops
  timers, releases UI items, and disposes the query and source. Cleanup failures
  are observable. Call before shutting down the dispatcher.
- `Unloaded`/hidden pauses viewport work and polling only. Loaded/visible resumes
  the same query and overlay without rematerializing an unchanged specification.

## Verification Gates

The owned pure model hook is:

```powershell
& './Surf2/Controls/RelationalGrid/Tests/RunBindingPrototypes.ps1'
```

It writes only `Controls/RelationalGrid/.prototype-artifacts`, uses installed
compiler/reference packs, and needs no WPF, SQL, project build or shared obj.
Fixtures exercise million-row count without allocation, random deep positions,
no-I/O getters, byte/item budgets, stable item identity, stale generations,
raw-ordinal edit identity, eviction/pinning, count growth, retry/error states,
explicit Int32 overflow, and owner-driven teardown. It does not validate WPF.
The provider's separate 4,786-assertion hook already passed parent verification.

The integrated solution builds without warnings or errors. The regression runner's
`--runtime-wpf` gate exercises actual MainWindow and DataGrid controls in a dedicated
offscreen WPF host, with owned SQL fixtures. It checks deep scrollbar seeking,
bounded row residency, no SQL in paint/getters, edit preservation, sort/column
projection, all-matching output, tab/canvas reparenting, retry/reopen, and awaited
logical close. It produces light/dark desktop/narrow rendered control images.
This is not a native-window, accessibility, touch, or keyboard-navigation soak.
Accessibility code attempting full list enumeration surfaces the explicit
bounded-view limit rather than building a full row graph. See the implementation
report under `docs` for final gate results and remaining release checks; no
application-wide memory reduction is inferred from the component tests.
