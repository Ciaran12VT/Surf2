# Relational Grid Provider Prototype

This slice owns only `Services/RelationalGrid`. It does not change either WPF
window, MainWindow, project files, storage schemas, or source databases/files.
There is no resident dataset, generated whole CSV, universal row collection, or
placeholder object per source row.

## Public Contracts

- `CapturedGridSource.OpenAsync(RelationalCaptureStore, dataSetKey, limits, ct,
  displayColumns)` describes one immutable captured revision before starting
  asynchronous staging. The optional ordered `GridColumn` projection pins
  effective historical/context headers independently of the stored layout.
  Its ordinals must be consecutive starting at zero; `SourceName` is the raw
  property lookup name and `Header` is the presentation label. Later unknown
  properties are not implicitly promoted to headers.
- `CsvGridSource.OpenAsync(path, CsvGridOpenOptions?, GridLimits?, ct)` streams
  and fingerprints a read-only physical file into an owned disk store. It
  returns after indexing, because legacy header inference needs the widest
  retained record anywhere in the file.
- `IDataGridSource` owns the descriptor, sparse edits, source invalidation, and
  logical lifetime. `SetCell` accepts rows returned by that source. It is a
  session-only operation: it never writes the CSV or captured dataset.
- `DisplayRowCount` completes with the unfiltered visible source count. Use it
  for the UI denominator, not `Descriptor.SourceRowCount`: raw captures can
  contain non-objects or blank display records that never appear in the grid.
- `CreateQueryAsync(GridQuery?, ct)` snapshots filters, sort, culture, and the
  current edit generation, returning `IGridQuerySession` without waiting for
  a complete count or result scan. Each session owns one result index. The
  supplied cancellation token is also that query's build/lifetime token.
- `ReadPageAsync(GridPageRequest(start, maxRows, maxBytes), ct)` is positional
  and asynchronous. Positions are `long`; they are not raw row ordinals.
  Pages identify source, query generation, and overlay generation.
- `TryGetCachedPage` and every descriptor/count/cache property perform **no
  I/O**, start no tasks, and allocate no placeholders. A missing page is a
  loading state, not a synchronous read request.
- `Count.AvailableRows` is the published result-prefix count. `Completion`
  supplies the final exact count or an observable error/cancellation. Reported
  legacy counts and raw source counts remain separate descriptor fields.
- `StreamAsync` visits all matching rows, not just cached/viewport rows.
- `GridOutput.WriteDelimitedAsync` leaves its caller-owned destination open.
  The caller owns cleanup/publication of that stream.
- `GridOutput.ExportAsync` writes an owned same-directory pending file and
  moves/replaces it only after full success. Replacement is explicit and
  defaults off; an existing destination survives failure/cancellation.
- `GridOutput.CopyAsync` returns full tab-delimited text only within an explicit
  UTF-16 clipboard limit. It never silently truncates. Actual clipboard access
  and Export alternatives remain the WPF coordinator's responsibility.

Example, after committing the active editor:

```csharp
await using var source = await CapturedGridSource.OpenAsync(captureStore, dataSetKey,
    limits: gridLimits, cancellationToken: cancellationToken);
await using var view = await source.CreateQueryAsync(
    new GridQuery(Filters: [new GridFilter(0, "literal")],
        Sorts: [new GridSort(1)], SortCultureName: "en-US"), cancellationToken);
var page = await view.ReadPageAsync(new GridPageRequest(0, 128, 8 * 1024 * 1024),
    cancellationToken);
// Publish on the dispatcher only if view is still the active owner/generation.
```

Capture the visible column ordinals in `DataGridColumn.DisplayIndex` order and
pass those to output. Output snapshots the projection before doing asynchronous
work. Prepare an **operation-owned query after committing edits** for Copy or
Export; do not dispose that query when a newer interactive view replaces it.
The old operation deliberately retains its original overlay/filter/sort.

## Paging, Indexes, and Memory

A source is decoded/formatted **once** into an operation-owned binary display
row file plus a fixed 24-byte source-reference index. Strings are stored as
exact UTF-16 code units, including unpaired units in session edits. This is a
temporary view cache, not authoritative relational persistence or a JSON graph.
Captured source storage is streamed with no filter/sort request, so storage's
fallback is never re-sorted per grid page. SQL connections remain inside storage
operations; no SQL connection is held by a grid view.

