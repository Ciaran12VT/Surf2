# Persistence, remote resources and live SQL implementation specification

Status: implementation plan and specification. The first delivery is the local Surf2 milestone in section 13.

Baseline: `relationalDb_V2`, commit `b0830ddfa201758ec26c810f02cd43c60cb61014` (`history load`). Re-audited against the annotated architecture diagram supplied on 12 November.

Replace the single connection editor in Settings > Persistence with a local connection catalogue, separate Local and Azure grids, a read-only current-connection label, and connection-to-connection transfer. A user must be able to work against an existing Azure SQL Database using the same relational persistence services as local SQL Server.

Scope resources must also support remote Git repositories, Azure Blob folders and live SQL databases alongside local folders and independent database snapshots. All source types work with either local or Azure SQL persistence. A migration plan can relocate external files and rebind references when the destination environment cannot access the original locations.

The supplied mock-up, handwritten relationships and latest annotated diagram define the requested behavior. Proposed engineering choices are identified below, including a dedicated live-query view. This specification maps them to the actual branch; it does not implement the changes or run a database migration.

## 1. Scope and decisions

The first milestone keeps the Surf2 application's own database local. It delivers Git resources, Blob resources and paired-folder reconciliation, then live SQL and linked snapshots. A live source can be local SQL Server or Azure SQL Database regardless of where Surf2 stores its own data. The Persistence connection switch, migration to shared Azure persistence, profiles, permissions, and private versus shared resources are later work. The existing transfer design remains in sections 3–9 and 12 for that milestone.

The full design also includes a connection catalogue, a single startup default, read-only connection testing, workspace switching, selected-connection export/import and database transfer. The local milestone uses the resource designs in sections 10 and 11. The later transfer path supports local-to-Azure, Azure-to-local and same-platform transfers.

The following choices define the behavior. Replacement of an existing target after confirmation and a recovery export is confirmed by the user; the other details complete the implementation design.

| Topic | Behavior |
| --- | --- |
| Current versus default | Current identifies the database actually used by this running workspace. Default selects the next ordinary startup. Changing either does not implicitly change the other. |
| Azure grid fields | Use the same columns as Local. Server, database and authentication belong in the connection-string dialog, avoiding two conflicting copies of connection settings. |
| Meaning of Local | SQL Server connections outside the Azure SQL Database category, including LocalDB, SQL Express and network SQL Server. It does not mean the legacy local JSON persistence store. |
| Transfer | Copy all supported Surf2 application data from source to destination; leave source data unchanged. No merging or continuous synchronization. |
| Existing target data | **Replace the target's Surf2 contents after explicit confirmation and a verified recovery export.** Empty and supported Ready V2 targets are eligible. Source remains unchanged. Replacement is required for this release; merging is outside scope. See section 9 for publication and recovery rules. |
| Azure provisioning | Connect to a database provisioned outside Surf2. Surf2 may initialize its schema after explicit user confirmation, but does not provision Azure servers, databases, networking or accounts. |
| Legacy formats | Recognize legacy Surf2 storage and explain migration requirements. Only a supported Ready relational V2 database can become current directly. Preserve the existing separate legacy migration workflow. |
| Blob and local folders | Add Blob resources in Scope Setup. Connect to an existing container or create one in a selected storage account. An ordinary folder and a Blob location can be used independently or explicitly paired under one node. |
| Git repositories | Use one Object Explorer node per repository in a scope. A user can attach their working folder, a direct Git connection, or both. The direct connection uses a persistent Surf2-managed checkout and both views use the same folder reader. |
| Local cache | Keep managed Git checkouts and Blob copies across launches. Check active Blob sources for changed files on startup. Settings exposes per-source and total storage limits, usage and cleanup. Decline downloads that would exceed those limits or available space. |
| Live SQL source | Add an explicit live database resource for local SQL Server or Azure SQL. Browse metadata/definitions and query any accessible table without capturing it first. Its ordinary business database is not subject to the Surf2 persistence-format gate. |
| Snapshots | Preserve generated-SQL import, direct capture and exported-folder import. A snapshot remains self-contained and requires no live connection to its original database. |
| Resource relocation | Before SQL publication, optionally switch local Git references to verified remote Git, copy selected files to Blob, or rebind to an existing Blob location. Support the inverse materialization to local folders. Apply mappings only to destination state. |
| Proposed query scope | First release provides generated table queries and editable, parameterized, single SELECT queries in a dedicated read-only query view. It does not provide SQL administration or write-through grid editing. Section 11 defines the limits. |

Surf2 supports creating a Blob container in an existing storage account. SQL servers/databases, storage accounts and networking are provisioned separately. Standalone Blob resources use local cache refresh. Paired folders use reviewed reconciliation in both directions. Explicit content transfer for persistence relocation is part of the later migration milestone.

### Interpreting the diagram

Three relationships must remain distinct: **reference/read** an external source; **capture** independent content into a snapshot; and **copy/replace** Surf2 persistence. A black arrow into a Surf2 database means its workspace can register and use that source; it does not mean ingest every external file or table into Surf2 SQL storage. The red arrows represent persistence transfer; the blue arrows represent explicit materialization or rebinding, not continuous synchronization.

This table retains the original handwritten numbering. The annotated diagram extends relationships 1 and 2 to both directions and clarifies live SQL versus snapshot behavior.

| No. | Relationship | Implementation meaning |
| --- | --- | --- |
| 1 | Remote Git ↔ local Git folder | Identify correspondence; use a remote reference or materialize a selected remote revision locally. Local changes need an explicit preservation choice before rebinding. |
| 2 | Blob folder ↔ local folder | Pair locations under one node and switch the active view. A reviewed relocation plan can also copy files between locations. |
| 3 | Local Git folder → local Surf2 DB | Register a local folder source; show Git branch and repository details. |
| 4 | Local folder → local Surf2 DB | Retain existing folder parsing, rendering, search and references. |
| 5 | Local SQL source → local Surf2 DB | Register a live SQL resource with metadata browsing and explicit table queries. |
| 6 | Local SQL source → snapshot held locally | Capture/import independent definitions and selected data; future viewing needs no source connection. |
| 7 | Local Surf2 DB ↔ Azure Surf2 DB | Copy all supported Surf2 state into Empty or compatible Surf2 targets; populated targets require confirmation and a verified recovery export. |
| 8 | Remote Git → local Surf2 DB | Use the remote URL to prepare a managed checkout and browse, read and index it through the folder reader. |
| 9 | Blob folder → local Surf2 DB | Cache and index the selected container or prefix on the desktop. |
| 10 | Azure SQL source → local Surf2 DB | Use an Azure SQL business database as a live resource. |
| 11 | Azure SQL source → Azure Surf2 DB | Same live-resource behavior with independently located persistence. |
| 12 | Azure SQL source → snapshot held in Azure | Capture/import a portable snapshot into the active Azure Surf2 store. |
| 13 | Remote Git → Azure Surf2 DB | Same Git provider and logical resource bindings with Azure persistence. |
| 14 | Blob folder → Azure Surf2 DB | Same Blob provider and logical resource bindings with Azure persistence. |
| 15 | Azure SQL source → snapshot held locally | Capture/import remotely sourced data into local Surf2 persistence. |

Apply the same independence to combinations not drawn explicitly: a local SQL source can feed a live resource or snapshot in Azure-backed Surf2 when reachable from the running desktop. A snapshot's storage location never determines its source location.

### Logical model

| Concept | Stored where | Responsibility |
| --- | --- | --- |
| Persistence connection | Protected local catalogue | Select the Surf2 application database; current/default and format eligibility apply here. |
| Source definition | Surf2 relational state, without credentials | Stable source ID, provider and versioned locator/configuration for LocalFileSystem, AzureBlob, Git or LiveSql. Local Git is a detected capability of LocalFileSystem. |
| Scope membership | Surf2 relational state | One explorer node with a stable identity and scope-specific name, IncludeChildren and loaded state. Git and explicitly paired local/Blob folders can have more than one location behind that node. |
| Local location and cache | Per-user desktop settings and app-owned storage | Record the chosen view, linked checkout or folder, and managed copies. Shared nodes default to the remote view. |
| Authentication binding | Protected local user storage | Map a source ID to this user's connection/authentication material; never export secrets in the Surf2 package. |
| Source item/document binding | Surf2 relational state | Typed logical locator and revision; independent of temporary cache paths. |
| Database snapshot | Surf2 relational snapshot/history tables | Independent materialized definitions and captured rows. Optional provenance is descriptive, never a required source connection. |
| Relocation plan | Local operation journal plus destination mapping records | Approved source-to-destination mappings and verified content manifest, separate from SQL replacement status. |

Source configuration edits create a new configuration revision. Saved documents retain their intended source/revision until explicitly rebound; renaming an alias does not change source identity. A source and a persistence connection may point at the same technology, but they have different roles, permissions and lifecycle.

## 2. Branch behavior and implementation impact

| Existing component | Baseline behavior | Required change |
| --- | --- | --- |
| `SettingsWindow.xaml`, Persistence panel | One connection textbox and Test, Use Default, Export, Import and Save controls; changes apply after restart. | Replace with the proposed layout and row-scoped commands. |
| `PersistenceConnectionSettings` | One `ConnectionString`. | Versioned catalogue with stable connection IDs and one default ID. |
| `LocalConnectionSettingsStore` | Atomic JSON publication to `%APPDATA%\Surf2\connection-settings.json`. | Migrate the single entry, protect stored connection strings, validate catalogue invariants and handle concurrent saves. |
| `SqlServerConnectionOptions` | Environment override, saved connection, then built-in LocalDB. Normalization supplies missing database, certificate-trust and timeout defaults. | Resolve a saved default record and apply Azure-safe options consistently. |
| `PersistenceFormatProbe` | Distinguishes Empty, Legacy, Relational, Incomplete, Unsupported and Unavailable. | Reuse its classification; add UI status and operation eligibility without reducing validation to login success. |
| `RelationalApplicationConnection.ActivateAsync` | Saves a startup connection or returns validated process-only options. It does not replace a running workspace. | Integrate catalogue selection and a new application-level workspace handover. |
| `RelationalSchemaInstaller` | Can initialize an existing empty database; separate creation helper connects to `master`. | Use target-only initialization for Azure. Keep local SQL Server creation separate. |
| `RelationalPersistenceTransfer` | Imports a package into a new database on the current server. | Accept independent source and destination connection records; do not derive destination credentials/server from current. |
| `RelationalPackageExporter` / `RelationalPackageImporter` | Consistent streaming export and validated fresh-empty-target import. Import batches commit before final Ready publication. | Retain the empty-target path; reuse codecs/contracts and refactor validators for a new transactional replacement service. Do not remove the existing importer's Empty guard. |
| `FileTreeService` | Legacy tree detects `.git` directories/files and branch/detached HEAD; relational explorer does not expose that enrichment. | Extract detection, integrate relational resource details, add remote/upstream/status discovery for relocation. |
| `ScopedResource`, physical explorer/documents/reference index | Filesystem paths, Windows path identity and local file reads/watchers. | Introduce provider-based enumeration/read/indexing and typed saved bindings. No remote Git/Blob transport exists in this baseline. |
| `RelationalDatabaseCaptureService`, `SqlSnapshotSource` | Direct connection reads a SQL source and persists a `DatabaseSnapshot`; it is not a live reference. | Preserve capture; extract reusable metadata readers for a new live source service. |
| `DatabaseMetadataQueryService`, `DatabaseQueryFileService` | Generate SQL for execution outside Surf2. `RelationalQueryPickerWindow` is an item picker. | Add a real query editor/executor; do not present current Query actions as an existing live query facility. |
| `RelationalDocumentService`, `CapturedGridSource` | Database documents and table data bind to snapshot/resource/revision identities; table data requires `HasFullData`. | Add live object identities and query-result grid adapter; live table access must not depend on capture flags. |
| `RelationalCaptureSchemaUpgrade` | Resumable schema-1→2 conversion of captured table data, invoked during startup. | Preserve this path and explicitly sequence the next resource/maintenance schema upgrade. |

At the baseline commit, the database format is `Surf2.RelationalV2`, **schema version 2**. The format probe recognizes schemas 1 and 2 with version-specific checksums; normal runtime requires the current schema. Schema 2 stores captured rows as JSON in `surf.FullTableData`; generated `capture.Data_*` tables belong to schema-1 compatibility. The package identifier is `Surf2.RelationalV2.Package`, package version 2. The exporter emits schema 2; the importer accepts schema 1/2 and converts schema-1 captures before Ready publication. Product V2, database schema and package versions are separate values.

