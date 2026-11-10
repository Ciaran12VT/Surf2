# Relational State Slice

Implemented in `Surf2.Storage.Relational.State.RelationalStateStore`. This is storage-only: no MainWindow integration, database initialization, root-library adapter, or JSON graph column.

## Import Contract

Constructor: `RelationalStateStore(RelationalSession session, RelationalContentStore? content = null, StateLimits? limits = null)`.

All methods below return `Task<StateToken>` and accept an optional final `CancellationToken cancellationToken = default`:

```csharp
ImportScopeAsync(SqlConnection connection, SqlTransaction transaction,
    Scope scope, long sourceOrdinal, Guid publicationId, CancellationToken cancellationToken = default)
ImportDiagramAsync(SqlConnection connection, SqlTransaction transaction,
    DiagramDocument diagram, long sourceOrdinal, string pastedImageDirectory,
    Guid publicationId, CancellationToken cancellationToken = default)
ImportWorkbenchAsync(SqlConnection connection, SqlTransaction transaction,
    WorkbenchState workbench, long sourceOrdinal, string pastedImageDirectory,
    Guid publicationId, CancellationToken cancellationToken = default)
ImportSettingsAsync(SqlConnection connection, SqlTransaction transaction,
    AppSettings settings, Guid publicationId, CancellationToken cancellationToken = default)
ImportWorkspaceAsync(SqlConnection connection, SqlTransaction transaction,
    WorkspaceState workspace, Guid publicationId, CancellationToken cancellationToken = default)
ImportScopeSelectionAsync(SqlConnection connection, SqlTransaction transaction,
    int schemaVersion, string? lastActiveScopeId, Guid publicationId,
    CancellationToken cancellationToken = default)
```

Frozen-byte overloads also accept `DiagramState` or `WorkbenchAggregate`, without a directory parameter. `PrepareDiagramImportAsync(connection, transaction, diagram, pastedImageDirectory, frozenPngReader?, ct)` captures one diagram and safely prepares ordinal-keyed pasted/fallback bytes. A cached reader can supply a previously frozen PNG by original path without re-opening that original. These overloads preserve the same transactional/row-budget contract and report the same import warnings.

Imports require an open connection and its active transaction. They do not check Ready, create schemas, commit, activate, initialize defaults, or load a root library. Each graph write uses an internal savepoint; a failure rolls back that unit where SQL permits. On any import failure, the caller MUST roll back its transaction, especially if SQL has doomed it or the connection failed. A returned token is provisional until that transaction commits. Imports are inserts, not retry/upsert operations: the coordinator must atomically write its deterministic `MigrationIdentityMap`, issues, and checkpoint in this same transaction, and skip already mapped units on resume. Composable transactions, rather than independently committed/idempotent graph writes, are the chosen recovery contract.

Import settings before diagrams/workbenches that need definition-image fallback. `ImportScopeSelectionAsync` stores the original scope root `SchemaVersion` and nullable `LastActiveScopeId` without normalization. Keyboard shortcut `Version` is also preserved. The coordinator owns supported-version/unknown-field preflight and provenance.

`Migration/LegacyStateImporter.cs` is the only additional migration file owned by this slice. Constructor: `(RelationalSession, MigrationJournal, LegacySourceStage, string pastedImageDirectory)`. `ImportAsync(ct)` runs **after snapshot import and before final source verification**, imports settings/session/scopes/diagrams/workbenches with `Journal.UnitAsync`, checkpoints collection-free headers even for empty arrays, writes fallback issues in the same graph/checkpoint transaction, and invokes `ResolveScopeTargetsAsync(ct)`. This is the combined journaled pass: scope targets, then `ResolveDiagramReferencesAsync` for each imported diagram and `ResolveWorkbenchReferencesAsync` for each imported workbench. Repeating it after `ImportAsync` skips committed checkpoints. `ResolveStateReferencesAsync(ct)` is also public for just the latter two passes. Duplicate/missing legacy targets remain explicitly unresolved, rather than binding arbitrarily. The passes read only paged IDs from target catalogues and one selected owner's reference projection, with fresh owner tokens read inside each journal transaction. They do not touch the settings/session/profile selection heads.

