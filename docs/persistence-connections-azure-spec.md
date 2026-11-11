# Persistence connection management and Azure SQL implementation specification

Status: proposed implementation specification. Date: 11 November 2026.

Baseline: `relationalDb_V2`, commit `91409844647b969f1797f762e0e093faa6f26e1f` (`Comparison UI`).

Replace the single connection editor in Settings > Persistence with a local connection catalogue, separate Local and Azure grids, a read-only current-connection label, and connection-to-connection transfer. A user must be able to work against an existing Azure SQL Database using the same relational persistence services as local SQL Server.

The supplied mock-up and accompanying request define the layout and required actions. This specification adds the lifecycle, compatibility, failure, and migration rules needed to implement them against the branch. It specifies application changes; it does not perform them.

## 1. Scope and decisions

The release includes connection editing and local storage, a single default across both grids, read-only database testing, switching the running workspace, selected-connection package export/import, and transfers between local SQL Server and Azure SQL Database in either direction. Local-to-local and Azure-to-Azure transfers use the same path.

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

Automatic database synchronization, Azure resource management, portable connection-profile export, and changes to database snapshot capture are outside this change.

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

The current database format is `Surf2.RelationalV2`, **schema version 1**. The relational package identifier is `Surf2.RelationalV2.Package`, package version 2. Product V2, database schema version and package version are separate values.

Replacement adds a versioned maintenance/operation contract and therefore requires an explicit schema upgrade from this baseline, updated compatibility checks and package handling as specified in section 9. The baseline version numbers above do not imply that this feature needs no schema migration.

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
| Connection failure | Red X and short reason | Malformed string, network/TLS/authentication failure, missing database or unreadable required metadata. No data operations. |
| Incompatible or incomplete | Orange ! and short reason | Unknown/non-Surf2 database, unsupported/future format, invalid marker, legacy migration required, unfinished import/migration, or unresolved replacement. Cannot become current or be selected as either transfer endpoint. |

Keep accessible text and tooltips with every icon; color alone is insufficient. Distinguish `Cannot connect`, `Cannot verify database contents`, `Migration required`, and `Import incomplete` in the detailed explanation.

Reuse `PersistenceFormatProbe` checks for the exact format identifier, supported schema and reader/writer versions, marker structure, singleton, nonempty database identity, state and schema checksum. Keep its conservative emptiness rule: no user objects/types/schemas and sufficient metadata visibility. Failure to see tables due to permissions must never qualify a database as empty. Block system databases such as `master`, `model`, `msdb` and `tempdb` from initialization/activation/transfer.

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
| Import Database | Choose a supported package and import it into the selected connection using its full options. A V2 package may replace a supported Ready target using the same confirmation, recovery export and transactional replacement path as Transfer. An empty target uses the existing importer. Preview package type/destination before changes; do not ask for a new same-server database name. |
| Remove Connection | Remove the local catalogue record only. Never drop the database or delete its contents. |

Export includes authoritative Surf2 application state, snapshots, captured rows, history, diagrams, workbenches, preferences and SQL-stored assets according to the existing package contract. Exclude connection profiles, credentials, default/current selection, caches and logs. Referenced physical files remain references unless an existing explicit file-inclusion workflow is invoked; ordinary database transfer does not copy arbitrary local folders.

Preserve V2 package limits, hashes/counts, schema checks, data fidelity, independent destination identity and regenerated rowversion tokens. Never execute stored SQL definitions or package-provided DDL. Keep the existing safe legacy package conversion path available for import into an empty destination; do not reuse the old broad-AppData export behavior in `PersistencePackageService`. A legacy package must first be converted in an empty database before its V2 contents can replace an existing target. Reject unsupported package versions and detectable ZIP/manifest structural errors before destination mutation. For an empty target, streamed row/content/checksum failures can leave a non-Ready database. Replacement additionally requires full package prevalidation and transactional rollback, as described in section 9. Legacy database migration remains a separate workflow, not implicit activation of an orange row.

Disable removal of the current connection until another has become current. To remove the default, require selection of another saved record and publish removal/replacement together. Prevent removal of the final saved connection. Remove an unsaved draft without database effects. Clear transfer selections referencing a removed row and delete its stored protected payload.