For the local milestone, version the relational schema and package as needed for authoritative source definitions, bindings and snapshot origins. Schema and package version 3 are candidates if still available. Retain explicit import conversion for version-2 packages using schema 1 or 2. Existing files and snapshots retain their identities during upgrade. Update installer checksums, reader and writer compatibility, and package validation together. Add maintenance admission and replacement metadata with their own compatibility change in the later transfer milestone.

## 3. Settings layout and editing

Use the mock-up's vertical order: Current Connection, Local Connections, Azure Connections, Transfer. Apply existing WPF theme resources and normal Settings sizing/scrolling behavior.

### Current Connection

Display `Current Connection: <saved name>` as a read-only label. Obtain it from the committed workspace owner and its connection ID, not a selected grid row, default checkbox or pending operation. A tooltip may show the server and database, with credentials redacted.

For an environment or external-open connection that is not saved, show `External connection — <database>` and a separate explanation that startup was overridden. Do not silently save that connection. During initial recovery, display `No connection open` until a runtime has loaded successfully.

### Connection grids

Both grids use these columns:

| Column | Behavior |
| --- | --- |
| Id | Read-only display identifier. A short row number is acceptable; internal identity is a stable GUID and does not depend on sorting or the group. |
| Name | Free-text inline editor. Trim surrounding whitespace; require a nonblank value to save a new row. Names do not determine database format or environment. |
| ConnectionString | Single-line redacted preview with ellipsis. Clicking opens the larger connection-string dialog. The status icon sits beside this value. |
| IsDefault | Checkbox derived from the catalogue's single `DefaultConnectionId`. Exactly one saved row is checked across both grids. |

Allow duplicate names, but disambiguate transfer choices and confirmations with group, server/database and a short ID when necessary. A connection named `Surf2_V1_TEST` is classified from its database contents, not its name.

The `+` button appears after the final row of each grid. It adds one in-memory incomplete row. If that grid already has an incomplete row, select/focus it and add nothing; a partially completed row also counts as incomplete. Disable the DataGrid's independent automatic new-item row so two creation mechanisms cannot produce duplicates.

Commit a new row automatically once both Name and ConnectionString contain non-whitespace text and the cell/dialog edit is committed. No page-level Save action is required. Connectivity is not a prerequisite for saving. A complete but malformed connection string may be stored for correction, with a validation error and no database-operation eligibility; this preserves the requested save-on-completion rule.

Existing row changes save on edit commit. Do not persist an edit that clears either required field: show an inline error and retain the last saved value until corrected or cancelled. On disk-write failure, show the row as unsaved and retain the last durable catalogue/default; do not report success. A blank draft closes silently; closing Settings with a partially filled draft or failed save offers return to editing or discard.

Rows being used by a running database operation cannot have their connection string edited or be removed until that operation finishes. Changing a current row's connection string is also disabled until the user switches away: the saved definition must not appear to redirect an already running session. Renaming the current row is allowed and updates its label after saving.

### Connection-string dialog

Provide a resizable multiline editor and separate **Save** and **Close** buttons. Save commits the row's connection value; Close closes the dialog, prompting to save/discard/cancel if text changed. Escape follows Close. If Name is still blank, Save updates the draft without persisting a half-complete record.

Show a redacted value initially and an explicit Reveal/Edit action before displaying stored secrets. Never save the redacted preview as a real connection string. If parsing fails, show `Invalid connection string` in the grid rather than raw text that might contain an unrecognized secret. Do not copy a connection string to the clipboard automatically.

Include a Local/Azure category selector in the dialog. Saving a category change moves the record to the corresponding grid while preserving its ID and default status, and invalidates its test result. Apply the same current-row and running-operation restrictions as connection-string edits.

For Azure, offer optional SQL-password and Microsoft Entra interactive templates and a compact helper for server, database and authentication. These edit the one canonical connection string. Preserve supported advanced keywords when a pasted string is edited; never reconstruct it from a reduced set of visible fields and silently discard options.

### Default checkbox rules

Checking another saved row atomically moves the default to it. Clicking the checked box cannot leave it unchecked. Draft rows cannot become default. The catalogue always retains at least one complete saved row; an unreachable database does not invalidate the default-record invariant.

A saved untested or unavailable connection may be chosen as default, with its status still visible. On next startup it must pass the same activation checks as any other connection. Changing default neither creates schema nor changes the current workspace.

## 4. Connection catalogue and migration

Keep the catalogue on the user's computer, independently of whichever SQL database is current. Saving a name, connection or default must not require a working database or publish unrelated pending Settings preferences.

Proposed persisted model:

| Field | Purpose |
| --- | --- |
| `Version` | Catalogue serialization version, initially 2. |
| `Revision` | Optimistic concurrency token for local catalogue writes. |
| `DefaultConnectionId` | Required ID of exactly one saved connection. |
| `Connections[].Id` | Stable GUID assigned once on first save. |
| `Connections[].Kind` | `LocalSqlServer` or `AzureSqlDatabase`. |
| `Connections[].Name` | User-entered display name. |
| `Connections[].ProtectedConnectionString` | Whole connection string protected using Windows DPAPI for the current Windows user. |

`IsDefault` is computed, not stored independently on each row. Current connection, drafts, decrypted strings, authentication tokens and actionable test status are runtime state. A new application session starts saved rows as Untested, except for a current connection freshly validated during startup.

Retain the existing atomic pending-file/flush/replace protocol and bounded input handling. Serialize updates through a process-shared lock; re-read and compare Revision inside that lock before publishing. On conflict, reload and ask the editor to reapply its change rather than silently overwriting another Surf2 instance's catalogue. Retain the invariant even if two instances move the default simultaneously.

Migration is local and idempotent:

1. Recognize the old single `ConnectionString` file. Preserve its effective connection behavior, assign a stable ID, derive an editable name from the database or `Existing connection`, and make it the default.
2. Classify known Azure endpoint names as Azure; otherwise preserve as Local and allow the category to be corrected in the connection dialog. Explicit category and detected engine capabilities must be checked together before database operations; categorization alone must not authorize local creation against Azure.
3. Protect the string and atomically publish the new file. Preserve compatibility defaults that were effective for an existing local connection; flag an old Azure connection requiring secure-option correction rather than silently treating it as trusted.
4. With a genuinely missing file or a valid legacy blank connection, create the built-in LocalDB default record. This creates configuration only, not a SQL database.
5. An unreadable, malformed, future-version or undecryptable file opens a connection-recovery UI. Do not overwrite it or silently select another database. Allow explicit repair or creation of a replacement catalogue.

Do not keep a plaintext migration backup. Exclude this file, its pending/backup variants and any credential material from persistence packages and diagnostic logs. A catalogue copied to another Windows identity may require credentials to be re-entered; do not treat decryption failure as an empty catalogue.

## 5. Database testing and eligibility

**Test connection string** parses and opens the selected connection, then runs read-only format and required metadata checks. It must not create a database, install a schema, change database options or write a probe row. A successful SQL login alone is insufficient.

| State | Display beside ConnectionString | Meaning and permitted actions |
| --- | --- | --- |
| Untested | Neutral dash and `Not tested` | Test/edit/remove/default allowed; activation/transfer/export/import require a fresh successful classification. |
| Testing | Busy indicator and `Testing` | Cancellable; prevent duplicate tests for that row. |
| Empty | Green tick and `Empty database` | May initialize and become current after confirmation; valid import/transfer target. No source data to export/transfer. |
| Ready | Green tick and `Surf2 V2` | Supported schema and Ready marker. May become current, export, be a transfer source, or be replaced as a destination after confirmation and a recovery export. |
| Upgrade required | Orange ! and `Surf2 — upgrade required` | Recognized older relational schema. Read-only Test may identify it, but current/transfer eligibility waits for separately confirmed compatibility preparation. |
| Connection failure | Red X and short reason | Malformed string, network/TLS/authentication failure, missing database or unreadable required metadata. No data operations. |
| Incompatible or incomplete | Orange ! and short reason | Unknown/non-Surf2 database, unsupported/future format, invalid marker, legacy migration required, unfinished import/migration, or unresolved replacement. Cannot become current or be selected as either transfer endpoint. |

Keep accessible text and tooltips with every icon; color alone is insufficient. Distinguish `Cannot connect`, `Cannot verify database contents`, `Migration required`, and `Import incomplete` in the detailed explanation.

Reuse `PersistenceFormatProbe` checks for the exact format identifier, supported schema and reader/writer versions, marker structure, singleton, nonempty database identity, state and schema checksum. Keep its conservative emptiness rule: no user objects/types/schemas and sufficient metadata visibility. Failure to see tables due to permissions must never qualify a database as empty. Block system databases such as `master`, `model`, `msdb` and `tempdb` from initialization/activation/transfer.

Testing must remain read-only even when it recognizes a schema that can be upgraded. Show `Surf2 — upgrade required` with an explicit preparation action before activation/transfer. Today startup automatically upgrades schema 1 to 2, and today's exporter cannot export schema 1 directly. The new workflow must disclose and separately confirm any required source/target schema preparation, including an interrupted-upgrade recovery, before transfer preflight. Transfer itself leaves source data unchanged; do not conceal a source-schema mutation inside that promise. Preserve the existing resumable capture conversion and its identity/data guarantees.

Recognizing a compatible format does not prove that every write/export permission exists. Check capabilities for the requested operation separately; retain the green format result but show a specific permission error when an operation is unavailable. Do not mutate data to test permissions.

Capture a row ID, connection revision and immutable options snapshot when testing. Discard a result if the row was edited, removed or superseded before completion. Changing connection text, category or credentials resets status to Untested; changing only Name/default does not. Refresh affected status after initialization/import/transfer and when an operation detects that the database changed.

Re-probe endpoints immediately before operations, even if a green badge exists. Pin database identity and format through the operation. A badge is a last observed status, not authorization to skip checks.

## 6. Setting the current connection

**Set as Current** takes effect in this running application through a controlled workspace handover. Merely saving a connection for restart does not satisfy this command.

1. Commit pending row edits and capture the selected connection. Freshly probe it and verify operation permissions. If already current with the same effective connection, return without reloading.
2. For a verified empty database, ask `Initialize this empty database for Surf2 and open it?`, naming the endpoint. Only after acceptance call `InitializeNewDatabaseAsync` against that database and re-probe for Ready. Cancelling leaves it unchanged. Testing or checking IsDefault must never initialize it.
3. Resolve unsaved documents, diagrams, workbenches and settings through the existing save/discard/cancel flow. Saves belong to the old runtime. Cancellation or save failure stops the switch.
4. Create a fresh workspace owner with explicit destination options and fresh lifetime/cancellation state. Preflight its required settings/scope/catalogue loads while retaining the old owner for recovery. Do not restore windows from the old database into the new one.
5. Reversibly pause admission of old writes and external-open requests, drain in-flight writes and fence stale callbacks. Retain a usable old owner until preparation succeeds; terminal lifetime cancellation cannot be rolled back. Commit the new owner as the application's main workspace, then cancel/drain remaining old background work and retire/dispose the old owner and windows. If preparation fails, release the pause and retain the old workspace.
6. Apply the destination's preferences and workspace, refresh Current Connection, and raise `PersistenceConnectionChanged` to rebind database/scope external-open pipes. Reopen/rebind Settings to the new owner if necessary. Keep the saved default unchanged.

Implement this at application/main-window lifecycle level. The existing `CurrentProcessOnly` activation result is useful validation, but does not itself switch sessions. Replacing `_relational` while retaining old cancellation sources, document locators, grids or settings ownership is insufficient.

On failure before handover commits, keep the old runtime and label. If empty-target initialization succeeded but later loading failed, report that the destination was initialized; do not claim all effects were rolled back. After commit, cleanup problems must not change which database the label identifies.

Startup precedence remains: explicit external-open/environment override, saved default, built-in fallback only when bootstrap configuration is genuinely absent as described in section 4. A manual Set as Current is a session choice and does not rewrite `SURF2_CONNECTION_STRING`; a later launch may still be overridden. Expose that fact in Settings. If the selected startup database is unavailable/incompatible, offer connection recovery rather than silently writing to another database.

## 7. Azure SQL connection support

