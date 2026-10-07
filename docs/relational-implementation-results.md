# Surf 2.0 Relational Implementation Results

Date: 6 October 2026.

SQL Server relational persistence and the application query layer are implemented in the working tree. Storage verification preceded application rollout. The integrated solution builds with zero warnings and zero errors. Functional verification uses disposable LocalDB databases and owned files, not the user's persistence database. No user database has been migrated, selected, or modified by these tests.

The functional implementation is available for migration trials. Production qualification still requires representative-corpus benchmarks, native UI/accessibility testing, and SQL plan review. A smaller retained managed graph is not, by itself, evidence of a particular reduction in Task Manager memory.

## Storage and Migration

The five embedded SQL scripts define independently addressable scopes/resources, immutable snapshot revisions and reverse history, captured layouts/rows/exceptions, diagrams and image assets, workbenches, document/reference bindings, settings/session state, and derived search/reference indexes. Individual code documents and image bytes remain content values; collection-sized JSON documents are no longer the application storage unit.

Migration maps all six legacy documents and their supported local fallbacks. It freezes source data and pasted images, preserves typed identities and original ordering, validates the destination, and only then publishes its Ready marker. It supports recovery and idempotent completed verification. Unknown schemas, incomplete migrations, ambiguous identities, and failed validation cannot become a normal application runtime. The original database and source files remain unchanged.

Diagram assets, including supported legacy pasted PNGs, are ingested into SQL. Occurrence-specific image bindings survive editing, undo, clipboard cloning, and workbench copies. Filename fallback metadata uses weak references so it cannot retain orphaned image byte arrays. External source files and folders remain external authorities; normalization does not make those files part of a SQL backup.

Both legacy format-1 and relational format-2 persistence packages can be imported into a fresh destination. A package is held unchanged during inspection/import, checksummed, validated against the schema, and published without silently changing the current connection. Relational packages exclude local connection settings, operational logs, migration journals/staging, scratch data, and unpublished units. Format 2 preserves raw UTF-16 code units; legacy format export explicitly rejects text that its representation cannot preserve rather than silently replacing it.

Implementation entry points:

- [Schema](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Storage/Relational/Schema/001.Core.sql>) and [selected runtime](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/RelationalRuntime.cs>).
- [Format detection](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Storage/Relational/PersistenceFormatProbe.cs>) and [migration coordinator](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/Storage/Relational/Migration/RelationalMigrator.cs>).
- [Application startup and migration choice](<C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.RelationalPersistence.cs>).

## Application Queries

Startup reads preferences/session state and the selected scope, not the global snapshot, diagram, or workbench libraries. Explorer expansion requests metadata only. Opening code fetches the selected immutable definition; clicked alias membership and resource identity are retained even when names or aliases are duplicated. TXT filenames use the clicked hierarchy without querying SQL during clipboard-name lookup.

References use a compact published symbol catalogue. Editor painting performs no I/O. Background indexing reconciles changed sources; navigation validates current coverage and selected revisions before fetching a target. Search uses scoped queries and bounded scans where required to preserve literal substring and .NET regex behavior. An incomplete scan or stale index is reported as such, not as a complete empty result.

Captured tables and physical CSVs use streamed disk staging, bounded provider pages, a positional WPF viewport, and a separate sparse edit overlay. The selected capture streams once rather than rerunning SQL for each painted page. Filter/sort output is disk-backed; table edits do not modify the captured SQL rows or source CSV. Copy/Export include all matching rows and visible headers/column order, not just the resident viewport. Logical close drains providers; tab/canvas reparenting does not discard edits or dispose a document.

Settings, scopes, diagrams, workbenches, and session saves use selected-owner transactional commands with expected revision tokens. Failed, stale, wrong-owner, or partial loads cannot replace unrelated entities. Capture/history/restore/copy, comparisons, SQL trace, link/portal selection, and persistence transfers use selected queries and operation-owned cancellation. Shutdown cancels and drains work, including cancellation callbacks, before retiring its owners.

No runtime NuGet dependency or DLL was added. The existing SQL Client, compiler/parser, diff, editor, and PDF dependencies remain; a separate regression project and embedded SQL resources were added.

## Verification

Final functional runs against the same clean production build:

| Gate | Runner options | Passed checks |
| --- | --- | ---: |
| Storage, migration, packages, selected state, bootstrap and dialog renders | `--storage --local --legacy-package --package-export --package-import --pasted-freeze --state-access --visual --bootstrap` | 4,516 |
| Access, scoped queries, saved bindings, trace, metrics, scratch, raw text and runtime state commands | `--access --query-integration --saved-targets --trace --metrics --scratch --raw-text --runtime-state` | 1,189 |
| Actual WPF MainWindow/DataGrid workflows | `--runtime-wpf` | 421 |
| Actual SQL capture/history/comparison and package transfers | `--runtime-capture --runtime-comparison --transfer` | 569 |
| Grid provider prototypes | `RunPrototypes.ps1` under `Services/RelationalGrid/Tests` | 4,786 |
| Positional binding model prototypes | `RunBindingPrototypes.ps1` under `Controls/RelationalGrid/Tests` | 2,026 |
| Isolated comparison, filesystem ownership, WPF lifecycle and styles | `RunPrototypes.ps1` under `Services/RelationalComparison/Tests` | 116 |
| Bounded storage benchmark with 20,000 captured rows | `--performance` | 92 |

