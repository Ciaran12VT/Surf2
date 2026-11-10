# Relational Explorer Adapters

Second-plan providers and isolated MainWindow bindings. The parent owns startup,
runtime activation and the first-plan validation/publication gate. These adapters
do not install schema, migrate source data or replace the legacy FileTreeService.

## Bindings

- `RelationalExplorerMetadataAdapter` (the previously added Index adapter) reads one
  scope's membership, aliases, raw virtual-folder membership, snapshot headers and
  diagram headers. It does not select definitions, rows, assets or diagram graphs.
- `RelationalExplorerService.OpenScopeAsync` preserves the supplied unloaded-ID
  set's comparer. The context also contains explicit unloaded ScopeResource keys.
- Runtime composition uses `RelationalExplorerMetadataQueries(session, index)`.
  Its authoritative domain fence permits metadata browsing during derived index
  publication; search and reference results still fence the derived generation.
- `GetRootsAsync` probes physical existence, not directory contents. Database
  roots start unloaded. `GetChildrenAsync` reports loading, bounded loaded batches,
  final completion, or a non-complete failure. Expanding a database root reads no
  definitions and enumerates only its five category headers.
- `ExplorerChildrenLoader.ReadAsync` shares in-flight loads within the same scope
  instance and occurrence. Defaults: two workers, 32 pending requests. One
  departing consumer cannot cancel another; the last cancels underlying work.
  Failed/cancelled tasks are not cached. Await `DisposeAsync` on logical close.
- `ResolveAddressAsync` accepts a canonical/readable database locator, diagram
  locator or physical file path, and optionally a preferred ScopeResource key.
  It returns identity, current metadata revision and logical ancestry, including
  aliases/virtual folders. TXT seed preparation is asynchronous; existing clipboard
  sanitization and datetime formatting remain elsewhere. Logical TXT hierarchy
  deliberately ignores visible workspace unloading, as the legacy callback does.
- Database leaf `FullPath` uses `MetadataDocumentPath(epoch, snapshotKey,
  resourceKey, category)` (`db://@epoch:snapshotKey/.../resourceKey.sql`), not a
  name lookup. `NodeKey` retains the legacy readable locator for virtual-folder
  matching, including duplicate-name occurrences. The selected-summary overload
  of `ResolveAddressAsync(scope, summary, ct)` binds typed document identity plus
  the clicked `ScopeResourceKey` and rejects changed revisions; the string overload
  accepts typed metadata paths with that preferred membership. Typed table-data
  paths belong to Capture/Documents and must not reuse a table metadata key.
  `PrepareRelationalTextFileNameSeedAsync(path, preferredScopeResourceKey)` preserves
  that alias when used outside per-window document address binding. Opening an
  existing typed document through alias two must refresh its window-local TXT seed
  and pass the same preferred membership to related-table auto-open. The opener
  still applies spreadsheet filters to the returned typed dataset path and content
  navigation to the metadata leaf's typed FullPath.
- Table-data addresses retain the selected table metadata identity and set
  `IsTableData`; `RevisionKey` is null until the capture provider resolves the data
  companion. `RelationalExplorerSearchSources.ResolveTableDataAsync` returns its
  selected immutable revision, choosing the first source-order case-insensitive
  schema/table match. Do not substitute the metadata revision as a dataset revision.
- `ExplorerNodeSummary` is FileSystemNode-compatible data, not a WPF node. Bind
  fields on the dispatcher, including effective icon, natural/effective parents,
  loaded/root flags and unresolved-query indicator. `OccurrenceKey` distinguishes
  aliases and duplicate virtual-folder IDs; `NodeKey` retains legacy move matching.
  Descendant ScopeResourceId remains empty while typed owner keys remain available.

## Search

`RelationalExplorerService.SearchAsync(request, sources, ct)` yields at most a
configured page of hits/outcomes at a time, then an explicit terminal batch.
Enumerating owns back-pressure. Each batch binds the caller's nonempty request ID.
`ExplorerRequestOwner` cancels a superseded enumeration and supplies `Accepts(id)`
for a second check immediately before dispatcher publication. Also check coverage;
generation changes invalidate all provisional batches, not just the final batch.
Await the owning enumeration task after cancellation to observe actual disposal.

The source adapter binds up to 128 occurrences using typed TVP owner/resource/
revision sets. File path hashes are only an ASCII acceleration: Unicode candidates
remain eligible and actual paths are verified with OrdinalIgnoreCase. Content
uses the existing Index search API in document-key batches. A stale database
projection cannot suppress a match: evaluate the selected current revision instead.
Unindexed sources also use the one-document fallback. Files are authoritative,
using the existing locked, fingerprinted, BOM-aware asynchronous reader.

