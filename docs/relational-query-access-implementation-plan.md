# Surf 2.0 Relational Query Access Implementation Plan

Status: proposed follow-on implementation for review. Date: 6 October 2026.

Make Surf request only the metadata, document content, table rows, and saved state needed for the user's current work. Keep SQL Server, preserve current navigation and search behaviour, and replace whole-library reads and writes with narrow asynchronous operations. Normalized tables alone will not reduce Surf's retained memory if the application recreates its current library objects from those tables.

This is the application-side companion to the [Relational Persistence Implementation Plan](<C:/Users/ciara/source/repos/Surf 2.0/docs/relational-persistence-implementation-plan.md>). It assumes that plan's entity identities, immutable revisions, history mapping, migration safeguards, and captured-data prototype have been implemented and validated. Proposed interface and component names below are design contracts, not existing implementation. This document does not authorize runtime code changes or a database conversion.

## 1. Scope and Required Outcomes

The primary outcome is to decouple resident application data from the total size of the persistence database. An idle Surf session should not retain the complete snapshot library, every diagram, saved workbenches, historical payloads, or every captured table row. Some memory must still scale with the current work: open editor text, active diagram objects, undo state, compact reference metadata, and deliberately retained row edits.

Preserve these behaviours:

- Object Explorer names, aliases, virtual folders, order, missing-resource indicators, and workspace-specific unloaded resources.
- Navigation between source files and captured database documents, including line and column addresses, previews, and reference connections.
- Case-insensitive literal substring search and the current .NET regular-expression options.
- Existing diagram search fields, generated table code, formatted table values, and result-driven grid filters.
- Table editing within an open grid, filter AND/OR rules, sorting, displayed column order, and Copy/Export over all matching rows.
- Canvas and tab modes, centered non-overlapping canvas layouts, clicked-tab clipboard actions, and hierarchy-based TXT filenames.
- Diagram fonts, layer ordering, overlap selection, links, workflows, queries, undo, and unsaved-change prompts.
- Saved workbench copies of diagrams, history reconstruction, comparisons, capture/import, and persistence export/import.

Do not add SQLite, a central service, synchronization, a different regex engine, or an ORM as a prerequisite. The existing `Microsoft.Data.SqlClient` dependency can implement the query layer. Reassess an additional dependency only if a measured implementation need justifies it.

## 2. Current Access Paths to Replace

| Current code area | Current access pattern | Required replacement |
| --- | --- | --- |
| `MainWindow` startup | Loads all six persisted root models | Load settings, session state, small catalogues, and the selected scope |
| `IDatabaseMetadataStore` and SQL stores | Load/save complete libraries through JSON | Narrow query contracts and transactional commands |
| `FileTreeService.CreateRoots` | Database roots depend on complete snapshot objects; physical folders already support lazy children | Query database children by parent; retain lazy physical browsing |
| `LoadScopeAsync` and `ScopeReferenceIndexService.Build` | Builds reference metadata by reading/parsing scope files and inspecting full snapshots | Load compact published metadata; incrementally update changed sources |
| Object Explorer search | Traverses resources and reads source text; captured content is already in memory | Scoped query/scan jobs returning small result pages and ancestors |
| Database document opening | Resolves against the snapshot library and can render a whole dataset as CSV | Resolve stable identity, then fetch one definition, metadata revision, or grid provider |
| `FloatingSpreadsheetWindow` | Holds a full CSV string and all parsed `CsvGridRow` instances | Page provider, bounded page cache, and separate edit overlay |
| Reference highlighting | Synchronous dictionary lookup; style dictionaries are recreated for windows | Shared immutable lightweight lookup, with no database I/O during rendering |
| Scope, link, portal, and comparison pickers | Receive or enumerate libraries | Paged metadata queries with details on selection |
| History, collection comparison, and SQL trace | Reconstruct/inspect broad snapshot graphs | Query selected revisions and fetch only required content |
| Save helpers and shutdown | Save all six root objects | Flush only pending entity/session commands |

`FloatingCodeWindow` is read-only. This redesign must not introduce writes to original code files. The spreadsheet grid is different: its two-way cell bindings mutate `CsvGridRow` in memory, while current Copy/Export read those values. No source-file or captured-dataset save handler is attached to those edits. Paging must not silently discard them or turn them into persistent edits.

The current spreadsheet's visual virtualization is not data virtualization: all rows remain in its underlying collection. WPF distinguishes the two and does not supply a complete data-virtualization solution automatically. [Microsoft WPF control performance guidance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls).

## 3. Application Architecture

Use four responsibilities, wired through a small composition root rather than rewriting the entire application into a new framework:

1. WPF controls and view models own visual state, selection, loading/error state, and dispatcher updates.
2. Workflow services coordinate opening documents, searching, loading diagrams, comparing resources, and restoring workbenches.
3. Query and command contracts expose purpose-specific immutable results and explicit mutations.
4. SQL implementations own connection, command, transaction, reader, parameter, and mapping details. Physical-file providers implement the corresponding file operations.

Extract coordination from `MainWindow` as each workflow is converted. Keep unrelated diagram interaction, layout, and styling code in place. Do not combine this work with a wholesale UI rewrite.

### Data contracts

Separate summaries from details. A document summary contains identity, display name, kind, language, locator, current revision, and small status values; it does not contain code. A dataset descriptor contains layout, columns, actual row count, revision, and formatting policy; it does not contain rows. A diagram or workbench catalogue entry contains display information and a revision token, not its object graph.

Each request includes the applicable database/session identity, scope membership generation, workspace unloaded-resource state, and requested version or revision. Each result identifies the revision/generation it actually represents. Introduce small generation records or equivalent validated tokens alongside the first plan where needed; do not assume these fields already exist in the proposed DDL.