Do not change the selected current/default connection records on successful export, import or transfer. If the target is already current, replacement must suspend and then reopen its workspace with a fresh runtime and database identity; the selected connection remains the same. A completion action for any other target may offer **Set as Current**, which must invoke the same handover flow.

## 9. Transfer contract and target policy

Display `FROM: [connection]  TO: [connection]  [OK]` as in the mock-up. Use the union of both saved grids, with group/name and safe endpoint details. Show ineligible entries disabled with their reason; orange rows cannot be selected at all. Drafts never appear. Untested rows require Test before becoming selectable.

FROM requires a supported Ready V2 database. Empty databases show `No Surf2 data to transfer` and cannot be a source. TO accepts either Empty or supported Ready V2, including a database with existing data. A Ready target displays `Existing Surf2 contents will be replaced after a recovery export`. Clear a selection if an edit/test makes it ineligible. Enable OK only with two eligible, distinct endpoints and no operation already running. Check replacement capability during preflight; failure to establish safe exclusive access stops the operation with a reason.

Reject source and destination that resolve to the same physical database, even through different names, credentials or aliases. Use the existing `SameDatabase` logic as a starting point, verify its server/database identity behavior on Azure, and combine it with canonical endpoint evidence and pinned Surf2 identity. Neither connection-string equality nor a copied Surf2 marker alone proves distinct physical databases. If distinction cannot be established, stop before writes with an explanation.

### Transfer sequence and confirmation

1. Capture both connections and block conflicting edits/operations. Freshly test format, permissions, supported schema and physical separation. Resolve pending unsaved work if this application's current database is either endpoint. Target saves complete before taking its recovery snapshot.
2. Export a consistent source snapshot to an operation-owned local temporary package using `RelationalPackageExporter`. For package import, pin the selected archive instead. For replacement, perform a full bounded streaming validation pass over the input, including every table/local-file stream, declared counts and hashes, before target mutation. Keep the package immutable through validation and use.
3. Show one final confirmation naming FROM (or package) and TO with server/database details. For a Ready target, state `Replace all Surf2 data in <target> with <source>? Its existing contents will first be exported to <recovery path>. The source and your default connection will remain unchanged.` Use **Replace target contents** and **Cancel** buttons. Display a unique recovery filename in a persistent user-selected folder; do not overwrite an older recovery file. For Empty, confirm import/initialization without claiming existing data needs a recovery export.
4. Recheck the pinned target identity and format after confirmation. If it changed, stop and request a new preflight/confirmation. For Ready, establish the maintenance exclusion below, export and fully verify the recovery package, then run transactional replacement. For Empty, retain the existing fresh-target importer and migration lease; do not initialize it as Ready first.
5. Validate all imported data and constraints, publish Ready last, and verify the committed operation and new identity. Reopen a current target with fresh runtime state; otherwise leave current untouched. Report destination, available counts and the retained recovery-package path, and refresh its badge.
6. Dispose connections/leases and remove only disposable temporary source files owned by the operation. Keep the recovery export after success, cancellation or failure. Report cleanup problems and retain operation evidence required for reconciliation.

Maintain the exporter's consistent snapshot requirement. Where snapshot isolation is unavailable, offer the existing explicit SERIALIZABLE blocking alternative with an explanation that source writers may wait; never silently weaken consistency or enable database options. No distributed transaction spans the two servers. Source consistency ends at the exported snapshot; later source changes are not included.

### Maintenance access and schema compatibility

Add a database-scoped maintenance admission protocol and durable operation journal. A proposed `surf.PersistenceOperation` record holds operation ID, kind, status, expected old/new database identities and source/recovery package hashes. A durable local journal records the same operation ID, connection-record ID/revision, safe physical target identifiers, source package reference and recovery path/hash; store no credentials or decrypted connection strings in either journal. Exclude operational records from exported application data.

All normal persistence operations must acquire shared admission before checking readiness/identity and retain it through their SQL work. Write paths acquire it in the same transaction that performs the write, and recheck identity/format after acquisition. Cover state, snapshots, capture, background index work, schema changes and import; a check before a later transaction is insufficient. A replacement coordinator holds exclusive admission continuously from before the target recovery snapshot until publication or reconciled rollback. Retain the existing migration lease too, with one documented acquisition order to avoid deadlocks. Maintenance-owned exporter/validator work must use the coordinator's authorized context rather than deadlock waiting for normal admission.