Counts are per gate, not a count of unique scenarios; some shared checks run more than once. SQL fixtures verify that their legacy/source databases remain unchanged and clean up only validated generated test targets. WPF uses actual controls/events in offscreen HWND hosts, not native screenshots or an unattended production launch. Light/dark desktop/narrow grid images and smaller capture/history dialog images were inspected. Native clipboard delivery and SaveFileDialog interaction are deliberately not exercised; their post-dialog workers and full output envelopes are tested with owned destinations.

The runner accepts no production connection argument for these gates. Build the solution, then run each options group in its own process, for example:

```powershell
dotnet build .\Surf2.slnx
dotnet run --project .\Surf2.RegressionTests --no-build -- --runtime-wpf
```

Fresh run logs are under `C:\Users\ciara\source\repos\Surf\build-check`: `storage-final.log`, `query-final.log`, `runtime-wpf-final.log`, `secondary-final.log`, `comparison-pure-final.log`, and `performance-final.log`. Provider/binding prototype logs are under its `relational-integration` subdirectory. Offscreen PNGs are under `relational-integration\runtime-wpf-checks` and `relational-integration\migration-visual-checks`. These are verification artifacts outside the Surf 2.0 repository, not runtime dependencies.

## Bounded Storage Benchmark

The final disposable LocalDB benchmark streams and verifies 20,000 heterogeneous rows with fixed row/byte budgets. Timings include fixture generation and validation, exclude the boundary full GCs, and are not application interaction latencies. The peak is a 20 ms sample of the process-wide live managed heap; retained deltas are post-full-GC estimates. Neither measures Surf working set/private bytes, SQL Server memory, or a guaranteed transient peak.

| Operation | Elapsed seconds | Sampled peak managed bytes | Retained managed delta bytes |
| --- | ---: | ---: | ---: |
| Write and seal | 12.343 | 10,688,984 | 27,648 |
| Paged read and validate | 54.779 | 6,498,720 | 41,256 |
| Stream read and validate | 8.117 | 5,837,488 | -25,904 |
| Stream CSV export | 7.828 | 6,076,040 | 79,304 |

The paged route performed 541 reads, with at most 37 rows/258,944 estimated bytes in a returned page. The export contains all 20,000 rows, is 3,628,583 UTF-8 bytes, and writes chunks no larger than 16,384 bytes. The capture table uses 20,750,336 bytes and reserves 31,080,448 bytes, including its indexes/LOB pages but excluding database/log files.

This fixture demonstrates bounded retained buffers and exact output, not a percentage memory improvement over the legacy application. Tiny SQL-page round trips are substantially slower here than a bounded stream, supporting the grid's selected-stream-to-owned-spool design rather than querying SQL on every scroll.

## Limits and Release Checks

Budgets fail explicitly rather than silently truncating data or evicting unsaved edits. Default grid limits include 8 cached pages/32 MiB provider payload estimates, a separate 512-item/32 MiB UI payload budget, 10,000 edited cells/8 MiB edit estimates, 16 MiB clipboard text, and 8 GiB owned disk staging per source. These are payload estimates and component limits, not an application-wide private-byte cap; open editors, active diagrams, undo, SQL Server, and concurrent operations consume additional memory.

The provider grid uses single-cell selection. Extended multi-cell/range selection and Select All do not materialize an unbounded WPF selection. Toolbar Copy/Export still operate on all matching data. This is a deliberate compatibility limitation requiring review before broad rollout.

Interactive DiffPlex comparisons reject pairs above 256 Ki characters or 20,000 combined newlines instead of creating an unbounded diff model. Large catalogues/pickers and individual documents also have explicit budgets; narrow the scope where a picker reports partial coverage. Physical-folder Git branch tooltip enrichment is not currently supplied by the relational metadata view.

Release checks still required:

- Compare legacy and relational cold/warm startup, searches, first code/grid display, deep scrolling, export, and realistic repeated workflows on a representative copy of the user's corpus. Record Surf private bytes/working set, GC retention, active readers, SQL Server memory/CPU/I/O, and tail latency separately.
- Review actual SQL execution plans and tune indexes/budgets using that workload. Small SQL pages and exact substring/regex scans can be slower than resident-memory scans; selective loading trades retained memory for work and I/O.
- Run native-window keyboard, selection, touch/accessibility, DPI, offline, disk-full, and longer leak/resource soaks. Offscreen functional tests do not certify those behaviors.
- Agree release thresholds before production rollout. No percentage reduction from the original 1.48 GB Task Manager screenshot is claimed.

Unpublished SQL stages/history and migration recovery files are retained for explicit recovery/retention decisions; there is no destructive automatic SQL-history pruning.

## Read and Write Boundaries

This section covers the new persistence/query path, not a complete security audit of all existing application commands.

