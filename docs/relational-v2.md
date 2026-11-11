# Relational V2 architecture and migration

`relationalDb_V2` starts from master `9d068d2`. It reuses the relational storage, migration, history, and paged-grid foundation from `RelationalDB`, with a new catalogue and in-memory reference path. Its database format identifier is `Surf2.RelationalV2`; the earlier experimental relational database is a separate format.

## Opening a scope and working while it loads

The application reads the scope list, the selected scope, and a lightweight catalogue of its loaded database snapshots. The catalogue contains resource identities, names, kinds, immutable revision keys, and table summaries. It excludes procedure bodies, captured data rows, and image bytes.

| Operation | V2 behavior |
| --- | --- |
| Expand Stored Procedures | Build children from the loaded catalogue. SQL checks verify the selected domain is still current; there is no query per child or catalogue page. |
| Open one procedure | Resolve its stable identity from the catalogue, then read that definition. Opening does not enumerate sibling definitions. |
| Search names | Match catalogue names and enumerate physical file names without reading file bodies. Available immediately. |
| Follow a procedure or table reference | Resolve through an in-memory name dictionary populated from the catalogue. Table-column metadata is fetched in one batch. |
| Follow a method in another source file | Wait for the background source parser to populate the in-memory definition index. |
| Search content | Scan physical files and current snapshot content concurrently. Database definitions are streamed in batches of at most 128, then matched with the existing .NET literal/regex matcher. |
| Open captured table data | Stream the selected capture into a disk-backed grid source; viewport pages, filtering, sorting, copy, and export use that source. |

Paged grids keep cell selection as a contiguous rectangle or the complete result. Mouse drag, Shift selection, and Ctrl+A do not create an object for every selected row. Ctrl+C reads the selected range from a pinned query, with a clipboard-size limit and cancellation. Toolbar Copy and Export continue to operate on the complete filtered result. Additive, disjoint Ctrl-click selections are not supported in the paged grid.

The search dropdown starts on **Name** and remains disabled until the initial reference load succeeds. A compact Object Explorer footer displays discovery/loading progress and offers retry after incomplete loading. Potential file-internal references appear light grey while pending; known database references retain their normal highlighting. A scope switch cancels work belonging to the previous scope. File changes trigger a debounced background rebuild.

Content search uses bounded SQL streams instead of `LIKE '%text%'`. This preserves master's ordinal case-insensitive and regex matching behavior without depending on the database collation, and removes the former sequence of one text fetch per procedure. The database still sends all candidate definition text for a content scan. Captured cells and generated table definitions retain their own search sources. V2 does not preload every snapshot body into a permanent text cache.

The source parser retains reference entities in memory, not entire source files. Individual source files are bounded at 32 MiB for parsing, and scope discovery is bounded at 100,000 physical files. Failed files remain visible in the loading status. These bounds and selected-state checks prevent a failed or changed source from silently becoming a complete index.

The scope resource toolbar has one **Add** menu: **New** and **Existing**, each offering **Folder**, **File**, **Database**, and **Diagram**. New Folder/File browses the filesystem to register a reference; Existing Folder/File reuses a saved reference from another scope. New Diagram creates an empty saved diagram, and Existing Diagram/Database attaches a shared saved resource. Only the selected resource row shows a blue pencil followed by a red X. The pencil edits a database or the other resource's scope alias; the X asks for confirmation before removing that membership, retaining the underlying resource.

Adding or editing a database starts with three source choices: **SQL statement generated** (default), **Direct connection**, and **Surf2 export folder**. Generated SQL preserves master's query-copy, clipboard import, and individual-entity editing window. Direct connection captures SQL metadata and optional table rows with per-table TOP/WHERE selections. Adding creates a new snapshot; editing keeps the selected snapshot identity fixed. Opening the explicit snapshot editor materializes that selected snapshot. Saving reuses unchanged immutable resources and publishes actual changes with concurrency checks and retained history. A no-op save creates no history version; a name-only edit updates the snapshot header. Metadata ordering, orphan children and capture-selection changes still receive exact history baselines even when no rendered file changes.

Export-folder import accepts extracted master named-folder exports and V2 `resources` exports. It reads definitions, table descriptions and CSV data without executing SQL or changing the export folder, then shows format limitations and the normal content editor before saving. These are display exports: original history, capture timestamps, TOP/WHERE predicates and some metadata are absent, while CSV cells preserve displayed strings rather than original JSON types or NULL distinctions. Malformed or ambiguous exports are rejected. Import is bounded at 32 MiB per file, 128 MiB total, 100,000 files and 1,000,000 CSV rows; a CSV may have at most 128 columns.

V2 history saves an immutable baseline of the prior metadata collections in the same transaction as each publication. It retains exact selection predicates and column/key ordering while referencing the existing immutable bodies and captured rows. Earlier migrated history continues to replay master's reverse changes, including the current TOP/WHERE selection merge rules. Baselines are included in V2 package exports.

Database history retains master's expandable five-column list, Find box, and version/file context menus. File details and line counts load when needed; View, Compare With, Restore and Restore To use selected relational revisions. A snapshot without saved versions displays an initial row from a read-only projection, without writing history just to open the window. Object Explorer retains master's menu labels, order and separators; database editing and capture remain in the scope resource editor.