Use database-scoped application locks with explicit ownership, bounded waits and checked return codes. Session locks disappear when the owning session ends, so durable recovery gating remains necessary after disconnection. [SQL application locks](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver17)

The baseline migration lease coordinates import/migration only; it does not exclude ordinary writers. Introduce a versioned schema upgrade and reader/writer compatibility gate for this new protocol. Drain and exclude pre-protocol client sessions as part of enabling it, and reject older clients on reconnect. An application lock does not constrain old clients or independent SQL writers that ignore it. Require exclusive maintenance access against those writers too; if it cannot be established, refuse replacement before replacing application data. Never treat a successful migration lock alone as proof of exclusivity.

Update exact schema/checksum validation, installer scripts, supported reader/writer versions and package compatibility together. Preserve a read/export/upgrade path for the baseline schema, and an explicit import mapping for its existing V2 packages; do not label every baseline database unusable after introducing the feature. Compatibility preparation is separate from the destructive replacement: explain and obtain confirmation for a required schema upgrade, preserve application contents through that upgrade, and finish it before the final replacement confirmation and recovery snapshot. Cancelling replacement does not undo a separately completed upgrade.

### Recovery export

Under exclusive maintenance access, export the target's complete supported Surf2 state using the package contract, then verify the finished archive by streaming every entry and checking structure, counts and hashes. Flush and atomically publish the file before replacement may begin. Record its operation ID, manifest hash, size and path in the durable local journal and retain an immutable read lease. Recheck ownership/target identity before proceeding. A failed export, verification failure, disk-space/permission problem or cancellation stops replacement with the old application data intact.

The recovery export represents the target immediately before replacement; no other writer may change it between that snapshot and publication. It contains the target's application state and SQL assets, with the same external-file and credential exclusions as ordinary export. It is a persistent recovery artifact, not a temporary file, and is never silently removed on success. Completion and recovery views provide **Open recovery folder** and instructions to import it into an empty database, or explicitly replace a compatible target through this same guarded workflow.

### Transactional replacement

Implement a distinct replacement service. Reuse package codecs, whitelists and validation logic; refactor the loader/validators to accept one caller-owned SQL connection and transaction. **Do not invoke the existing batch-committing `ImportAsync` against a cleared target.** Every target-side mutation and validation, including bulk-copy batches, capture-table DDL, identity reseeding, constraints, derived-state rebuilding and publication, must use that same transaction. Existing validators that open separate sessions must be adapted to see the uncommitted replacement. Do not use per-batch internal transactions.

`SqlBulkCopy` supports an existing `SqlTransaction`, allowing multiple copy operations and other changes to commit or roll back together. Use that contract for the replacement batches. [Transactions and bulk copy](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sql/transaction-and-bulk-copy-operations)

Within one target transaction at SERIALIZABLE isolation, clear/replace the known Surf2 application content and generated capture objects, load the source package, rebuild derived state, validate models/content/constraints, and assign a fresh database identity and rowversion values. Include the completed operation ID and hashes in the final publication. Commit only after all checks pass. Preserve the target database/server, users, roles, grants, network settings and local connection record. Retain installed schema/object permissions; for any owned object recreated by the restore, record and reapply its explicit permissions within the same transaction, or refuse during preflight if they cannot be preserved. Only known application-owned content may be reset. Unexpected objects or dependencies that prevent a complete safe replacement cause preflight failure, not arbitrary object deletion.

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

## 10. Service boundaries and implementation sequence

Keep SQL and catalogue rules in services; WPF event handlers bind state, request choices and display results. Suggested new types are illustrative names, not an obligation to introduce a framework.