The importer freezes referenced local PNG bytes before State import into `source.DirectoryPath/assets/<sha256>.png`, calls `LegacySourceStage.RegisterAssetAsync(originalPath, stagedPath, ct)`, and exposes `ImportedAssetPaths` and `ImportedAssetHashes`. On resume it reads asset metadata from the persisted stage manifest and uses hash-verified frozen bytes, never substitutes a re-read original for missing/corrupt staged bytes. Fallback-only images have no external file dependency. Originals remain read-only, filenames remain exact, and the parent source-verification phase checks registered originals/staged hashes before activation.

Tokens expose `Key`, connection `Epoch`, a defensively copied eight-byte `Version`, `PublicationId`, and `ImportWarnings`. Missing pasted-image warnings are `MissingPastedImageUsedInline` or `MissingPastedImageUsedDefinition`, with aggregate kind and object ordinal; the caller can translate these into its `MigrationIssue` rows. Fatal unresolved/corrupt-image errors never omit an object or modify the source file.

## Runtime Contract

- `ListScopesAsync`, `ListDiagramsAsync`, `ListWorkbenchesAsync`: bounded seek pages in original ordinal/key order; catalogue cursors bind to connection epoch and catalogue generation. Names/timestamps/IDs/tokens only, no object graphs, windows, documentation, base64, or bytes. Changed generations throw `StateConflictException`.
- `ReadScopeAsync(key)`, `ReadDiagramAsync(key)`, `ReadWorkbenchAsync(key)`, `ReadWorkspaceAsync()`, `ReadSettingsAsync()`, `ReadScopeSelectionAsync()`: nullable selected aggregate plus owner token. Missing is distinct from empty; exceptions are not converted to default models.
- `ReadDiagramRevisionAsync(revisionKey)`: exact immutable selected revision, including a saved workbench copy; it does not substitute the current diagram.
- `ReadPreferenceSummaryAsync()`: scalar startup preferences and all input flags, without image-definition payloads.
- `ReadScopeTokenAsync`, `ReadDiagramTokenAsync`, `ReadWorkbenchTokenAsync`, `ReadWorkspaceTokenAsync`, `ReadSettingsTokenAsync`, `ReadScopeSelectionTokenAsync`: tiny publication/recovery probes without graph hydration.
- `CreateScopeAsync`, `CreateDiagramAsync`, `CreateWorkbenchAsync`, `CreateWorkspaceAsync`, `CreateSettingsAsync`: explicit new selected owners. Scope/diagram/workbench creates also take a sort ordinal. Initialize selection with the composable selection import during destination provisioning/migration.
- `SaveScopeAsync`, `SaveDiagramAsync`, `SaveWorkbenchAsync`, `SaveWorkspaceAsync`, `SaveSettingsAsync`, `SaveScopeSelectionAsync`: full **selected** aggregate, expected `StateToken`, caller-supplied publication GUID, optional cancellation. Never pass a partial selected model or a catalogue projection to an aggregate save. The owner rowversion is checked inside one transaction; conflicts roll back. Diagrams append revisions before publishing their pointer. Workbenches append independent embedded revisions.
- `ResolveScopeResourceTargetsAsync(connection, transaction, expected, targets, publicationId, ct)`: bounded explicit second-pass typed snapshot/diagram resolution by selected resource ordinal, without replacing original IDs or paths. The caller validates locator matches and commits its transaction.
- `ReadDiagramRelationshipsAsync(revisionKey)`, `ReadScopeFolderRelationshipsAsync(scopeKey)`, `ReadWorkbenchScopeTargetAsync(workbenchKey)`: bounded typed-link/status projections, without documents or assets. Missing revision/scope owners throw, and a missing workbench returns null.
- `ResolveDiagramReferencesAsync(expected, publicationId, ct)` and `ResolveWorkbenchReferencesAsync(expected, publicationId, ct)` also have caller-connection/transaction overloads for Converting imports. They optimistically touch the selected owner, invalidate its catalogue generation, resolve selected portal/scope locators, and return its new token. They do not append or substitute an embedded/current diagram revision.

Runtime connections require Ready and are disposed per operation. Short serializable transactions keep selected owner/children and catalogue generations coherent without changing database isolation settings. Runtime writes never automatically retry an unknown commit outcome. Compare `PublicationId` using a token probe before deciding recovery; this stores the latest publication, not a complete command receipt history.

The parent's pinned migration-validation session may read while Converting. It cannot publish State: `Expected`, `ImportArguments`, explicit creates, and shared `ExecuteAsync`/`InsertAsync` write helpers call `RejectValidationWrite`. Future deletion commands must use these guarded helpers rather than bypass the central guard.