| Location | Access |
| --- | --- |
| Selected Surf SQL database | Read selected metadata/content; transactional writes to application-owned state, snapshots, captures and indexes |
| Source SQL databases being captured | Read only; prefer enabled snapshot isolation, or obtain explicit blocking-capture consent for serializable reads; do not alter source database options |
| Legacy migration database, local fallback JSON and old pasted-image files | Read/freeze only; never replace or delete the originals |
| Scoped source files and folders | Read for browsing/indexing/opening; the new grid edit overlay does not write the source CSV |
| `%APPDATA%\Surf2\Migrations\<generated identity>` | Write frozen source/recovery staging and retain it for recovery |
| `%TEMP%\Surf2\relational-grid` and `%TEMP%\Surf2\Comparisons` | Write generated owned scratch files; clean exact registered paths; recover at most 128 validated non-live GUID directories older than two days |
| User-chosen export paths | Stream to a generated same-directory pending file, then atomically publish after success; failure/cancellation preserve an existing destination |
| `%TEMP%\Surf2\ClipboardText` and `%APPDATA%\Surf2\Traces` | Existing TXT clipboard-file and trace output locations; trace publication is atomic |
| `%APPDATA%\Surf2\connection-settings.json` | Existing local bootstrap selection; saving a migrated destination for future starts requires a separate user choice |

Scratch recovery refuses redirected ancestors/payloads, unmarked/non-GUID directories, malformed ownership records, unknown files, and live leases. It never recursively deletes a calculated directory. Query/SQL metrics retain bounded aggregate records and hashes, not SQL parameters or content; existing optional application logging retains its separate configuration and paths.

## Post-Migration Scopes and Reference Indexing

The relational Scopes picker restores the legacy **Add Scope Resources** label instead of **Merge Scope**. Adding another scope's resources and virtual folders already existed in the legacy application; the relational implementation preserves the source scope and uses identity-aware target/link mapping rather than guessing from duplicate original IDs.

The managed picker has a 220-pixel scope list and flexible resource details. Its Add/Edit commands fit one row at the supported 1,000-pixel minimum width; reduced client areas can wrap and scroll without hiding commands. The resource grid shows **Alias**, **Type**, **Path/Resource**, and **Added at**. The old display bound its timestamp to `AddedAtUtc`, not a resource modification timestamp; the new heading makes that distinction explicit. An empty alias still falls back to the resource's display name. The grid retains a finite, virtualized viewport and theme-aware selected-cell contrast.

`IncludeChildren` was already a stored resource property. It controls whether a folder's descendants are offered as link/portal targets, not whether background reference discovery recursively scans the folder. It is no longer an unexplained **Children** grid column; a selected folder has an **Include children** option with a tooltip. Other resource types retain their stored metadata without offering an ineffective edit. The bottom-left arrows page through the bounded scope catalogue (100 entries per page), with Previous/Next tooltips and accessibility names. They are hidden when no previous/next page exists.

Scope-switch dirty detection now compares the actual loaded/restored workspace with its own baseline, instead of treating a missing/different saved default Workbench as an edit. Successful restoration and publication establish a new baseline after deferred viewport restoration. Genuine resource metadata, virtual-folder membership, loaded-resource selection, layout, filters and diagram changes retain confirmation; cancelling or failing a save does not acknowledge them. Session table-cell overlays have a separate discard warning because saving a Workbench does not persist those cell edits.

**Derived reference indexing** is a post-migration background catalogue build for reference highlighting/navigation, not another database migration. The first run still needs to inspect and publish the selected sources. The worker now skips nested `.git`, `.vs`, `bin`, `obj`, and `node_modules` directories, matching legacy reference discovery; explicitly scoped files and directory roots remain eligible. Captured snapshots/diagram revisions shared by aliases are enumerated once, and unchanged captured definitions reuse published metadata without loading their bodies. Obsolete memberships are pruned in bounded batches only for successfully enumerated owners; another scope's memberships are preserved. Explicit content search retains its existing generated-file scan behavior.

The status identifies the current resource/document and phase, elapsed time, indexed/reused counts and the last failure reason/resource. A one-second elapsed-time update does not pretend that completed counts advanced. Finished failures are reported as incomplete rather than leaving an endless indexing message. **Unloaded resources** are deliberately excluded by the workspace selection, not failed reads. A successful exact loaded-resource view can now become reference-ready without declaring excluded sources globally indexed: its bounded readiness proof is specific to the runtime, selection and catalogue/domain versions. A changed selection, fresh runtime, failed source or arbitrary document subset cannot reuse that proof. No reliable total or ETA is invented for an undiscovered corpus.

The scope-usability fixes did not alter the original relational schema, migration checksum/converter, recovery format or checkpoint identities. The subsequent warm-start update below adds a separate derived-index extension, not a new migration. Deploy against the already migrated database; no repeat migration is required.