Names are matched against the same legacy fields, not row-count suffixes or
category names. IncludeChildren and parser-directory exclusions are intentionally
not used for explorer search. Existing physical directories, database roots and
the loaded Diagrams container contribute to searched-source counts. Unloaded
resources do not scan. Missing/inaccessible roots add incomplete outcomes without
inflating legacy searched-source counts. Missing diagram placeholders retain their
name/empty-projection matching behaviour but are not called current.

Tables remain one result each: generated metadata code OR captured-cell matches.
Only the selected table metadata and selected dataset are read. Rows stream through
Capture without accumulation. Header fallback uses the first row only, preserving
duplicate properties; primitive first rows do not infer later headers. Metadata
headers retain source ordinal/duplicates. Cell lookup uses the first property under
OrdinalIgnoreCase, distinguishes missing from null, and keeps legacy boolean and
numeric/object display spellings. Matching data-only columns do not create a code
highlight. A known code/data hit survives another route timing out, with incomplete
coverage. Diagram content uses the existing type/label/image-name projection only.

Literal matching is exact OrdinalIgnoreCase. Regex is full-document .NET
IgnoreCase|CultureInvariant, with the existing Index matcher timeout (250 ms default,
at most five seconds). No LIKE, fulltext, RE2, independent-chunk regex, or unproven
collation/content prefilter is introduced.

Hits carry **natural** ancestors. Feed them to `ExplorerSearchResultBuilder` to
apply virtual folders after filtering, preserving the legacy duplicate-alias move
rules and membership-key child order. The assembler retains only result nodes and
ancestors, never scanned documents or the entire original tree. Its snapshots are
provisional until `Completed`; invalidate on cancellation or generation change.
Virtual folders under empty retained nodes are not invented, matching legacy search.

`Completed`, `Incomplete`, `IndexStaleOrUnindexed`, `DiscoveryReconciled`, and
`CurrentSourceEvaluated` are distinct. Empty newly installed indexes are not ready.
An observed physical-directory traversal sets `PhysicalDiscoveryNotFrozen`, so
`FullyCurrent` cannot imply a frozen, atomic filesystem candidate set. Reparse
directory descent/depth overflow is explicitly incomplete rather than silently
excluding subtrees. Explorer traversal rejects document-key subset contexts; use
Index.SearchAsync directly for those scoped subset requests.

## References

`RelationalReferenceService.LoadPaintAsync(scope, ct)` reads compact persisted
name/language/kind pages and document freshness summaries. The shared frozen
`SharedReferencePaintLookup` has no provider, delegate, task, GUI object or lazy
value. Painting only calls `TryGetStyle`. The preferred-language/kind and fallback
kind/language ranking match the legacy style selection. Qualified-name tie-breaks
cannot change a style key once language/kind match, so paint metadata omits target
locations and qualified names. Navigation retains them.

`ResolveAsync(context, queries, ct)` batches at most 64 tokens and completes every
candidate page before applying existing ReferenceMetadata normalization, exact /
simple fallback, ambiguity, non-file preference, callable arguments and ranking.
Both context and scope overloads reject incomplete, stale, subset or unindexed
coverage with `ReferenceIndexNotReadyException`; an empty target list is not a
successful answer from an unready index. `ResolveWithCoverageAsync(scope, queries,
ct)` supports explicitly provisional callers and returns coverage with candidates.
Each returned symbol retains revision/freshness. No source parser runs here.
The loader never publishes a truncated catalogue: oversized metadata throws and
the owner should retain its last good immutable instance. Catalogue coverage means
persisted-generation coverage, not proof that external files have not changed;
the opening/preview coordinator must verify the authoritative target before using
line locations.

`ReferenceStyleValue.CaptureWithRuntimeDefaults(settings)` captures every loaded
style in source order, including duplicate language/kind pairs. Defaults are added
to a temporary rendering list only; the loaded settings collection is not changed
or saved. `SharedReferenceHighlightStyles` selects the first existing style and
shares immutable, GUI-free name/style metadata. `ExplorerIndexLanguagePolicy`
likewise retains duplicate extension order and the legacy first-match, blank-language
fallback and language normalization. The state owner loads ordered, bounded
ExtensionAppearance/ReferenceStyle summaries before scope preparation and reloads
them after Settings. The adapter consumes `_appSettings`, not assets or root JSON.

## Reconciliation And UI Hooks

