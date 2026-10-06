# Selected State Access

These adapters are not wired into startup. They do not implement the legacy root-store interfaces, create databases, activate connections, or synthesize empty writable models when reads fail. Only files in this directory are owned by this slice; common Access infrastructure is independent.

## Contracts

- `SelectedStateAccess(RelationalSession, StateLimits?)`: `ListScopesAsync`, `ListDiagramsAsync`, and `ListWorkbenchesAsync` return typed, generation-bound summary pages. They delegate to the State store without aggregate reads. Cursors detach rowversion arrays and are catalogue-type-specific.
- `LoadScopeAsync`, `LoadDiagramAsync`, and `LoadWorkbenchAsync`: read one bounded owner. Numeric-key overloads open the current owner; summary overloads reject a changed owner or different epoch. Workbench loads preserve the exact saved embedded diagram, never substituting the current diagram.
- `LoadWorkspaceAsync` and `LoadScopeSelectionAsync`: load only the current layout/session or selection aggregate. Workspace descriptors include geometry, filters, ordered unloaded resources and locators, not opened document text.
- `StatePreferenceAccess.LoadStartupPreferencesAsync`: reads the scalar preference head and all input flags/version, with no image definitions, base64 or assets, and no style/appearance child enumeration.
- `ListImagesAsync`, `ListStylesAsync`, `ListExtensionAppearanceAsync`: bounded, owner-generation-pinned pages. Image summaries carry no base64, regex text or asset bytes.
- `LoadImageAsync`, `LoadStyleAsync`, `LoadExtensionAppearanceAsync`: read one chosen child into an edit session. The chosen image verifies its stored base64 and asset, with text/byte/pixel budgets. An empty definition is valid; unresolved/nonempty image data is a failed load.
- `ReadImageAssetAsync(key, expectedProfileToken, ct)`: fetches only the chosen image's verified bytes; `CopyBytes()` transfers a detached buffer. No cache or decoded bitmap is retained by the adapter.
- `ReadMostRecentWorkbenchSummaryAsync`: uses one summary-only `TOP(1)` query with the existing Updated/Saved/Created fallback and source-order tie-breaker. It does not load any workbench diagram or layout.

## Editing And Failure

`StateLoad<T>` distinguishes Ready, Missing, Failed and Cancelled. Non-Ready results have no Value or write capability. Errors/content are excluded from JSON diagnostics; no diagnostics are logged here. Failed reads are not cached.

Successfully loaded `StateEditSession<T>` instances expose `Snapshot()`, `Replace(capturedOnDispatcher)`, `SaveAsync(ct)`, `RecoverAsync(ct)`, expected owner tokens and dirty generations. Snapshot and Replace use the existing exact State maps/copies, not model Clone/EnsureDefaults methods that normalize persisted data. The parent retains its existing undo/dirty-comparison rules and calls Replace only for an actual captured change.

Saves are serialized per edit session and use optimistic owner tokens across sessions/processes. A successful delayed save updates the token and marks only its captured generation clean; newer edits remain dirty. Duplicate queued captures coalesce. Conflicts require a reload. Unknown/cancelled write outcomes block further saves until a narrow owner-token probe resolves the publication identity or proves the old owner is unchanged. No blind write retry occurs.

Preference saves change only the scalar head or selected child row and bump the shared profile owner token transactionally. Unqueried style/image/extension siblings and their order remain untouched. A child that disappeared causes rollback, not recreation. There are deliberately no implicit create, delete, reorder or whole-AppSettings-save methods.

`StartupPreferences.ApplyToRuntime(AppSettings)` applies only the loaded scalar subset and leaves all child collections unchanged. Do not call full `SaveSettingsAsync` or infer deletions from the resulting partial runtime settings. Load further pages/selected details only for the relevant settings or reference workflow. Any explicit default migration is a separate command, not a startup-read side effect.

Call DisposeAsync on logical owner close, not on WPF reparenting/Unloaded. It rejects queued/new writes and awaits an active save; the parent owns prompting, cancellation, scheduler priority and stale-request publication. The adapters reject migration-validation sessions rather than issuing writable runtime leases for Converting databases.

## Verification

`StateAccessContractChecks.RunAsync(ct)` is an opt-in pure hook for the parent harness. It covers summary payload exclusion, raw startup/input values and child preservation, missing/failed/cancelled/cross-epoch loads, immutable cursors, detached models, dirty generations, coalescing, expected-token chaining, lost acknowledgements/recovery, conflicts and logical close. It does not open SQL, decode images, read files or alter startup. Parent builds/SQL fixtures and actual WPF integration remain separate.
