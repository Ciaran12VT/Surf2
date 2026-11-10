# Selected Relational Documents

`RelationalDocumentService` opens one selected definition, table metadata aggregate
or grid descriptor. It never loads a library/history graph or renders captured rows
as a resident CSV string. Store and index owner validation remains authoritative.

## APIs and Wiring

- `ResolveAsync` and `ResolveResourceAsync` return a session-bound address. Saved
  `Resolved` keys take priority over names; `Missing`/`Ambiguous` fail before lookup.
  Unbound legacy addresses must identify a unique owner in the best legacy match
  tier, never the first duplicate. Typed internal paths retain epoch and bigint keys.
- `DescribeTextAsync`, `ReadTextAsync` and `ValidateTextPlanAsync` separate size
  warning, selected read, and final publication checks. Immutable revision plus
  renderer policy keys text; physical keys also retain exact normalized paths and
  fixed-size content/path digests. No WPF controls or live readers are cached.
- `OpenGridAsync` returns `IDataGridSource`: physical CSV is provider-backed;
  captured tables use the selected dataset descriptor and independent data revision.
- `ReferenceDocumentAsync`, `ResolveReferenceAddressAsync`,
  `ValidateReferenceSource` and `PrioritizeReferencesAsync` use bounded index
  metadata and verify authoritative revisions/fingerprints before applying positions.
- `InitializeRelationalDocumentAccess` must run before saved layout restoration.
  The parent supplies `RelationalGridDocumentPresenter` as an **Action**, with
  synchronous dispatcher publication and successful provider-ownership transfer.
- Main hooks are `TryOpenRelationalFileAsync`, `TryOpenRelationalResourceAsync`,
  `TryOpenRelationalExplorerNodeAsync`,
  `PreviewRelationalReferenceAsync`, `NavigateRelationalReferenceAsync`, and
  `ShowRelationalReferenceMenuAsync`. Scope/layout/reconnect changes call
  `InvalidateRelationalDocumentContext`; logical closes call
  `ReleaseRelationalDocumentAccess`; shutdown/reconnect awaits
  `DisposeRelationalDocumentAccessAsync`. WPF reparenting is not a logical close.
  Document context invalidation synchronously invokes comparison, auxiliary query
  and snapshot workflow fences before its runtime-null guard. Document initialization invokes the
  comparison reset after guards and before new providers, refusing undrained work.

## Clicked Explorer Identity

`ResolveExplorerNodeAsync(scope, selected, ct)` resolves a database document by its
`SnapshotResourceKey` and verifies snapshot, kind/category, selected revision and
loaded `ScopeResourceKey`. It never resolves the clicked database resource by path
or name. The selected membership is used for effective virtual-folder ancestry,
not replaced by the first loaded alias. Physical files use that preferred
membership but retain the actual absolute source path; numeric occurrence/member
identifiers are never inserted into their raw file paths.

`TryOpenRelationalExplorerNodeAsync(selected, ct)` returns the prepared address only
when a code/grid window exists in the unchanged context. Mill's caller uses its
`DocumentPath` for code-search navigation and related-grid/filter lookup. Null is a
handled unavailable/cancelled/declined selection, not permission for legacy fallback.
Core opening accepts `preferredResolvedAddress`, validates the exact owner/revision
and saved binding, and does not re-resolve or lose alias ancestry. Reopening one
logical document through another alias reuses text/window identity while refreshing
its clipboard TXT seed. The grid presenter remains synchronous `Action`.

For prepared related targets, callers can use
`ResolveResourceAsync(snapshotKey, resourceKey, scope, ct, preferredScopeResourceKey)`
and `TryOpenRelationalFileAsync(..., ct: token, preferredResolvedAddress: address)`.
Related-data selection in the document service retains the table's selected alias.
These presentation hints are not new domain keys or persisted physical locators.

## Limits

Default resident selected text is 16 MiB UTF-16 with a warning at 4 MiB; configurable
maximum is 128 MiB. Generated table output is conservatively estimated before its
string allocation. Snapshot metadata remains independently capped at 8 MiB by the
store. Preview never displays a modal large-text warning. Text cache is 32 MiB,
32 entries and four shared reads by default; uncached admission is not truncation.
Open editor/undo/control copies are separate owners, not part of the cache budget.
Reference menus cap occurrences, selected membership metadata and text extraction.
Late results must pass both domain/index fences and workflow epoch/generation gates.

`ReadObjectAsync` supplies definition bytes through the snapshot raw UTF-16 reader;
selected-document caching and historical previews do not transcode that string.
`RelationalDocumentRawTextSqlChecks.RunAsync(runtime, resourceKey, historicalVersionKey,
expectedCurrent, expectedHistorical, ct)` is a read-only SQL regression hook for a
parent-owned published fixture with distinct current/previous revisions. Seed each
definition with its public `CurrentDefinition` / `HistoricalDefinition` constants
or supply exact expected strings containing both unpaired high and low code units.
It checks current/reverse-revision reads, actual document preview/cache, actual
selected-version history preview, actual runtime cold-read/warm-hit metrics and
below-size read-budget rejection. It performs
no SQL mutation, database initialization, UI activation or fixture cleanup.

## Diagnostics