Earlier scope-usability verification, before the warm-start update: the solution built with zero warnings/errors; fresh-process `--scope-ui` passed 18 light/dark layout, edit, virtualization and pagination scenarios; `--scope-dirty` passed 53 actual runtime scope/workspace/grid-edit checks; `--scope-index` passed 100 checks including the baseline contracts, cold/warm refresh, batched pruning, loaded-view coverage, current-source content search, missing sources and cancellation. The existing `--access --query-integration --saved-targets --runtime-state` group passed 788 checks and `--runtime-wpf` passed 421. SQL gates used generated disposable LocalDB fixtures and verified that their legacy source payloads/timestamps remained unchanged; no user database or remote recovery directory was accessed. The five-source index fixture then issued 286 cold-refresh and 102 warm-refresh commands including identity/readiness probes. Logs are `post-migration-usability-build.log`, `post-migration-scope-ui.log`, `post-migration-scope-dirty.log`, `post-migration-scope-index.log`, `post-migration-query-workflows.log` and `post-migration-runtime-wpf.log` under `C:\Users\ciara\source\repos\Surf\build-check`. Scope PNGs are under the regression runner's `bin\Debug\net10.0-windows\scope-picker-visual-checks`. Native-window/DPI/accessibility testing and representative-corpus timing remain release checks.

### Warm Reference Startup

Unchanged captured databases and diagrams now reuse durable **resource completion checkpoints**. A restart reads bounded root/dependency metadata, not every document's published state and membership. Alias configuration, source rowversion heads and parser/renderer policy must match. Derived document, membership and locator changes invalidate checkpoints transactionally. Current source pointers are checked through indexed rowversion heads without adding triggers to authoritative tables, whose existing save statements use SQL `OUTPUT`. Failed or incomplete owners do not acquire a successful checkpoint.

Changed captured resources still reconcile their metadata, but existing document states and membership differences are handled in bounded page batches. Only changed memberships are replaced; another scope's ownership is preserved. Body fetching and parsing remain selective. The first run of this updated build establishes these new checkpoints, even when the older document index already exists; subsequent starts reuse them. Editing a source or relevant alias invalidates only the corresponding owner group.

Reference painting and navigation share one bounded, streamed active-view metadata catalogue. It retains names, target addresses, kinds and overload information, not code bodies, image bytes, table rows or parsed syntax trees. Navigation still checks session/domain/index generations, but does not rescan the scope or query symbols on every click. The default candidate character budget is 16 Mi characters; row and individual-value limits remain explicit. Over-budget metadata fails rather than silently truncating candidates.

Captured targets become usable while physical files reconcile in the background. During that interval, status and reference menus identify verified-only coverage; a missing captured target does not imply that no file target exists. Unrelated file writes can advance the global catalogue generation: the captured view is rebased only after its root proofs and domain versions are checked, without reloading its immutable target/name metadata. File watchers coalesce changes and reconcile affected root groups, including overlapping aliases. A restart or scope reopen always performs background file reconciliation: persisted file checkpoints and timestamps are not proof that files were unchanged while Surf was closed. Missing roots, watcher failures and overflow remain visible/incomplete; unavailable watchers have a five-minute fallback and always join subsequent targeted reconciliation. Even a readable unwatched root remains excluded from the verified reference view without changing the user's loaded Explorer selection. A watcher error reconciles its affected roots, not every captured definition. Explicit content search keeps current-source fallback behavior and does not apply the reference index's generated-directory exclusions.

The progress clock covers checkpoint checks, discovery, publication, metadata loading and final highlight-style preparation. Its displayed elapsed time is monotonic across captured and file stages, including the terminal message. Reused resource checkpoints are distinguished from documents actually checked in this run.

The extension is `002.ReferenceCompletion.sql`, installed transactionally under its own version/checksum/application lock in an already **Ready** database. It creates only derived completion tables, an indexed source-version access path and derived-index invalidation triggers. Original `001` scripts, format version, converter, recovery manifest, fingerprints and migration checkpoint identities are unchanged. Installation requires the corresponding schema/table/index/trigger permissions in the selected Surf database. It does not access the old recovery directory, write source files, or add a disk cache. Application Export Database packages continue to carry authoritative data, not these rebuildable checkpoints.

Disposable LocalDB evidence: a fresh-session restart with **11,676 captured documents** verified checkpoints and prepared reference metadata in **1.263 seconds**, using **55 SQL commands**, including mandatory identity/readiness checks. The three-document fixture used the same 55 commands and took 0.932 seconds. No captured definition body, per-document membership transaction, captured enumeration or derived publication occurred on either warm path. Four three-token cached resolutions plus one highlight lookup took 0.225 seconds/20 constant-size guard commands in the large fixture. A two-folder fixture reconciled one changed folder and reused the other in 1.481 seconds/83 commands; reopening detected a file edited while closed. A 514-root fixture reused all roots in two bounded proof batches, correctly counting three unique documents across aliases.

These are generated-fixture observations, not a measured comparison against the user's legacy build or nine-minute workload. Acceptance on the actual device remains: compare legacy/current cold and warm startup, captured-reference readiness, complete file readiness, scope switches, navigation and text search on identical corpus copies; record median/tail latency, Surf/SQL Server memory, CPU and I/O separately. Require unchanged captured startup and repeated interaction to meet or beat the measured legacy baseline, with no stale targets, hidden failures, lost aliases or authoritative negative file results during reconciliation. No percentage memory reduction or universal faster-than-RAM claim is made.