A query scans source references once and evaluates effective edited cells.
Unsorted matches append fixed-size references incrementally. Sorted matches
create bounded runs containing only references and sort keys, then merge with
bounded fan-in into the final reference index. Runs are deleted as passes finish.
Repeated deep positional reads seek the materialized index; they never skip or
re-sort all previous source rows. Filters use exact .NET ordinal-ignore-case
literal substring rules, including literal `%`, `_`, and `[`. An active
search-generated OR group has precedence over unrelated AND filters. Blank
filters are inactive. Sorts use pinned .NET culture/version and raw ordinal as
an ascending tie-breaker, including descending string sorts; there is no numeric
reinterpretation of displayed text and no SQL-collation assumption.

Captured non-object rows are excluded from the display index, but original raw
ordinals are retained. Missing/null/empty values display empty; booleans and raw
numeric spelling use `CaptureDisplay`. Duplicate/case-colliding JSON properties
use first-property lookup. Initial all-whitespace display records are excluded,
matching legacy CSV blank-record removal. A zero-column capture receives a
display-only synthetic `Column 1`, padded with an empty cell; even `{}` therefore
produces no visible row and Copy contains the generated header only. Synthetic
columns never look up an empty JSON property name. Raw captured rows/counts are
unchanged. Editing an initially visible row to blank does not remove it.
Nonzero captured headers remain explicit catalogue/context metadata, not a
heuristic reparse of generated CSV. Physical CSV retains the legacy parser's
blank-record removal and header heuristic.

These contracts expose row/cell streams, not a complete CSV document text search.
A cell-stream search cannot claim whole-document or cross-row regex coverage.
Explorer owns explicit coverage/completeness policy; the provider makes no such
claim or implicit concatenation of rows.

An unsorted first page can precede source/count completion. A sorted result
requires the source scan and merge before its first correctly ordered prefix.
A page can contain fewer than `MaxRows` because of bytes or construction progress.
`IsRangeComplete` distinguishes a stable byte/row/EOF range from an initial
construction prefix. Advance sequential requests by returned row count, not the
requested count. Incomplete prefixes are never frozen in cache, including a
race where count completion occurs during a page read.

Separate configurable budgets cover headers, one cell/row, rows/bytes per page,
cache pages/bytes, queued page work/consumers, read concurrency, query sessions,
streams, edits, sort runs/fan-in, owned files, disk, and clipboard text. Quota
failure is explicit, never truncation. Edits are not evicted; additional edits
are rejected before altering previous overlay state. Queries snapshot the
bounded overlay, so old and new generations may coexist deliberately.
Returned rows retain bounded original cell lengths, not original cell strings,
so edit-size validation stays correct even when a caller commits from an older
query after clearing the overlay. Base rows are never retained by the overlay.

Defaults: 128-row/8 MiB pages, 8-page/32 MiB cache **per query**, four query
sessions per source, two disk reads and two streams per query, 8 MiB overlay,
16 MiB sort runs with fan-in eight, 8 GiB total live owned disk, 16 MiB clipboard.
The disk quota includes source rows, all query indexes, and merge workspace.
Memory estimates are conservative string/object estimates, not measured CLR
allocation accounting. Peak includes the current source/storage batch, row
encoding/decoding, read candidates, pinned returned pages, active sort keys,
fan-in records, overlays/snapshots, and output buffers. Cache eviction cannot
release pages still retained by a control/caller. Clipboard additionally needs
StringBuilder/final string and OS clipboard allocations. Parent benchmarks must
measure these transients; cache counters alone do not prove a private-byte cap.

## Cancellation and Logical Close

Identical page requests share actual work. Canceling one consumer does not cancel
another. When the last consumer leaves, the underlying read/wait is canceled,
and it remains tracked until it releases resources. Far-position waits do not
occupy interactive disk-read slots. Failed/canceled work is not retained in the
cache or reused as a permanently poisoned task.

Cancel/dispose an obsolete **interactive query** before replacing it; compare
page source/query/overlay generations before publication. Cancellation checks
prevent later cache publication. The source may have additional explicitly
pinned operation queries, within its session budget.