`DocumentTextCache(limits?, diagnostics?, epoch)` preserves the previous constructor
usage. Supplying diagnostics requires a nonempty session epoch. The document service
passes `runtime.QueryMetrics.Diagnostics` and its session epoch. Measurements start
inside the single-flight producer, not per waiter; fixed fingerprints and the known
`DefinitionRead` / `TableMetadataRead` operations retain no path, body, SQL or parameter.
Physical text uses `DefinitionRead`; generated table text uses `TableMetadataRead`.

A validated provider result records one document output (`RowsRead=1`) and its
`text.Length * 2` UTF-16 output bytes. These are not SQL result rows, input bytes,
server-network traffic, retained-cache bytes or total-process residency. Cache hits
record a hit only; joined callers and metadata-only `TryDescribe` do not add hits.
`QueryCount` is always zero here; actual SQL roundtrips belong to `Session.Metrics`.
Completed/cancelled/failed measurements emit once after the provider has exited and
the cache lease has been released. A cancelled waiter cannot end a still-running
provider measurement. Completion is a fetch result, not a UI publication fence.
Rejected oversized outputs do not count as validated reads. Sink failures cannot
change the document result. `DocumentTextCacheDiagnosticChecks.RunAsync(ct)` adds
13 pure regression checks and is included by `RelationalDocumentContractChecks`.
The parent owns fresh build, pure and WPF/SQL telemetry verification; no such rerun
has been performed here for these additions.

The prepared synchronous TXT hierarchy seed is invalidated when context changes;
it does not query SQL from copy/export callbacks. Reopening prepares a current seed.

## Legacy Settings Export

`LegacyPersistenceExport.ExportAsync(path, connectionString, appDataDirectory,
replaceExisting, allowBlockingConsistentFallback, ct)` supports only read-only-probed
known legacy format. It never calls the initializing legacy connection test and
never changes database options or source data. SQL documents are streamed under
snapshot isolation, or explicitly authorized serializable blocking reads. Local
files are read-locked one at a time; they are not a global point-in-time filesystem
snapshot. Bootstrap connection settings and backups, `InternalLogs`, `Migrations`,
and `.partial`/`.pending`/`.tmp`/`.temp` artifacts are excluded at every path level
before traversal. Allowed names use the shared `PackageLocalFiles` policy, including
Windows reserved names and invalid Unicode rejection. Normalized case collisions
and reparse points fail explicitly; opened Windows handles must resolve to the
expected path, rejecting redirection before any source file bytes are copied.

The SQL reader is sequential, checks sizes before selected strings, and streams
payload JSON through bounded segments. SQL JSON text containing escaped raw UTF-16
remains exact. Literal unpaired surrogates in the outer SQL payload explicitly fail
instead of encoder replacement. No root body/list is deserialized or retained.

Limits: 8 GiB compressed archive, 8 GiB expanded per entry, 16 GiB expanded total,
4,094 local files, bounded traversal depth and 16 MiB traversal names. Reparse
points fail explicitly. A complete operation-owned same-directory temporary archive
is moved/replaced atomically; failure preserves the previous destination. Imports
remain separate-target-only and never activate or alter saved connection selection.

Settings uses the same tested async cancellation owner as Access. Closing requests
cancellation without SQL callbacks on the dispatcher; operation ownership and
disabled controls remain until actual operation and callback completion. All error
types, including `InvalidDataException` and package/regex/provider exceptions, reach
a fixed generic status without credential-bearing exception messages or logging.

## Checks and Remaining Gates

Public pure hooks are `RelationalDocumentContractChecks.RunAsync` and
`LegacyPersistenceExportChecks.RunAsync`. The isolated current-source harness passed
60 Access checks, 36 legacy streaming/path checks, and ten cancellation/streaming repeats.
The current-source isolated harness also passed 11 clicked-identity/alias checks,
including repeated runs. `RelationalDocumentContractChecks.RunAsync` includes them.
Those runs do not establish actual SQL legacy-export consistency, destination
replacement failure behavior, or WPF menu/window/provider ownership. The parent
owns full-build, SQL and interactive lifecycle validation. No shared build, SQL,
Session/schema edit, or runtime activation was performed for these checks.

`LegacyPersistenceExportSqlChecks.RunAsync(connectionString, fixtureAppDataDirectory,
newPackagePath, allowBlockingConsistentFallback, ct)` is a compiled, not yet executed
read-only SQL hook for a parent-owned small exclusive legacy fixture. It writes only
the requested NEW archive and returns eight checks: unchanged source object count,
raw payload hashes/IDs/timestamps and format identity; exact manifest/result counts;
format-1 envelope round-trip; exact selected file set and PNG bytes; denied canary
omission; and unchanged allowed local files. It does not create/delete fixture
databases, perform target imports, initialize, or activate anything. Parent
`TransferCases` owns fixture canaries and the separate-target import round-trip.

`RelationalDocumentExplorerSqlChecks.RunAsync(runtime, scope, firstDuplicate,
clickedDuplicate, secondAlias, expectedClickedText, ct)` is a parent-executed
read-only query fixture hook for duplicate same-schema/name resources and a second
membership of the clicked resource. It verifies unique keys/window identities,
shared immutable identity across aliases, selected alias TXT hierarchy, optional
selected definition body and explicit ambiguity of unbound name locators. It has
not been executed here. Parent runtime WPF cases must additionally exercise the
actual explorer callback, hot-window seed refresh, related-data opening, filters and
content-search navigation. No parent query/WPF fixture files were edited here.