`RelationalScopeIndexRefresher.RefreshAsync` is single-flight and publishes one
document at a time. It pages selected object/table metadata, parses only changed
individual documents using the existing parsers, and never loads snapshot libraries
or captured rows. Content and symbols publish atomically under fingerprint,
parser/renderer/policy and source-revision checks. Failed work retains the last
successful revision; initial empty indexes remain unindexed and non-ready.
Scope membership updates preserve other scopes and separate alias occurrences.
Pruning is limited to successfully enumerated roots; inaccessible and unloaded
roots cannot cause a successful empty replacement. Physical files remain authoritative.

`MainWindow.RelationalExplorer.cs` reuses the state-owned scope, catalogue, edit
token and node dictionary. Main hooks are `LoadRelationalScopeAsync`,
`LoadRelationalRootsAsync`, `SearchRelationalExplorerAsync`,
`LoadRelationalChildrenAsync`, `PrepareRelationalTextFileNameSeedAsync` and
`GetRelationalReferenceHighlightStylesForFile`. State calls
`BeginRelationalExplorerContext` immediately after applying a prepared scope;
background reconciliation does not block browsing. Painting is cached dictionary
lookup only, with no fake ReferenceEntity targets. Documents/navigation calls
`CaptureRelationalReferenceScopeAsync` before resolution; it checks readiness and
retains the shared scope instance for publication fences. Parent shutdown awaits
`StopRelationalExplorerAsync` before disposing the runtime. Owned cancellation
paths schedule callbacks with CancelAsync/AccessCancellation and await actual
retirement, avoiding synchronous SQL cancellation on the dispatcher.
Root/search supersession, scope retirement and reference-read linking use
`ExplorerCancellationLifetime`, backed by AccessCancellation's retained first
callback task. Linked parents only signal this owned source. Repeated cancellation
and async retirement drain that same batch before disposing registrations/sources;
raw repeated CTS.CancelAsync calls are not used by the explorer partial.

`ExplorerResourceCatalogue.ListAsync` supplies bounded, generation-stamped header
pages for the resource picker. Selected candidates retain explicit typed owner
keys and origin stamps. A filtered page may be empty with a continuation; that
does not mean the catalogue is exhausted. Its stable source-kind/key ordering is
not claimed to reproduce global legacy culture sorting or cross-library deduplication.
History/export and typed relationship-changing add/remove commands require the
corresponding MainWindow provider delegates. Unbound operations remain unavailable,
not reported as saved or completed. The state publisher must revalidate candidate
origin stamps and publish typed relationships atomically.
The Add Existing Resource picker links its owner lifetime before ShowDialog and
always awaits DrainQueriesAsync in finally, including cancellation, rejection or
dialog failures. Explorer context retirement/shutdown also awaits its separate
drain-completion task, so modal return cannot race runtime disposal while SQL or
the first callback batch is still running. The drain task completes before any
selected-state write or roots refresh, avoiding a retirement dependency on its
own subsequent context replacement. Publication rechecks the original scope token
and owner after the picker is fully drained.

## Bounds And Gates

Defaults: 64-item batches (maximum 128), 100,000 selected metadata rows, four million
metadata characters, 128 ancestry levels, eight million document characters.
The existing Index context admits at most 512 unloaded resource/document keys.
One selected category/directory is bounded and culture-sorted in memory to preserve
legacy .NET ordering; over-budget lists fail explicitly, not truncate or silently
change ordering. Very large list/result paging requires the plan's measured spool /
data-virtualized collection experiment before runtime activation. Compact reference
metadata still scales with names and has an explicit selected-catalogue budget.

Physical folder Git-branch tooltip enrichment is not implemented in this metadata
slice (`ToolTip` remains optional); reuse an independent bounded metadata provider
at binding time, not a root-library facade. SQL cancellation/plan quality, actual
LocalDB projection tests, file mutation/permission races, large-scope memory and
dispatcher/control lifetimes remain integration gates. No UI activation is claimed.

`RelationalExplorerContractChecks.RunPureAsync(ct)` is the parent StorageSuite entry
point. It requires no SQL, real filesystem fixture, WPF window or parser and covers
summary shape, lazy children, locators/TXT hierarchy, aliases/unloaded state,
virtual-folder order/duplicate IDs, value formatting, literal/regex compatibility,
paint/navigation ranking, index routing/stale fallback, table filter semantics,
coverage/generation changes, superseded searches, shared/last-consumer cancellation,
duplicate-first appearance rules, non-mutating runtime defaults, immutable
style/language capture, nonblocking linked cancellation and first-callback-batch
retirement while a simulated provider callback is blocked, clicked alias-two
hierarchy and distinct typed addresses for duplicate-named resources.
The checks are included but not executed here; no shared build or SQL run occurred.