Use stable entity identities internally, with separate presentation aliases and locators. A rename or virtual-folder move invalidates display/ancestry metadata, not immutable code content. A database reconnect creates a new connection epoch so results and cache keys from the previous database cannot be reused merely because numeric keys match.

### Representative proposed API

```csharp
public interface IExplorerQueries
{
    Task<Page<ExplorerNodeSummary>> GetChildrenAsync(
        ExplorerRequest request, CancellationToken cancellationToken);
    Task<DocumentAddress> GetAddressAsync(
        AddressRequest request, CancellationToken cancellationToken);
}

public interface IDocumentQueries
{
    Task<ResolvedDocument> ResolveAsync(
        DocumentRequest request, CancellationToken cancellationToken);
    Task<OpenedDocument> ReadAsync(
        DocumentRevisionId revision, CancellationToken cancellationToken);
}

public interface IDataGridQueries
{
    Task<DataSetDescriptor> DescribeAsync(
        DataSetRevisionId revision, CancellationToken cancellationToken);
    Task<GridPage> ReadPageAsync(
        GridRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<GridRow> StreamAsync(
        GridRequest request, CancellationToken cancellationToken);
}

public interface IScopeSearch
{
    IAsyncEnumerable<SearchBatch> SearchAsync(
        SearchRequest request, CancellationToken cancellationToken);
}
```

The referenced types are proposed contracts. Implementations of async streams must forward cancellation into enumeration and dispose readers/connections when enumeration ends early. `SearchBatch` carries progress, coverage, and completion state; cancellation or a failed source is not a successful empty result. Mutation contracts remain separate, for example `SaveDiagram`, `SaveWorkbench`, `UpdateScopeMembership`, and `CommitCapture`, with expected owner revisions.

Avoid a generic repository that exposes arbitrary `IQueryable`, lazy-loading properties, or complete graph retrieval. Do not create a compatibility facade that reconstructs `DatabaseSnapshotLibrary` for the relational provider. Temporary adapters may handle one selected small aggregate, never all snapshots and their history.

## 4. SQL Execution and Consistency