Historical reconstruction reads a bounded identity plan and replays legacy changes using ordinal case-insensitive lookups in memory. It then bulk-writes the final temporary projection. The plan contains metadata only, capped at 100,000 entries and 32 MiB; larger histories retain the streaming SQL fallback. Point selection and batched table-child reads replace repeated collection scans. File lists retain their issued version/resource locators, so choosing a listed file does not reconstruct the version again.

Version rows appear before initial-file totals finish loading. Expanded-file line counts run independently of View and Compare actions, with cancellation and head validation. Procedure counts read up to 128 selected revisions per SQL batch, bounded at 32 MiB in aggregate and by the existing individual-file limit. Identical immutable content needs no definition read. Table/CSV counts share at most one current and one preceding projection. Closing or reloading drains pending work; restore actions cancel counts before publication.

Database comparisons build one bounded metadata catalogue per side and reuse one historical context per selected version. Matching procedure content identities skip body reads; differing definitions are batched and normalized with the existing comparison options. Small collection results remain in memory within the configured sort budget. Larger results and row-level table diffs retain disk storage, with an index per active filter so paging no longer rescans the complete result. Scope comparison choices reuse the already-loaded resource catalogue. Selected-head checks still reject stale results.

These infrastructure changes require no schema migration or rebuild of an existing V2 database. Previously recorded redundant Modified rows are retained; new publications record actual changes only.

## SQL image storage

`surf.Asset` holds immutable binary image bytes, content hashes, media types, and lengths. Library entries and diagram/workbench occurrences refer to those assets. Replacing one image updates that occurrence; historical versions and other occurrences retain their previous bytes. Identical content can share storage.

Canonical Base64 strings are reconstructed only for existing in-memory UI models and legacy export compatibility. They are not duplicated in the canonical SQL text fields. Noncanonical legacy strings are retained when necessary for exact migration preservation. Diagram restoration batches asset reads, and the image chooser reads available image definitions and payloads together.

Clipboard images in a migrated database reopen from SQL without the original user's AppData folder. A future hosted service can serve these asset identities after authorization; desktop V2 remains a Windows WPF client with SQL access, rather than a complete cloud deployment.

## Migrating a master database

1. Start V2 with the current master-style SQL database selected. Accept the prompt to migrate to a separate database.
2. Choose a distinct destination name. The default adds `_RelationalV2`. Create a new database, select an existing empty database, or resume an interrupted migration using its saved staging directory.
3. Run migration on a machine with access to the original pasted-image files under `%APPDATA%\Surf2\PastedDiagramImages`. The utility freezes the six legacy documents and referenced image files before conversion.
4. Allow conversion and validation to finish. Progress includes the current phase, counts where known, elapsed time, and the recovery log path.
5. Choose **Done** after validation. The application opens the validated destination and separately offers to remember that connection for future starts.

The migrator checks physical database identities and never overwrites the source. It stages snapshot objects, table metadata and captures, reverse history, scopes, diagrams, workbenches, settings, images, and saved document bindings. Validation checks preservation and content hashes before committing the destination's `Ready` marker. Incomplete destinations cannot be opened as usable application databases. Recovery uses the matching staged manifest and journal; the source and frozen assets are rechecked before publication.

The source must remain unchanged during a migration or resume. If an original pasted PNG is missing, migration uses preserved inline or library image bytes when available. If no usable bytes remain, migration stops before publication; it cannot recover missing content. Local legacy storage and legacy ZIP packages are also supported by the migration service and package workflows.

## Validation commands

Build on Windows with the .NET 10 SDK. SQL integration fixtures use isolated `Surf2_Regression_*` databases on `(localdb)\MSSQLLocalDB` and remove their own databases afterward.

```powershell
./tools/verify-relational-v2.ps1
# Or select suites; -SkipBuild uses an already-built test runner.
./tools/verify-relational-v2.ps1 -Suites catalogue,storage,capture,workspace
# Focused resource-management and export-folder checks after building:
dotnet Surf2.RegressionTests/bin/Debug/net10.0-windows/Surf2.RegressionTests.dll --scope-ui
dotnet Surf2.RegressionTests/bin/Debug/net10.0-windows/Surf2.RegressionTests.dll --resource-management
dotnet Surf2.RegressionTests/bin/Debug/net10.0-windows/Surf2.RegressionTests.dll --export-folder
dotnet Surf2.RegressionTests/bin/Debug/net10.0-windows/Surf2.RegressionTests.dll --history-file-rows
dotnet Surf2.RegressionTests/bin/Debug/net10.0-windows/Surf2.RegressionTests.dll --history-performance
dotnet Surf2.RegressionTests/bin/Debug/net10.0-windows/Surf2.RegressionTests.dll --snapshot-publication
dotnet Surf2.RegressionTests/bin/Debug/net10.0-windows/Surf2.RegressionTests.dll --runtime-comparison
```

