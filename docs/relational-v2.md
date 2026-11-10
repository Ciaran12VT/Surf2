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

**Add Database** and **Edit Database** preserve master's query-copy, clipboard import, and individual-entity editing window. Opening that explicit editor materializes the selected snapshot. Saving publishes a new revision with concurrency checks and retained history. **Capture from SQL** is a separate direct capture workflow with per-table TOP or WHERE selections.

V2 history saves an immutable baseline of the prior metadata collections in the same transaction as each publication. It retains exact selection predicates and column/key ordering while referencing the existing immutable bodies and captured rows. Earlier migrated history continues to replay master's reverse changes, including the current TOP/WHERE selection merge rules. Baselines are included in V2 package exports.

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
```

The script builds the solution and runs each test process sequentially, retaining detailed logs under the test output's `verification-logs` directory. Its `master` suite covers inherited editor, search-link, scrollbar, image-swap, pending-reference presentation, capture-query behavior and paged-grid selection. The `catalogue` suite checks 1,000 procedures, duplicate aliases, query-count bounds, concurrent cancellation and raw UTF-16 text. The `storage` suite checks migration, SQL image reopening, concurrent asset deduplication, snapshot editing, exact history and package roundtrips. The `capture` suite exercises TOP, WHERE, empty captures, and source preservation. The `workspace` fixture opens a migrated database in an offscreen WPF host and checks availability before and after background loading, navigation, and mixed physical/SQL search; it writes loading and ready screenshots under the test output directory. The `compatibility` suite covers the inherited migration, local/ZIP import, export, recovery, raw text and SQL capture paths.

Implementation verification passed: a build with zero warnings/errors, 18 master regression groups, 19 catalogue checks, 27 storage/editor checks, 6 capture checks, 22 migrated-workspace checks, and 3,875 compatibility checks. Separate grid provider and binding suites passed 4,790 and 2,026 assertions respectively. These checks use synthetic, isolated fixtures; no user database was migrated or benchmarked.