Open a connection for each bounded operation and dispose it afterwards; let SqlClient pooling manage physical connection reuse. Do not keep a live connection or reader on a WPF control. Use actual `OpenAsync`, `ExecuteReaderAsync`, `ReadAsync`, and asynchronous file I/O rather than wrapping synchronous SQL calls in `Task.Run`. CPU parsing, diffing, and regex evaluation can run in bounded background workers. [Microsoft SqlClient asynchronous programming](https://learn.microsoft.com/en-us/sql/connect/ado-net/asynchronous-programming).

Select explicit columns and typed parameters with defined lengths. Keep large text, assets, history, and table rows out of listing queries. Batch independent metadata lookups where they share context; use table-valued parameters for symbol/document identity sets rather than one round trip per item. Inspect plans for large batches because table-valued parameters do not have column statistics. [Microsoft table-valued parameter guidance](https://learn.microsoft.com/en-us/sql/relational-databases/tables/use-table-valued-parameters-database-engine).

Whitelist sort/filter operations and map generated captured-table identifiers through trusted catalogue metadata. User text, source table names, and source SQL definitions must never become interpolated executable SQL. Ordinary query code must not create databases or perform schema upgrades.

### Paging policy

Use stable ordering with an identity tie-breaker. Explorer pages use preserved order and node identity; datasets use row ordinal by default and append it to any selected sort. Prefer seek/keyset paging for sequential browse, search result traversal, and streaming exports. Cursor state binds to owner, revision, filter/sort, and generation; reject or restart an invalid cursor rather than mixing results.

Sequential paging does not by itself implement arbitrary scrollbar jumps. The grid prototype must provide an explicit positional strategy: verified offset/range queries for a pinned immutable dataset, or a session-owned ordinal result index for expensive filtered/sorted views. Do not repeatedly skip millions of rows without measuring it. Count work may finish after the first page; distinguish actual counts, pending counts, and reported legacy counts.

For immutable revisions, pin the revision across an operation. For changing scope membership and current pointers, capture a generation with a consistent metadata read and detect changes before subsequent pages. Use a short snapshot transaction only if enabled and justified; do not hold a transaction open while a person browses. Search sessions that require a fixed candidate set can stage identities/revisions on disk or in owned SQL work tables. Stable paging requires a unique order and appropriate consistency. [Microsoft pagination guidance](https://learn.microsoft.com/en-us/sql/t-sql/queries/select-order-by-clause-transact-sql).

## 5. Startup and Workspace Restoration

Replace the six root loads with this sequence:

1. Read-only format detection chooses the validated relational provider or the explicitly supported legacy/migration path. Never use the current initializing connection test as the format probe.
2. Load small preferences, appearance/input rules, workspace layout, selected-scope identity, and catalogue pages needed by the initial UI.
3. Load the selected scope's membership metadata and top-level explorer nodes. Do not fetch definitions, captured rows, histories, diagram assets, or every workbench.
4. Restore lightweight document-window descriptors, positions, filters, and reference-line endpoints.
5. Open the active tab or initially visible canvas windows with bounded concurrency. Queue remaining content requests; layout restoration does not require all text first.
6. Load the selected diagram or exact saved workbench diagram revision only when that view is needed. Load reference metadata and reconcile source indexes separately.

Make loading, missing, failed, and ready states explicit. A failed detail request must not be interpreted as a new empty entity eligible for saving. Preserve saved state for missing files and broken targets. Readiness to write is determined by the selected provider, a successful entity load/create, and the expected revision, not by hydration of the entire database.

Inactive tabs can initially remain resident to minimize behaviour changes. A later measured memory-pressure policy may suspend read-only hidden editors while retaining layout and navigation state, then rehydrate on activation. Do not promise a cache limit bounds memory while every open AvalonEdit document remains pinned outside the cache. Canvas windows actually on screen remain live.

## 6. Object Explorer and Metadata Pickers

Keep the lazy browsing already present for physical directories. Move directory enumeration off the dispatcher where needed; publish one batch of immutable node summaries on the dispatcher. Query captured database categories and children only when expanded. Apply virtual folders and aliases through effective parent relations without generating code or datasets.

Represent children with unloaded, loading, loaded, and failed states. Expanding a node twice shares one in-flight request; collapsing may stop low-priority prefetch. A retry must not create duplicate children. For very large expanded lists, use a verified data-virtualized collection or paged child loading rather than an unbounded `ObservableCollection` of every descendant.

Search results need their ancestor paths, including virtual folders. Fetch those ancestors in batches and build only the result tree, not the complete original tree. Preserve expansion/selection keys across refreshes where their identities still exist.

Scope selection, existing-resource lists, portal targets, link targets, history version lists, workbench lists, and comparison candidates use the same summary/detail separation. Opening a picker must not fetch definitions or image bytes. Respect each picker's current membership rules rather than applying active-scope restrictions to workflows that intentionally list other resources.

For Copy as TXT, request or cache the effective Object Explorer ancestry for the clicked document in its current scope/alias context. Keep the current hierarchy levels and datetime filename format. Convert the synchronous filename-seed callback into an asynchronous preparation path, or refresh a complete small address before enabling the action. Do not block the dispatcher on a SQL request or rebuild the full explorer to determine a filename.

## 7. Opening Code and Generated Documents

The document coordinator resolves a locator to stable document/revision identity, checks an existing window or in-flight open, then fetches only that content. Maintain one logical window per identity under the current reopening rules; test multiple aliases, readable/canonical database locators, and simultaneous navigation requests.

For physical files, read the authoritative file asynchronously and validate indexed fingerprint information. For SQL definitions, fetch one `TextContent` value. For generated table metadata, fetch one metadata revision's columns and keys, then reuse `DatabaseDocumentService` formatting; cache the output by metadata revision and renderer version. Full Table Data opens a row provider, never a complete generated CSV string.

AvalonEdit needs a resident text document for the opened page. This is deliberate single-document materialization, not zero-memory streaming. Account for the string, editor document, syntax structures, and transient copies. Very large individual files require a measured large-document policy, such as an explicit warning and deliberate open/export choice, rather than silently truncating code.

Preserve initial source-window placement, line-range navigation, scroll offsets, history suppression, font/background settings, reference previews, copy status reporting, and clicked-tab behaviour. Reference connections bind to stable identities with saved line/column boundaries; aliases remain presentation metadata.

Hover previews have their own debounced cancellable lifetime. Request one target/revision and bounded preview information, not its containing snapshot. Verify locations against the actual target revision; a stale physical-file index triggers refresh rather than navigation to a wrong line.

## 8. Reference Resolution and Index Maintenance

### Highlighting and navigation

Keep reference highlighting synchronous and local. `ReferenceHighlightColorizer.ColorizeLine` must not issue SQL, open files, wait on a task, or start one query per token. Load shared immutable scope name/kind/style metadata for the current published generation, and preserve `ScopeReferenceIndex` normalization, qualified/simple-name fallback, ambiguity, non-file preference, callable argument matching, and language/kind ranking.

Separate the minimal highlighting lookup from complete navigation records where that meaningfully reduces memory. Fetch candidate target locations in one batched query when a click or preview requires them. Share style metadata across editors instead of reconstructing and copying a scope-wide dictionary for each window. Freeze the published lookup and replace it atomically after index changes.

Measure the compact index for the largest scope. If even names become too large, prototype a token-demand cache populated asynchronously for opened document token batches; painting still reads local ready data only, and highlights update after a batch completes. Do not silently drop symbols to satisfy a fixed cache size. The shared compact scope lookup is the initial implementation, not a claim that its memory is constant as symbol count grows.

### Incremental source index

Reuse existing Roslyn, ScriptDom, VB, and JavaScript parsers and their extraction rules. Key derived content/symbol records by source identity, source revision/fingerprint, parser version, language/settings, and scope membership generation. Enumerate metadata first and parse only sources whose fingerprints or index policy changed.

Publish a source's content projection and symbol generation together once parsing succeeds. Keep the last successful generation visible as stale when appropriate; never mark a failed parse as a successful empty symbol list. Database definitions already have immutable revisions. Physical files require reconciliation because files can change outside Surf.

Use bounded indexing queues, deduplicate updates, and delay repeated writes while a file is changing. File watchers are hints, not a complete source of truth. Reconcile on scope activation, explicit refresh, and after watcher loss; handle deletions, renames, missing permissions, excluded directories, and changed resource membership. Read with a fingerprint check before/after, retry unstable files, and identify incomplete coverage.

Queued index work must not outrank opening the user's selected document. Parser syntax trees and source buffers are released after extracting one source's records; they are not retained as the application-wide index. Index persistence is derived and rebuildable. This remains name-based navigation, not a claim of complete compiler binding.

## 9. Search Routing and Semantic Compatibility

Capture a search request containing active scope, unloaded-resource state, target Name/Content, literal/regex mode, query, generation, and request identity. Cancel the previous request when it becomes obsolete and check the captured generation before publishing any batch. Clearing search restores lazy browsing without waiting for the old scan to finish.

### Routes

| Search request | Preferred execution | Compatibility rule |
| --- | --- | --- |
| Name substring | Scoped SQL metadata/projection query; exact verification if necessary | Match the names/path fields each current resource matcher examines |
| Name regex | Fetch small scoped name metadata in batches; .NET evaluation | Preserve existing options and result hierarchy |
| Code/content substring | Scoped searchable projections evaluated in SQL where equivalent | Literal input is not a wildcard expression; SQL collation must be tested |
| Code/content regex | Stream candidate document projections to bounded .NET evaluation | Preserve whole-document regex semantics and report timeouts |
| Captured table content | Query versioned display values; return matching column identities | Do not search a generated whole-table CSV or change value formatting |
| Diagram content | Search the saved type/label/image-name projection | Do not add tooltip/workflow text to current results |
| Optional future word search | Full-text query under a separately identified mode | Never replace substring search with word search implicitly |

Current literal matching uses `StringComparison.OrdinalIgnoreCase`; regex uses `IgnoreCase | CultureInvariant`. SQL collation is not automatically equivalent. Establish a shared tested matcher/formatter contract. For any unsupported Unicode/case behaviour, use a safe candidate selection that cannot omit matches, followed by exact bounded application evaluation. Where no safe selective prefilter exists, scan scoped content sequentially. Do not advertise SQL-only execution until equivalence is demonstrated.

Escape SQL wildcard characters if a route uses `LIKE`; parameterization alone does not make `%`, `_`, or `[` literal. Do not use word full-text indexing as a mandatory prefilter: it can miss valid substrings. SQL Server full-text indexing is word-oriented. [Microsoft full-text search documentation](https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-search).

### Regex and scan jobs

SQL Server 2025 regex uses RE2 and is not a transparent replacement for Surf's .NET syntax. Keep .NET evaluation initially, regardless of whether that optional server feature is installed. [Microsoft SQL Server regex documentation](https://learn.microsoft.com/en-us/sql/relational-databases/regular-expressions/overview).

Use an explicit per-document regex timeout, a cancellable scan loop, and bounded workers. Materialize at most the worker's current document where arbitrary .NET regex requires it; do not evaluate independent chunks because patterns may cross chunk boundaries or refer to the complete document. An extreme-size document needs an explicit supported-size/scan policy and an incomplete-source result, not silent exclusion. A timeout or unsupported source is shown as incomplete coverage. [Microsoft .NET regex best practices](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices-regex).

Use sequential SqlClient text streaming for scan/export transport so the provider does not buffer the complete corpus before processing. A single editor/regex operation may still need its current document in memory. Dispose readers when a scan is canceled; tokens alone are not a guarantee that server-side streaming has stopped. [Microsoft SqlClient streaming and cancellation guidance](https://learn.microsoft.com/en-us/sql/connect/ado-net/sqlclient-streaming-support).

### Results and freshness

Return document/entity identity, revision, display metadata, ancestor identities, and the matching table-column/filter information needed by current navigation. Fetch snippets only where the UI needs them. Cap queued batches and virtualize result presentation; do not retain every matching document's text. For a huge result set, stage result identities and page them rather than accumulate every result node in the UI.

Keep searched-source count, match count, index coverage, and completion separate. Do not require an exact count before showing initial results. Present inaccessible, timed-out, changed, or unindexed sources as partial/incomplete. Explicit refresh/search should reconcile changed physical files and either evaluate their current content or state clearly which index generation was searched. An old indexed copy is not silently treated as the current file.

This can reduce client memory substantially while still being an expensive scan. Indexed name/reference lookups should benefit most; arbitrary substring/regex searches can be slower than searching already-resident text. Debouncing, progress, cancellation, and measured query plans determine the user experience, not normalization alone.

## 10. Table Paging and Existing Grid Edits

Replace the CSV-string constructor dependency with an `IDataGridSource` that supplies descriptor, page, count, and streaming operations. Provide separate implementations for captured relational datasets and external CSV files. `FloatingSpreadsheetWindow` owns grid presentation, filters, sort descriptors, column visibility/order, and row-edit state, not SQL connections or a complete row collection.

### Captured dataset provider

- Load headers/layout before requesting rows; sample column widths from a bounded initial page.
- Pin the immutable dataset revision and its display-value formatting version.
- Load the visible range and a small adjacent prefetch range; bound both page count and estimated bytes.
- Redirect filter/sort requests to the provider instead of `ICollectionView.Filter` over all data.
- Preserve current AND filters and search-generated OR-over-selected-columns filters.
- Use row ordinal as stable identity, independently of imported primary-key values or duplicates.
- Cancel obsolete filter/count/page jobs and reject pages for an older filter generation.

Prototype the binding adapter against WPF selection, keyboard navigation, cell editing, thumb dragging, row recycling, and sort indicators. Avoid synchronous database calls from an indexer. Return placeholders for requested missing pages and notify the dispatcher when their items arrive. An adapter that creates one placeholder object per database row is not bounded data virtualization.

If the full scrollbar adapter cannot meet correctness/performance requirements, ship explicit page navigation after reviewing that UX change. WPF collection limits also require explicit handling for datasets beyond addressable collection sizes. Do not claim arbitrary huge datasets fit a single virtual list without this gate.

### Edit overlay

Preserve current session-only editing. Commit the active cell edit before copying, exporting, changing a view, or closing under the existing interaction rules. Record edited cells in a sparse overlay keyed by dataset/source revision, row ordinal, and column identity. Page eviction must not discard the overlay. Captured revisions and original CSV files remain unchanged.

Filters, sorting, Copy, and Export must evaluate effective edited values, not just database values. Exclude edited base rows from the base result and merge their effective replacements in the correct position. For large overlays or non-SQL-compatible predicates, use an operation-owned spool/result index with exact matching; do not pull the whole dataset into RAM to reconcile a few edits. Bound the overlay separately and warn or spill session-owned state deliberately rather than quietly evict edits.

A session-only overlay closes with its logical grid, matching current non-persistent editing. Persistent table editing or recovery of unsaved cell edits after application restart would be a separate feature decision. Workbench layout/filter restoration must not accidentally imply those edits were saved.

### External CSV provider

Do not assume external CSV content has already become a captured SQL dataset. Implement a streaming parser based on the current `CsvGridParser` rules and test quotes, escaped quotes, embedded newlines, duplicate/empty headers, ragged rows, encodings, and empty files. Build an application-owned disk-backed row/offset index or staging representation for paging. Fingerprint the source and invalidate that staging when it changes.

Simple line-by-line splitting is incorrect for multiline CSV records. Filtering/sorting large external files may build a bounded-memory result index on disk. Source CSV remains read-only; staging uses only generated paths in an owned cache directory. Remove the existing loaded-event closure that keeps the complete input CSV alive after parsing.

## 11. Copy and Export

Current "visible" grid content means every row passing the filters, not just the viewport. Keep that meaning, including visible columns in `DisplayIndex` order, headers, effective cell edits, selected sort, quoting, and the current UTF-8 CSV output policy without a BOM. Clearing filters returns the full dataset view.

Take an immutable operation specification after committing cell edits: source revision/fingerprint, overlay generation, filters, sort, and column projection. Copy/Export must not change mid-operation because the user changes a filter or a source is reimported. If an external file changes during the operation, stop or restart with a clear explanation rather than produce mixed-version rows.

Export streams all matching rows from that specification to the user's chosen path. Write an operation-owned partial file in the chosen directory and publish the completed file only after success, using an overwrite policy consistent with the save dialog. Cancellation/failure removes only the partial file created by that operation and preserves an existing destination. Report progress and completed row counts without building the complete CSV in a `StringBuilder`.

Clipboard text ultimately requires a complete string and clipboard allocation; streaming retrieval cannot eliminate that final cost. Estimate size where possible, enforce an explicit reviewed clipboard limit, and offer Export when it would be unsafe. Never silently truncate to cached pages or a maximum row count. Preserve tab-delimited Excel-compatible quoting and header behaviour for ordinary copies.

Code Copy Text and Copy as TXT operate on the clicked open document's full content. Keep their existing temporary-file/clipboard ownership and hierarchy naming rules. Do not change source files or use arbitrary source directories for temporary output.

## 12. Cache and Memory Ownership

Use independently budgeted caches instead of one unbounded object graph:

| Data | Cache identity | Lifetime/invalidation |
| --- | --- | --- |
| Explorer/catalogue pages | Database epoch, scope/membership generation, parent, page | Evict by budget; invalidate membership/alias changes |
| Document text | Database epoch, immutable revision or physical-file fingerprint | Pin while used by an editor/operation; evict unpinned content |
| Generated table code | Metadata revision and renderer version | Same as document text |
| Grid pages | Dataset/source revision, filter/sort generation, page | Small byte-bounded cache per active provider |
| Reference metadata | Scope/index generation, language/style policy | Shared immutable lookup; replace generation atomically |
| Assets | Asset identity plus decoded size/resolution | Share immutable data; account for decoded bitmap memory |
| Preview/negative lookup | Target revision and request context | Short lifetime; do not cache permission errors as absence |

Account for retained strings, row objects, buffers, editor documents, decoded images, result collections, overlays, and undo state. Cached byte estimates need calibration against profiler measurements. A bounded text cache does not bound payloads copied into controls. Avoid storing identical definitions as a string, rendered document, preview text, and cached graph without accounting for each owner.

Deduplicate identical in-flight reads. Track consumers so canceling one hover does not cancel a shared read still needed by a document open. Cancel the underlying job when its last consumer leaves, or let a small near-complete read finish within policy. Never cache canceled/faulted tasks indefinitely. Failed lookups retain a short retry state rather than poisoning an immutable key forever.

Document suspension, cache eviction, and logical close are different operations. A tab/canvas switch re-parents controls and can raise `Unloaded`; that is not authorization to dispose the document or delete its edit overlay. Introduce an explicit logical lifetime owner that cancels requests, detaches external subscriptions, stops timers, releases payloads, and disposes providers on actual close. Await outstanding disposal where needed.

Set page sizes, text/asset budgets, prefetch distance, and maximum worker concurrency from representative measurements. Initially expose them as internal configuration with conservative defaults, not user-visible tuning controls. Budget queue buffers and peak transients as well as settled caches. No fixed total-memory or percentage reduction is promised before benchmarking.

## 13. Cancellation and UI Coordination

Each scope, window, search, grid filter, preview, and database connection epoch owns cancellation and generation state. Capture WPF state on the dispatcher into immutable requests, do work outside the dispatcher, then publish only if owner, epoch, scope, and request generation still match.

Use a small scheduler with separate interactive and background limits. Opening a selected document and fetching a visible grid page must remain responsive while indexing, regex scans, imports, or exports run. Bound queued work and coalesce stale page/prefetch requests. Avoid a single global lock that lets a long export block all reads.

Use command timeouts and cancellation tokens, with provider-specific cancellation/disposal for streaming. A stale result is ignored immediately, but its scheduler slot and resource ownership remain active until the real task/reader finishes or is disposed. Do not declare a canceled scan stopped while it continues consuming server resources.

Catch exceptions at workflow boundaries and provide retryable states. Do not use `.Result`, `.Wait()`, synchronous SQL in property getters, or background access to WPF collections. Event handlers may be `async void` only at the WPF boundary; delegate to tracked `Task` operations so errors and teardown are observable.

On shutdown, reject new work, cancel background jobs, finish or explicitly abandon pending transactional saves under the existing timeout/prompt policy, and dispose readers/providers. Do not perform a last full-library serialization. On database/scope changes, retain dirty-state prompts and save ordering before replacing the old owner context.

## 14. Targeted Commands and Saving

Read models must not become a new mutable master library. Capture changes as commands with explicit owners and expected revisions. Distinguish create, update, delete, membership reorder, and preference updates. An unloaded child is not evidence that it was deleted.

Serialize commands that update the same owner and use optimistic concurrency checks even for local use: multiple windows or another Surf process can race. Snapshot active WPF state on the dispatcher before saving, then persist that captured generation. If the user edits again during the save, mark only the saved generation clean; do not clear newer edits.

| Workflow | Command scope and consistency |
| --- | --- |
| Scope/resource/virtual-folder edits | Changed membership/aliases/order in one owner transaction; preserve unqueried rows |
| Diagram save | One active diagram revision, changed assets/workflows/queries, and current pointer atomically |
| Workbench save | One selected workbench, layout/connections/unloaded state, and its exact embedded diagram revision |
| Workspace autosave | Changed current-session descriptors/viewport state; coalesce high-frequency moves |
| Preference change | Named preference or small ordered settings aggregate |
| Snapshot capture/refresh | Staged metadata/content/data revisions followed by atomic snapshot publication |
| History restore/copy | Selected version/resource identities and explicit new current publication; original history preserved |
| Deletes | Explicit owner-aware command with dependency checks; never infer from a partially loaded list |

Saving a selected diagram may write its complete bounded active aggregate initially. That is compatible with eliminating library-sized writes; optimize object-level deltas only if measured. Retain current undo and dirty-comparison semantics for the active aggregate while avoiding serialization of unrelated library contents.

Use expected owner revision or `rowversion` tokens on mutable records. Report conflicts and offer reload/review without overwriting newer state. Immutable revisions are appended then published by pointer update in a transaction. Do not retry writes blindly after an unknown outcome: reread the owner/operation identity to determine whether the command committed. Migration and runtime command identities must support the appropriate recovery semantics.

Invalidate only affected summary generations and derived indexes after commit. Mark unchanged immutable content reusable. Enqueue index work transactionally with source publication where feasible; derived-index failure does not erase committed authoritative content. A new revision cannot be paired with old symbol locations without being marked stale.

## 15. History and Secondary Workflows

### History

Query version summaries and resource change pages first. Resolve one requested historical resource through the first plan's state ranges/reverse-change semantics. Fetch its definition, metadata, or dataset only when requested. Listing versions must not clone current snapshots or reconstruct every previous dataset.

A whole-version restore enumerates identity/revision mappings in batches and publishes the selected state transactionally; it need not deserialize a complete snapshot into Surf. Preserve next-version numbering, deleted/re-added resources, original IDs, and reverse-change meaning.

### Comparisons

For collection comparisons, retrieve summaries/relative identities and trustworthy content hashes first. Raw equal hashes plus verified collision policy can identify identical content; differing hashes do not prove a difference under ignore-case/whitespace options. Fetch only necessary pairs and reuse DiffPlex for actual text comparisons. Include normalization policy/version in any derived comparison hash.

Virtualize or page comparison results. A single large file pair still requires measured transient memory. Table comparisons use dataset streams and a SQL join or disk-backed merge appropriate to captured layouts, while preserving current value formatting and key semantics.

The current `CreateRowIndex` uses a case-insensitive composite display key, a blank-key marker, and a global duplicate-suffix counter while scanning source rows. Reproduce that identity assignment before comparing, including collisions with naturally suffixed keys and the composite separator, unless a separate comparison behaviour change is approved. Do not add uniqueness constraints or pair duplicate rows arbitrarily.

### SQL trace and links

Replace `SqlTraceService`'s full stored-procedure index with batched metadata resolution and definition reads for reachable procedures only. Keep current schema/name ambiguity behaviour, cycle detection, parsing, and rendering. Cache repeated targets within one trace and bound depth/output/work with explicit partial-result status.

Link and portal pickers query metadata; resolving the chosen target fetches only that target. Opening documentation or exporting a selected diagram loads the requested rich text/assets, not every diagram. Preserve unresolved original links and workbench-specific embedded revisions.

### Capture and package operations

Source database capture and persistence database queries remain separate responsibilities and connection contexts. Stream/stage large capture operations, compare revision/hash metadata where possible, and publish only validated captures. Never execute captured definitions as commands.

New-format package export enumerates relational entities and streams content/rows/assets from a consistent revision set. It must not serialize a reconstructed global JSON graph. Import uses staged validated writes and atomic activation/publication. If a legacy-compatible package is required, implement bounded serialization deliberately and document any format/size limits; do not make legacy packaging force normal startup back to eager loading.

## 16. File Index Freshness and Storage Permissions

Physical files remain authoritative. SQL stores searchable derived copies plus fingerprints, not permission to overwrite originals. Missing, changed, or inaccessible files are distinct states. Avoid claiming a search is fully current while source reconciliation is incomplete.

Define access ownership for the new components:

| Location or system | Reads | Writes |
| --- | --- | --- |
| Surf persistence database | Narrow catalogue/content/history/row queries | Explicit state commands, index generations, owned work/staging records |
| External source folders/files | Discovery, fingerprinting, code/CSV reads | None from these query providers |
| Captured source SQL database | Existing capture/metadata queries under source credentials | None added by the persistence/query redesign |
| Local bootstrap configuration | Locate selected persistence connection | Only existing explicit connection/configuration actions |
| Application-owned staging/cache directory | CSV/result indexes and operation recovery | Generated bounded temporary/index files with ownership and cleanup |
| User-selected export destination | Existing-target checks where needed | Completed export and operation-owned partial file |
| Existing clipboard temporary directory | Existing clipboard file flow | Existing Copy as TXT temporary artifacts |

Validate resolved paths before cleanup, delete only files owned by the operation, and use quotas/expiry for abandoned staging. Do not recursively clean source folders. Do not include connection strings, credentials, code bodies, sensitive row values, or rich documentation in normal diagnostic logs.

Keep runtime SQL privileges separate from migration/schema creation privileges where deployment permits. This plan adds no source-file write permissions and no new DLL by default. A full dependency/security audit remains a separate deliverable.

## 17. Instrumentation and Performance Experiment

Record a baseline before implementation with a representative scope and the largest problematic scope: counts and bytes of documents, symbols, datasets, history, diagrams/assets, and workbenches; loaded views and reproducible actions; database/server version and local configuration. The Task Manager screenshot shows high Surf memory, but does not by itself attribute that memory to a particular allocation path.

Measure Surf private bytes, working set, managed heap, allocation rate, large-object retention, GC time, dispatcher delays, active readers, cache/overlay bytes, and queued jobs. Measure SQL Server memory, CPU, logical reads, query duration, spills, and staging growth separately. Reducing Surf memory by moving work into SQL must not be described as an equal reduction in total machine memory.

For each operation record time to first useful display, completion time, median/tail latency, rows/bytes read, query count, cache hit rate, cancellations, and coverage. Use operation names/query fingerprints and redacted identifiers, not SQL parameters or returned content. Compare cold and warm runs with the same corpus and restore state.

| Experiment | Required observation |
| --- | --- |
| Launch with many large unused datasets | No dataset-row or historical-content reads before requested views |
| Open one procedure/file | One resolved target's content; no snapshot-wide hydration |
| Expand database category | Metadata page only, no definitions |
| Highlight and scroll code | No database/file I/O initiated by painting |
| Search substring and regex | Bounded client buffers; measured scan cost and truthful coverage |
| Open/filter/sort a million-row grid | First page precedes complete scan/count; page/cache memory stabilizes |
| Export a large filtered grid | Full result streamed; bounded buffers; existing destination preserved on failure |
| Repeated scope switches and closes | No steadily growing retained views/readers/jobs after settle |
| Load one history item/workbench | Selected revisions only, exact saved-state identity |

Set numerical launch, open, scrolling, cancellation, and memory thresholds after the baseline/prototype. Agree a release budget before broad rollout. Indexed metadata lookup should be fast, but arbitrary filters/search, large regex documents, active editors, edit overlays, and high-resolution diagrams remain material costs.

## 18. Implementation Phases

### Phase 1 Contract and measurement foundation

Create query/command DTOs, database epoch/generation policy, lifetime ownership, redacted instrumentation, and baseline fixtures. Add a small SQL connection/execution helper using the current SqlClient dependency. Confirm first-plan DDL supports scoped lookup, immutable revision reads, row paging, and mutable owner tokens.

Gate: contract tests prove summary results cannot carry library payloads; read-only format selection works without invoking legacy initialization. Resolve captured-row prototype decisions before implementing its provider.

### Phase 2 Explorer and document opening vertical slice

Implement selected-scope metadata, lazy database children, locator/address resolution, one definition read, generated table metadata, window single-flight, and stale-request handling. Convert the relevant `MainWindow` and `FileTreeService` calls first, leaving unrelated interactions intact.

Gate: navigate through aliases/virtual folders to one procedure with no unrelated content fetch. TXT filenames, tab/canvas transitions, line navigation, and close/reopen retain behaviour.

### Phase 3 Reference catalogue and indexing

Persist/query compact symbols, extract incremental source indexing from whole-scope parsing, share highlight lookup/style metadata, and convert preview/navigation resolution. Add generation publication, file reconciliation, and priority/cancellation controls.

Gate: current reference fixtures give equivalent targets/ranking; editor paint causes zero I/O; changed files refresh without rebuilding the entire scope; memory is shared across windows.

### Phase 4 Scoped search

Extract current search/format rules into testable compatibility contracts. Implement literal SQL routes with proven semantics, bounded fallback scans, .NET regex timeout policy, paged result ancestry, and current-source coverage reporting.

Gate: matched identities/filter columns/hierarchy agree with the legacy path on fixtures; case/wildcard/regex edge cases pass; rapid searches and scope changes never publish stale results. Worst-case scans remain cancellable and bounded.

### Phase 5 Data grid and streaming output

Implement captured-row and CSV providers, binding prototype, row/page count strategy, sparse edit overlay, exact filter/sort reconciliation, and streaming Export. Add explicit clipboard size policy and preserve full-result Copy semantics.

Gate: large-grid memory plateaus outside deliberate overlays; edited off-page values survive and affect results; Export matches the full filtered view; arbitrary scrolling/selection does not trigger blocking I/O or allocate every row.

### Phase 6 Saved state and targeted writes

Convert settings, scope manager, session save, diagram/workbench catalogues and selected aggregates, dirty-state handling, autosave queues, immutable publication, and conflict reporting. Restore only visible/selected details while preserving saved missing targets.

Gate: no whole-library save calls remain in the relational runtime path; failed loads cannot overwrite entities; delayed saves do not clear newer dirty state; workbench diagram copies remain independent.

### Phase 7 History and secondary consumers

Convert history restore/copy/diff, resource comparison/pickers, table diff, links/portals, SQL trace, capture, documentation output, and persistence package import/export. Search all consumers of old root models and remove relational dependencies on them.

Gate: every supported workflow can run without constructing a global snapshot/diagram/workbench library. Large secondary operations have their own bounded buffers and cancellation/partial states.

### Phase 8 Soak and release

Run realistic sessions, leak/resource tests, SQL plan review, offline/error scenarios, shutdown races, and cold/warm benchmarks. Tune budgets and indexes from results. Release relational schema and compatible access code together; incomplete feature conversion is not a production-ready migration.

Gate: agreed performance thresholds and compatibility tests pass; original legacy source remains preserved; operational rollback is documented without claiming that post-migration edits automatically synchronize back to it.

## 19. Verification Matrix

Use unit tests for pure matching, formatting, cursor validation, scheduling, and overlays; real SQL integration tests for projections, paging, transactions, typed captured layouts, and cancellation; WPF integration/manual automation for dispatcher and control lifetimes. A mocked repository does not establish SQL plan quality or server cancellation.

| Test group | Required cases |
| --- | --- |
| Loading and browsing | Empty/missing entities, many scopes, unloaded resources, duplicate aliases, virtual folders, large categories, lazy physical folders |
| Identity and generations | Two databases with overlapping numeric keys, reconnect, alias rename, scope switch during slow load, revision replaced during paging |
| Code and references | Canonical/readable locators, ambiguous/qualified names, overloads, case/bracket normalization, stale lines, shared reads, repeated hover/close |
| Search | Current names/projections, wildcard literals, Unicode/case edges, cross-line regex, backreferences, invalid patterns, timeout, inaccessible/changed files, partial coverage |
| Grid | Null/missing/empty formatting, duplicate rows/headers, wide cells, multiple layouts, AND/OR filters, tie sorting, deep scroll, page eviction, stale counts |
| Edits and output | Off-page edits, edit changing filter membership/sort order, active-cell commit, full headers/column order, no-BOM CSV, quoted tabs/newlines, clipboard limit, export cancel/failure |
| CSV staging | Multiline quotes, ragged rows, encodings, source mutation, stale index, disk full, cache ownership/cleanup |
| Saves | Partial loads, optimistic conflicts, out-of-order responses, edits during save, transaction rollback, unknown commit outcome, delayed index publication |
| History and comparisons | Reverse add/delete/re-add, version gaps, independent table revisions, duplicate-key suffix assignment, ignore-case/whitespace, large result paging |
| Diagrams/workbenches | Embedded revision vs current diagram, assets, fonts, overlap/layers, workflows, queries, links, undo, dirty state, saved missing targets |
| Lifetimes | Logical close vs WPF `Unloaded`, tab/canvas reparenting, timer/subscription cleanup, early stream exit, abandoned result jobs, shutdown/reconnect during I/O |
| Scale and responsiveness | Corpus-size growth with fixed active views, reader/connection counts, managed/decoded memory, SQL server load, priority inversion, cold/warm tail latency |

Use weak-reference/profiler checks to investigate retained closed controls, not an assertion that existing event handlers alone prove a leak. Test UI settle and collection separately from working-set release because the runtime may retain committed memory after objects become collectible.

## 20. Completion and Remaining Design Gates

The redesign is complete when the relational runtime no longer loads/saves global persistence roots, metadata requests exclude payloads, opening a target reads only its selected content, reference painting does no I/O, grids page real data, and searches/exports/history/secondary workflows remain compatible and bounded. Benchmark evidence must demonstrate these behaviours, not just smaller serialized documents.

Resolve these gates during the relevant prototypes:

- Production captured-layout/exception representation and its exact display/search formatting.
- SQL/.NET substring equivalence strategy for supported Unicode and collations.
- Grid random-position adapter versus reviewed explicit page navigation.
- Initial cache/worker/clipboard budgets and huge single-document behaviour.
- Consistent source-generation/search-result staging policy and SQL isolation deployment requirements.
- Legacy package compatibility requirements versus the new relational package format.

The implementation order is metadata and one-document access first, then references/search, grids, targeted saving, and all secondary workflows. This provides measurable improvements early without mistaking an unfinished access layer for a safe production conversion.

## 21. Source References

These implementation locations establish the existing behaviours and integration points; line numbers reflect the current working tree and will move during implementation.

- [Current SQL document load/save and initialization](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Storage/SqlServerDocumentStore.cs:38>)
- [Database metadata store contract](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Storage/IDatabaseMetadataStore.cs>)
- [Startup root loading](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:487>)
- [Scope loading](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:622>)
- [Current explorer search call](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:1106>)
- [Document open integration](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:8185>)
- [Clipboard hierarchy naming](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:8452>)
- [Logical window close integration](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:8506>)
- [Workbench capture and restoration integration](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:15619>)
- [Reference index rebuilding](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:17921>)
- [Per-file reference style creation](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:18051>)
- [Whole-library save and shutdown](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs:18430>)
- [Explorer hierarchy and search rules](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/FileTreeService.cs:88>)
- [Current literal and regex matcher](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/FileTreeService.cs:1182>)
- [Definition indexing](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/ScopeReferenceIndexService.cs:27>)
- [Reference resolution rules](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/ScopeReferenceIndex.cs>)
- [Synchronous editor colorizer](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Controls/ReferenceHighlightColorizer.cs>)
- [Database document rendering](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/DatabaseDocumentService.cs>)
- [Grid loading, filters, Copy and Export](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Controls/FloatingSpreadsheetWindow.xaml.cs>)
- [Grid visual virtualization and editing](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Controls/FloatingSpreadsheetWindow.xaml:79>)
- [Session row mutation](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/CsvGridRow.cs>)
- [CSV parsing rules](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/CsvGridParser.cs>)
- [Resource comparison and duplicate-key handling](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/ResourceComparisonService.cs:1109>)
- [SQL trace dependency expansion](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/SqlTraceService.cs:21>)
- [Link target catalogue](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/LinkableResourceService.cs>)
- [Existing dependency set](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Surf2.csproj>)
