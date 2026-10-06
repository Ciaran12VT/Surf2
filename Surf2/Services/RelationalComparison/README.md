# Relational Comparisons

This component does not load `DatabaseSnapshotLibrary` or captured-row collections.

## Integration

- `MainWindow.RelationalComparison.cs` owns comparison operation cancellation, publication generation checks, and both selected-text and disk-result windows.
- The six existing comparison handlers route here only when a relational runtime is selected. Legacy behavior is unchanged.
- `GetRelationalComparisonAvailability(node, out tableData)` checks the already-loaded explorer summary without SQL.
- `OpenRelationalComparisonAsync(left, right)` accepts typed history/current targets. `RelationalComparisonService.FromResource` builds a selected target from a resource summary; collection targets use `DatabaseSnapshot`/`DatabaseObjectFolder`, `SnapshotKey`, optional `VersionKey`, and optional category.
- Historical table targets retain `HistoricalEntryKey`. Selected historical projections use `HistoricalSnapshotContext`, including data-companion tables and replayed columns/keys.
- `InvalidateRelationalComparisons()` is a synchronous context-transition barrier. It immediately fences generation, cancels existing operations, and snapshots/removes old windows before any cleanup await. Their tracked retirements cannot close windows opened subsequently in a new context. Document-context invalidation calls this hook.
- The parent must await `StopRelationalComparisonsAsync()` before retiring the runtime on reconnect/shutdown. It refuses new operations and drains prior retirements, active operations, and the current windows. Faulted operation retirement still removes its registry entry, and cleanup failures are observed and reported without retaining exception graphs.
- `InitializeRelationalComparisons()` permits a new runtime only after the preceding context drained cleanly. Completed results whose final publication check is cancelled or fails are disposed without opening a window.

## Read Policy

Candidate discovery fetches metadata only. It recursively requests explorer children, does not follow physical reparse points, and reports incomplete discovery rather than silently treating it as complete. The bounded picker warns when its 5,000-choice/8 MiB metadata limit is reached; its existing name/path filter can narrow displayed choices. Narrowing the active scope reaches choices beyond that bounded set.

Selected text comparisons fetch one immutable revision per side, or a bounded locked physical file. Collection comparison hashes one selected document at a time, not all text simultaneously. A physical leaf remembers its exact raw-text digest, so opening a changed file cannot silently display text inconsistent with its earlier collection result.

Collection inputs are externally sorted by hierarchy to keep subtrees contiguous during folder-status propagation. Duplicate relative paths retain the first source occurrence, and matching is ordinal-ignore-case. Final display order remains ordinal-ignore-case relative path order. Whitespace/case options use raw UTF-16 hashing with the existing whitespace-removal and ordinal-case rules, including supplementary characters.

Captured-table comparisons stream original JSON rows and build a disk-backed unique-key index before external sorting. They retain the existing global duplicate suffix counter, literal suffix collisions, first case-insensitive property match, blank/null/missing value display, non-object-row omission, union column ordering, and 800-character row previews. Preferred keys come from the selected table's ordered primary-key metadata; otherwise the existing key-column picker is used.

Result pages, search, difference-only filtering, collection collapse/exclusions, and Compare With choices read a completed disk spool. No WPF indexer performs source I/O. Export streams every matching comparison summary row, not just the current page, with standard CSV quoting. Collapsed folders do not remove their descendants from export; exclusions do. Table exports contain the same bounded previews as the comparison view, not raw captured-table replacement files. Raw full-table export belongs to the grid provider.

## Budgets And Failure Behavior

Defaults are a 1 GiB aggregate temporary-disk quota, 8 MiB sort-run budget, 8 MiB decoded-page budget, 128 result rows per page, bounded records/runs, and at most eight merge inputs. Temporary files are UUID-owned; cleanup deletes exact owned paths, never a recursively computed target. Quota, malformed-source, cancellation, and concurrency failures publish no partial comparison.

`ComparisonScratch` delegates ownership and abandoned recovery to the common `OwnedScratchLease`; it implements no independent recovery sweep. Each generated `.spool` name is durably registered before payload creation. The shared constructor scans at most 128 directory candidates and reclaims only validated, marked, non-live GUID children older than two days. The shared manifest permits at most 32,768 generated filenames and 2 MiB of marker data.

Comparison operations retain registration history for deleted merge inputs. With the maximum 4,096 initial runs and minimum fan-in of two, each comparison sorter registers at most 8,191 files across all generations. A table comparison therefore registers at most 16,387 files (two sorts, four key-index files, one result); a collection comparison at most 24,574 (three sorts, one final result). Both fit the shared limit. A full 32,768-entry marker is under 1,278,000 bytes with current generated suffixes, below 2 MiB. Comparison rebuilds use new leases, not an indefinitely reused registration history. Long-lived grid workspaces additionally retire deleted registrations and compact their manifests through the same helper.