The regression project and application built with zero warnings/errors; `git diff --check` passed. The warm-start/metadata/batch gate passed 170 checks, including source-pointer/alias/policy changes, failure repair, generation rebasing, 514-root batches, overload/Unicode compatibility and changed-only membership repair. The file-watcher gate passed 29 checks, the existing scope-index/access/query/saved-target/runtime-state group passed 817, the actual offscreen WPF gate passed 451, and scope-switch dirty detection/grid-overlay preservation passed 53. WPF checks include a fresh-coordinator warm reload, blocked physical discovery, real captured-target navigation after an unrelated generation change, readable-but-unwatched provisional coverage, delayed final highlight publication and cancellation/draining. SQL checks used generated disposable LocalDB fixtures, not a user database or remote recovery directory. Verification logs are `reference-fast-build.log`, `reference-fast-warm-start.log`, `reference-fast-watch.log`, `reference-fast-workflows.log`, `reference-fast-runtime-wpf.log` and `reference-fast-scope-dirty.log` under `C:\Users\ciara\source\repos\Surf\build-check`.

### Startup Failure Diagnostics

Workspace/scope restoration no longer reads the derived reference catalogue before displaying the selected scope. The existing tracked, cancellable background job loads and publishes reference metadata separately; an empty initial catalogue is not a readiness proof. Core saved-state read failures still disable saving, and retire background reference work so it cannot overwrite the failure status. An unused startup scope-summary read was removed.

Internal logging starts enabled before database access. New settings and missing legacy runtime logging preferences default to enabled; a persisted false value is applied immediately after preferences are read, before other relational state reads. Logs use the existing `internal-logs` directory at the solution/project root or application directory, falling back to `%LOCALAPPDATA%\Surf2\internal-logs` (and the temporary directory if no candidate is writable), not Roaming AppData. Existing persisted preference values are not rewritten. The frozen migration reader retains the historical omitted-value default, preserving the meaning of already journaled settings during recovery retries; the schema, checksum and recovery format are unchanged.

Startup progress/failure messages identify the active operation. When logging is enabled, a failure also writes the latest sanitized report to `%LOCALAPPDATA%\Surf2\diagnostics\startup-failure.log`. It contains the phase, elapsed times, SQL error codes and method call stack, but no exception messages, connection properties, SQL text/parameters, file paths or document contents. Selected-state failures now preserve the original SQL call stack when propagated. An intentional window-close cancellation does not write a failure report.

The isolated `--startup-resilience` gate passed 25 checks, including real SQL locks: a blocked reference catalogue leaves startup/saving available, while a preference-read timeout keeps saving disabled and preserves its original SQL stack and saved preference revision. Tests cover runtime defaults, persisted opt-out, frozen migration defaults and report redaction, using disposable LocalDB databases and owned temporary files only. The existing `--runtime-wpf` gate passed 451 checks, `--scope-dirty` passed 53, and the application/regression build completed with zero warnings or errors. This does not identify the timed-out query on the migration machine; reproduce there with the updated build and inspect the named stage and log before attributing the failure to indexing, blocking or SQL Server load.

### Verified Reference Navigation

Double-click navigation, previews and reference menus now resolve current positive targets even when unrelated documents or discovery are incomplete. The former whole-scope readiness check could reject an already indexed captured table merely because another source failed. The new explicitly coverage-bearing lookup filters out stale, failed and unindexed candidates before applying name/overload ranking. Authoritative whole-scope resolution retains its strict readiness contract.

Navigation still checks runtime/scope/catalogue generations, loaded-resource membership, the selected symbol's revision, the current captured-source revision or physical-file fingerprint, and request ownership before displaying content. Incomplete coverage is shown in choice menus and preview/navigation status; a missing match is explicitly inconclusive. Clicking before any catalogue is available now reports that indexing is not ready rather than silently returning. Lookup reuses the bounded active-view metadata cache, not an eager code-body load or a whole-scope scan on each click. Captured table navigation continues to open related data through the paged grid presenter.

SQL failures caught within source discovery/reconciliation/publication now record their exception stack and operation in the existing configured internal log. This does not diagnose or repair the separate `SQL_-2` timeout on the migration device; those logs are needed to identify the failed SQL operation and its cause. No schema, migration/recovery format or user database is changed by this navigation update.

The application/regression build completed with zero warnings or errors. `--reference-navigation` passed 76 checks for cached current positives, stale-target exclusion, provisional discovery/subset coverage, unloaded-resource exclusions, generations, cancellation and budgets. `--runtime-wpf` passed 467 checks, including actual table preview/navigation and related paged-data opening while another document's index is stale, inconclusive misses, early-click feedback and restoration for subsequent workflows. SQL checks use disposable LocalDB fixtures; the migration machine and its database were not accessed.

### Supplied Backup Verification (2026-10-07)

Tested `sample-db/Surf2_20261007_020320.zip`, containing the migration device's `Surf2_20261007_020320.bak`. The SQL Server 2025 backup was restored into a newly created, private SQL Server 2025 LocalDB instance and a generated test database. No existing SQL instance was upgraded, no normal bootstrap connection was used, and the original archive was not modified. Archive SHA-256: `EBF01A408B2ECC320996A59BF441EF0EA0387EE7AC131CE088082CDDD7FADC49`.