## Owned Relationships

`StateLinkResolution`: 0 None, 1 Resolved, 2 Missing, 3 Ambiguous, 4 ContextMismatch. Every unresolved typed target is null; original strings remain unchanged. Resolution uses .NET `OrdinalIgnoreCase`, not a database collation or first-match policy.

- `DiagramWorkflowBinding` resolves a marker's workflow within its exact `DiagramRevision`, then its item inside that workflow. Duplicate workflow or contextual item IDs stay ambiguous. Non-marker subtype fields are retained but cannot acquire a marker binding.
- `WorkflowItemMarker` resolves an original marker ID only when unique in that exact revision, marker-typed, and pointing back to the same unambiguous workflow/item context. Both relations are written after all children exist, in the aggregate transaction. Saved workbench instances use their own objects/workflows, never current ones.
- `WorkflowItem.DiagramRevisionKey` and `QueryItem.DiagramRevisionKey` participate in composite owner FKs. A query has exactly one object/item owner; SQL cannot assign either owner from another revision. A resolved binding item cannot belong to a different workflow.
- `DiagramPortalTarget` self-links contain that exact revision/object FK, including saved embedded copies. Cross-diagram links contain only a unique logical `DiagramKey`; the original paired object ID stays on `DiagramObject`. Cross-diagram `Resolved` means logical diagram identity only, not validation/pinning of a mutable current object. Missing/duplicate logical diagram IDs remain unresolved. A deferred import pass resolves forward references after all diagrams are published inside the Converting destination.
- `Workbench.ResolvedScopeKey` plus `ScopeResolution` resolves the raw scope ID only when unique. The deferred workbench pass runs after all scopes/diagrams; it does not use a display name or choose the first duplicate.
- Virtual-folder parent/member rows have mutually exclusive optional virtual-folder or folder-root `ScopeResource` FKs. Composite FKs enforce the same scope and, for resources, Kind=Folder. Original node keys and duplicate membership ordinals remain authoritative. Only exact encoded virtual-folder IDs or unique owned folder-root paths resolve. Unknown filesystem descendants remain raw for the derived ExplorerNode/NodeLocator slice; filesystem discovery is not performed here. Self-parent/member links are context mismatches. The separate derived explorer still owns effective-parent conflict/cycle validation.

Runtime edits that introduce/rename logical IDs may require explicitly re-resolving affected selected owners. This slice never scans and rewrites every referencing diagram/workbench during a save. Local immutable revision links are not rebuilt against a newer current diagram.

## Image Contract

`DiagramState(Document, PastedImages, PastedImageFallbacks)` and `WorkbenchAggregate(Workbench, PastedImages, PastedImageFallbacks)` key byte dictionaries by object **collection ordinal**, not potentially repeated object IDs. The optional fallback dictionary holds `PastedImageFallback(Resolution, Bytes)`. Pass both dictionaries back to runtime saves; rendering should use these authoritative stored bytes, not depend on re-opening a legacy local filename.

`PastedImageResolution`: 0 None, 1 CapturedLocal, 2 MissingUseInline, 3 MissingUseDefinition. The original filename is independent of these references. Valid local PNG and inline bytes are stored independently even when different. Missing local PNG uses validated inline bytes first, otherwise the first matching ordered image definition using .NET `OrdinalIgnoreCase` semantics. The immutable fallback asset remains available if that definition is edited later. Missing local plus no usable fallback is fatal. Invalid inline base64, invalid bytes, unsafe paths, access failures, and corrupt local PNG are fatal. Whitespace-only legacy image strings retain their exact text but are treated as absent, matching the renderer.

Only a leaf PNG filename is resolved inside the provided dedicated `app-data/PastedDiagramImages` directory. Traversal, alternate streams, device names, trailing-dot/space filenames, and reparse points are rejected. On Windows the opened file handle's final path is verified before reading. Files are opened read-only; neither files nor directories are created, rewritten, or deleted by storage. Remote/redirected app-data paths are not a supported migration input for this local-file resolver.

Image bytes are decoded for validation with the existing WPF bitmap decoder, cancellation, and pixel limits; they are not re-encoded. Exact original base64, including its formatting, is separately retained through `TextContent`. Large documentation/description/label/regex strings also reference shared immutable `TextContent`; all binary content references shared `Asset`.