Live markers are held exclusively. Unmarked directories, malformed or traversal manifests, unknown files, redirected roots/ancestors/payloads, and non-GUID source folders are never adopted. Old unmarked comparison directories are deliberately not migrated or removed automatically. Recovery never sweeps source folders or user-selected export destinations.

Normal cleanup reports deletion failures, retains at most the first underlying error, and always releases the lease handle. Failed payloads and their manifests remain eligible for later expiry recovery instead of holding a leaked live lock. `ComparisonResultStore` rejects reads after disposal begins but can retry failed payload cleanup. A released manifest may remain until recovery even after the retry removes the last payload. Engine failures preserve both the original error and any cleanup failure.

Payload deletion uses the shared `ValidateOwnedPath` API, including exact-operation registration and ancestor/payload reparse checks. Lease disposal also validates ancestors before marker cleanup, with live-handle release retained in `finally`; comparison cleanup does not duplicate these path validators.

The existing DiffPlex WPF presenter materializes its selected diff model. Interactive pairs therefore additionally reject more than 256 Ki characters total or 20,000 combined newlines. Selected source text itself has a 4 Mi-character limit. Neither limit truncates content. Raising these limits or replacing the presenter requires separate responsiveness/allocation measurements, not merely larger SQL reads.

Export uses a same-directory unique pending file, durable flush, and atomic replacement/move. Cancellation or failure preserves an existing destination. Closing a result window cancels queries/exports, waits for owned work, then disposes its spool.

Paging buttons use `SurfPrimaryButtonStyle`; Export uses `SurfSaveButtonStyle`. These are dynamic application resources, retaining the shared white foreground and normal/disabled button conventions.

## Verification

`ComparisonContractChecks.RunAsync()` is callable by the parent's regression runner. Its 52 checks use tiny legacy-model fixtures only as a compatibility oracle; no runtime flow reconstructs those libraries.

`ComparisonScratchContractChecks.RunAsync()` passed 46 pure filesystem checks locally, including actual directory/file links with no skipped link cases. It covers live-marker exclusion, separately locked expired markers and recovery after lock release, two-day expiry through comparison construction, future/out-of-range timestamps, invalid expiry/scan bounds, registered-but-uncreated payloads, duplicate durable registration, malformed/traversal/unregistered manifests, non-GUID/unmarked source preservation, redirected-root/ancestor/payload refusal, visible deletion failures, lease release after failure, payload cleanup retries, and artifact cleanup. Platforms without link privilege explicitly skip the link cases rather than counting them as passed. The current Windows filesystem prevented renaming an operation directory while its live marker was open; a conditional regression checks safe disposal on filesystems permitting replacement.

`ComparisonWindowContractChecks.RunAsync(appXamlPath: null)` has 15 actual WPF lifecycle/style-reference checks on its own STA dispatcher. Passing the repository `App.xaml` path adds three checks loading only its button styles into the test window, confirming the actual style identities, white foregrounds, and at least 4.5:1 contrast. The isolated runner passes this path and passed all 18 checks. No shared `Application` is created or mutated. Checks also cover immediate generation/cancellation fencing, delayed rebuild rejection, original/replacement spool disposal, new-window survival during old retirement, competing search refreshes, asynchronous cancellation callback ownership, and complete scratch cleanup. These checks exercise result windows, not a full `MainWindow` or visual layout.

`StorageRegressionSuite.RunRuntimeComparisonChecksAsync(check)` in `Surf2.RegressionTests/StorageCases/RuntimeComparisonCases.cs` is the public actual-SQL suite API. It accepts no connection string and creates only UUID-owned LocalDB fixtures. It covers selected current/history definitions and datasets, legacy null/duplicate/first-property/raw-token rules, full matching export, collection/historical locators, metadata seek paging, selected-only payload diagnostics, and spool cleanup. The regression runner opts in using `--runtime-comparison`; final integrated SQL gate results are recorded in the implementation report under `docs`.

The isolated runner compiles only this component's bounded core, against the parent's already-built assembly:

```powershell
& '.\Surf2\Services\RelationalComparison\Tests\RunPrototypes.ps1' `
  -ProductionDirectory 'C:\Users\ciara\source\repos\Surf\build-check\relational-integration'
```

It writes to its own output directory, runs no SQL, and does not build the shared project. Checks cover legacy table parity, duplicate collisions, null/case handling, external multi-pass sorting, late result pages/full export, folder-status propagation, binary UTF-16 fidelity, quotas/cancellation, shared-lease filesystem recovery, and the isolated WPF lifecycle/style suite. Add `-CompileSqlSuite` to compile the regression suite against the existing parent assembly without running it. A distinct `-OutputDirectory` avoids collision with another process inspecting an earlier test assembly.

The shared solution builds without warnings or errors. The current shared-helper integration was freshly compiled from source and passed all 116 isolated checks (52 comparison, 46 filesystem, 18 WPF/style). The integrated runtime suite additionally exercises actual SQL current/history comparisons. The component WPF checks above are not native MainWindow screenshots, and none of these functional checks establishes an application-wide memory reduction.