All scoped physical paths in the restored copy were redirected into an owned temporary directory before application startup. One scenario deliberately used missing paths; others reconstructed dummy source trees from the already persisted indexed text. Original machine-local files were never accessed. The saved Tempest scope and its intentionally unloaded NexusDev selection were preserved for the actual MainWindow scenarios. Separate captured-only checks exercised all four scopes, including normally unloaded captured databases, using production runtime limits.

The real corpus exposed a **local index-generation race**: physical-file publications could repeatedly invalidate a captured-reference request between resolution and display. The four-retry rebasing loop alone was insufficient. Index mutations and foreground reference operations now share a short-lived asynchronous publication gate. Navigation releases it for dialogs and content fetching, then revalidates the chosen symbol/source before publishing the window. The shared-open provider owns its final publication lease; joined requests recheck cancellation, scope, window ownership and text identity after awaits. Rejected navigation cannot add reference lines or open related data. Cross-process SQL/domain/source fences remain active; this does not waive stale-target checks or lock the entire background refresh.

Progress callbacks run outside the publication lease and source-failure handler. A cancelled callback therefore cannot incorrectly mark a successfully reused document stale. No schema script, migration checksum, recovery manifest or checkpoint identity changed in this follow-up.

| Supplied-dataset scenario | Result |
| --- | --- |
| Missing physical roots | Expected incomplete-file status; the captured table still previews and opens; no SQL command failures |
| First redirected physical reconciliation | 468 files checked and indexed; captured checkpoints reused; navigation works during reconciliation |
| Edits while Surf was closed | 468 files checked, 351 changed files republished; a table preview and five real table opens succeed during background publication |
| Fresh-process unchanged restart | 22 captured roots reused; 468 physical files verified; 11,676 unchanged documents; zero republished documents |
| All four captured-only scopes | Full metadata coverage and a current table lookup passed for each; fresh coordinators checked/rebuilt zero documents |

The screenshot case was exercised through the actual MainWindow resolver, preview and navigation handlers: `NexusLive / dbo.sproc_batchQs` opens `dbo.r_Zapp_Shifts`, plus its `calc_details`, `messages`, `recordIds` and `shift_parts` tables, before and after file reconciliation. Repeated cached lookup traces contain no reads of `SymbolDefinition`, `ResourceDocument`, code bodies, table-column payloads or assets. Source rowversions for saved scopes, preferences, workspace, workbenches, diagrams and captured catalogues remained unchanged by startup/navigation after the explicit test-only path setup. Legacy whole-library collections remained empty.

Final normal-pooling restart observations on this machine:

| Measurement | Observed |
| --- | --- |
| Core startup | 1.785 seconds |
| Captured references usable, from startup | 5.836 seconds |
| Background completion, including five navigation actions and highlight preparation | 32.838 seconds |
| Repeated reference resolution, median / p95 of 20 calls | 9.499 / 33.927 ms |
| First clicked table window open | 883.3 ms |
| Test-process managed memory / working set | 105.4 / 426.4 MiB |

The final edited-file stress run used disabled pooling deliberately: captured readiness was 8.141 seconds and full completion was 79.844 seconds, including the navigation actions. It republished 351 changed files with zero SQL failures. These are offscreen WPF test-process observations, not a legacy-build comparison, SQL Express/device guarantee, or standalone-app memory measurement. The original migration device's startup/reference `SQL_-2` was not reproduced by the supplied-database runs, so its cause is not established by this test.

The fixture runner initially hit `RESOURCE_SEMAPHORE` during tiny bulk imports when this machine had about 1.5 GiB free RAM and SQL reported `process_physical_memory_low=1`. Those were disposable-fixture setup failures, not failures of the restored dataset's startup/reference queries. Stopping the temporary sample instance and running the standard gates sequentially resolved that test-environment obstruction. A temporary cache-cap experiment on the private instance was reverted; existing server configurations were not changed. The two initial harness-limit failures were also corrected: synthetic 8 KiB document / 4 Mi-character metadata limits must not be applied to this real corpus; the all-scope harness now constructs the production runtime directly.

Final verification: application/regression build succeeded with zero warnings/errors; `--reference-warm-start` passed 191 checks, `--runtime-wpf` passed 469, `--reference-watch` passed 29, and `--startup-resilience` passed 25. Dataset modes passed 23 missing-root, 24 redirected-file, 25 changed-file, 24 restart, 24 pooled-restart and 19 all-scope checks; the changed-file and pooled modes were rerun successfully against the final build. Added checks cover publication-gate cancellation/idempotence, callback cancellation without stale marking, and stale-symbol rejection despite identical source text.

Evidence is retained under `C:\Users\ciara\source\repos\Surf\build-check`: `sample-final-build.log`, `sample-reference-warm-start.log`, `sample-runtime-wpf.log`, `sample-reference-watch.log`, `sample-startup-resilience.log`, and the `sample-database` directory containing per-scenario logs, timings and offscreen PNGs. The guarded regression entry point is `--sample-database <owned LocalDB connection> <matching temporary directory> missing|mapped|changed|restart|pooled|views`; it refuses ordinary application connections. After retaining evidence, the isolated database/instance and abandoned owned SQL fixtures were removed. Recursive file cleanup was blocked by the execution policy, so the owned extracted backup/dummy directory remains at `%TEMP%\Surf2_Regression_Sample_ef7e00e3c51f4a2e8170e7404b7c6101`; it is not configured application storage. The original archive remains in `sample-db`. No redeployment was performed.