The script builds the solution and runs each test process sequentially, retaining detailed logs under the test output's `verification-logs` directory. Its `master` suite covers inherited editor, search-link, scrollbar, image-swap, pending-reference presentation, capture-query behavior and paged-grid selection. The `catalogue` suite checks 1,000 procedures, duplicate aliases, query-count bounds, concurrent cancellation and raw UTF-16 text. The `storage` suite checks migration, SQL image reopening, concurrent asset deduplication, snapshot editing, exact history and package roundtrips. The `capture` suite exercises TOP, WHERE, empty captures, and source preservation. The `workspace` fixture opens a migrated database in an offscreen WPF host and checks availability before and after background loading, navigation, and mixed physical/SQL search; it writes loading and ready screenshots under the test output directory. The `compatibility` suite covers the inherited migration, local/ZIP import, export, recovery, raw text and SQL capture paths.

Implementation verification passed: a build with zero warnings/errors, 18 master regression groups, 19 catalogue checks, 27 storage/editor checks, 6 capture checks, 22 migrated-workspace checks, and 3,875 compatibility checks. Separate grid provider and binding suites passed 4,790 and 2,026 assertions respectively. These checks use synthetic, isolated fixtures; no user database was migrated or benchmarked.

The resource-management update passed 18 scope UI scenarios (light/dark themes and multiple window sizes), 9 resource-management SQL checks, the expanded 31 workspace/database-source checks, and 37 export-folder checks. New historical exports include object-kind metadata so empty definitions remain importable; older historical exports continue to infer object kind from a parseable SQL declaration.

The master history/menu restoration passed 94 migrated-workspace checks, 53 focused history checks, 27 storage checks and all 18 inherited master regression groups. History and file-picker XAML match master apart from their class names. The workspace checks compare expanded history rows and Find results against the original master window, verify Explorer menus for 16 node states, and render history/picker screenshots. History checks cover exact line counts and CSV text, deleted files, snapshots without history, bounded batched reads, stale heads and cancellation cleanup.

The history/comparison performance update was validated with a zero-warning/error build, 759 storage/capture checks, 113 SQL comparison checks, 96 migrated-workspace checks, 53 history file-row checks, 27 V2 storage/editor checks, 23 publication checks, and all 18 inherited master groups. The comparison component also passed 57 algorithm checks, 46 scratch-file checks and 18 WPF lifecycle checks. The new projection/batching checks exercise 500 objects and 1,000 historical changes, exact raw UTF-16 reads, missing/forged locators, read limits, cancellation and stale heads. A 100-modified-procedure line-count batch stays within 40 SQL commands, compared with the diagnosed 1,600. The UI fixture holds a line-count SQL request pending and successfully invokes View before releasing it.

The 261-procedure comparison fixture uses 108 SQL commands for an identical current snapshot, 132 for differing current snapshots, 146 for historical/current, and 156 for identical historical versions. Identical comparisons read no procedure bodies or captured rows; the changed-current fixture reads its four differing definitions in one batch. These counts include readiness and identity checks. Mixed-history results match master, including duplicate names, root children and captured-data companions. No test connects to a user database.

## History/comparison measurements (2026-11-11)

The same generated LocalDB workload was run before (`c1c2e2a`) and after this update: 500 small stored procedures, three versions, all procedures changed in each later version, and a separate identical current snapshot. These are single instrumented service-level runs in a Debug build with pooled local SQL connections and partial warmup. Migration, application startup, UI rendering and master's original JSON load are excluded. They demonstrate the reduction in repeated SQL work, rather than a timing guarantee for other databases.

| Operation | Before | After | SQL commands before → after |
| --- | ---: | ---: | ---: |
| History metadata and initial file list | 15.264 s | 0.261 s | 9,050 → 58 |
| Expand latest version's file metadata | 7.844 s | 0.169 s | 4,574 → 77 |
| Count lines for 500 modified procedures | 3.580 s | 0.189 s | 8,000 → 128 |
| Compare identical current snapshots | 2.130 s | 0.125 s | 4,134 → 88 |
| Compare historical versions | 15.691 s | 0.405 s | 15,104 → 167 |
| Open one oldest-version procedure | 27.673 s | 0.120 s | 18,060 → 34 |

The history-metadata measurement deliberately retains the old workload, including the complete initial file list. The updated window can display its collapsed version rows before that work. Historical opening includes issuing the selected target, opening its projection, locating it and reading the text.

The 100-procedure/full-change case improved historical comparison from 1.542 s to 0.144 s and historical opening from 2.474 s to 0.033 s. The first small/sparse case did not improve every timing: initial metadata took 0.321 s versus 0.251 s, and a single changed file's counts took 0.071 s versus 0.030 s with the same 16 SQL commands. Fixed costs and first-use variation remain; the substantial gains are in removing work that grew with resource/change counts. The already-resident master model remains faster for many operations.

A real editor publication within the 100-procedure fixture changed one definition. It now records one history change rather than 100; counting that new version fell from 1,600 SQL commands/0.785 s to 16 commands/0.011 s. Existing redundant historical records are retained. Captured-row table comparisons and histories that exceed the metadata buffer still use bounded streaming/disk fallbacks and are not represented by the procedure-only timing table.