## Field Coverage

`StateMaps.cs` is the explicit property/column mapping. `Verify-State.ps1` checks writable scalar coverage against actual models, exact scalar copies, all 14 input flags, nested metadata, every mapped DDL destination, length-before-LOB sequential projections, duplicate/order handling, fonts, filters, and unsafe pasted paths. `Verify-Relationships.ps1` checks the pure resolver against real models in memory without MSBuild or SQL, including duplicate IDs, owner contexts, self/cross portals, folder links, and validator-write guards. The authoritative destinations are:

| Model | Destination and complete scalar/collection treatment |
| --- | --- |
| `ScopeLibrary` selection | `ScopeCatalogueState.SchemaVersion`, `UserProfile.LastActiveScopeId`; collection ordinal on each `Scope` |
| `Scope`, `ScopedResource` | ID/name/description; resource ID/kind/path, both overrides, offset timestamp, IncludeChildren; resources ordered separately, aliases never deduplicated |
| `VirtualFolder` | ID/name/original parent key; ordered membership rows retain duplicate original child keys |
| `DiagramDocument` | ID/name, created/updated offset timestamps, zoom, both viewport offsets; ordered objects/workflows, independent current/workbench revision ownership |
| `DiagramObjectSnapshot` | Every scalar, regardless of discriminator: ID/type/Z, workflow bindings, portal locators/name, shape kind, image fields and original filename, label text/font, colors, arrow/loose/tether flags, all line/anchor/label-box and Left/Top/Width/Height geometry |
| `DiagramObjectMetadata`, `QueryItem` | Link and exact documentation XAML; contextual query ID/number/date/status/exact description, owner-specific ordered rows |
| `WorkflowDocument`, `WorkflowItem` | IDs/names, both timestamps, marker visibility; marker ID, item number/description/XAML, ordered items and queries |
| `WorkbenchState` | Every scalar: ID/name/default flag; three timestamps; saved scope ID/name; both view flags, active view/orientation/mode/pinned tab/connection toggle; all code/diagram zoom/offsets; active document/diagram ID/name and lock; exact nullable embedded diagram, ordered unloaded IDs/windows/connections |
| `WorkspaceState`, `OpenDocumentState` | Nullable last folder, zoom/viewport; ordered unloaded IDs/windows; file path/display name, full geometry, visible FontSize, scroll offsets; column-index filters with insertion order and exact text |
| `ReferenceConnectionLineState` | Connection ID; independent source/target paths, line numbers, start/end columns; original order |
| `AppSettings` | Theme/startup/comparison/logging/background/version; **all 14** named input flags; ordered extension appearances, reference styles, image definitions; no defaulting or normalization |
| Appearance/style/image definitions | Extension/background/language; language/kind/foreground/bold/italic/underline; image ID/name/legacy regex/match target/name regex/content regex/type filter/SortOrder/original filename/exact base64 plus validated asset, all in original collection order |

Derived display/Exists/clone/helper properties are not authoritative columns. Local connection bootstrap remains outside this slice.

## Limits and Verification

Defaults: 100,000 aggregate rows, 64 MiB aggregate payload, 16 MiB per text/asset, 16 million decoded image pixels, 500 catalogue rows/page. Limits are configurable and fail explicitly; no truncation or object dropping. These bound selected persistence units, not total WPF/decoded/editor/process memory. Selected reads currently use multiple indexed child queries; batch them only after measurement. Scope saves retain existing child surrogate keys by contextual legacy-ID occurrence and invalidate resolved targets only if kind/path changes; ambiguous duplicate IDs retain occurrence order.

No live database was used for this slice. Initial project compilation succeeded before the parent's shared-build restriction. Pure scalar/coverage verifier initially passed 481 checks; subsequent image fallback/schema/importer changes require the parent's final combined compilation and regression fixtures. State DDL was parsed by ScriptDom as one batch with zero errors. SQL creation, FK enforcement, optimistic-conflict/rollback/cancellation behaviour, actual selected round trips, redirected-path cases, stage resume/hash/cancellation fixtures, and query plans still require the parent harness. Retention/garbage collection, explorer-derived hierarchy/index generation, and UI/autosave orchestration are separate responsibilities. No source files outside State, State DDL, and the additionally assigned LegacyStateImporter were manually edited.