### Reference Catalogue Timeout Follow-Up (2026-10-07)

The migration-device log `surf2-20261007-112337-19112.log` confirms a command timeout in `RelationalReferenceService.ReadMetadataAsync`, at `ExecuteReaderAsync`, while installing captured reference metadata. Initial workspace loading completed in approximately 1.3 seconds; the reference query failed about 30 seconds later. The 11,208 unchanged documents were reused successfully. Reference names/targets were not installed, so navigation remained unavailable. The log does not establish the server wait type or identify a blocking session. The pending Stored Procedures expansion has no corresponding failure recorded in that log.

A new isolated restore of the supplied backup exposed a wide sort in the old estimated plan, with 147,336 KiB serial desired memory. The first query rewrite still produced a large hash allocation; the final implementation stages bounded numeric document/membership and symbol keys in connection-local temporary tables, then streams wide symbol metadata through primary-key loop lookups. SQL no longer sorts or hashes wide symbol strings. The bounded application cache restores symbol-key ordering, preserving duplicate/style tie-breaks. Scope exclusions, document subsets, alias membership counts, source freshness, generation fences, cancellation and metadata budgets remain enforced. No source bodies or assets are loaded by the catalogue query, and warm navigation still uses the shared cache rather than rereading symbols.

Actual execution-plan checks passed for all four captured-only scopes. Maximum requested grants were 10,224 KiB for Tempest, 3,744 KiB for Atlas, 1,024 KiB for Keystone and 4,592 KiB for RateOps. These are new actual-plan measurements; the old estimated grant is evidence of a plan risk, not proof of the migration device's precise wait or a controlled old/new throughput comparison.

The status bar now distinguishes **Loading reference catalogue** from **Preparing highlights**, and a failure identifies its stage. Enabled internal logging records scope selection, membership/stale counts, symbols read and elapsed times without logging query parameters or source contents. Failed explorer expansions replace their perpetual loading row with a retryable unavailable row. Failed batches and thrown exceptions log typed owner context and method stacks, not provider messages or source locators; retired requests cannot replace a new owner's nodes/status.

The real MainWindow startup test now expands `TempestTest / Stored Procedures` concurrently with reference preparation. Both redirected-file initialization and a fresh-process, normal-pooling restart passed that check, plus preview and five captured-table navigation actions during file reconciliation. The restart loaded the core workspace in 2.892 seconds and made captured references usable after 6.418 seconds. Completion took 28.173 seconds including navigation and checking 468 physical files: zero documents republished, 11,676 unchanged, 22 captured checkpoints reused, zero SQL failures. Twenty cached resolutions had median/p95 latency 5.364/8.796 ms. These are local offscreen test observations, not a legacy-RAM comparison or a promise for the migration device.

Saved-state/source rowversions and the original backup remain unchanged; only the owned restored copy's physical paths and derived index were updated. The persistent schema scripts, parser/completion policies, migration checksums and recovery format are unchanged. No remigration or index reset is required by this fix. Evidence is under `C:\Users\ciara\source\repos\Surf\build-check`: `reference-metadata-sample-views.log`, `reference-metadata-sample-mapped.log`, `reference-metadata-sample-pooled.log`, and `sample-database/metadata-fix-*` execution plans, reports and PNGs. No redeployment was performed.

Final verification: build succeeded with zero warnings/errors; `--reference-warm-start` passed 194 checks, `--runtime-wpf` 485, `--startup-resilience` 41 and `--reference-watch` 29 (749 total). Supplied-backup scenarios passed 27 all-scope/actual-plan, 25 redirected-file and 25 normal-pooling restart checks. An independent static correctness review found no issues in limits, coverage, freshness, ordering, fences or progress handling; it did not independently exercise mid-stream cancellation with pooled connection reuse. The intermediate wide-allocation candidate and one case-sensitive trace assertion failed during development and were corrected before the final passes. The private restored database and LocalDB instance were removed after retaining evidence; the existing owned extracted-backup/dummy directory remains for inspection. Migration-device confirmation is still required because no server wait trace from that device was supplied.

## Migration Trial and Rollback

1. Preserve an application Export Database package as well as the original database backup when legacy diagrams use external pasted images. A legacy SQL backup alone does not include those files.
2. Start the updated build against the legacy database. The format prompt appears before legacy initialization/loading. Choose migration and supply a new destination; do not overwrite the original.
3. Wait for validation. The destination cannot serve normal application queries before Ready publication. Choose separately whether it should be remembered for future starts; an environment connection override is reported explicitly.
4. Compare scopes, captured history, diagrams/images, links, saved workbenches, search and exports on the migrated copy, then run representative performance checks.
5. To roll back operationally, select the preserved original legacy database. Post-migration edits in the destination are not synchronized back to the original. Choosing No at the initial migration prompt continues the legacy path and its existing whole-document loading behavior.

