# Surf Android Implementation Plan

Status: proposed design. Date: 4 November 2026. Baseline: Surf 2.0 at commit `9d068d2`.

Build an Android companion for understanding and reviewing Surf workspaces away from the desktop. Start with offline access to selected scopes, code, captured database definitions, diagrams, workflows, and references. Use a new mobile interface with shared C# domain logic. Add connected access and editing in later stages if those workflows justify their cost.

The working assumption is a phone and tablet companion. A standalone authoring application or full desktop replacement changes the scope substantially; those alternatives are described below. Component names, package schemas, and delivery estimates here are proposals, not existing capabilities.

## 1. What the current application implies

| Current implementation | Consequence for Android |
| --- | --- |
| [Surf2.csproj](../Surf2/Surf2.csproj) targets `net10.0-windows`, enables WPF, and references AvalonEdit and DiffPlex.Wpf. | Android needs a new presentation layer. Existing XAML, controls, and windows cannot simply be retargeted. |
| [MainWindow](../Surf2/MainWindow.xaml.cs) coordinates storage, resource browsing, navigation, code windows, and diagrams. | Extract workflows incrementally. Moving the entire window into a shared project would carry desktop dependencies with it. |
| [SQL connection defaults](../Surf2/Storage/SqlServerConnectionOptions.cs) use LocalDB and Windows integrated authentication. The [document store](../Surf2/Storage/SqlServerDocumentStore.cs) persists collection-sized JSON payloads. | Leave SQL persistence on Windows. Mobile needs a portable content provider or an application API, with bounded reads. |
| [The code viewer](../Surf2/Controls/FloatingCodeWindow.xaml) is read-only. | Reading, searching, and following references preserve Surf's existing purpose. Source-code editing is additional product scope. |
| [Diagram objects](../Surf2/Models/DiagramObjectSnapshot.cs) already store scalar geometry, object kinds, links, and IDs. | Much of the document model is reusable; rendering, hit testing, gestures, and accessibility need new implementations. |
| [Diagram metadata](../Surf2/Models/DiagramMetadata.cs) stores documentation as WPF XAML. Its queries are tracked questions with Active, Resolved, and Irrelevant states. | Define a portable documentation representation. Query review is useful mobile functionality; it does not require SQL execution. |
| [Workbench state](../Surf2/Models/WorkbenchState.cs) includes open documents, desktop layout, and an embedded diagram snapshot. | Share content selection while storing mobile navigation separately. Preserve saved diagram copies as distinct revisions. |

The [relational persistence plan](relational-persistence-implementation-plan.md) and [query access plan](relational-query-access-implementation-plan.md) remain proposed desktop work. Their decisions retain SQL Server and avoid making SQLite, a central service, or synchronization prerequisites. Android can adopt their identity, revision, and bounded-query principles without waiting for the entire migration or changing those decisions.

## 2. Recommended technology

Use **.NET MAUI for the Android shell**, a portable .NET library for domain behavior, and a dedicated diagram renderer. Keep the existing Windows WPF application as the desktop client. This is the starting recommendation, subject to a short device prototype of Surf's demanding views.

| Option | Fit for Surf | Main tradeoff |
| --- | --- | --- |
| .NET MAUI | Recommended starting point: C# reuse, mobile controls, Android integration, and room for other mobile platforms. | WPF UI still needs rebuilding; code reading and complex diagrams need focused implementation and measurement. |
| .NET for Android with native views | Good fallback if MAUI abstractions obstruct the reader, canvas, or lifecycle behavior. | More Android-specific UI work, with less future UI reuse. |
| Kotlin and Jetpack Compose | Strong candidate if Android becomes the primary product and native Android expertise is available. | Most client-side C# behavior must be rewritten, or retained behind a service/exporter. Search compatibility needs particular care. |
| Avalonia | Worth a prototype if a future shared desktop/mobile canvas is a strategic goal. | Similar XAML concepts do not make WPF controls portable; evaluate mobile input, accessibility, and the required device support range. |