Use `Microsoft.Data.SqlClient` for both categories. The baseline already references version 7.0.1. Its 7.x driver separates Entra support into `Microsoft.Data.SqlClient.Extensions.Azure`; add and test a compatible extension package when implementing Entra sign-in rather than assuming the existing core reference provides it. The extension has its own version series; select compatible package versions explicitly. [SqlClient 7.0 release notes](https://github.com/dotnet/SqlClient/blob/main/release-notes/7.0/7.0.0.md)

The supported Azure authentication paths for this desktop release are SQL username/password and Microsoft Entra interactive sign-in with MFA. An optional user ID is a sign-in hint for interactive authentication; do not store an Entra password or access token. Managed identity is not a default desktop option: it requires a host that supplies that identity. Other driver modes can remain advanced options with clear host prerequisites, but are not required for first-release acceptance. [Microsoft Entra authentication](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/azure-active-directory-authentication?view=sql-server-ver17)

Example templates, containing placeholders only:

```text
Server=tcp:<server>.database.windows.net,1433;Database=<database>;User ID=<user>;Password=<password>;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;

Server=tcp:<server>.database.windows.net,1433;Database=<database>;Authentication=Active Directory Interactive;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60;
```

Azure connections must explicitly identify server and database. Require certificate-validating encryption (`Encrypt=True`/Mandatory or supported Strict, with `TrustServerCertificate=False`); never carry the existing implicit trust bypass into Azure. Accept the actual endpoint supplied by the user, including private networking or sovereign-cloud endpoints, rather than hard-coding one suffix as the only valid address. [Azure SQL transport security](https://learn.microsoft.com/en-us/azure/azure-sql/database/security-overview?view=azuresql)

Centralize parsing/policy in a connection-options factory used by startup, probe, sessions, migration, export and import. Read keywords through `SqlConnectionStringBuilder`, not substring searches. Preserve explicit safe advanced settings, reject conflicting authentication options, and do not silently supply `Database=Surf2` for an incomplete Azure string. An Azure connection placed in the Local category must not bypass TLS or database-creation restrictions. Use known endpoint detection plus engine verification; report uncertain capabilities instead of guessing permission to create/drop anything.

Keep database creation separate from schema initialization. Do not invoke `RelationalDatabaseBootstrap.CanProveDatabaseAbsentAsync` or the installer's SQL Server `master`/CREATE DATABASE path for an Azure profile. Do not use `USE`, cross-server SQL queries or native BACKUP/RESTORE to implement transfer. Azure SQL Database has different instance-level, cross-database and backup/restore capabilities. [Azure SQL T-SQL differences](https://learn.microsoft.com/en-us/azure/azure-sql/database/transact-sql-tsql-differences-sql-server?view=azuresql)

Provide actionable errors for firewall/private-network access, sign-in cancellation, expired credentials, database permissions and certificate failure. Never suggest disabling certificate validation as the automatic fix. Network/database access is configured by the user or administrator outside this Settings page.

All network work is asynchronous and cancellable, with visible progress and bounded connection/command timeouts. Use a bounded transient-connection retry policy, with one total retry budget rather than stacked driver/service loops. Do not blindly replay imports, publications or other mutations after a timeout: first reconcile the operation's committed state. Interactive sign-in needs a suitable timeout and must be initiated by an explicit user action or startup, not repeated background testing of every row. [SqlClient retry guidance](https://learn.microsoft.com/en-us/sql/connect/ado-net/configurable-retry-logic?view=sql-server-ver17)

## 8. Context-menu commands

Every command acts on the row under the context menu, regardless of keyboard selection, current connection or default. Capture that row's identity/options before an asynchronous operation starts. Provide equivalent keyboard access through the menu key/Shift+F10.

| Command | Behavior |
| --- | --- |
| Test connection string | Run section 5's read-only test and display its result beside that row. |
| Set as Current | Run the controlled handover in section 6. |
| Export Database | Export the selected Ready V2 database as a Surf2 persistence ZIP package, even when another connection is current. A verified legacy Surf2 database may use the existing safe legacy exporter while remaining orange and ineligible for current/transfer. Ask for a local output path and confirm replacement of an existing file. |
| Import Database | Choose a supported relational package and import it into the selected connection using its full options. It may replace a supported Ready target using the same confirmation, recovery export and transactional replacement path as Transfer. An empty target uses the adapted fresh-target importer. Preview package type/destination before changes; do not ask for a new same-server database name. |
| Remove Connection | Remove the local catalogue record only. Never drop the database or delete its contents. |

Export includes authoritative Surf2 application state, snapshots, captured rows, history, diagrams, workbenches, preferences and SQL-stored assets according to the existing package contract. Include credential-free folder/live-SQL definitions and typed saved document/reference bindings. Exclude connection profiles, credentials, default/current selection, caches and logs. Ordinary package export preserves external references. Transfer additionally offers the explicit relocation plan in section 12; files selected there are staged and copied separately before destination references are published.

Preserve relational package limits, hashes/counts, schema checks, data fidelity, independent destination identity and regenerated rowversion tokens. Never execute stored SQL definitions or package-provided DDL. Keep the existing safe legacy package conversion path available for import into an empty destination; do not reuse the old broad-AppData export behavior in `PersistencePackageService`. A legacy package must first be converted in an empty database before its relational contents can replace an existing target. Reject unsupported package versions and detectable ZIP/manifest structural errors before destination mutation. For an empty target, streamed row/content/checksum failures can leave a non-Ready database. Replacement additionally requires full package prevalidation and transactional rollback, as described in section 9. Legacy database migration remains a separate workflow, not implicit activation of an orange row.

Disable removal of the current connection until another has become current. To remove the default, require selection of another saved record and publish removal/replacement together. Prevent removal of the final saved connection. Remove an unsaved draft without database effects. Clear transfer selections referencing a removed row and delete its stored protected payload.

Do not change the selected current/default connection records on successful export, import or transfer. If the target is already current, replacement must suspend and then reopen its workspace with a fresh runtime and database identity; the selected connection remains the same. A completion action for any other target may offer **Set as Current**, which must invoke the same handover flow.

## 9. Transfer contract and target policy

Display `FROM: [connection]  TO: [connection]  [OK]` as in the mock-up. Use the union of both saved grids, with group/name and safe endpoint details. Show ineligible entries disabled with their reason; orange rows cannot be selected at all. Drafts never appear. Untested rows require Test before becoming selectable.

FROM requires a supported Ready V2 database. Empty databases show `No Surf2 data to transfer` and cannot be a source. TO accepts either Empty or supported Ready V2, including a database with existing data. A Ready target displays `Existing Surf2 contents will be replaced after a recovery export`. Clear a selection if an edit/test makes it ineligible. Enable OK only with two eligible, distinct endpoints and no operation already running. Check replacement capability during preflight; failure to establish safe exclusive access stops the operation with a reason.

Reject source and destination that resolve to the same physical database, even through different names, credentials or aliases. Use the existing `SameDatabase` logic as a starting point, verify its server/database identity behavior on Azure, and combine it with canonical endpoint evidence and pinned Surf2 identity. Neither connection-string equality nor a copied Surf2 marker alone proves distinct physical databases. If distinction cannot be established, stop before writes with an explanation.

### Transfer sequence and confirmation

1. Capture both connections and block conflicting edits/operations. Freshly test format, permissions, supported schema and physical separation. Complete any separately confirmed compatibility preparation. Resolve pending unsaved work if this application's current database is either endpoint. Target saves complete before taking its recovery snapshot.
2. Export a consistent source snapshot to an operation-owned local temporary package using `RelationalPackageExporter`. For package import, pin the selected archive instead. For replacement, perform a full bounded streaming validation pass over the input, including every table/local-file stream, declared counts and hashes, before target mutation. Keep the package immutable through validation and use.
3. Build the resource relocation plan in section 12 from the pinned package, including **Keep existing references** when appropriate. Show one final confirmation naming FROM (or package), TO, content-copy destinations, inaccessible references and any intentional differences. For a Ready target, state `Replace all Surf2 data in <target> with <source>? Its existing contents will first be exported to <recovery path>. The source and your default connection will remain unchanged.` Use **Replace target contents** and **Cancel** buttons. Display a unique recovery filename in a persistent user-selected folder; do not overwrite an older recovery file. For Empty, confirm import/initialization without claiming existing data needs a recovery export. After confirmation, stage/verify approved external content before taking target maintenance access; report any copies left behind if subsequent SQL publication fails.
4. Recheck the pinned target identity and format after confirmation. If it changed, stop and request a new preflight/confirmation. For Ready, establish the maintenance exclusion below, export and fully verify the recovery package, then run transactional replacement. For Empty, retain the existing fresh-target importer and migration lease; do not initialize it as Ready first.
5. Apply the approved locator/source mappings while importing destination state. Validate all imported data, mapped bindings and constraints, publish Ready last, and verify the committed operation and new identity. Reopen a current target with fresh runtime state; otherwise leave current untouched. Report destination, relocation results, any unresolved references, available counts and the retained recovery-package path, and refresh its badge.
6. Dispose connections/leases and remove only disposable temporary source files owned by the operation. Keep the recovery export after success, cancellation or failure. Report cleanup problems and retain operation evidence required for reconciliation.

Maintain the exporter's consistent snapshot requirement. Where snapshot isolation is unavailable, offer the existing explicit SERIALIZABLE blocking alternative with an explanation that source writers may wait; never silently weaken consistency or enable database options. No distributed transaction spans the two servers. Source consistency ends at the exported snapshot; later source changes are not included.

### Maintenance access and schema compatibility

Add a database-scoped maintenance admission protocol and durable operation journal. A proposed `surf.PersistenceOperation` record holds operation ID, kind, status, expected old/new database identities and source/recovery package hashes. A durable local journal records the same operation ID, connection-record ID/revision, safe physical target identifiers, source package reference and recovery path/hash; store no credentials or decrypted connection strings in either journal. Exclude operational records from exported application data.

All normal persistence operations must acquire shared admission before checking readiness/identity and retain it through their SQL work. Write paths acquire it in the same transaction that performs the write, and recheck identity/format after acquisition. Cover state, snapshots, capture, background index work, schema changes and import; a check before a later transaction is insufficient. A replacement coordinator holds exclusive admission continuously from before the target recovery snapshot until publication or reconciled rollback. Retain the existing migration lease too, with one documented acquisition order to avoid deadlocks. Maintenance-owned exporter/validator work must use the coordinator's authorized context rather than deadlock waiting for normal admission.

Use database-scoped application locks with explicit ownership, bounded waits and checked return codes. Session locks disappear when the owning session ends, so durable recovery gating remains necessary after disconnection. [SQL application locks](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver17)

The baseline migration lease coordinates import/migration only; it does not exclude ordinary writers. Introduce a versioned schema upgrade and reader/writer compatibility gate for this new protocol. Drain and exclude pre-protocol client sessions as part of enabling it, and reject older clients on reconnect. An application lock does not constrain old clients or independent SQL writers that ignore it. Require exclusive maintenance access against those writers too; if it cannot be established, refuse replacement before replacing application data. Never treat a successful migration lock alone as proof of exclusivity.

Update exact schema/checksum validation, installer scripts, supported reader/writer versions and package compatibility together. Preserve a read/export/upgrade path for the baseline schema, and an explicit import mapping for its existing version-2 packages; do not label every baseline database unusable after introducing the feature. Compatibility preparation is separate from the destructive replacement: explain and obtain confirmation for a required schema upgrade, preserve application contents through that upgrade, and finish it before the final replacement confirmation and recovery snapshot. Cancelling replacement does not undo a separately completed upgrade.

### Recovery export

Under exclusive maintenance access, export the target's complete supported Surf2 state using the package contract, then verify the finished archive by streaming every entry and checking structure, counts and hashes. Flush and atomically publish the file before replacement may begin. Record its operation ID, manifest hash, size and path in the durable local journal and retain an immutable read lease. Recheck ownership/target identity before proceeding. A failed export, verification failure, disk-space/permission problem or cancellation stops replacement with the old application data intact.

The recovery export represents the target immediately before replacement; no other writer may change it between that snapshot and publication. It contains the target's application state and SQL assets, with the same external-file and credential exclusions as ordinary export. It is a persistent recovery artifact, not a temporary file, and is never silently removed on success. Completion and recovery views provide **Open recovery folder** and instructions to import it into an empty database, or explicitly replace a compatible target through this same guarded workflow.

### Transactional replacement

Implement a distinct replacement service. Reuse package codecs, whitelists and validation logic; refactor the loader/validators to accept one caller-owned SQL connection and transaction. **Do not invoke the existing batch-committing `ImportAsync` against a cleared target.** Every target-side mutation and validation, including bulk-copy batches, `surf.FullTableData` JSON streams, legacy capture conversion where applicable, identity reseeding, constraints, mapped bindings, derived-state rebuilding and publication, must use that same transaction. Existing validators that open separate sessions must be adapted to see the uncommitted replacement. The current FullTableData importer commits each payload independently; it requires adaptation along with ordinary row loaders. Do not use per-batch or per-dataset internal transactions.

`SqlBulkCopy` supports an existing `SqlTransaction`, allowing multiple copy operations and other changes to commit or roll back together. Use that contract for the replacement batches. [Transactions and bulk copy](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sql/transaction-and-bulk-copy-operations)

Within one target transaction at SERIALIZABLE isolation, clear/replace the known Surf2 application content, load the source package, apply reviewed reference transformations, rebuild derived state, validate models/content/constraints, and assign a fresh database identity and rowversion values. Include the completed operation ID, input/mapping hashes and expected-output fingerprint in final publication. Commit only after all checks pass. Preserve the target database/server, users, roles, grants, network settings and local connection record. Retain installed schema/object permissions; for any owned object recreated by the restore, record and reapply its explicit permissions within the same transaction, or refuse during preflight if they cannot be preserved. Only known application-owned content may be reset. Inventory objects, triggers and dependencies before replacement; unexpected objects or dependencies that prevent a complete safe replacement cause preflight failure, not arbitrary object deletion.

The source's scopes, resources, history, captures, diagrams, workbenches, preferences and assets replace the corresponding target state in full. No old target application records remain merged into the result. Keep target maintenance/compatibility metadata under the new service's ownership rather than importing source operational records.

Use bounded buffers even though there is one transaction. Estimate required client disk space and target transaction-log/data capacity, enforce tested replacement size/time budgets, expose progress/cancellation, and report service-tier capacity or timeout limits clearly. If the transaction cannot complete within supported limits, roll it back and retain the old target; do not silently switch to destructive batch commits. Cancellation may require a long rollback: show `Rolling back` and keep normal use blocked until the outcome is confirmed. A future staged publication implementation must meet the same failure contract before it can replace this path.

When the target is current, suspend its workspace, resolve unsaved work and close/drain target-bound windows and operations before maintenance. On commit, invalidate every old locator/cache/session and load the replacement using a fresh owner as in section 6. Keep the connection's name/ID/default choice. On rollback, reopen the old target after reconciliation. Reopening failure after a confirmed commit is reported as `Replacement completed; workspace could not reopen`, with retry/recovery access; it is not reported as an uncommitted transfer.

### Failure and recovery outcomes

| Point of failure or cancellation | Required outcome |
| --- | --- |
| Before replacement confirmation, or during source preparation | Target application contents unchanged; no replacement. A separately confirmed compatibility upgrade may remain. |
| During recovery export/verification | Old target application data unchanged; report recovery failure and do not start replacement. |
| During transactional replacement before commit | Roll back the whole target transaction. After rollback is confirmed, the old application state remains available; keep the verified recovery file. |
| Connection loss or crash around commit/rollback | Mark outcome unresolved and keep normal use blocked until the server-side operation record, old/new identity and package fingerprint establish the result. Never automatically replay replacement. |
| Commit confirmed | Report success even if cancellation/connection loss arrived afterward; retain recovery export and reload any current target. |
| Empty-target import fails after batches committed | Preserve the existing non-Ready `Migrating` behavior. Show orange `Import incomplete` and require a fresh target or a separately verified recovery operation. This does not describe replacement of a populated target. |

Persist intent before mutation and commit completion evidence with the new contents. A maintenance record left unresolved after a crash must prevent ordinary activation/writes until a recovery coordinator establishes committed success or completed rollback; recovery is available independently of a working main workspace. Re-probe and reconcile before retrying. Do not treat a recovery ZIP as proof that a failed replacement was rolled back, and do not automatically restore it over a target whose commit outcome remains unknown.

## 10. Remote folder sources

### Folder configuration and user behavior

In **Scope Setup → Add Resource**, keep **Folder** and add **Add Blob…** and **Git Repository…**. **Add Blob…** opens the container wizard below. **Git Repository…** registers a remote and offers the direct view. **Add Existing** reuses registered resources across scopes. Retain folder aliases, IncludeChildren, loaded state and virtual folders. Removing scope membership leaves the source content in place.

| Provider | Required configuration | Optional configuration |
| --- | --- | --- |
| Local filesystem | Existing folder path | Existing local options, unchanged. |
| Azure Blob Storage | HTTPS service endpoint and container | Blob-name prefix restricting the root; local authentication binding. An empty prefix exposes the container root. |
| Git repository | Repository clone URL and branch, tag or commit selection | Repository-relative subfolder; local authentication binding. Resolve a default-branch choice visibly rather than assuming `main` or `master`. |

The Blob implementation targets Azure Blob Storage. The provider boundary may accommodate other object stores later; support for S3 or other services is not implied. Git support must work through ordinary HTTPS/SSH repository access, including Azure Repos, GitHub, GitLab and compatible servers, without requiring one hosting vendor's API.

Provide **Test access**, **Browse**, **Refresh**, **Edit source** and **Reconnect** in the folder editor or resource context menu. Test checks access to the configured root/ref and lists a bounded sample without writing remote content. Show provider, root and selected revision in resource details. Distinguish authentication required, inaccessible, missing, loading, stale/offline and partially indexed states. A folder-access failure belongs to that resource and must not change a healthy SQL connection's tested format status.

Match the existing source viewer: `FloatingCodeWindow` is read-only and offers copying/exporting content rather than saving into the original file. Remote users can browse, open, search, follow references, compare and save a local copy. Display provider/revision and read-only status. Upload/download for the explicit relocation workflow in section 12 is included; general remote editing/deletion and Git commit/push are separate capabilities, not implied by opening a file. Never save into a cache while suggesting that the source changed.

### Provider contracts and stored identity

Retain `ResourceKind.Folder` for all three providers. Add a separate source descriptor/provider discriminator instead of changing Folder's existing numeric value or embedding a URL in a field assumed to be a Windows path. Existing virtual-folder relationships and schema constraints depend on Folder semantics.

Persist a versioned, credential-free source definition with a stable public source ID, provider, endpoint/repository identity, container/prefix or ref/subfolder selector, and source configuration revision. Scope memberships refer to that definition and retain their own alias/load options. Existing folder records migrate to `LocalFileSystem` without changing their paths or identity. Deduplicate/reuse sources using provider-specific identity rules, not display names.

Use a common source-definition identity across folder and LiveSql resources, with validated provider-specific configuration rather than a bag of interchangeable fields. Preserve existing `ResourceKind` values 0–3; append `LiveDatabase` (proposed value 4). Extend `surf.ScopeResource` kind/binding constraints and add a source reference while retaining existing snapshot/diagram bindings. Remote folders still have kind Folder. File resources and all path-bearing saved state also need typed binding migration so moving a folder can preserve its opened files.

Add a folder-source interface for access checks, listing, revision resolution, reads and refresh. Both Git views supply a local directory to the existing folder reader. Blob supplies logical file names and locally cached bytes. Reuse folder parsing, name and content search, reference matching, comparison and document opening for both. Persist logical source and item identifiers. Keep the local paths of Surf2-managed caches on the desktop.

A typed remote document locator contains source ID, source configuration revision/immutable origin, provider-relative item identity and a content revision token. It must identify remote content independently of any local cache filename. Persist these locators in saved windows, workbenches and reference links. Keep enough versioned source configuration to resolve older bindings without reinterpreting them against a newly edited endpoint. Extend the current non-database `External` binding path rather than treating remote documents as physical files. A saved pinned revision that is unavailable must show that fact; do not silently substitute the latest file.

Use provider-specific path comparison and encoding for logical identifiers. Preserve exact case and names. Generate safe local cache names for Blob files and keep them mapped to the original names. If paths in a Git commit collide or cannot be represented in a Windows checkout, report the affected paths and keep the previous usable checkout. A failed materialization leaves the direct view unavailable or stale.

### Azure Blob folder behavior

**Add Blob…** offers **Use an existing container** and **Create a container**. Select an Azure storage account, enter or select the container and sign in. For an existing container, choose the root or a folder prefix, then run **Test access**. For a new container, show the account and container name before the explicit **Create container** action and check that the user has create permission. An empty container is a valid resource. Show the selected source and estimated local storage before saving and preparing its local copy. [Create a container](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blob-container-create)

Use `Azure.Storage.Blobs` for paged listing. The selected prefix defines the root and the exact set of eligible content. For example, `src/` excludes `src-old/`. Keep a local catalogue of the folder structure, including when a blob and a virtual folder have the same display name. Download eligible files to the local cache with bounded concurrency and index them as they arrive. Show progress and mark search coverage as incomplete until the index is ready. [Azure Blob listing](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blobs-list) · [Download with .NET](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blob-download)

Support Microsoft Entra interactive access and scoped SAS credentials, plus anonymous read access where the container permits it. Store SAS material separately from the sanitized endpoint. Listing and reading need their respective permissions; possession of an Azure SQL login does not establish Blob access. Report expired SAS, forbidden listing/read, missing container and private-network failures distinctly. Do not acquire broader permissions or create resources during Test.

For standalone Blob resources, open the last complete local copy at startup and check active resources in the background. Also offer **Refresh**. Compare a fresh listing with the saved manifest by exact blob name and version ID or ETag. Show the last modified date for users. Download and reindex new or changed entries and reuse unchanged files and index entries. Remove absent entries from browse and search results only after successful listing and refresh. Pin downloads to the listed version or use a conditional read with the observed ETag. If content changes during the refresh, retry within the operation budget or keep the previous usable copy with a stale indication. Publish the new catalogue and index together. A listing is an observation of the selected prefix over the course of the scan. [Blob concurrency](https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage)

### Pair a Blob location with a local folder

Support three setups: an ordinary local folder, a standalone Blob resource with a Surf2-managed cache, and a Blob resource paired with a user-selected local folder. Offer **Link Blob Storage…** on a local folder and **Link local folder…** on a Blob node. Pairing uses one node with both locations identified. The selected folder supplies the browsable files and index after reconciliation. It replaces the managed copy for this pairing and is outside managed-cache cleanup. A view indicator or switch cannot bypass reconciliation or select an inconsistent copy for search.

**Compare locations** lists files by exact relative path. The wizard matches versions by content and can reuse verified hashes from the last comparison for unchanged files. Last modified time and size help the user choose a version. The Blob version ID or ETag pins the reviewed object, while a content hash verifies equality across file systems. Preflight Windows name collisions, traversal, unreadable files, local disk space and required Blob permissions.

| File state | Initial pairing action |
| --- | --- |
| Local only | Propose upload. An empty Blob location receives the local files. |
| Blob only | Propose download. An empty local folder receives the Blob files. |
| Same contents | Leave both copies in place. |
| Same path, different contents | Require **Keep local** or **Keep Blob** before replacing the other copy. |

Reuse `ResourceFileDiffWindow` and its bounded text diff for **Compare**, with clearly labelled Local and Blob sides, modification times and sizes. For binary or oversized content, show available metadata and the same version choices. Confirm the plan before any writes.

Save a reconciliation manifest after both locations agree. On later checks, use that manifest to distinguish a newly added file from a previously matched file that is now missing on one side. Propose copying a new file to the other side. For an edit on one side, show the proposed copy direction and require review. If both copies differ, let the user compare and choose. For a previously matched file missing on one side, offer **Restore the missing copy** or **Delete the remaining copy**. Require an explicit choice for a deletion. Startup and Refresh detect changes without applying them automatically.

On startup for a previously loaded pair, on manual **Refresh**, and on every **Load**, compare before admitting the resource to the scope. While checking, exclude it from browse, search and reference results. If differences are found, offer **Review changes** and only resume after the full reconciliation is applied and verified. If the user declines or cancels, verification fails, or differences remain, use the existing **Unload** behavior and keep the node visible with a reason. The next **Load** repeats the comparison. A separately registered ordinary folder retains its own load behavior.

Before applying each transfer, recheck the reviewed source and destination. Use conditional Blob writes or deletes and staged local replacement with conflict checking. If a file changes, return it to review. A partial failure can leave individual transfers applied. Report which succeeded and rescan both sides before retrying. Publish a new index and reconciliation manifest only after both sides verify. For paired resources, an older index cannot make the resource available while reconciliation is pending.

### Git node, connection wizard and managed checkout

Extract the existing local Git detection from `FileTreeService.CreateFolderResourceToolTip`/`TryGetGitBranch` and expose it through relational explorer resource details. Support a repository root or a nested scoped folder, `.git` directories and `gitdir:` files, relative/absolute worktree metadata, branch names, detached HEAD and unborn branches. Keep filesystem reads bounded; Git detection failure must not prevent ordinary folder browsing. Refresh branch details when HEAD changes or the user refreshes. Show repository root, relative scoped subfolder, branch and abbreviated commit.

Show one Git node per repository in a scope. When adding a remote or a local checkout, find an existing matching node and link the new location to it. Match verified repository identity using the checkout's Git remote configuration. Ask the user to select when there are several remotes or the match is ambiguous. Preserve the scoped subfolder in both views. Show the active view, configured locations and branch or revision in the tooltip. Keep the same node name and position through a switch.

| Active view | Node icon | First row when expanded |
| --- | --- | --- |
| Local folder with no direct connection | Folder with Git badge | **Add Git repository connection…** |
| Local folder with a direct connection saved | Folder with Git badge | **Switch to direct Git connection** |
| Direct connection with a local folder linked | Git icon | **Use local folder** |
| Direct connection with no local folder linked | Git icon | **Link local folder…** |

Place the focusable action row above the files and subfolders and support Enter to activate it. The connection wizard suggests the checkout's remote URL, allows the user to select a configured remote and branch or revision, and tests access with the user's credentials. Verify repository correspondence before enabling **Save**. Saving keeps the local view active. **Link local folder…** opens a folder picker and verifies the checkout's remote before adding it to the node. The node's accessible name identifies the active view.

Resolve the chosen branch/tag to a commit before enumeration; every tree listing, content read, reference index and comparison within that generation uses the same commit. A commit selection remains pinned. Refresh may advance a branch or tag selection, but existing open documents retain their pinned content until deliberately reloaded. Show the symbolic ref and resolved commit in source details. [Git revision resolution](https://git-scm.com/docs/git-rev-parse)

**Switch to direct Git connection** reuses or prepares a private Surf2-managed working checkout of the selected commit. Use Git fetch and checkout in the managed location and the existing folder reader for browsing and indexing. Keep the managed working files and Git data on disk across launches. **Refresh** fetches when requested. Count Git data, working files and staged refresh data toward the local disk budget. A scoped subfolder can still require downloading objects outside that subfolder. Pass refs and paths as structured arguments and keep credentials out of command arguments and logs.

Show progress and allow cancellation while preparing the alternate view. Change the icon, action row, file list and active search source together when the switch succeeds. On failure, retain the current view and offer retry. Your linked checkout shows working files, including uncommitted changes. Surf2’s checkout shows the committed revision it prepared. The shared database stores the remote URL, ref and subfolder. Each desktop chooses its own linked or managed checkout, with the direct view as the shared default.

Handle HTTPS credentials through a credential manager and SSH through the user's configured key/agent with host verification. Missing credentials, an unknown host key, an inaccessible repository and a missing ref are separate user-visible outcomes. Do not execute repository hooks, builds or configured content filters merely to attach, browse or index a source.

Expose symlink entries, submodule boundaries and Git LFS pointers explicitly. Do not follow symlinks outside the root, recurse into additional repositories or present an LFS pointer as the actual file silently. First-release browsing may show these entries as unsupported/not fetched with an explanation; fetching their content requires the applicable provider capability and separate access validation. Never modify the user's existing checkout as part of browsing a remote repository.

### Local cache limits, Settings and performance

Before a Blob download, page through names and sizes within the selected scope. Show the file count, estimated transfer bytes and expected local space for file data, indexing and temporary staging. Count the peak space needed during refresh, when the old and new copies may overlap. Stop the estimate early if the limit has already been exceeded. If the container is too large, offer a smaller prefix or tell the user where to free space or adjust limits. For Git, present an estimate when available and enforce the storage budget during fetch and checkout even when the final size is unknown.

In **Settings → Local Cache**, show managed Git and Blob copies with their source name, disk usage, last used and last successful refresh, plus total disk usage. Users can adjust a per-source limit and a total cache limit and select **Remove local copy** to reclaim space. Keep those limits per desktop. Check actual free space and leave a reserve on the cache drive. Include index, staging and open-document data in accounting. Coordinate concurrent jobs so combined reservations fit the global budget. Measure representative workloads before selecting default limits.

Refuse a new download whose estimate exceeds the available budget. Recheck limits and free space as files arrive and stop if the source grows past what fits. Keep the last published managed copy and index available with its freshness status. A paired folder stays unloaded if the reconciliation cannot complete. Check free space on the user-selected destination drive and include app-owned staging in the cache budget. For a first download, show which limit was reached and that full search is unavailable. Apply the same checks to Git checkout preparation and refresh. Keep existing parser size and file count limits and identify content omitted from an index.

**Remove local copy** clears app-owned cache files and their index while retaining the source node and connection. Offer to download the copy again when the user needs it. If a copy is in use, coordinate with active downloads or open documents before deletion. Limit cleanup to paths owned by Surf2. A user’s linked checkout or folder is outside the cache. Cancellation, authentication loss or a partial listing retains the last usable published generation. Fence work after a source edit, scope switch or persistence replacement.

Key cached content by source configuration, exact item identity, content revision and local user and credential. Preserve a pinned document’s content while it is in use. An authorized offline view shows the last successful revision and refresh time. Disconnecting an account invalidates its live handles and private cache access.

Measure steady-state browse, open, search and reference matching for each remote view against a local folder with the same eligible content, on the same desktop. Repeat operations use the published local copy and index. Aim for comparable interactive performance and report first download, first index and refresh separately. Validate the target on representative sources before release.

### Credentials and package behavior

Keep tokens, SAS query strings, account keys, Git passwords/PATs, private keys and credential-manager handles out of SQL source definitions, document locators, diagnostic logs and packages. Split credential-bearing pasted URLs into a sanitized descriptor plus protected local authentication binding. Bind credentials locally by user, source identity and trusted provider/endpoint/repository/root scope. An endpoint edit, redirected origin or imported source ID must not silently send an existing credential to a different destination; revalidate the trust/authentication binding. Never infer that SQL and remote-folder authentication are interchangeable. Apply the same origin binding to live SQL source credentials.

Database export, import, replacement and recovery must preserve the source definitions, scope memberships, ref/prefix choices and typed saved locators. Add their authoritative tables to package whitelists, foreign-key closure, schema validation and replacement/reset plans. Coordinate this schema change with the maintenance upgrade in section 9; update package compatibility explicitly. Preserve existing local-folder bindings on upgrade.

Ordinary database packages copy references, not external content. The optional relocation plan explicitly copies selected bytes and rewrites destination references; it never commits/pushes Git changes or deletes source content. Restored sources remain visible and request local credential binding when needed. Database recovery restores saved references; access to exact historic remote bytes depends on retained remote versions/commits. Preserve artifacts referenced by recovery packages. A package must not claim to back up external bytes merely because it contains their locators.

## 11. Live SQL resources and independent snapshots

### Adding and configuring a database resource

Change the database-resource chooser to distinguish **Live SQL connection** from **Database snapshot**. Under snapshot, retain **Generated SQL result**, **Capture from connection** (the current Direct connection flow) and **Import exported folder**. This avoids changing the meaning of existing saved snapshots or implying that editing an old snapshot establishes a live link.

A live resource stores a stable source ID, display alias, sanitized server/database locator, connection-policy options and configuration revision. Its SQL password or local Entra authentication binding stays in protected per-user storage. Provide local SQL Server and Azure SQL choices using the shared connection dialog/policy, plus Test access, Connect, Refresh, Edit source and Create Snapshot. Reuse an existing source across scopes by ID; do not identify it by its display name.

Source testing checks connection, database selection and metadata/read permissions. An ordinary non-Surf2 business database is a valid source even though it would be orange and forbidden as a Persistence destination. Do not run the Empty/Ready initialization gate, install Surf2 tables, create a database or change source options. A Surf2-format database is not automatically forbidden as an explicitly chosen read-only SQL source either; any operation against the same physical endpoint as persistence must participate in cancellation/maintenance coordination. Keep capture-specific source/destination safeguards in the capture workflow rather than copying them into all live reads.

When imported on another computer, show **Authentication required** until that user binds credentials. Failed authentication, changed database identity, missing object or permission loss leaves the resource visible with an actionable status; it must not silently resolve a similarly named connection or object elsewhere.

### Explorer, definitions and search

Use one database node per linked live source and snapshot collection in a scope. Standalone live resources and standalone snapshots can each have their own node. A linked node retains its name and position when the user switches. Give each view a distinct database badge, tooltip and accessible name. Use the first focusable row beneath the node for the other view or for attaching it.

| Active view | First row when expanded |
| --- | --- |
| Live with a linked snapshot | **View snapshot** |
| Snapshot with a linked live connection | **Switch to live connection** |
| Live only | **Capture snapshot…** |
| Snapshot only | **Link live connection…** |

Publish the selected view and its browse, search and reference context together. A failed switch keeps the current view available. Saved documents and links stay bound to their chosen source or snapshot revision. When the snapshot view is active, offer its revision selector and comparison history. Local SQL Server and Azure SQL present the same explorer, query and capture UI. Their connection setup supplies the platform-specific authentication options.

Display the existing Procedures, Views, Functions, Triggers and Tables categories for live databases. Load object names/metadata in cancellable pages and definitions on demand. Extract reusable metadata queries from `SqlSnapshotSource`; do not reuse its snapshot transaction/publication lifecycle as a permanently open live connection. Use `DatabaseDocumentService` for compatible definition/table rendering.

Every visible table offers **View Data / Query Table**, subject to the source's SELECT permission. There is no live **Full Table Data** category or `HasFullData` requirement. A denied table remains visible with a permission explanation rather than disappearing or pretending to contain zero rows. Keep snapshot-specific capture flags and captured-data grouping for existing snapshots.

Add live-source branches to `RelationalExplorerService`, `ExplorerResource`, `ExplorerDatabaseItem` and document resolution. A live object locator includes source ID/configuration revision, schema, object name/type and observed object identity/version evidence. Verify identity when opening/refetching; an object dropped and recreated under the same name must not silently inherit an earlier pinned definition. Do not generate fake `SnapshotKey` or `SourceRevisionKey` values for live objects.

Metadata/name search covers discovered accessible objects; SQL definition search and reference indexing use bounded fetched definitions with a refresh generation and completeness status. Reuse parsers and reference resolution with source-scoped qualification. Encrypted/unreadable definitions are individual unavailable entries, not a reason to discard every other visible object. Do not query all table rows automatically to build an index. Querying row contents remains an explicit action.

Refresh builds a new metadata/definition generation; retain the last usable generation as stale if the source fails. Already opened definitions retain their observed bytes until reloaded, with a changed/stale indicator. This is a browsing cache, not an independent persistent database snapshot. Reloading, removing a resource, changing its configuration or switching Surf2 persistence must cancel/fence old work.

### Proposed query view

Implement a dedicated **SQL Query** view associated with one live source. The diagram leaves this interaction open; the following is the proposed first-release contract.

| Element | Behavior |
| --- | --- |
| Source header | Display alias, server/database and current source generation. Changing source is explicit and clears/fences the previous execution. |
| SQL editor | View Data starts with a safely quoted `SELECT TOP (@limit) ... FROM [schema].[table]`. Permit an editable single SELECT, including CTEs, with typed parameters. Reuse `SqlName` quoting and the ScriptDom patterns in `RelationalSourceTable.CreateReadQuery`. |
| Read-only scope | Reject multiple statements, DDL, DML, EXEC, SELECT INTO and source-changing commands. Limit supported queries to the selected database; reject explicit remote/cross-database access constructs. Parse structure rather than searching for keywords. Use a database principal with only the required read permissions; parsing and `ApplicationIntent=ReadOnly` do not enforce server-side security. |
| Controls | Run, Cancel, Refresh/re-run, parameter editor, row/byte/timeout limits and optional saved query. Make SQL errors visible beside the editor without exposing connection secrets. |
| Results | One bounded tabular result, column names/types/nulls, elapsed time, fetched row count and completed/limited/cancelled status. Duplicate column names use ordinal identity. Copy and export use existing grid facilities. No cell update is written to the source. |
| Defaults | Proposed initial limits: table query 1,000 rows, any query 10,000 fetched rows, 64 MiB decoded result bytes, 4 MiB per cell and 30 seconds execution. Users may raise limits within documented, tested application caps. Never silently truncate a cell or call a row-limited result complete. |
| Save and restore | Save query text, source binding and non-secret parameter definitions only on an explicit save action. Sensitive parameter values are transient. Restoring a window restores the query, not execution; no query auto-runs after startup, transfer or reconnection. |

Execute asynchronously on a dedicated source connection with a cancellation token and bounded command timeout. On cancellation request command cancellation and dispose the reader/connection; keep the view in Cancelling until the operation ends or is abandoned behind a fenced owner. Cancellation is best effort at the server, not proof that a request ended instantaneously. [SqlClient asynchronous readers](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlclient.sqlcommand.executereaderasync), [command cancellation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlclient.sqlcommand.cancel)

Reuse the grid's bounded streaming/paging architecture and `IGridQuerySession` rather than collecting a DataTable in memory. Add a live-result adapter which executes once and materializes a bounded result into per-user scratch storage. Page/sort/filter that result using adapted grid infrastructure; scrolling must not re-execute a query or issue unstable OFFSET pages against changing source rows. Source-side filtering is a new Run.

The baseline grid stores display strings: `GridRow.Cells` contains strings and `GridColumn` does not carry SQL type metadata. Introduce a typed, null-preserving result schema/backing store with a separate display adapter. Preserve every row and column ordinal, duplicate names, NULL versus empty string, exact supported numeric values and binary bytes. Do not reuse `CapturedGridValues.ShouldDisplay`, which can omit all-blank rows, for live query results. Sorting/filtering and faithful export must operate on typed backing values rather than reparsed display text. Unsupported types and oversized values receive an explicit status; never silently coerce them into misleading text or omit their rows.

The source connection is held only while executing/reading; it is not held for the lifetime of the displayed grid. No long-lived source transaction spans user interaction. Do not promise a transactional snapshot of all live database objects: consistency follows the source's configured isolation and each explicit query. A result collected over one query is separate from the stored snapshot/capture feature. Rows without an explicit ORDER BY have no promised source order.

If a limit is reached, stop reading/cancel and label the result Partial with the reason. Grid filters/search operate on that fetched result, with its limit prominently visible. Retrying after a network failure is an explicit re-execution because the result may have changed; do not append a second execution to partial rows. Query result scratch files are disposable and excluded from persistence transfer; preserving results requires an explicit export.

### Snapshot independence and existing import paths

Retain `RelationalDatabaseCaptureService` for direct capture, `DatabaseMetadataImportService.ParseClipboardResult` for Surf2's generated metadata-query results, and `DatabaseExportFolderImportService` for exported snapshot folders. Generated metadata SQL is a structured capture/import format; it is not the arbitrary live SELECT-result format. Continue publishing reviewed snapshots through `RelationalSnapshotEditorService` and existing history/capture contracts.

**Create Snapshot** on a live resource uses the current capture workflow to select table data and capture limits. A successful capture creates a saved snapshot linked to that live source. **Refresh Snapshot** appears on the main node's context menu while the snapshot is active and on the **View snapshot** row's context menu while the live view is active. It uses the linked live connection to add a revision to that snapshot, with the existing capture selections available for review. It keeps the current view selected. Cancellation or failure keeps the previous successful revision and history available.

### Snapshot origin and linking

Read a versioned origin record from the source connection and include the same optional fields in the generated SQL result payload. Record the engine type, reported server and database names, and the engine-specific identifier when readable. SQL Server can expose `sys.database_recovery_status.database_guid`. Azure SQL Database can expose `sys.databases.physical_database_name` for the current physical database. Use the logical server as additional evidence. Attach, restore and Azure service changes can duplicate or change identifiers. These values serve as matching evidence. [SQL Server recovery metadata](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-recovery-status-transact-sql) · [Azure SQL database metadata](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-databases-transact-sql)

Capture origin per revision and preserve it in folder exports and relational packages. Keep older pasted results and exports importable with unknown origin. A direct capture from a verified registered live source links automatically. For independently captured snapshots, link automatically only when the engine-specific identifier and server evidence agree on one source. Show recorded and live origin details and ask for explicit confirmation when evidence is missing, changed or ambiguous. A name alone cannot establish a match. Recheck the live source before refreshing a linked snapshot when identity evidence has changed. Snapshots remain readable without their live connection or its credentials.

### Full catalog requirement for direct capture

Require proven catalog visibility on the same source session before allowing any direct capture or **Refresh Snapshot**. A database-owner context is an example of positive evidence. A database-level `VIEW DEFINITION` check alone cannot establish full visibility because an object or schema can have a more specific denial. Lack of positive proof is a capture failure. Keep the linked live view available for the metadata and rows that login can read. [Metadata visibility](https://learn.microsoft.com/en-us/sql/relational-databases/security/metadata-visibility-configuration) · [Database owner context](https://learn.microsoft.com/en-us/sql/relational-databases/security/authentication-access/principals-database-engine#dbo-user-and-dbo-schema)

On insufficient catalog visibility, warn that the current account cannot produce a verified snapshot. Offer using an account with sufficient visibility or importing the generated query results run under such an account. Publish a revision only after the full capture validates. If a selected table is visible but its rows cannot be read, report which capture failed and keep the prior revision. When a verified catalog shows that a formerly captured table is absent, show its removal in the new revision. Compare tables by schema and name, so a rename appears as the old name removed and the new name added. Preserve historical rows under their original revisions.

Keep snapshots and live sources visibly distinguished in the resource picker and explorer. A snapshot remains at its captured revision; Refresh on a live source queries its current state. Source deletion or credential loss must not break an existing snapshot. All local/Azure source and persistence combinations use the same capture/import contract when the source is reachable from the desktop.

## 12. Resource relocation during persistence migration

### Plan and user interaction

Add a **Resource locations** step to Transfer and package import, and an equivalent **Relocate source** action for a selected folder. The same planner is used in either persistence direction. Ask whether existing locations will remain accessible in the intended environment; **Keep existing references** is valid when using Azure SQL from the same desktop. Test access from the running client and distinguish that observation from access in a different future environment.

Inventory every source and affected binding in the pinned input package, including unloaded resources and other scopes. Show source/provider/current location, detected Git details, chosen destination/action, file/count/byte estimate and readiness. Group reused sources once. Provide these actions:

| Source | Available actions |
| --- | --- |
| Local Git folder | Keep; use verified corresponding remote Git/ref/subfolder; copy selected working-tree files to Blob; choose an existing accessible Blob prefix; explicitly retain unavailable. |
| Ordinary local folder | Keep; upload selected files to a chosen Blob prefix; choose an existing accessible Blob prefix or another local root; explicitly retain unavailable. |
| Remote Git | Keep; change verified remote/ref; materialize selected commit in a new local checkout/folder. |
| Blob folder | Keep; choose a verified existing prefix; download selected manifest/version into a new local folder. |
| Live SQL source | Keep; bind a reachable local/Azure source with a reviewed object mapping; or explicitly retain unavailable. If offline captured content is needed, create an independent snapshot before preparing/pinning the transfer package. Surf2 persistence transfer does not migrate the business database itself. |
| Independent snapshot | Transfer with Surf2 data; no source access or relocation required. |

An intentional change of source content is allowed only with an explicit differences preview. A failed proposed copy or mapping blocks that plan; the user may choose **Retain unavailable** instead, with the consequence recorded in the final report. Never silently skip a failed resource or equate accessibility with matching content. Removing resources is a separate user action, not an automatic migration repair.

The final confirmation combines the SQL replacement decision, persistent recovery path and external copy/rebinding plan. Show any dirty Git files, intentionally omitted content, changed revisions and unresolved documents before authorization. The chosen plan and immutable input package are pinned by hashes so confirmation applies to exactly the operation later executed.

### Local Git to remote Git

Resolve repository root, the scoped relative subfolder, HEAD commit/branch, configured remote candidates/upstream and working-tree status. Verify the chosen remote can supply the chosen commit and subtree. Compare manifests of the selected actual file bytes as well: a clean checkout can differ from commit blobs because of line-ending conversion, attributes, filters or LFS. Show those differences and require explicit acceptance or preserve the actual bytes through Blob copy. Store a credential-free repository URL plus selected ref, resolved commit and subfolder. A branch may later advance on explicit Refresh; the initial migrated state must resolve to the reviewed revision.

Do not assume `origin` exists, branch names match, the local commit was pushed, or a clean-looking root has no nested changes. For local-only commits, staged/unstaged/untracked changes, submodules or LFS content not available remotely, show the preservation gap. Offer copying the selected actual files to Blob, using an available committed revision with differences acknowledged, or cancelling. Do not auto-commit, push, rewrite a user's working tree or silently discard local-only content. Publishing code through an external Git tool can be followed by a new preflight.

When selecting an existing Blob prefix or other source, verify mapped file availability and content fingerprints against the intended files. Equal names alone are insufficient. Deliberately different content requires explicit acceptance and produces changed/unresolved document states rather than pretending that old pinned content survived.

### Content staging and verification

Use a versioned `ResourceRelocationPlan` with operation ID, input-package hash, source ID/configuration revision, action, destination descriptor, exact item mappings, expected byte/row counts where relevant and a file-manifest hash. Each file manifest entry records exact relative name, length, SHA-256 and source revision evidence. Keep credentials in local authentication bindings, never in the plan. Freeze/copy local bytes into an operation-owned staging area before upload, checking for file changes while staging. If a coherent input cannot be established, stop and rebuild the plan; do not describe a mixed working-tree read as an atomic filesystem snapshot.

Preview inclusion rules and omitted files. Exclude `.git` metadata and credential files by default; show ignored/untracked/generated content explicitly and allow reviewed selection. Preserve the bytes actually selected, including line endings. Do not silently follow junctions/symlinks outside the chosen root. A Blob copy of a Git working tree preserves selected files, not repository history. Retain IncludeChildren semantics while showing the exact copied set.

Upload to a fresh operation-owned prefix under an existing user-selected container, using conditional create and bounded concurrency. Never overwrite an arbitrary existing prefix as a side effect of migration. Test read/list/write capabilities separately from the read-only folder Test. Use streamed/block uploads and progress/cancellation; Microsoft's client supports stream and block-based upload. [Azure Blob upload](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blob-upload)

Verify uploaded length and bytes against the frozen manifest, using an independent read/hash or an explicitly supported end-to-end checksum contract; writing an unverified hash into blob metadata is insufficient. Record each returned version ID or ETag. Publish a completed manifest only when every entry verifies. Bind copied content to immutable versions where available; otherwise use the recorded ETags and conditional reads, and report later change as stale/unavailable. Do not claim an atomic container snapshot or guaranteed future availability without retained versions.

Before SQL publication, all required destinations must be verified and the exact mapping manifest sealed. Restart/retry resumes only operation-owned verified uploads, after rechecking input/manifest identity and destination conditions. Expired credentials or a changed source triggers explicit repair/replanning rather than silently starting a different copy.

### Reverse materialization

For remote Git → local, fetch/clone the reviewed commit into an explicitly selected new/owned destination, preserving the scoped subfolder and selected ref/commit. Apply the same no-hooks/content-filter policy as remote browsing. Verify the materialized file-byte manifest before publishing mappings, with any intended line-ending/attribute differences reviewed explicitly. If the materialization cannot reproduce an LFS/submodule/symlink entry, stop or record an explicitly accepted omission; never claim complete parity. Do not reset or overwrite an existing dirty checkout. Reuse of an existing checkout needs verified revision/content and explicit conflict handling.

For Blob → local, download the pinned manifest/version to a staging directory, verify hashes/lengths, then publish the completed folder at the chosen destination before mapping references. Preflight traversal, reserved/invalid Windows names, case collisions, path-length limits and existing-file conflicts. A collision blocks automatic materialization; any rename requires an explicit per-item mapping that is applied to every affected binding. Preserve the original remote source. A downloaded working-tree copy cannot recover Git history that was never uploaded.

### Applying mappings to destination state

Introduce a `ResourceBindingMapper` operating on source IDs, typed item locators and exact relative identities. Never perform global string replacement on paths or SQL text. Handle nested/overlapping source roots by explicit membership/item identity, not ambiguous path prefixes. Preserve scope aliases, loaded state, virtual-folder organization, selected items, document positions and relevant pinned content revisions.

Cover at least `ScopeResource.Path`/source bindings, `Workbench.ActiveDocumentPath`, `DocumentWindowState.FilePath`, both ends of `ReferenceConnectionLine`, `DiagramObject.MetadataLink`, virtual-folder parent/child node keys and persisted index/document addresses. Extend `RelationalStateStore.DocumentBindings` beyond today's non-database External classification. Rebuild derived indexes/node addresses from mapped logical identities; never persist local cache paths. Keep historical snapshot content immutable. Historical external references use a versioned resolution mapping or remain explicitly unavailable, rather than rewriting historical content.

Validate the original immutable package's hashes before transformation. Compute an expected transformed-state manifest using the approved versioned mapping and explicit schema conversion. Only approved locator/configuration fields may change; ordinary content/history fields still require exact fidelity. `ValidateStoredTableAsync` currently compares original bytes with restored rows, so add transformation-aware expected-output validation for affected tables. Do not disable hash checks globally. Include input, plan and expected-output hashes in the destination operation receipt.

For populated-target replacement, write mapped source definitions, bindings and mapping records within section 9's one publication transaction. For empty-target import, batch loading may retain its existing semantics, but Ready publication requires all mapping/closure checks to pass; failures leave a non-Ready target. Standalone Relocate source publishes the source configuration and all affected active bindings atomically after staging succeeds, then invalidates old requests/caches. Source data and the original source database remain unchanged by a Transfer.

Live-SQL rebinding validates intended schema/object correspondence and marks changed/missing objects explicitly. Create Snapshot remains a separately confirmed acquisition action before source export when required: it adds a snapshot to the active Surf2 database, and the UI must disclose that preparatory change. It does not silently convert live-source bindings or saved queries into captured data. Any source edit or new capture after the input package was pinned requires a new export/preflight to include it.

### Cross-service failure and recovery

There is no transaction spanning SQL, Blob, Git and the filesystem. Track resource phases separately: Planned, Staging, Verified, SQL publication pending, Published, Cleanup pending/Failed. The SQL operation receipt is the authority for whether destination references were published. A network timeout must not trigger a second replacement or delete copied content until SQL outcome reconciliation completes.

| Failure point | Required outcome |
| --- | --- |
| Before external staging | No content copied or target application state changed. |
| During upload/download | Target references unchanged; retain a resumable operation-owned manifest or offer cleanup of only verified operation-owned artifacts. |
| After staging, before SQL commit | Original locations and target state remain intact; report staged copies. Do not describe SQL rollback as having undone external writes. |
| SQL outcome unknown | Retain all evidence and staged content; block replay/cleanup until operation identity/receipt establishes the result. |
| SQL committed, credentials unavailable on another client | Keep the published resources visible as requiring authentication; do not undo committed transfer or substitute other sources. |
| Cleanup requested | Delete only operation-owned artifacts proven unreferenced by published state and retained recovery plans. Never remove original source content, shared prefixes, user checkouts or files needed for rollback recovery. |

Completion reports copied/rebound/kept/unavailable resources, file counts and verification results, the SQL result and recovery path, and any retained staging artifacts. Exporting the old target for recovery preserves its old external references; the transfer must not alter/delete the external content they require.

## 13. Service boundaries and implementation sequence

### First milestone — local Surf2 persistence

Keep every slice usable with the current local Surf2 workspace. Verify against the current tip of `relationalDb_V2`, including the durable document links added after the original audit. Git and Blob should meet the same browse, open and search expectations as a folder once their local copies are ready.

| Order | Implementation | Exit check |
| --- | --- | --- |
| 1. Resource identity and Git | Add stable scope-node identity and location bindings where Git needs them. Preserve old folder records and durable links. Reuse `FileTreeService` Git detection and the existing physical folder reader. Add Git matching, the first-row wizard and view switch, managed checkout reuse, and cache Settings with a shared disk budget. Keep credentials on the desktop. | Both add orders produce one node. Linking and switching preserve the alternative view. Restart reuses the managed checkout. Refresh or cleanup failure cannot damage a user checkout. |
| 2. Standalone Blob | Add the container create/connect wizard, prefix selection, capacity preflight, persistent local cache and background startup refresh. Use the existing text parsing and reference pipeline with exact Blob names and locally cached content. | Empty and existing containers work. Unchanged files are reused. Changed and removed entries update browse and search. Oversized sources are refused. Measure equivalent local folder and cached Blob workloads. |
| 3. Paired Blob folders | Add the pairing wizard, saved baseline manifest, transfer preview and per-file decisions. Reuse `ResourceFileDiffWindow` for text diffs and existing resource unload behavior for the reconciliation gate. Implement explicit, conflict-checked transfers and restore/delete choices. | Both empty-side cases, divergent files and intentional deletions reconcile correctly. Startup, Refresh and Load all check. Declining, access failure, a race or a partial transfer keeps the node unloaded and out of search. A new Load rescans. |
| 4. Live SQL and queries | Add the live resource and metadata reader, existing category and document presentation, and bounded query results. Reuse capture-side metadata patterns and the existing grid. Qualify local SQL Server and Azure SQL authentication and equivalent read behavior. | Any readable table can be queried without prior capture. Permission, timeout and cancel behavior are visible. Switching scopes cannot publish an old query or index. |
| 5. Snapshot linking and refresh | Extend generated SQL, direct capture, saved revisions and export-folder provenance. Add optional live/snapshot links, view switching and **Refresh Snapshot** in both menu locations. Gate direct capture on verified catalog visibility. Reuse capture and history publication. | Old imports still work. Same-name sources stay separate. A reliable match links automatically. Restricted capture cannot publish a revision. A verified removal and a rename compare correctly, and earlier revisions stay readable. |
| 6. Local milestone verification | Run local schema upgrade, package round-trip, scope, document, snapshot and search regressions. Exercise actual Git, Blob and Azure SQL source fixtures alongside the local equivalents. Measure cache size and browse/index performance to tune defaults. | One local Surf2 workspace can use all three source types, preserve its saved state, and explain loading, capacity, conflict and permission failures. |

Make the minimum additive persistence and package changes required by these resource features. Use the existing upgrade and serialization paths and verify old folders, snapshots and open documents through the conversion. Keep any new source ID, scope-node ID and pinned item locator distinct. Preservation of these identities also prepares the later migration to Azure.

Use a verified owner context for the initial full-catalog check. A supported mechanism for proving equivalent visibility with less privilege can be added when its SQL Server and Azure SQL behavior is verified. Adjustable cache limit defaults need representative measurements. Scheduled history capture remains a separate design choice after manual **Refresh Snapshot** works.

### Later milestone — shared Azure persistence

Design identity, profiles, scope permissions and private versus shared resources before publishing migrated scopes. Include a review of which local source bindings become shared remote links, remain personal, or become snapshots. The connection-switch, migration and recovery work below belongs to this later milestone. The resource milestone uses the reusable identity, binding and authentication components as needed.

Keep SQL and catalogue rules in services; WPF event handlers bind state, request choices and display results. Suggested new types are illustrative names, not an obligation to introduce a framework.

| Work package | Changes and exit condition |
| --- | --- |
| 1. Catalogue | Evolve `PersistenceConnectionSettings`/`LocalConnectionSettingsStore`; add connection records, protected storage, migration, default invariants and revision-aware writes. Existing single-connection users retain their effective startup choice. |
| 2. Shared connection policy | Extend/refactor `SqlServerConnectionOptions`; add Azure authentication dependency and shared parsing/redaction/capability rules used by every SQL entry point. Local bootstrap creation remains separate. |
| 3. UI and test state | Replace `PersistenceSettingsPanel`; add a reusable connection dialog and row view model with draft/dirty/busy/revision/test fields. Implement all requested editing and checkbox behaviors. |
| 4. Runtime activation | Add an application-owned `PersistenceWorkspaceSwitcher` around `App`, `MainWindow.RelationalPersistence`, `RelationalApplicationConnection` and disposal. Verify current/default independence, cancellation and external-open routing. |
| 5. Selected-connection operations | Refactor `SettingsWindow.RelationalPersistence` and `RelationalPersistenceTransfer`; add selected-target import and source-to-target orchestration while reusing package services. Preserve legacy migration entry points. |
| 6. Schema and compatibility | Define the shared source/binding model and maintenance tables, then implement the coordinated schema/package revision with explicit baseline conversion. Preserve resumable schema-1→2 capture upgrade before the next upgrade. Update probes, checksums, admission, operation journal and old-client exclusion together. |
| 7. Replacement and recovery | Add a transactional replacement service, transaction-aware loaders/validators, full input/recovery-package verification, retained recovery exports and commit/rollback reconciliation. Verify current-target reopening and preservation of old application state on rollback. |
| 8. Provider and binding foundation | Add source definitions, typed item locators, local credential bindings and provider-based discovery/read/index contracts. Migrate existing folders to LocalFileSystem; extend saved windows/references/diagrams/virtual folders and package compatibility. |
| 9. Local Git, Blob and remote Git | Add one-node switching, Git linking, managed checkouts and the Blob add/create wizard. Implement persistent copies, Blob startup refresh, local cache limits and cleanup. Qualify browse, search and reference matching against local folder performance. |
| 10. Live SQL metadata | Add LiveDatabase resource kind, source editor and metadata/definition service. Adapt explorer, document bindings, reference indexing and refresh. Keep snapshot acquisition separate and usable with both persistence locations. |
| 11. Live queries | Implement the SQL Query view/executor and bounded result adapter over existing grid/scratch contracts, source permissions, parameters, limits, cancellation and saved-query behavior. Every accessible table is queryable without capture. |
| 12. Resource relocation | Add inventory/planning, Git correspondence, Blob upload/download, Git materialization, verified manifests, exact binding mapping and transformation-aware package validation. Integrate before SQL publication; provide operation reconciliation and owned-artifact cleanup. |
| 13. Remote qualification | Exercise Azure SQL initialization/authentication/import/replacement, live metadata/query/capture, Blob/Git access and relocation in both directions against explicitly owned fixtures. Verify offline/auth/permission and interrupted-publication outcomes. |
| 14. Release | Run the full acceptance matrix, document setup/limits/recovery, and update existing persistence/resource/query help. |

One application-level operation coordinator must serialize conflicting switch/import/transfer/remove actions. Keep immutable operation contexts containing captured record IDs/revisions, endpoint options and verified database identities. Persist no decrypted options or secrets in progress/error records. Preserve bounded streams and the branch's session-epoch/stale-result protections.

Recommended service responsibilities: `FolderSourceProvider` owns listing/reads; `LocalGitInspector` owns checkout facts; `LiveSqlMetadataService` owns source discovery; `LiveSqlQueryService` owns bounded execution; `ResourceRelocationPlanner` owns choices/manifests; `ResourceBindingMapper` owns reviewed transformations; `PersistenceTransferCoordinator` orders external staging, recovery export and SQL publication. Keep provider reads separate from relocation writes so browsing cannot accidentally trigger upload, checkout or replacement.

Use packages 1–7 to deliver connection UX, maintenance and recovery in the later milestone. Packages 8–11 describe reusable components delivered through the local sequence above. Relocation integration builds on those components, the package format and the replacement service. Qualify the shared Azure deployment after the resource and access rules are in place.

## 14. Acceptance and regression criteria

| Area | Required acceptance cases |
| --- | --- |
| Layout and input | Read-only current label; both grids; click-to-open large editor; Save/Close/dirty cancellation; keyboard navigation; long strings; light/dark themes and supported window sizes. |
| Row creation and saving | Repeated + creates one incomplete row per grid; Name-only/ConnectionString-only stays a draft; complete rows save automatically, including offline entries; blank edits and disk errors do not destroy saved values. |
| Default invariant | Exactly one saved default across both grids after add/edit/remove/reload/migration; checked row cannot be unchecked alone; concurrent updates cannot persist zero or multiple defaults. |
| Current lifecycle | Changing default does not switch current; Set as Current does not move default; unsaved-work cancellation preserves old runtime; successful switch loads destination preferences/data and rebinds pipes; current-target replacement reloads a fresh owner; stale work from any client cannot publish into the new database identity. |
| Format classification | Verified Empty and Ready are green; foreign/legacy/future/invalid/incomplete are orange; network/TLS/auth/metadata failures are red; no test writes; hidden metadata and system databases never become usable empty targets. |
| Status freshness | Editing connection/category invalidates the badge; late test responses are ignored; operation preflight detects changed credentials, contents, identity and permissions. |
| Azure | SQL password and Entra interactive/MFA; cancelled sign-in; valid/invalid certificates; private/firewall failures; explicit database required; no implicit trust bypass; contained target access without `master`/sysadmin permissions. |
| Context menus | Every action uses the clicked row even when another row/current database is selected; remove affects only configuration; protected current/default/final-row removal rules hold. |
| Export and import | Selected non-current source export; safe legacy export retained; target-specific empty import and V2 replacement; structural preflight before mutation and full validation before replacement; empty-import corruption leaves non-Ready target; replacement corruption preserves old data; round-trip history/captures/raw values/assets/preferences; catalogue/credentials excluded. |
| Transfer | Local→Local, Local→Azure, Azure→Local, Azure→Azure with Empty and populated Ready targets; different credentials; same-database aliases and Empty sources blocked; full replacement with no merged leftovers; source and current/default connection selections unchanged. |
| Confirmation and recovery export | Cancel leaves target unchanged; confirmation identifies exact endpoints and persistent recovery path; every recovery entry/hash/count verifies before replacement; disk-full/export/verification failures prevent replacement; file retained on success and failure; recovery package restores old target application data in an isolated fixture. |
| Maintenance and compatibility | Concurrent writers drain before recovery snapshot; writers cannot slip in afterward; old clients are excluded; queued writes recheck identity after admission; lock loss fails closed; schema upgrade and baseline-package compatibility work; unknown dependencies fail without arbitrary deletion. |
| Transactional replacement | Interrupted bulk batch, constraint/model failure, cancellation and capacity/time-limit failures roll back prior application data; all validators see the same transaction; new identity/operation receipt publish atomically; stale sessions are invalidated; no per-batch commits occur. |
| Failure and recovery | Cancel in every phase; crash before/after commit; lost acknowledgement reconciled without replay; unresolved outcomes block normal use; empty-import partial target remains non-Ready; replacement rollback restores the previous usable contents; reopen failure after commit reports committed success accurately. |
| Bootstrap and protection | Old single-string migration is idempotent; missing versus corrupt/locked files distinguished; undecryptable payloads recover visibly; environment overrides honored; no plaintext secrets in JSON, package metadata, logs or previews. |
| Remote folder registration | Local/Blob/Git choices, valid root/ref/subfolder selection, source reuse across scopes, aliases, IncludeChildren and access tests. Add Blob uses an existing container or creates an empty one in the selected account after an explicit action. |
| Blob folders | Paged enumeration and exact prefix boundaries, empty container, blob/prefix name collision, permitted authentication options, expired credentials and offline status. Restart with the same contents and reuse the copy. Refresh with new, changed and removed blobs. Interrupted listing or a changed ETag retains the last usable generation. |
| Git folders | Add in either order and obtain one node. Reject an unrelated checkout. Verify the four icon and first-row states, switch atomically or retain the current view on failure, reuse the managed checkout on restart, and fetch on Refresh. Exercise HTTPS and SSH, branch/tag/commit/subfolder, pinned open documents, Windows path collisions and special Git entries. Verify user checkouts retain their files. |
| Paired folder reconciliation | Independent resources remain usable. An explicit pair uses the selected local folder. Verify empty-side transfer, equal files, text diff, keep-local and keep-Blob choices, subsequent edits and restore/delete choices. Startup, Refresh and Load compare again. Decline, cancellation, changed files and failed verification keep the pair unloaded and excluded from search and references. |
| Cache limits and cleanup | Reject oversized Blob sources before downloading. Enforce limits during a growing Blob refresh and a Git fetch of unknown size. Account for simultaneous downloads and staging. Retain the previous view after a budget failure. Remove a managed copy and verify the source node remains available to download again. |
| Remote folder performance | On a representative desktop, compare browse, file open, search and reference matching with the same local files after indexing. Record first download, first index and refresh separately. |
| Remote search and identity | Browse/open/name/content search/reference/compare use provider locators; Windows-invalid and case-colliding names remain distinct; no path traversal; limits/cancellation/partial listings do not produce false complete indexes; stale callbacks cannot publish. |
| Local Git recognition | `.git` directory and relative/absolute gitdir file, nested scope, linked worktree, detached/unborn HEAD, branch change, missing remote, multiple remotes and upstream mismatch; ordinary folder browsing survives Git inspection failure. |
| Live SQL resource | Local/Azure source × local/Azure persistence; ordinary non-Surf2 source allowed while barred as persistence target; no schema writes on Test; visible partial metadata/permission failures; source credential rebinding after package import. |
| Live definitions and bindings | Categories match snapshots; objects open without fake snapshot keys; encrypted/inaccessible definitions isolated; rename/drop/recreate and source-generation changes detected; refresh/search/reference coverage accurate; stale requests cannot publish. |
| Live queries | Every permitted table offers View Data without HasFullData; generated and editable single SELECT/CTE/typed parameters; rejected writes/SELECT INTO/multiple statements; read-only SQL principal; quoting, null/Unicode/decimal/raw values, duplicate columns and unsupported types; row/byte/cell/time caps, cancel/timeout/network failures; scroll does not re-run query; source connection released; saved windows never auto-execute. |
| Snapshot independence | Generated-SQL result import, direct capture and exported-folder import continue to work; completed snapshot remains readable after original source is disconnected/deleted; local and Azure sources can be captured into either persistence location; snapshots retain existing history/data semantics. |
| Snapshot links and capture visibility | Verify legacy origin handling, generated-query provenance, round-trip export/import, duplicate names, changed identity, both menu positions, and historical revision selection. Direct capture with unverified or specifically denied catalog visibility preserves the last revision. Verified removal and a rename compare by schema and name. |
| Reference-only package transfer | Local/Azure SQL round-trips preserve source definitions, saved queries and typed bindings without SAS/tokens/cache paths; authentication rebinding is visible; recovery restores references without implying external-content backup; existing remote content remains unchanged. |
| Relocation planning | Same-desktop Azure persistence can keep local paths; all scopes/unloaded resources inventoried; reused/nested sources mapped unambiguously; inaccessible resource requires explicit keep-unavailable choice; selected existing Blob contents verified; live SQL migration does not copy the business DB. |
| Git relocation | Unpushed commits and staged/unstaged/untracked/ignored files surfaced; corresponding remote commit/subfolder verified; dirty content preserved by reviewed Blob copy or explicitly excluded; no automatic push/commit; reverse materialization does not overwrite a dirty checkout. |
| Blob/local copying | Exact selected bytes/line endings and hashes; source changes during staging; version/ETag checks; expired credentials, retry and cancellation; traversal, Windows names, case collisions and existing-file conflicts; partial uploads never published as a complete source. |
| Mapping fidelity | Source memberships, workbenches, open windows, both reference ends, diagram links and virtual folders remapped; original input and expected transformed output separately validated; historical content unchanged; unavailable pinned revisions visible; no global path replacement or disabled checksum checks. |
| Cross-service recovery | Upload succeeds then SQL fails; destination commits then client disconnects; empty import remains non-Ready on mapping failure; no unsafe replay; staged artifact ownership checked before cleanup; external content referenced by recovery exports retained. |
| Version compatibility | Test performs no schema upgrade; required upgrade clearly disclosed; schema-1→2 cancellation/resume/data fidelity retained; schema 2→new schema migration and version-2 packages converted explicitly; old clients excluded; schema-2 FullTableData and mapped state roll back with the replacement transaction. |

Extend `StorageCases/BootstrapCases.cs`, `TransferCases.cs`, `PackageExportCases.cs`, `PackageImportCases.cs`, `NewDatabaseCases.cs`, `JsonCaptureUpgradeCases.cs`, `JsonCaptureStartupCases.cs`, legacy-package cases and existing WPF runtime/settings fixtures. Add focused replacement, maintenance/upgrade, recovery, current-target reload, live source/query and relocation/provider fixtures. Use deterministic provider fakes for paging/failure/revision races, plus owned Blob/Git and ordinary source-SQL integration fixtures. Explicitly test schema-1 packages as well as packages generated by the current exporter. Reuse the regression runner and `tools/verify-relational-v2.ps1`; retain grid/document/explorer regressions.

Azure integration tests need their own explicitly owned, disposable, preprovisioned fixtures. Do not reuse LocalDB create/drop assumptions or run destructive tests against saved user connections. Unit tests cover state/invariants; SQL integration tests prove recognition and data preservation; WPF tests prove lifecycle and user-visible command behavior.

Local milestone completion requires the six exit checks in section 13 and the applicable local relational regressions. Qualify Git and Blob and both live SQL platforms while Surf2 persistence is local. For the later milestone, also verify actual data and history through Azure transfers into empty and populated destinations. Verify replacement, recovery exports, maintenance isolation, crash recovery and resource relocation in both directions with the shared access rules in effect.

## 15. Repository reference map

Paths below are relative to the repository root at the baseline commit.

- UI: `Surf2/SettingsWindow.xaml`, `Surf2/SettingsWindow.xaml.cs`, `Surf2/SettingsWindow.RelationalPersistence.cs`, `Surf2/SettingsWindow.RelationalPreferences.cs`.
- Catalogue/startup: `Surf2/Models/PersistenceConnectionSettings.cs`, `Surf2/Storage/LocalConnectionSettingsStore.cs`, `Surf2/Storage/SqlServerConnectionOptions.cs`, `Surf2/Services/RelationalDatabaseBootstrap.cs`.
- Runtime ownership: `Surf2/App.xaml.cs`, `Surf2/MainWindow.xaml.cs`, `Surf2/MainWindow.RelationalPersistence.cs`, `Surf2/MainWindow.RelationalState.Commands.cs`, `Surf2/Storage/Relational/RelationalApplicationConnection.cs`, `Surf2/Storage/Relational/RelationalSession.cs`.
- Database recognition/provisioning: `Surf2/Storage/Relational/PersistenceFormatProbe.cs`, `Surf2/Storage/Relational/RelationalSchemaInstaller.cs`.
- Transfer/packages: `Surf2/Services/RelationalPersistenceTransfer.cs`, `Surf2/Storage/Relational/Packages/RelationalPackageExporter.cs`, `Surf2/Storage/Relational/Packages/RelationalPackageImporter.cs`, `Surf2/Storage/Relational/Packages/README.md`, `Surf2/Services/RelationalDocuments/LegacyPersistenceExport.cs`.
- Folder resources and discovery: `Surf2/Models/ScopedResource.cs`, `Surf2/Models/ResourceKind.cs`, `Surf2/Services/RelationalExplorer/ExplorerContracts.cs`, `Surf2/Services/RelationalExplorer/PhysicalExplorerQueries.cs`, `Surf2/Services/RelationalExplorer/ExplorerResourceCatalogue.cs`, `Surf2/Services/RelationalExplorer/RelationalExplorerService.cs`.
- Remote read/index/binding integration: `Surf2/Services/RelationalExplorer/MemoryReferenceIndex.cs`, `Surf2/Services/RelationalExplorer/PhysicalReferenceWatch.cs`, `Surf2/Services/RelationalDocuments/DocumentContracts.cs`, `Surf2/Services/RelationalDocuments/RelationalDocumentService.cs`, `Surf2/Storage/Relational/State/RelationalStateStore.DocumentBindings.cs`, `Surf2/Storage/Relational/Schema/001.State.sql`, `Surf2/Storage/Relational/Packages/PackageTableCatalogue.cs`.
- Local Git and source viewing: `Surf2/Services/FileTreeService.cs`, `Surf2/Services/RelationalExplorer/CONTRACT.md`, `Surf2/RelationalScopePickerWindow.cs`, `Surf2/Controls/FloatingCodeWindow.xaml`.
- Snapshot acquisition: `Surf2/DatabaseResourceWorkflowWindow.cs`, `Surf2/RelationalDatabaseCaptureWindow.cs`, `Surf2/Services/RelationalSnapshots/RelationalDatabaseCaptureService.cs`, `Surf2/Services/RelationalSnapshots/SqlSnapshotSource.cs`, `Surf2/Services/RelationalSnapshots/SnapshotOperationContracts.cs`, `Surf2/Services/DatabaseMetadataImportService.cs`, `Surf2/Services/DatabaseExportFolderImportService.cs`, `Surf2/Services/RelationalSnapshots/RelationalSnapshotEditorService.cs`.
- Query generation and documents: `Surf2/Services/DatabaseMetadataQueryService.cs`, `Surf2/Services/DatabaseQueryFileService.cs`, `Surf2/Services/RelationalDocuments/RelationalDocumentService.Content.cs`, `Surf2/Models/DatabaseMetadataSnapshot.cs`, `Surf2/Models/OpenDocumentState.cs`, `Surf2/Storage/Relational/Access/QueryLocators.cs`.
- Grid result reuse: `Surf2/Services/RelationalGrid/GridContracts.cs`, `Surf2/Services/RelationalGrid/GridSourceBase.cs`, `Surf2/Services/RelationalGrid/GridQuerySession.cs`, `Surf2/Services/RelationalGrid/CapturedGridSource.cs`, `Surf2/Services/RelationalGrid/README.md`, `Surf2/Controls/RelationalGrid/README.md`.
- Current capture schema/upgrade: `Surf2/Storage/Relational/Schema/002.FullTableData.sql`, `Surf2/Storage/Relational/RelationalCaptureSchemaUpgrade.cs`, `Surf2/Storage/Relational/Packages/RelationalPackageImporter.FullTableData.cs`, `Surf2/Storage/Relational/Packages/RelationalPackageImporter.Validation.cs`.
- Baseline architecture: `docs/relational-v2.md` and existing relational persistence/query implementation plans.