### Migration Progress and Compatible Retry

The migration dialog now reports the active phase, current item, processed count, elapsed/phase time, and age of the last activity update. A one-second heartbeat keeps elapsed and activity age visible during long SQL/file operations; it does not advance completed work. Source inspection reports bytes, conversion distinguishes newly written and reused units, and preservation validation uses the existing checkpoint total. Coverage checks, final source/image hashes, and publication have separate phase labels.

Remaining-time estimates are approximate and apply only to the active phase. They appear after enough throughput samples when that phase has a known total; unknown totals remain indeterminate. There is no fabricated whole-migration ETA. Work costs vary substantially between metadata, large definitions, history, and captures, and SQL waits can extend an estimate.

Each attempt creates `migration-<identity>-attempt-<guid>.progress.jsonl` inside the existing recovery directory. The dialog exposes the selectable full path. Entries include UTC timestamps, phase/counts, elapsed time, activity age, and the phase estimate. A terminal SQL failure also records its error number, state, and client connection identity for correlation, not its potentially sensitive full message. Writes are throttled to ten seconds plus transitions/termination and capped at 4 MiB per attempt, including reserved terminal space. Logging failures never prevent migration. No connection string, credentials, SQL parameters, or object contents are logged. Logs are diagnostic files only, not authoritative checkpoints; existing SQL progress queries still show committed conversion checkpoints rather than live validation progress.

Validation now uses 256-item catalogue pages with the existing byte budgets, 128-identity indexed journal batches with an eight-block/8 MiB cache, bounded child/revision owner batches, and streamed historical column/key pages. Historical metadata that exceeds the normal 8 MiB page budget is handled as one bounded row, retaining the old selected-row behavior within the source JSON limits. Exact field, ordering, ownership, hash, missing/extra-row, and publication checks remain in place; it does not load a whole library to accelerate validation.

The schema scripts/checksum, converter version, recovery manifest, source fingerprint, and checkpoint/identity format are unchanged. A run started by the previous build can therefore be resumed by the updated build, provided the original source, staging directory, and destination still match. This update does not change a migration already running on another device. If that run fails, deploy the updated application there, choose **Resume an interrupted migration**, and select the same destination and recovery directory. Do not empty/recreate the destination or replace its manifest. Resume reuses committed units but repeats source/fidelity checks and validation; it does not skip directly to publication. A matching already-completed migration is recognized without importing it again.

The updated build passed 167 validation/mapping checks, 166 isolated progress checks, and 1,076 existing storage/local/package/pasted-image migration checks. The solution built with zero warnings or errors. The validation fixture covers more than 2,000 current/historical child fields, 256-item page boundaries, historical values larger than 8 MiB pages, deliberate owner/order corruption, cancellation, unchanged legacy sources, and byte-identical recovery manifests across resume. Its 1,097 mapped units validated using 685 SQL commands in 17.443 seconds on this disposable LocalDB fixture. This is evidence of batching and preservation, not a predicted duration or measured speedup for the user's SQL Express migration. The progress checks include light/dark offscreen dialog layouts at default and reduced sizes, stalled heartbeats, late callbacks, log limits/redaction, and ETA behavior. Run logs are `migration-update-validation.log`, `migration-update-progress.log`, and `migration-update-storage.log` under `C:\Users\ciara\source\repos\Surf\build-check`.

### SQL Connection Failure Diagnostics

An SSPI-handshake/Shared Memory pipe error is a connection or authentication failure, not a reported preservation mismatch. The client message alone does not establish why the login failed. The validation batching reduces repeated queries; it is not a demonstrated repair for that failure. Test a **fresh** SSMS Windows Authentication connection under the same user to both the source and destination, and inspect SQL Server and Windows event logs at the failure time. An already-open SSMS session does not exercise a new login.

Run these read-only checks on the migration machine's SQL Express instance; they neither alter migration checkpoints nor publish the destination:

```sql
SELECT name, state_desc, user_access_desc, is_auto_close_on
FROM master.sys.databases
WHERE name IN (N'Surf2_TEST', N'Surf2', N'TestLogDB');

EXEC master.sys.sp_readerrorlog 0, 1, N'SSPI';
EXEC master.sys.sp_readerrorlog 0, 1, N'Login failed';
EXEC master.sys.sp_readerrorlog 0, 1, N'17806';
```

Log reading requires the appropriate server permission. If the relevant failure predates the current log, select the matching archive in SSMS rather than treating empty current-log results as proof that no error occurred. Preserve timestamps and full SQL/Windows error codes when sharing results; redact account/machine details as needed. Repeated startup/recovery messages for `TestLogDB` alone do not diagnose a login failure against the Surf databases. `AUTO_CLOSE` can cause databases to close and reopen between uses, but its actual setting and relevance must be checked before changing anything.

References: [SQL connectivity troubleshooting](https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/connect/resolve-connectivity-errors-overview), [read and filter SQL error logs](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-readerrorlog-transact-sql), and [AUTO_CLOSE behavior](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-database-transact-sql-set-options#auto_close--on--off-).