`DisposeAsync` is the explicit logical close. It cancels and awaits source builds,
query builds, page work, and active fingerprint reads, then closes files and
deletes only tracked generated files plus the empty GUID workspace. It never
recursively deletes a caller directory. Cleanup failures are reported. A paused
caller-owned row enumerator holds no file handle and is canceled on its next
move; callers must still dispose enumerators and release returned rows. No WPF
`Unloaded`, tab/canvas reparenting, or cache eviction disposes the source/overlay.

The staging parent is an application-owned trusted cache directory, not a source
folder. `OwnedScratchLease` holds an exclusive ownership marker, registers generated
payload names before creation, and conservatively recovers validated abandoned
operations older than two days. Recovery scans at most 128 candidates and never
recursively sweeps a directory. Unknown files, malformed markers and redirected
paths are left untouched. Deleted merge inputs relinquish their live registration;
bounded manifest compaction prevents repeated sorts from exhausting a lifetime
counter. Compaction retains the exclusive marker handle; a crash-torn record is
left for manual cleanup rather than trusted for deletion.

## Physical CSV Fidelity and Freshness

The parser matches `CsvGridParser`, including quotes in permissive positions,
doubled quotes, quoted CR/LF, CRLF recognition outside quotes, unterminated quotes,
trailing commas, blank/whitespace record removal, duplicate-header rejection,
empty-header normalization, generated header collisions, global ragged width,
padding, and current-culture decimal/date header inference. A line is not a record.
The default UTF-8 replacement fallback plus BOM detection matches the current
`File.ReadAllText` path. Caller-selected encodings/fallbacks are explicit;
UTF-8/UTF-16/UTF-32 BOMs and Latin-1 are prototype fixtures. No guessing of an
unmarked legacy code page occurs.

Open hashes the raw bytes while decoding and rechecks them before returning.
Read-only handles deny writers during those short reads where FileShare is
enforced; no source handle/lock is held during human browsing. Row/index staging
is an immutable source snapshot. Positional requests check length, last-write,
and creation metadata before/after reading, including cached page requests.
Full streams/Copy/Export SHA-256-verify bytes before and after output. A detected
change invalidates the whole source and its queries; the overlay is retained
until explicit logical close. The caller reports the change and reopens rather
than treating it as an empty grid.

**Freshness limitation:** a modification preserving all metadata can escape a
positional page check until a strong full-stream verification. Cached paint
lookups intentionally cannot inspect the file. Staging never mixes versions,
and full output rejects even such same-metadata changes. A live file watcher or
explicit reconcile action is a parent workflow gate, not a paint-time hash scan.
Export rejects the original source path and resolved file/directory-link aliases;
it never saves session edits back to that source. Destination directory races
must be treated as filesystem errors, not authorization for arbitrary writes.

## Gates and Remaining Integration

Run the owned pure entrypoint:

```powershell
& './Surf2/Services/RelationalGrid/Tests/RunPrototypes.ps1'
```

It uses the installed .NET 10 compiler/reference pack and writes only owned
`.prototype-artifacts`, without a project/shared `obj` build, SQL, or WPF. It
compares streaming records against the actual legacy parser with tiny read
boundaries and randomized quoted/ragged fixtures; tests real temporary files and
encodings, edits/eviction, query snapshots and Unicode filters, stable sorts,
pending counts, concurrency/cancellation, disk/edit/clipboard quotas, exact
20k-row full output, deep positional reads, zero-layout `{}`/empty capture
descriptor/paging/Copy parity with the actual legacy parser, and logical-close
cleanup.

The concrete capture adapter additionally has an isolated compile gate against
the parent's already-built storage assembly; it has not been exercised against
live SQL in this task. Parent SQL tests must verify the chosen capture batch
budgets, first-page latency, non-object/raw ordinal count distinctions, and
connection disposal. Provider compatibility is not WPF binding verification.
Selection, keyboard navigation, row recycling, thumb dragging, editing commit,
sort indicators, dispatcher publication, logical window close, and collection
limits above `Int32.MaxValue` remain parent integration gates. This API supports
long positions but does not pretend WPF can bind an arbitrary long-count IList.