| Work package | Changes and exit condition |
| --- | --- |
| 1. Catalogue | Evolve `PersistenceConnectionSettings`/`LocalConnectionSettingsStore`; add connection records, protected storage, migration, default invariants and revision-aware writes. Existing single-connection users retain their effective startup choice. |
| 2. Shared connection policy | Extend/refactor `SqlServerConnectionOptions`; add Azure authentication dependency and shared parsing/redaction/capability rules used by every SQL entry point. Local bootstrap creation remains separate. |
| 3. UI and test state | Replace `PersistenceSettingsPanel`; add a reusable connection dialog and row view model with draft/dirty/busy/revision/test fields. Implement all requested editing and checkbox behaviors. |
| 4. Runtime activation | Add an application-owned `PersistenceWorkspaceSwitcher` around `App`, `MainWindow.RelationalPersistence`, `RelationalApplicationConnection` and disposal. Verify current/default independence, cancellation and external-open routing. |
| 5. Selected-connection operations | Refactor `SettingsWindow.RelationalPersistence` and `RelationalPersistenceTransfer`; add selected-target import and source-to-target orchestration while reusing package services. Preserve legacy migration entry points. |
| 6. Maintenance and compatibility | Implement versioned schema upgrade, shared operation admission, exclusive maintenance, durable operation journal and old-client exclusion. Update probes, checksum/version rules and baseline package import/export compatibility together. |
| 7. Replacement and recovery | Add a transactional replacement service, transaction-aware loaders/validators, full input/recovery-package verification, retained recovery exports and commit/rollback reconciliation. Verify current-target reopening and preservation of old application state on rollback. |
| 8. Azure qualification | Exercise target-only initialization, both authentication modes, endpoint identity, permissions, snapshot export, empty import, populated-target replacement and remote failures against explicitly owned Azure test databases. |
| 9. Release | Run the acceptance matrix, document setup and recovery, and update existing persistence documentation/menu help to remove obsolete single-connection/restart-only guidance. |

One application-level operation coordinator must serialize conflicting switch/import/transfer/remove actions. Keep immutable operation contexts containing captured record IDs/revisions, endpoint options and verified database identities. Persist no decrypted options or secrets in progress/error records. Preserve bounded streams and the branch's session-epoch/stale-result protections.

## 11. Acceptance and regression criteria

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

Extend `StorageCases/BootstrapCases.cs`, `TransferCases.cs`, `PackageExportCases.cs`, `PackageImportCases.cs`, `NewDatabaseCases.cs`, legacy-package cases and the existing WPF runtime/settings fixtures. Add focused replacement, maintenance/upgrade, recovery and current-target reload fixtures. Reuse the repository's regression runner and `tools/verify-relational-v2.ps1`.

Azure integration tests need their own explicitly owned, disposable, preprovisioned fixtures. Do not reuse LocalDB create/drop assumptions or run destructive tests against saved user connections. Unit tests cover state/invariants; SQL integration tests prove recognition and data preservation; WPF tests prove lifecycle and user-visible command behavior.

Completion requires the new workflow and the existing relational regression suites to pass, plus Azure transfers that verify actual data/history in both empty and populated destinations. Populated-target replacement, verified retained recovery exports, maintenance isolation and crash/rollback reconciliation are required release criteria. Empty-target transfer alone does not complete this specification.

## 12. Repository reference map

Paths below are relative to the repository root at the baseline commit.

- UI: `Surf2/SettingsWindow.xaml`, `Surf2/SettingsWindow.xaml.cs`, `Surf2/SettingsWindow.RelationalPersistence.cs`, `Surf2/SettingsWindow.RelationalPreferences.cs`.
- Catalogue/startup: `Surf2/Models/PersistenceConnectionSettings.cs`, `Surf2/Storage/LocalConnectionSettingsStore.cs`, `Surf2/Storage/SqlServerConnectionOptions.cs`, `Surf2/Services/RelationalDatabaseBootstrap.cs`.
- Runtime ownership: `Surf2/App.xaml.cs`, `Surf2/MainWindow.xaml.cs`, `Surf2/MainWindow.RelationalPersistence.cs`, `Surf2/MainWindow.RelationalState.Commands.cs`, `Surf2/Storage/Relational/RelationalApplicationConnection.cs`, `Surf2/Storage/Relational/RelationalSession.cs`.
- Database recognition/provisioning: `Surf2/Storage/Relational/PersistenceFormatProbe.cs`, `Surf2/Storage/Relational/RelationalSchemaInstaller.cs`.
- Transfer/packages: `Surf2/Services/RelationalPersistenceTransfer.cs`, `Surf2/Storage/Relational/Packages/RelationalPackageExporter.cs`, `Surf2/Storage/Relational/Packages/RelationalPackageImporter.cs`, `Surf2/Storage/Relational/Packages/README.md`, `Surf2/Services/RelationalDocuments/LegacyPersistenceExport.cs`.
- Baseline architecture: `docs/relational-v2.md` and existing relational persistence/query implementation plans.