Microsoft supports Android development through [MAUI](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms?view=net-maui-10.0) and through [standalone .NET for Android](https://github.com/dotnet/android/blob/main/README.md). Avalonia also targets Android, with support tiers that vary by OS version; check the actual deployment fleet against its [platform matrix](https://docs.avaloniaui.net/docs/supported-platforms).

Start the prototype on the supported .NET 10 mobile toolchain to match the repository. Pin SDK/workload versions in the eventual build configuration and budget for mobile upgrades: MAUI has its own support lifecycle, and MAUI 10 currently reaches end of support on 11 May 2027. Recheck supported versions when implementation begins. [Microsoft MAUI support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/maui).

Use MAUI `GraphicsView` as the initial diagram-rendering candidate; it supplies drawing and interaction primitives, not a ready-made Surf diagram editor. Switch to a different renderer only if the prototype demonstrates a need. [GraphicsView documentation](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/controls/graphicsview?view=net-maui-10.0).

## 3. The mobile experience

The central journey is **scope → diagram or workflow → linked code or SQL → reference or search result → back**. A prototype should prove that whole journey.

| Surface | Phone behavior | Tablet behavior |
| --- | --- | --- |
| Library and scope browser | Available packages/scopes, content date, recent items, resource hierarchy, prominent scope search. | Resource browser beside the selected document. |
| Code and SQL reader | One active document, line numbers, copy/share, wrap toggle, document search, reference preview, document switcher. | Reader with references/details alongside it; optional second document later. |
| Diagram | Pan, pinch zoom, fit-to-content, object selection, details sheet, explicit Follow Link/Open Portal. | Diagram and persistent object details pane. |
| Workflow | Ordered steps, documentation, query status, Show on diagram. | Workflow list alongside the selected marker or linked document. |
| Captured table | Paged rows, column selection, row details as key/value fields, bounded filtering. | Horizontally scrollable grid and row details. |
| Workbench | Reopen its content collection and saved diagram revision, using the mobile document switcher. | Reopen into an appropriate two-pane arrangement. |

Adapt to available window width rather than assuming a device's marketing category determines its layout. This follows Android's [adaptive layout guidance](https://developer.android.com/develop/ui/compose/build-adaptive-apps); the design applies even when implemented in MAUI rather than Compose.

Replace hover previews with tappable previews, right-click menus with visible actions/overflow menus, and floating code windows with a document switcher. Keep Android Back consistent with the user's navigation history. A diagram object list and workflow list should provide accessible alternatives to precise canvas tapping.

The first usable release includes scope browsing, code/captured SQL reading, local and scope search, reference navigation, interactive diagrams, portals, documentation, workflow steps, read-only query status, saved-workbench reopening, and modest captured-table browsing. Show missing targets and omitted table data explicitly. A flattened diagram image is insufficient because it loses links, object metadata, and workflow navigation.

Defer source editing, database capture/restore, advanced comparisons, arbitrary table edits, PDF generation, full diagram authoring, and bidirectional synchronization. Desktop grid cell edits currently affect an in-memory view used by Copy/Export; Android must not imply those edits update a captured or source database.

## 4. Data access in three stages

### Stage A Portable workspace packages

Add an **Export for Android** workflow to desktop Surf and import the resulting package on Android. The package contains an immutable snapshot of selected content and works with the PC switched off. New exports are explicit refreshes; this is not synchronization.

Do not use the existing [persistence backup](../Surf2/Storage/PersistencePackageService.cs) as the mobile format. That exporter copies all stored SQL documents and Surf app-data files, potentially including connection configuration, while external source trees are not bundled. Mobile export must explicitly collect selected source content and approved assets.

Proposed package contents:

| Part | Purpose |
| --- | --- |
| Manifest | Independent format version, workspace ID, export revision, creation time, producer version, supported features, counts, sizes, content hashes, and exclusions. |
| Catalogue pages | Scope membership, hierarchy, display aliases, virtual folders, ordered children, unloaded-resource state, and document summaries. |
| Document entries | Actual source text and captured definitions, language, encoding/line-position policy, resource ID, and content revision. |
| Diagrams and workflows | Geometry, layer order, labels, object IDs, portal pairs, documentation, queries, and distinct saved-workbench diagram copies. |
| Asset entries | Deduplicated image bytes and portable documentation assets, referenced by ID/hash. |
| Reference metadata | Desktop-produced definition locations and the metadata needed for existing resolution behavior. Resolve occurrences against the exported document revision. |
| Address aliases | Legacy Windows paths, database addresses, diagram addresses, and their mapping to package resources. |
| Optional table chunks | Column/type metadata, bounded row chunks, total captured count, exported count, and explicit coverage information. |

Use a ZIP transport container with independently readable entries; validate and expand it into app-private storage through a staging area. Activate it atomically only after validation. Keep a small catalogue/index resident and load documents, assets, and table chunks on demand. Persist local navigation in a small separate file. This provides a first implementation without introducing SQLite or another authoritative database.

Export selection must resolve dependencies: linked definitions, diagram assets, portals, workflow markers, and workbench snapshots. Show unresolved or deliberately excluded targets in the export summary. Do not silently pull every linked workspace into the export. Table data is an explicit selection with a size preview; metadata-only exports remain useful.

Export from a frozen application snapshot after deciding how to handle unsaved diagrams. Copy each external source file once into staging, then derive hashes, references, and line locations from those exact bytes. Detect files changing during capture and retry or report them. Record per-document revisions; do not claim that independent source files and SQL state represent a globally transactional instant. The existing library store can feed an initial desktop exporter; mobile must not deserialize those whole libraries.

Validate package version, entry paths, duplicate IDs within their owning context, expanded sizes, and hashes. Saved-workbench copies legitimately reuse diagram and object IDs: identify objects by workspace, owning diagram revision/copy, and object ID, with equivalent ownership for workflows and queries. Use an allowlist of exportable content rather than copying application directories. Exclude connection settings, credentials, logs, and unrelated local files. Display what source content is selected for transfer. If enterprise policy requires encrypted portable packages, that becomes a release requirement with an explicit key-management design.

Android imports through the system document picker and copies the chosen content into its private store. A later direct-folder provider must use Android document URIs and permission grants, not assume a filesystem path. [Android Storage Access Framework](https://developer.android.com/training/data-storage/shared/documents-files).

### Stage B Optional connected companion

Add an authenticated HTTPS application API only when manual exports become a real limitation. Android calls narrow browse/open/search/table operations; SQL Server connections and source-filesystem access stay behind the API. API authorization is scoped to selected workspaces and resources, with no arbitrary file-path reads or SQL execution endpoint.

A personal deployment could use a Windows companion host under the user account that owns LocalDB and the source files. It is available only while that PC and host are running; cached packages remain usable when it sleeps. A team service requires a separate deployment, identity, storage, and operational design. A Windows service account does not automatically inherit access to a user's LocalDB instance or source directories.

Reuse package resource IDs, revisions, pagination, and cancellation semantics in the API. Pin offline reads to a complete revision. Downloads stage changed content and atomically switch the active revision; partial refreshes must not mix old reference locations with new text. Use device tokens and validated TLS rather than exposing database credentials or copying desktop certificate defaults.

### Stage C Editing and synchronization

Begin with device-local bookmarks/notes, then consider query-status changes and plain-text annotations. These are smaller mobile workflows than manipulating diagram geometry.

Shared changes require a base revision and explicit operations. A command identifies workspace, owning diagram revision, object/query ID, base revision, and idempotency key. On a conflict, retain the user's draft and offer comparison/retry; do not replace a whole library with the phone's stale copy. Keep draft storage and retry state durable before any background transfer. Editing an embedded workbench diagram copy must not silently edit the current library diagram.

Rich documentation editing and simultaneous diagram geometry edits need separate format and merge designs. A standalone authoring application would also need durable local entity transactions, migration, indexes, undo recovery, and import/export ownership rules. Reassess an Android-local database at that point; it would be a separate decision from desktop SQL Server persistence.

## 5. Shared code and boundaries

Create only the projects required by the first vertical slice:

| Proposed component | Responsibility |
| --- | --- |
| `Surf.Core` | Portable DTOs, stable addresses, search/navigation rules, diagram geometry and workflow/query rules. No UI or storage-framework references. |
| `Surf.PortableWorkspace` | Package schema, bounded readers, validation, content/revision lookup, catalogue and asset access. |
| Existing `Surf2` | WPF application, Windows source/SQL access, exporter integration, rich-document conversion, reference parsing. |
| `Surf.Android` | MAUI screens, view models, gestures, lifecycle, renderer, file-picker/share/clipboard adapters, mobile session state. |
| Optional later API host | Authenticated implementation of the same read contracts; desktop/service storage adapters. |

Both clients may use the shared libraries; Android must never reference the WPF executable. Keep initial read operations aligned with the existing query-access proposal: get children, resolve address, read document revision, get diagram revision, resolve reference, search scope, and read a table page. Requests carry workspace/revision identity, page limits and cancellation; results identify their revision and completeness. Providers advertise capabilities so the UI can distinguish absent data from unsupported operations.

Good extraction candidates include [search-reference encoding](../Surf2/Services/CodeSearchReferenceService.cs), [diagram addresses](../Surf2/Services/DiagramDocumentService.cs), the portable parts of [database document addresses](../Surf2/Services/DatabaseDocumentService.cs), [navigation history](../Surf2/Services/NavigationHistoryService.cs), diagram/workflow DTOs, CSV parsing, and reference DTOs/resolution rules.

Keep WPF brushes, window geometry, themes, dialogs, clipboard/IPC integration, and AvalonEdit highlighting in platform adapters. `FileSystemNode`, `OpenWindowItem`, and `ResourceComparisonModels` already contain WPF types; do not extract the Models folder wholesale. Separate pure comparison algorithms from DiffPlex's WPF presentation if comparisons are later needed.

Roslyn, ScriptDom, SQL client code, and the current WPF-based PDF exporter should stay on desktop initially. Generate reference metadata during export. Any dependency later moved to mobile must pass release-build, trimming, startup, memory, and package-size checks; compiling a shared library alone does not prove Android compatibility.

## 6. Compatibility problems to solve explicitly

**Identity and links.** Existing links include absolute Windows paths, `db://` captured-document addresses, `surf2://diagram/…`, and `surf-search:v1:` payloads containing a document path plus search options. Preserve the current parsers and resolve legacy locators through an alias map scoped by workspace and applicable owner/revision. Report ambiguous aliases instead of overwriting a mapping. Introduce a structured address containing workspace, resource ID, owner/revision, and optional line/search target or column filters. Keep display names separate. Reuse existing IDs where available; persist exporter mappings for child files that currently lack their own stable identity. A rename cannot be inferred reliably from a content hash alone. Unknown external targets remain visible as unavailable.

Metadata links also support `:<line>` and `:["column"="filter",...]` suffixes, including both together. Extract their current parsing/composition rules from `MainWindow` and add portable fixtures. Database links distinguish `/object/<kind>/<name>.sql`, `/table/<name>.sql`, and `/table-data/<name>.csv` beneath a snapshot segment. That segment can use a readable name or canonical snapshot ID, with escaping rules different from object-name escaping. Preserve both forms rather than reconstructing URLs with a generic path routine.

**Text positions and search.** Share .NET literal/whole-word/regex behavior and validate it against desktop fixtures. A local WebView can be evaluated as a reader surface, but it must not become the authority for search semantics. Define line endings, Unicode column offsets, wrapping, and revision matching. Bound regex execution with cancellation/timeouts and report incomplete searches. Scope search scans only packaged content, streams result pages, and states omissions; it must not label a partial export as the complete desktop scope.

**Rich documentation.** Convert WPF rich text on desktop into a versioned, allowlisted block model for mobile display, preserving supported paragraphs, lists, emphasis, links, and assets. Retain the original XAML in desktop storage and optionally as an opaque package entry for future round-trip needs. Surface unsupported formatting instead of silently discarding it. Never instantiate arbitrary XAML from a package. If a WebView renders blocks as HTML, escape content and restrict navigation/bridges. Keep documentation read-only until an editing round-trip policy exists.

**Diagram fidelity.** Preserve document coordinates, z-order, tethered labels, line endpoints/arrows, image aspect ratios, workflow markers, and paired portals. Mobile viewport state is separate from document geometry. Prototype font metrics, hit testing at different zoom levels, overlap selection, and image memory use. Draw visible content and bound decoded image caches. Provide a semantic object list for TalkBack and dense diagrams.

**Tables.** Preserve actual types, nulls, formatting, row identity/order, and exported coverage. Start with a bounded read-only subset and filters over that subset. Clearly label that scope. Full-dataset operations require access to the complete dataset and a suitable scan, index, external-sort, or server implementation. Never present page-local sorting or filtering as global. Do not turn an entire dataset into one CSV string on the phone.

**Lifecycle.** Persist lightweight locations, active IDs/revisions, search state, table filters, and diagram viewport as they change. Reload content after process recreation; do not depend on a shutdown event or serialize the entire workspace into Android saved state. Stage interrupted imports so the previous valid package remains available. [Android state-preservation guidance](https://developer.android.com/topic/libraries/architecture/saving-states).

## 7. Delivery sequence and acceptance gates

Effort below is a planning estimate for one engineer familiar with Surf and C#, not a commitment. It assumes an existing representative workspace, device access, and the offline companion scope. The estimates exclude a desktop relational migration, a production hosted service, and full synchronization.

| Phase | Work | Exit condition | Indicative effort |
| --- | --- | --- | --- |
| 0 Feasibility | Agree representative devices/data; prototype MAUI reader, diagram renderer, rich-text projection, and small package. Compare a fallback only if a specific blocker appears. | Physical Android device completes diagram → source search → reference → Back; package works in airplane mode. Record rendering, memory, and accessibility findings before committing to the stack. | 1–2 weeks |
| 1 Portable boundaries | Extract minimal core, address/revision contracts, exporter, package validation, reference metadata, and import activation. | Exported source content and links match desktop; secrets/configuration are excluded; malformed or interrupted imports leave the old package intact. Desktop regressions pass. | 2–3 weeks |
| 2 Companion features | Scope browser, reader/search, diagrams/portals, documentation/workflows/query status, saved-workbench reopening, modest tables, mobile session restore. | Real workspace can be reviewed end-to-end on phone and tablet without desktop availability. Missing and partial content is clearly identified. | 3–5 weeks |
| 3 Release hardening | Large-content budgets, process recreation, accessibility, release builds, signing, update/import compatibility, distribution. | Signed release package passes the device/content matrix with measured performance and upgrade recovery. | 2–3 weeks |
| 4 Optional connected access | API host, pairing/auth, paged queries, cached revision refresh. | Same client workflows work connected/offline with consistent revisions and defined host-unavailable behavior. | Estimate after hosting requirements are chosen |
| 5 Optional authoring | Durable drafts, command API or change-package import, conflict handling, then selected editing UI. | A stale mobile edit cannot overwrite unrelated desktop changes; drafts survive interruption. | Separate project estimate |

The first distributable offline companion is roughly **8–13 engineer-weeks** under these assumptions. Phase 0 may change that range substantially: code-reader behavior, diagram fidelity, and rich-document conversion are the largest technical uncertainties.

Use a modest physical phone plus a tablet/resizable emulator. Select the supported minimum Android version from the actual device fleet and framework constraints; select the target SDK and distribution requirements at release time. Keep an arm64 release build in the validation path from the first prototype. Choose signed private distribution or a Play testing track according to the intended audience, and keep signing material outside source control.

Validation should cover:

- One canonical workspace with files, captured SQL, ambiguous references, portals outside the export, all diagram object kinds, embedded images, workflows, and a saved-workbench diagram copy.
- Search parity for literal/case/whole-word/regex, Unicode, invalid patterns, zero matches, link-restored search state, and line/table-filter suffixes; stale revisions and ambiguous legacy aliases must be detected.
- Package validation, changed source during export, interrupted import, low disk space, duplicate/invalid IDs, incompatible versions, excluded secrets, and missing dependency reporting.
- Rotation, resize, process death, offline launch, Back behavior, font scaling, TalkBack/object-list navigation, and touch/keyboard interactions.
- Release-mode loading, retained memory, search cancellation, and canvas interaction using normal and deliberately oversized documents, diagrams, images, and tables. Set numeric budgets on the chosen baseline device in Phase 0; increasing unopened library content should not cause proportional startup memory growth.
- Existing [Windows regression checks](../Surf2.RegressionTests/README.md) after extraction, portable contract tests for shared behavior, and Android UI/integration checks for the mobile journey. The current WPF test runner alone cannot verify Android.

## 8. Decisions that change the plan

The recommended default is MAUI, offline packages first, phone and tablet layouts, read-only shared content, and separate mobile session state.

If the priority is **always-current desktop content**, bring the connected provider forward after the prototype, accepting host availability, authentication, and deployment work. If the priority is **standalone offline authoring**, design transactional local storage and editing recovery before building most screens. If the priority is **desktop parity on tablets**, prototype diagram editing, multi-document navigation, and comparisons early, and revisit Avalonia alongside MAUI; the companion estimate no longer applies.

The next concrete implementation task is a small, real-data vertical slice: export one scope with linked source and a representative diagram, open it on Android, follow a search reference, and return to the same diagram selection and viewport. That establishes the technical foundations and demonstrates whether Surf's core workflow is useful on a touch device.
