# Surf 2.0 Relational Persistence Implementation Plan

Status: proposed design for review. Date: 6 October 2026.

Retain SQL Server and replace collection-sized JSON documents with relational records that can be read independently. Preserve existing data, history, Object Explorer hierarchy, links, diagrams, workbenches, and settings. Design the schema around browsing, opening a document, resolving references, searching a scope, paging table data, and inspecting history.

This stage covers the storage model, legacy mapping, indexes, migration, validation, and implementation sequence. Detailed application-side repositories, caching, cancellation, and UI orchestration are the next design stage. No production schema changes or application implementation are authorized by this document.

## 1. Decisions and Scope

### Agreed decisions

- Keep SQL Server for local per-user persistence. Do not introduce SQLite.
- Replace whole-library reads and writes with independently addressable entities.
- Do not require all persisted data to be in application memory before browsing or searching.
- Detect supported legacy formats before the existing loader or initializer writes anything.
- Offer migration into a separate destination database. Retain the original database and local files.
- Preserve existing substring and .NET regular-expression behaviour unless a separate feature change is approved.
- Prepare stable identities and explicit revisions for possible future sharing, but do not implement a central repository or synchronization now.

### Boundaries

Source code, SQL definitions, rich-text XAML, and image bytes are legitimate document content. They do not become individual relational rows per character or XML element. The problem is embedding entire collections, histories, and table datasets inside one payload, not storing an individual document in a large-value column.

The application persistence database remains separate from databases whose metadata or data Surf captures. Imported SQL definitions remain inert text. Captured schemas must never be executed as application DDL without validation and translation.

Connection bootstrap information remains local so Surf can locate its persistence database. Existing external files remain authoritative; any searchable copy is a derived index, not a replacement for the file.

## 2. Current Storage Inventory

The current physical schema is `app.Surf2Documents`:

| Column | Current SQL type | Meaning |
| --- | --- | --- |
| `DocumentKey` | `nvarchar(128)`, primary key | Selects an application-wide document |
| `PayloadJson` | `nvarchar(max)` | Serialized object graph |
| `UpdatedAtUtc` | `datetime2(3)` | Last whole-document write |

The SQL stores use six known keys. Each can fall back to a local JSON file when its SQL document is absent.

| SQL document key | Local fallback file under `%APPDATA%\Surf2` | Root model | Relational destination |
| --- | --- | --- | --- |
| `scope-library` | `scopes.json` | `ScopeLibrary` | Scopes, scope resources, virtual folders, selection preference |
| `database-snapshots` | `database-snapshots.json` | `DatabaseSnapshotLibrary` | Snapshot catalogue, metadata, captured data, history |
| `diagram-library` | `diagrams.json` | `DiagramLibrary` | Diagrams, revisions, objects, workflows, queries, links, assets |
| `workbench-library` | `workbenches.json` | `WorkbenchLibrary` | Workbenches, document layouts, saved diagram revisions, connection lines |
| `workspace-state` | `workspace-state.json` | `WorkspaceState` | Current workspace session, document layouts, unloaded resources |
| `app-settings` | `settings.json` | `AppSettings` | Preferences, appearance rules, input preferences, image definitions |

`connection-settings.json` is bootstrap configuration, not another SQL document. The environment override for the connection string must continue to work.

Persistence export currently packages SQL documents in `database/surf2-documents.json`, a manifest, and local files. Its package format version is 1. This is a separate version system from the `ScopeLibrary.SchemaVersion` value of 1 and `DatabaseSnapshotLibrary.SchemaVersion` value of 2. None is an existing relational database schema version.

Startup loads all six root models, including every captured dataset and its history. The database metadata store interface only supports loading and saving the complete library. A later application-access redesign must remove that requirement rather than reconstruct the same library from normalized tables.

## 3. Schema Conventions

### Ownership and identities

Use an application-owned `surf` schema for catalogue and state tables and a separate `capture` schema for generated captured-data tables. Leave `app.Surf2Documents` unchanged in legacy sources.

Use `bigint` surrogate keys for internal joins. Preserve original string identifiers in explicit legacy identity columns or alias records; do not assume every imported identifier is a valid GUID. Add stable public identifiers to externally addressable entities so later sharing does not depend on local identity values.

Existing identifiers are only unique in their appropriate context. For example, a diagram object identifier can recur in a saved workbench's embedded copy of the same diagram. A query identifier can recur in cloned metadata. Scope uniqueness by the owning diagram revision, workflow item, or other actual owner, not globally.

Collection entries without identifiers receive an identity once, recorded in a migration mapping keyed by source document, owner, collection name, and original ordinal. Rerunning migration must reuse that mapping. Preserve collection order explicitly, including duplicate entries where the source permits them.

### Types and constraints

| Value | Proposed storage policy |
| --- | --- |
| Small enum | Integer code with a documented mapping and a check constraint |
| Boolean | `bit` |
| Counts and row ordinals | `bigint` where counts can exceed an `int` |
| UTC or offset timestamps | `datetimeoffset(7)` for model timestamps; retain existing precision and offsets |
| WPF coordinates, zoom, font sizes | `float(53)` to match persisted .NET `double` values |
| Names, identifiers, paths | Unicode text; bounded lengths only after preflight proves the values fit |
| Code and documentation | `nvarchar(max)` in separately fetched content rows |
| Images | `varbinary(max)` in separately fetched asset rows |
| Optimistic concurrency | SQL `rowversion` on mutable entities; this is not a date or historical version number |

Use foreign keys for owned relational data. Persist external or unresolved locators separately with a nullable resolved target and a resolution status. Missing files or an unresolved portal must not cause otherwise valid data to disappear.

Do not use names, mutable display aliases, concatenated multipart names, or file paths as primary keys. Keep schema and object names in separate columns. Use hashes only as lookup or deduplication accelerators and verify the full value on matches.

Do not silently truncate strings, round numeric values, rewrite timestamps, normalize source text, remove duplicates, or invent missing relationships. Preflight must report any conflict with proposed constraints. SQL collation choices must be tested against Surf's current case-insensitive name matching and ordinal-ignore-case substring search.

### Separate content and derived data

`TextContent(ContentKey, ContentHash, HashEncodingVersion, CharacterCount, ByteCount, LineCount, Text)` stores immutable text. Hash exact text using an explicitly defined encoding without whitespace or newline normalization. Reuse identical content only after exact comparison.

`Asset(AssetKey, ContentHash, ByteCount, MediaType, Bytes)` stores immutable binary content. Asset references are separate from image names and original filenames. Invalid legacy base64 is a migration error, not an empty replacement image.

Derived indexes must record the source revision, parser or renderer version, and completion state. They may be rebuilt. They must not be confused with the authoritative imported data.

## 4. Scopes and Object Explorer

| Proposed table | Source mapping and principal columns | Relationships |
| --- | --- | --- |
| `UserProfile` | Local profile identity, last active scope, preference references | Last active scope may be null or unresolved |
| `Scope` | `ScopeId`, `Name`, `Description`, original ordinal | Profile owns scopes |
| `Resource` | Concrete target kind; physical path, snapshot target, or diagram target | Explicit typed target FKs where resolvable |
| `ScopeResource` | `ResourceId`, owning scope, target resource, `DisplayNameOverride`, `DetailsOverride`, `AddedAtUtc`, `IncludeChildren`, ordinal | One scope has many resource entries |
| `VirtualFolder` | `VirtualFolderId`, scope, `Name`, original `ParentNodeKey`, ordinal | Folder belongs to one scope |
| `VirtualFolderMember` | Original `ChildNodeKeys` as individual ordered membership rows | Folder owns memberships; node resolution may be pending |
| `ExplorerNode` | Node kind, display name, owning scope resource, natural parent, effective parent, concrete target, sort ordinal | Derived browse catalogue for natural and virtual hierarchy |
| `NodeLocator` | Original node/path keys, context, lookup hash, resolved node, resolution status | Preserves legacy hierarchy and link addresses |

`ScopeResource` is the identity of an entry in a scope. Two entries targeting the same folder or snapshot are not automatically interchangeable: their aliases and `IncludeChildren` settings can differ. Do not deduplicate these entries by path.

Physical resources may share an underlying target record after validated identity resolution. File discovery must respect recursion settings, exclusions, missing files, overlapping roots, and the existing visited-file behaviour. Preserve display aliases independently of target identity.

`ExplorerNode` is a rebuildable catalogue, not a persisted WPF `FileSystemNode` graph. Virtual-folder membership remains authoritative even when a referenced node is not currently discovered. Validate effective-parent cycles without discarding the original parent and membership values.

### Required query shapes

- List scopes without reading their resource contents.
- List resources belonging to one scope, including their display aliases.
- List immediate children under a natural or virtual parent, with deterministic ordering and no code or image payloads.
- Resolve a node to its document, dataset, snapshot, or diagram identifier.
- Compute an Object Explorer address from the effective hierarchy, including virtual folders, for TXT naming and navigation.
- Determine active membership using the selected scope and workspace-specific unloaded-resource entries. Unloaded is not a global property of the underlying resource.

### Initial indexes

- `ScopeResource(ScopeKey, SortOrdinal, ScopeResourceKey)` with short display metadata included.
- `VirtualFolder(ScopeKey, VirtualFolderKey)` and `VirtualFolderMember(VirtualFolderKey, SortOrdinal)`.
- `ExplorerNode(ScopeKey, EffectiveParentNodeKey, SortOrdinal, ExplorerNodeKey)`.
- Reverse membership indexes by target document/resource key.
- Context and locator-hash indexes on `NodeLocator`, followed by exact-value verification.

Keep large text outside browse indexes. Covering indexes should include only the short values required for listing. [Microsoft included-column guidance](https://learn.microsoft.com/en-us/sql/relational-databases/indexes/create-indexes-with-included-columns).

## 5. Snapshot Metadata and Code Documents

### Snapshot and resource catalogue

| Proposed table | Source mapping and principal columns |
| --- | --- |
| `DatabaseSnapshot` | `SnapshotId`, `DisplayName`, `DatabaseName`, `ImportedAtUtc`, current version reference |
| `SnapshotResource` | Snapshot, resource kind, separate schema/object names, exact legacy `ResourceKey`, stable identity |
| `SnapshotResourceRevision` | Immutable revision identity and kind; points to object, table metadata, or dataset revision |
| `DatabaseObjectRevision` | `SqlDatabaseObject`: schema/name, kind, type description, parent schema/name, definition document revision |
| `TableMetadataRevision` | `SqlTable`: schema/name, full-data flag, reported row count, full-data import timestamp |
| `TableColumnRevision` | `SqlColumn`: schema/table/column, datatype, maximum length, precision, scale, nullability, identity, original ordinal and collection order |
| `PrimaryKeyConstraint` | Distinct constraint identities within one table metadata revision |
| `PrimaryKeyColumn` | `SqlPrimaryKeyColumn`: constraint, column name/reference, key ordinal and collection order |
| `FullDataTableSelection` | Ordered entries from `FullDataTableNames`, including unresolved table names |

`DatabaseImportCounts`, `FullName`, and other computed display properties are projections, not additional authoritative copies. Persist reported capture counts separately from actual stored row counts so inconsistencies can be reported rather than concealed.

Table metadata and table data are different versioned resources in the existing history model. Keep them independently revisioned. Do not require every historical data revision to use the current table schema.

### Document catalogue

| Proposed table | Purpose |
| --- | --- |
| `Document` | Stable code/file/generated-document identity, kind, logical source, display metadata, current revision pointer |
| `DocumentRevision` | Immutable revision linked to `TextContent`; source generation, timestamps, content hash, syntax language |
| `DocumentLocator` | Original physical paths and readable/canonical `db://` addresses, owner context, resolved document |
| `FileSource` | Original path, discovery metadata, encoding information where available, last successful indexed fingerprint, missing/stale state |
| `ResourceDocument` | Derived relation from a resource to documents discovered beneath it or belonging to its snapshot |

Use explicit owner relations and foreign keys rather than an arbitrary target-kind/target-number pair that cannot be validated. A snapshot object's code document belongs to its logical snapshot resource; its definition belongs to a particular revision.

Generated table code can be rendered from one table metadata revision or cached as one derived document revision. Record the renderer version. Do not generate all table code during startup.

Preserve both readable and canonical database locators using the existing path parser. Rename or display-alias changes must not change document identity. Ambiguous readable names remain explicit resolution cases; do not arbitrarily bind them to the first matching database.

### Required query shapes and indexes

- Object listing: snapshot, requested version, category/kind, schema, name, identifiers; no definition column.
- Open definition: one resolved document revision and content key.
- Table metadata: one metadata revision's columns and keys in preserved order.
- Locator resolution: context and address lookup, then exact verification.
- Current snapshot counts: aggregates or transactionally maintained small summaries, without materializing rows.

Index `SnapshotResource(SnapshotKey, Kind, SchemaName, ObjectName, ResourceKey)` using lengths verified against SQL key-size limits. Where names cannot fit a combined key, use scoped name hashes and verify original names. Index metadata children by `(TableMetadataRevisionKey, Ordinal, ChildKey)`. Index document revisions by document/revision identity and locators by context/hash. Do not include definition content in listing indexes.

## 6. Captured Table Data

### Recommended storage model

Use a relational dataset catalogue plus generated row tables for validated column layouts. Do not put `SqlTableDataSet.Rows` into one JSON column, and do not make a universal one-row-per-cell model the default: that multiplies row counts and makes wide-grid queries expensive.

| Proposed table | Principal columns and mapping |
| --- | --- |
| `DataSet` | Dataset key, owning snapshot resource revision, layout key, reported `RowCount`, actual stored count, `ImportedAtUtc`, ready state |
| `DataLayout` | Immutable validated column-layout descriptor, layout hash, generated storage table identifier |
| `DataColumn` | Layout key, display/source name, source datatype metadata, generated physical column name, ordinal, encoding policy |
| `DataValueException` | Dataset, row ordinal, column/property locator, original value kind/token or presence information needed for exact reconstruction |
| Generated `capture.Data_<internal key>` | Dataset key, row ordinal, validated scalar columns for that layout |

Each generated row table contains application-owned `DataSetKey` and `RowOrdinal` columns followed by generated names such as `C0001`. Keep source column names in metadata. Multiple datasets with the same validated layout can share a physical table; different layouts receive separate tables.

The clustered row identity is `(DataSetKey, RowOrdinal)`. Preserve source array order and duplicate rows. Imported primary-key metadata is useful for comparison and optional lookup indexes, but is not automatically a uniqueness constraint on captured data.

### Lossless conversion rules

- Infer storage from declared metadata and observed JSON values together; do not trust a source datatype string as executable SQL.
- Allow only an explicit list of safe SQL scalar storage types, with validated lengths, precision, and scale. Generate all SQL identifiers internally and parameterize values.
- Convert a value to a typed column only when conversion is lossless. Preserve exact source representation where typed round-tripping changes formatting or value kind.
- Distinguish missing properties, explicit JSON null, empty strings, JSON numbers, JSON strings, and booleans. Source capture uses `FOR JSON PATH` without `INCLUDE_NULL_VALUES`, so omitted null properties are a real legacy case.
- Use the exception relation for missing-property markers, formatting differences, undeclared properties, and unsupported or structured values. A bounded individual complex value may remain encoded text; no complete dataset or library is retained as JSON in the new primary model.
- Preserve original property spelling and ordering where needed for legacy reconstruction. Detect duplicate property names and case-colliding names before choosing a layout. If they cannot be represented by the normal layout, use an explicitly supported exception representation or fail validation; never overwrite one value with another.
- Preserve original declared row counts independently from the number of captured rows. Empty and partial datasets remain distinguishable from datasets that were not captured.
- Do not coerce high-precision decimals through `double`, convert a captured `rowversion` into an automatically generated SQL `rowversion`, round dates to lower precision, or execute user-defined source types.

The exception path must be measured. If ordinary data requires exceptions for most cells, adjust the layout encoding rather than accept an unbounded secondary cell store. Audit SQL column-count and row-size limits before generating a layout; do not assume every imported shape fits one SQL table.

### Query requirements

- Return headers from `DataColumn` without reading data rows.
- Fetch one dataset's rows after a stable cursor, using `RowOrdinal` as the tie-breaker.
- Apply filters and sorts before returning a page. Preserve the existing grid's display-text matching, null/empty handling, and AND/OR filter rules; typed SQL equality is not a drop-in replacement for these filters.
- Find matching table columns for Object Explorer content search without building a CSV string for the complete dataset.
- Stream every row matching an export filter, not merely the page currently cached in memory. Keep existing Copy/Export result semantics during the later UI redesign.
- Compare selected datasets using chosen key columns while preserving duplicate-key behaviour.

Begin with the clustered dataset/ordinal index. Add secondary indexes only for measured filter, sort, or comparison workloads. Arbitrary substring filters may still scan data. Store or derive searchable display text using a versioned formatting policy rather than silently changing how numbers and dates match.

This storage model is a prototype gate. Validate wide tables, partial schemas, very large strings, binary values, decimals, duplicates, and historical schema changes before making it the production capture format.

## 7. Snapshot History

### Preserve the reverse-change semantics

The existing history stores the current snapshot and, for later versions, changes containing `PreviousPayload`. Reconstructing an older version starts at current state and applies changes from later versions in descending order. A migration that treats these payloads as the new version's contents will produce incorrect history.

| Proposed table | Source mapping |
| --- | --- |
| `SnapshotHistory` | Snapshot, `NextVersionNumber` |
| `SnapshotVersion` | `VersionId`, snapshot, name, number, `CreatedAtUtc`, `IsInitial`, collection order |
| `SnapshotChange` | Version, snapshot and version number for indexed lookup, resource identity/kind, change kind, display name, relative path, ordinal, nullable previous revision |
| `ResourceStateRange` | Derived resource state with inclusive start version, exclusive end version, and revision reference |

Normalize `PreviousPayload.DatabaseObject`, `Table`, `Columns`, `PrimaryKeys`, and `TableDataSet` into the same typed revision tables used for current resources. Keep all history identifiers and the original rollback changes. An added resource has no previous revision. A deleted resource may exist only in history and still needs a logical resource record.

Build `ResourceStateRange` from the current state and the reverse-change chain. For a target version, the earliest change after that version determines the previous state; if no later change exists, use current state. Validate this rule against the existing reconstruction service for every version in test fixtures, including add/delete/re-add sequences and gaps in version numbers.

Query one resource's historical revision or a page of history summaries without cloning an entire snapshot. Revisions are immutable. Identical definitions or datasets may share content after exact equality validation, but deduplication must not erase distinct version identities or import timestamps.

Index versions by `(SnapshotKey, VersionNumber)` and changes by both `(VersionKey, SortOrdinal)` and `(SnapshotResourceKey, VersionNumber)`. Constrain the change's snapshot/version-number pair to its referenced version, and constrain the resource owner to that same snapshot. Index state ranges for resource/version lookup and version-level resource listing. Validate non-overlapping ranges during publication; a simple row-level check alone cannot enforce interval correctness.

Preserve histories whose next-number value exceeds the next sequential number. Do not silently renumber versions. Duplicate numbers, missing owners, unknown changes, or unrepresentable payloads must be reported before activation.

## 8. Diagrams and Workflows

### Diagram revisions

Separate a logical diagram from its saved revisions. `DiagramLibrary` holds current diagrams, while `WorkbenchState.ActiveDiagramSnapshot` may hold a different saved state with the same diagram and object identifiers. Never merge them solely because those identifiers match.

| Proposed table | Mapping |
| --- | --- |
| `Diagram` | Logical `DiagramId`, catalogue name, current revision reference |
| `DiagramRevision` | `Name`, `CreatedAtUtc`, `UpdatedAtUtc`, canvas zoom, viewport offsets, origin, owning saved state where relevant |
| `DiagramObject` | Revision, original `Id`, object type, Z index, collection ordinal, `Left`, `Top`, `Width`, `Height`, label text/font size, outline/back colors |
| `DiagramShape` | Object key, `ShapeKind` |
| `DiagramImage` | Object key, image definition reference/original ID, image name, asset key, pasted filename |
| `DiagramLine` | Object key, arrow flag, loose flag, line start/end coordinates |
| `DiagramLabelGeometry` | Object key, tether flag, anchor coordinates, label-box position and size |
| `DiagramPortal` | Object key, portal name, original paired diagram/object IDs, nullable resolved logical target |
| `DiagramObjectMetadata` | Object key, original link, resolved link reference, documentation content key |
| `Workflow` | Diagram revision, `WorkflowId`, name, created/updated timestamps, marker visibility, collection order |
| `WorkflowItem` | Workflow, `WorkflowItemId`, item number, description, documentation content, original marker object ID, nullable resolved marker, ordinal |
| `DiagramWorkflowBinding` | Object key, original workflow/item IDs and resolved references |
| `QueryItem` | Contextual query identity, query number, created timestamp, status, description, ordinal |
| `ObjectQuery` / `WorkflowItemQuery` | Explicit owner relation for a query record |

Map the image's `ImageDataBase64` to decoded asset bytes. Preserve `ImageDefinitionId`, names, and pasted filenames independently. Store documentation XAML as text without parsing and reserializing it during migration.

Preserve every geometry field even if it is currently unused by a particular object type. Subtype rows must therefore be created whenever their source fields carry data, not only when an object's current discriminator makes those fields visible. Preserve meaningful empty/default values through a complete field mapping.

Portal targets resolve by logical diagram and object identity, not to whichever immutable revision happened to be migrated first. Workbench revisions keep their own workflow and object instances. Unresolved links remain visible and retain the original values.

Index object listing by `(DiagramRevisionKey, ZIndex, SortOrdinal, DiagramObjectKey)`, workflows by diagram revision, items by workflow/item number, and queries by owner/status. Asset bytes and rich documentation must never be included in catalogue queries.

### Search projection

Current diagram content search concatenates object type, label text, and image name. Preserve that projection and its joining/order rules initially; do not silently broaden search to tooltip documentation or workflow text. Any broader search is a separate feature decision.

Store the projection as derived text linked to its diagram revision and projection-format version. Fetch an active diagram's objects and required assets when opening it, not all diagrams at startup.

## 9. Workbenches and Current Workspace

| Proposed table | Mapping |
| --- | --- |
| `WorkspaceSession` | Current profile, `LastFolderPath`, canvas zoom and viewport offsets |
| `Workbench` | `WorkbenchId`, name, default-for-scope flag, created/updated/saved timestamps, scope identity and saved scope name, collection order |
| `WorkbenchViewState` | Code/diagram visibility, active workspace view, split orientation, code mode, pinned detail tab, reference-lines enabled, code/diagram zoom and offsets, active document locator, active diagram ID/name, diagram lock |
| `DocumentWindowState` | Owner session or workbench, document target and original `FilePath`, `DisplayName`, position, size, font size, horizontal/vertical offsets, collection order |
| `DocumentWindowFilter` | Window-state key, original column index, filter text |
| `WorkspaceUnloadedResource` | Session or workbench owner, original resource ID, resolved scope-resource reference, ordinal |
| `ReferenceConnectionLine` | Workbench, `ConnectionId`, source/target document locators and resolved targets, source/target line numbers and start/end column numbers, ordinal |
| `WorkbenchDiagramRevision` | Workbench and its exact embedded active diagram revision |

Use explicit owner relationships or a state-owner table with validated one-to-one ownership; do not use unchecked polymorphic foreign keys. The current workspace session and saved workbenches are different records.

Preserve both resolved scope/document references and their original saved names or locators. A missing scope, file, or diagram must retain its saved layout. Do not substitute a current diagram for an embedded snapshot just because the diagram name matches.

Preserve column-index filters as stored. Optional name-based filter identities can be added later, but conversion must not change which columns are filtered. The existing initial canvas-centering behaviour remains an application concern, not a migration rewrite of saved geometry.

Index workbench listing by update/saved time and scope, window states by owner/order, unloaded entries by owner/resolved scope-resource, and connection lines by workbench. Listing saved workbenches must not load their document contents or embedded diagrams.

## 10. Settings and Image Definitions

| Proposed table | Mapping |
| --- | --- |
| `ApplicationPreference` | Theme, load-most-recent-workbench flag, comparison whitespace/case defaults, internal logging flag, default code background, keyboard preference version |
| `InputPreference` | One named boolean entry for each `KeyboardShortcutSettings` flag |
| `ExtensionAppearance` | Ordered `BackcolorsByExtension` entries: extension, background, language |
| `ReferenceStyle` | Ordered language/kind entries: foreground, bold, italic, underline |
| `DiagramImageDefinition` | `Id`, name, legacy regex/match target, name/content regex, resource-type filter, sort order, original filename, asset reference, original collection order |

Preserve all current keyboard flag names and their values. Keep duplicate settings entries and their order until preflight establishes that a uniqueness constraint is compatible with current first-match behaviour.

Apply version-specific defaults only to properties actually absent in a supported legacy format. Record normalization actions; do not treat a present false, zero, or empty string as missing. Unknown properties or enum values require an explicit compatibility decision and retained source evidence, not silent omission.

Connection bootstrap settings and environment overrides remain outside the persistence database. Do not include connection credentials in migration reports or searchable tables.

## 11. Reference and Search Indexes

These tables support future queries without defining the application-side repository implementation yet.

| Proposed table | Columns and purpose |
| --- | --- |
| `SymbolIndexGeneration` | Source document revision/fingerprint, parser language/version, generation status |
| `SymbolDefinition` | Name, qualified name, kind, document revision, start/end line and column, parameter/minimum/maximum argument counts, language, container name |
| `SymbolLookupName` | Separate indexed simple and qualified lookup names, with a validated normalization policy |
| `SearchProjection` | Source entity/revision, projection kind/version, content reference, generation status |
| `IndexWorkItem` | Source identity/revision, queued operation, retry/error state, generation |

Reuse the existing C#, VB, SQL, and JavaScript parsers and current resolution rules. A symbol catalogue is not full compiler binding. Do not assume ambiguous names have one target, and preserve callable argument matching and non-file preference.

Physical files require indexed content and symbols to participate in SQL-only search. Record content fingerprints and index freshness. Watchers can request updates, but reconciliation must also handle missed events, deletions, inaccessible files, parser-setting changes, and changed folder membership.

Index lookup names separately so a very long qualified name does not make a composite index invalid. Filter lookup results through resource membership in the active scope. Large token batches can use a table-valued parameter rather than one query per token. [Microsoft table-valued parameter guidance](https://learn.microsoft.com/en-us/sql/relational-databases/tables/use-table-valued-parameters-database-engine).

### Search semantics

| Search kind | Storage support | Important limit |
| --- | --- | --- |
| Name or reference lookup | Scoped indexed names and symbols | Preserve identifier normalization, overloads, and aliases |
| Literal substring | Searchable content/projections evaluated against scoped current revisions | An arbitrary substring can still require a scan; validate SQL/.NET Unicode and case equivalence |
| Word or phrase | Optional full-text index where installed and supported | Not a replacement for existing substring matching |
| Existing .NET regex | Scoped candidate selection, then compatible bounded evaluation | Worst-case search still reads all scoped content sequentially |
| Table-data search | Dataset/column identity plus versioned display-value projection | Return matching columns/rows without generating full CSV |

Full-text search is optional and word-based. It must not gate basic relational migration or be used as an unsafe prefilter that omits valid substring/regex matches. [Microsoft full-text documentation](https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-search).

SQL Server 2025 provides regex based on RE2. Its availability and syntax differ from Surf's current .NET implementation, so adoption needs compatibility tests rather than an unconditional substitution. [Microsoft regex documentation](https://learn.microsoft.com/en-us/sql/relational-databases/regular-expressions/overview).

Retain current searchable representations: raw source files and SQL definitions, rendered table metadata, formatted table values, and the existing diagram projection. Search current in-scope revisions by default; historical versions require an explicit request. Resource-specific exclusions and unload state must be applied before returning results.

Bound regex evaluation time, support cancellation, and report incomplete or timed-out searches instead of presenting them as complete. Display incremental results without requiring an exact total first. Query paging must use stable ordering and a consistent source generation. [Microsoft pagination guidance](https://learn.microsoft.com/en-us/sql/t-sql/queries/select-order-by-clause-transact-sql).

## 12. Format Detection and Migration

### Format metadata

Add `StorageFormatInfo` with an application format identifier, relational schema version, minimum supported reader/writer versions, initialization state, and completed migration identity. Use a different format identifier from legacy JSON library versions.

Add `SchemaMigration` with ordered script identity/checksum and application time, plus `MigrationRun`, `MigrationCheckpoint`, `MigrationIdentityMap`, and `MigrationIssue` records. Migration metadata describes progress; only a validated ready-format marker permits normal use.

### Detection outcomes

| Observed source | Action |
| --- | --- |
| Supported relational format and ready state | Open normally |
| Supported earlier relational version | Offer the appropriate versioned schema upgrade |
| Known `app.Surf2Documents` structure without a relational marker | Offer legacy conversion |
| Supported local JSON fallback files | Offer explicit import/conversion with per-document provenance |
| Supported legacy export manifest/document entries | Offer conversion of the package into a new destination |
| Truly new, explicitly selected empty destination | Initialize only after the relevant user action |
| Newer unknown format, malformed known format, incomplete migration, or inaccessible metadata | Explain the problem; do not reinterpret it as an empty database |

Probe read-only metadata before calling current initialization methods. `SqlServerDocumentStore.TestConnectionAsync` creates the database/schema and is therefore not suitable as the new read-only format probe.

SQL document presence is authoritative for that key. Offer the corresponding local fallback only when it is genuinely absent, not when SQL is inaccessible or corrupt. A mixed source must record exactly which store supplied each key, its source schema version, and its original document timestamp where available. Unexpected document keys and unknown schema versions require explicit handling rather than silent dropping.

### Conversion sequence

1. Identify the source format and supported legacy versions without writing to it. Run preflight for document presence, sizes, identifiers, maximum string lengths, enum values, duplicate/colliding names, schema shapes, missing links, and history consistency.
2. Present source, destination, estimated space needs, detected compatibility issues, and the fact that the original remains unchanged. Offer migrate or cancel. A read-only legacy viewer is optional and must be explicitly implemented; do not claim the current loader is read-only.
3. Acquire a consistent source. Stop Surf writes during conversion and use a verified source snapshot/copy or a supported consistent SQL read transaction. Do not enable database-wide isolation settings without approval. Capture document hashes and provenance. Existing cross-document inconsistencies must be reported.
4. Create a separate, explicitly selected destination database, or use a verified empty pre-created destination when create permissions are unavailable. Reject source/destination identity equality and unrelated existing contents. Mark the destination as migrating.
5. Stream source payloads into protected staging files or incremental readers. Use `SqlDataReader` with `SequentialAccess` and streaming text access instead of `ExecuteScalar` returning the complete JSON string. Local files and ZIP entries must be streamed too. [Microsoft streaming guidance](https://learn.microsoft.com/en-us/sql/connect/ado-net/sqlclient-streaming-support).
6. Parse incrementally into bounded entity and row batches. Do not deserialize the complete `DatabaseSnapshotLibrary`, construct a `JsonDocument` over the complete source, or call the existing whole-snapshot clone/reconstruction methods for every history version.
7. Import root identities, catalogue records, revisions, captured datasets, history changes, diagrams, saved states, and preferences in dependency order. Resolve cross-links in a second pass. Record deterministic identity mappings and committed checkpoints in the same transaction as each batch.
8. Validate authoritative data, relationship resolution, historical reconstruction, and query results. Build required indexes and integrity checks before publication. Optional symbol/full-text indexing can complete afterwards with visible readiness state; it must not hide missing search coverage.
9. Publish the ready marker in a final transaction only after all mandatory validation passes. Recheck source consistency where a frozen copy was not available. An interrupted or failed destination remains non-ready.
10. Close current readers/caches and activate the new connection only after publication. Save bootstrap configuration atomically and respect an active environment override; if an external override prevents activation, explain the required configuration change rather than silently bypassing it.
11. Retain the original source and the migration report. Restart/load the new format and verify before offering a separate cleanup action. Never delete legacy SQL tables or local files automatically.

Batch size must be limited by bytes as well as row count. A single enormous definition or cell still needs special streaming/size handling. Detect insufficient storage or an over-large unit with an actionable error rather than exhausting memory.

Resume only when the source fingerprint, destination migration identity, schema version, and converter version match. Otherwise start a new destination or require an explicit recovery choice. Cancelled batches roll back; committed batches are idempotently reusable. Never run parallel legacy/new dual writes during transition.

### Recovery limits

Keeping the legacy source preserves the pre-migration state. It is not automatic reverse synchronization: edits made after activating the relational database will not appear in the old format. Any later rollback must account for those edits before switching connections.

## 13. Relational Writes and Integrity

Publish multi-record edits transactionally. Snapshot replacement must commit metadata revisions, dataset references, history entries, and current-version pointers together. Diagram saving must commit its object/workflow/query graph and revision pointer together. Workbench saving must commit its view state, window layouts, and embedded diagram reference together.

Use `rowversion` checks on mutable heads/settings and return a concurrency error instead of overwriting an unexpected revision. Keep immutable content and revisions until retention rules prove they are no longer referenced by current state, history, or saved workbenches.

Replace the existing save-before-hydration guard with a format/readiness guard and targeted entity/concurrency validation. Do not require loading every table to permit a safe save. Explicit deletion and garbage collection need their own ownership checks; absence from a partially loaded view must never mean delete.

Normal runtime access should not require master-database creation permission. Installation/migration provisions the owned schemas. The captured-layout creator needs controlled DDL permission restricted to the owned capture schema; ordinary browse/search code does not. Keep credentials and full source text out of routine diagnostics.

## 14. Export and Import Compatibility

Introduce a versioned relational export package with a manifest identifying the database format, schema version, exporter version, entity streams, counts, and checksums. Stream relational records and assets into separate entries; do not recreate the six huge root JSON documents as the new export format.

Keep a legacy package reader that routes format-1 document exports through the same conversion pipeline. Validate the manifest before creating or writing a destination. Do not mistake image-definition packages for complete persistence packages.

Preserve existing local-file inclusion rules deliberately, but review whether duplicated legacy backups should be excluded from new exports. Keep the current connection-settings restore exclusion. Check all extracted paths remain inside the permitted restore root, restrict entry sizes and aggregate extraction size, and never activate a partially validated import.

Migration reports must list source provenance, counts, preservation checks, unresolved external links, normalizations, and failures without exposing secrets or embedding entire source documents. Retain a protected source archive for unsupported fields when appropriate; an archive is recovery evidence, not the new application's primary data model.

## 15. Implementation Sequence

### Phase 0 Baseline and compatibility fixtures

Deliver a read-only inventory tool and representative fixtures, including a large dataset and multiple history versions. Record startup, scope switching, reference resolution, content search, grid opening/filtering, comparison, export, and save behaviour. Measure Surf private bytes, working set, managed retained memory, peak allocations, and SQL Server memory separately.

Exit condition: a documented source-format matrix, field inventory, search/filter correctness fixtures, and reproducible measurements. The Task Manager screenshot alone is not a heap diagnosis or performance target.

### Phase 1 Schema and mapping specification

Produce version-controlled SQL migration scripts and a property-by-property mapping manifest. Cover every persisted public model property, including nested collections. Classify each as authoritative storage, derived data, bootstrap-only, or explicitly unsupported. Validate proposed lengths and uniqueness assumptions against preflight.

Exit condition: an empty relational database can be provisioned and its schema version detected without touching a legacy source. No required library-sized JSON column exists in the design.

### Phase 2 Catalogue and content proof

Implement storage-only catalogue, content, metadata, locator, and history mappings. Test browse listings without text payloads, opening one definition, resolving one alias, and reading one resource at an older version. Keep fixtures independent of production persistence.

Exit condition: these queries do not assemble a complete library, and identities/history are correct.

### Phase 3 Captured-data prototype

Implement layout validation, generated row storage, value exceptions, paged reads, and filtered streaming export for representative captures. Compare correctness and cost with alternatives before fixing the capture schema. Include indexing/storage/log growth in the comparison, not only application memory.

Exit condition: lossless capture reconstruction and equivalent grid filter/search/comparison behaviour across difficult types and layouts. The chosen model supports bounded memory and acceptable first-page latency.

### Phase 4 Remaining persistent graphs

Implement scopes/virtual hierarchy, diagrams/workflows/queries, assets/image definitions, workbenches, workspace state, and settings. Preserve embedded diagram copies as separate revisions. Add transactional graph publication and targeted integrity checks.

Exit condition: every authoritative persisted property has a tested destination and no graph is inadvertently merged or dropped.

### Phase 5 Migration and package compatibility

Implement read-only detection, preflight, user-approved destination selection, streaming conversion, restartable checkpoints, validation, activation, and legacy-package import. Test missing permissions, insufficient space, source changes, cancellation, crashes, and corrupt inputs.

Exit condition: no failed migration can become active or damage its source; successful conversions preserve all validated data and recoverable unresolved locators.

### Phase 6 Query and index proof

Build representative storage queries for every workflow in section 16. Populate derived membership, symbol, and search indexes. Test correct scope/unload/version filtering, query plans, cold/warm latency, and index freshness behaviour.

Exit condition: query shapes and mandatory indexes are proven before the separate application-access architecture is finalized.

### Phase 7 Application access design and controlled rollout

Design the later application-side interfaces, request lifetimes, batching, caches, editor/grid adapters, dirty tracking, and UI readiness. Specify how whole-library stores will be retired without recreating eager object graphs. Then integrate behind a controlled format-aware rollout.

This phase requires the next design discussion. Completion of schema/migration work alone must not be presented as delivery of the memory improvement.

## 16. Query Acceptance Matrix

| Workflow | Data allowed in its normal response | Must not require |
| --- | --- | --- |
| Startup | Preferences, current session summary, requested scope/workbench identity | All snapshots, rows, histories, assets, and saved diagrams |
| Scope selection | Scope and resource summaries | Code definitions or complete datasets |
| Expand explorer node | One branch of names, kinds, IDs, order, counts | A complete descendant object graph |
| Open code | Requested document revision and needed metadata | All code in its snapshot |
| Reference highlighting | Compact symbol names/kinds or a batched subset | SQL calls while rendering each token |
| Follow reference | Scoped candidates/locations, then selected target text | Text for every candidate |
| Search | Progressive matching IDs, metadata, previews, readiness state | Returning or retaining the whole corpus |
| Open table | Headers, first bounded page, reported/actual counts where cheap | CSV materialization or all row objects |
| Filter/export table | Database-filtered pages or streamed matching rows | Restricting results to the cached page |
| History list | Version/change summaries | Reconstructing every full snapshot |
| Historical resource | One revision's metadata/content/dataset pointer | Whole-snapshot cloning |
| Workbench list | Names, timestamps, scope and identity | All embedded diagrams and windows |
| Open diagram | One revision and its required assets/workflows | The entire diagram library |
| Save edit | Explicit changed entity/revision set | Whole-library serialization or replacement |

## 17. Validation and Release Gates

### Data preservation

- Exact code, documentation, label text, image bytes, original locators, and meaningful source value representations survive conversion.
- Every scope, resource alias, virtual folder/membership, snapshot, object/table/column/key, selected full-data table, dataset, history version/change, diagram object, workflow/item/query, image definition, workbench/layout/filter/connection, unloaded entry, and preference has a counted mapping.
- Contextual IDs, array order, duplicate rows, missing/null/empty values, numeric precision, timestamps, font sizes, Z order, tether/line geometry, and saved diagram copies are preserved.
- Historical resources reconstructed from relational records equal the existing history service's results on fixtures. Current and historical data are independently validated.
- Unresolved external references are retained and reported. Unresolved owned relationships, unknown fields, or constraint conflicts block activation unless explicitly supported by the conversion policy.

### Behaviour and query correctness

- Name/content searches, Unicode/case behaviour, regex patterns, overload resolution, active-scope membership, unloaded resources, and historical selection produce equivalent results.
- Include files outside the database, overlapping folders, recursion exclusions, missing files, duplicate names, canonical/readable database locators, and renamed virtual folders.
- Table search identifies the same matching columns; grid filters preserve AND/OR and display formatting; Copy/Export and comparison retain their existing intended result sets.
- Workbench snapshots do not unexpectedly change when the current diagram is edited. Portal/workflow references remain correct within their owning revision/context.

### Safety and recovery

- Read-only detection never creates a schema or saves a fallback document.
- Every migration cancellation/crash point leaves the source untouched and the incomplete destination non-ready.
- Retrying a committed batch creates no duplicate identities, assets, rows, changes, or memberships.
- Unknown/newer formats and corrupt payloads never become empty successful imports.
- Local staging/report/export paths and SQL write permissions are documented for the safety audit. No new DLL is required merely to normalize existing SQL Server storage; reassess any additional parser/indexing/tooling dependency separately.

### Performance evidence

Use the same representative corpus and actions for old/new comparisons. Record first-result/page time, total operation time, bytes transferred, retained/peak application memory, SQL reads/CPU/memory, index-update work, storage size, and transaction-log growth. Include cold and warm runs and repeated open/close cycles.

Agree numerical latency and memory budgets after baseline measurement. Mandatory structural gates are that unopened dataset/history content is not hydrated at startup, each grid page is bounded by bytes and rows, normal saves do not serialize whole libraries, and scans do not retain the entire corpus. Substring and regex scans may remain expensive even when memory is bounded.

## 18. Decisions to Resolve Before Production DDL

1. Confirm supported SQL Server/LocalDB versions and optional search capabilities. Basic relational access must not depend on SQL Server 2025 regex or full-text installation.
2. Validate captured-row layout/exception costs and choose a supported strategy for shapes exceeding SQL table limits.
3. Set legacy identifier/name/path length policies from preflight rather than assumptions.
4. Decide whether a read-only legacy viewer is included in the first migration release; migrate/cancel is the required minimum.
5. Decide source consistency/backup methods for each permission/deployment scenario and destination-space requirements.
6. Agree index freshness behaviour for external files and search results during background indexing.
7. Set retention rules for immutable content, history, saved diagram revisions, and migration staging. No automatic destructive cleanup belongs in initial conversion.

## 19. Property Mapping Checklist

This checklist names the current writable model properties explicitly. Grouped scalar fields map one-to-one unless a transformation is stated. Collection properties become ordered child rows, never another whole-collection JSON column. Source wrapper version values are retained as migration provenance, not reused as the new database schema version.

### Scopes and snapshot wrappers

| Source properties | Destination or treatment |
| --- | --- |
| `ScopeLibrary.SchemaVersion` | Source-format provenance in `MigrationRun` |
| `ScopeLibrary.LastActiveScopeId` | `UserProfile` selection, with original ID and resolved scope |
| `ScopeLibrary.Scopes` | `Scope` rows in original order |
| `Scope.ScopeId`, `Name`, `Description` | `Scope` |
| `Scope.Resources`, `VirtualFolders` | `ScopeResource`, `VirtualFolder` children |
| `ScopedResource.ResourceId`, `DisplayNameOverride`, `DetailsOverride`, `AddedAtUtc`, `IncludeChildren` | `ScopeResource` |
| `ScopedResource.Kind`, `Path` | Concrete `Resource` target plus original locator; do not discard unresolved targets |
| `VirtualFolder.VirtualFolderId`, `Name`, `ParentNodeKey` | `VirtualFolder` and original/resolved parent locator |
| `VirtualFolder.ChildNodeKeys` | Ordered `VirtualFolderMember` records |
| `DatabaseSnapshotLibrary.SchemaVersion` | Source-format provenance |
| `DatabaseSnapshotLibrary.Snapshots`, `Histories` | `DatabaseSnapshot`, `SnapshotHistory` |

### Snapshot metadata and history

| Source properties | Destination or treatment |
| --- | --- |
| `DatabaseMetadataSnapshot.SnapshotId`, `DisplayName`, `DatabaseName`, `ImportedAtUtc` | `DatabaseSnapshot` |
| `DatabaseMetadataSnapshot.Objects`, `Tables`, `Columns`, `PrimaryKeys` | Typed current resource revisions and their ordered metadata children |
| `DatabaseMetadataSnapshot.TableDataSets` | Dataset catalogue and relational rows |
| `DatabaseMetadataSnapshot.FullDataTableNames` | Ordered `FullDataTableSelection` entries |
| `SqlDatabaseObject.SchemaName`, `ObjectName`, `Kind`, `TypeDescription`, `ParentSchemaName`, `ParentObjectName` | `DatabaseObjectRevision` and logical resource identity |
| `SqlDatabaseObject.Definition` | Exact `TextContent` through the definition's `DocumentRevision` |
| `SqlTable.SchemaName`, `TableName`, `HasFullData`, `FullDataRowCount`, `FullDataImportedAtUtc` | `TableMetadataRevision`; retain reported fields independently from actual capture counts |
| `SqlColumn.SchemaName`, `TableName`, `ColumnName`, `DataType`, `MaxLength`, `NumericPrecision`, `NumericScale`, `IsNullable`, `IsIdentity`, `Ordinal` | `TableColumnRevision` |
| `SqlPrimaryKeyColumn.SchemaName`, `TableName`, `ConstraintName`, `ColumnName`, `KeyOrdinal` | Constraint and ordered `PrimaryKeyColumn` records |
| `SqlTableDataSet.SchemaName`, `TableName`, `RowCount`, `ImportedAtUtc`, `Rows` | `DataSet`, layout, ordered row storage, value exceptions |
| `DatabaseSnapshotHistory.SnapshotId`, `NextVersionNumber`, `Versions` | `SnapshotHistory` and `SnapshotVersion` children |
| `DatabaseSnapshotVersion.VersionId`, `VersionName`, `VersionNumber`, `CreatedAtUtc`, `IsInitial`, `Changes` | `SnapshotVersion` and ordered changes |
| `DatabaseSnapshotResourceChange.Kind`, `ChangeKind`, `ResourceKey`, `DisplayName`, `RelativePath`, `PreviousPayload` | `SnapshotChange` and nullable previous typed revision |
| `DatabaseSnapshotResourcePayload.Kind`, `DatabaseObject`, `Table`, `Columns`, `PrimaryKeys`, `TableDataSet` | Same typed revision tables as current data, associated with the previous history state |
| `DatabaseImportCounts.StoredProcedures`, `Views`, `Functions`, `Triggers`, `Tables`, `Fields`, `PrimaryKeys`, `FullDataTables`, `DataRows` | Derived counts; not independently stored authoritative state |

### Diagram content and workflows

| Source properties | Destination or treatment |
| --- | --- |
| `DiagramLibrary.Diagrams` | Logical diagrams and their current revisions |
| `DiagramDocument.DiagramId`, `Name`, `CreatedAtUtc`, `UpdatedAtUtc`, `CanvasZoom`, `ViewportHorizontalOffset`, `ViewportVerticalOffset` | Logical `Diagram` identity and exact `DiagramRevision` values |
| `DiagramDocument.Objects`, `Workflows` | Ordered object and workflow children of that revision |
| `DiagramObjectSnapshot.Id`, `ObjectType`, `ZIndex`, `LabelText`, `LabelFontSize`, `OutlineColorText`, `BackColorText`, `Left`, `Top`, `Width`, `Height` | Common `DiagramObject` record |
| `DiagramObjectSnapshot.Metadata` | `DiagramObjectMetadata` plus query children |
| `DiagramObjectSnapshot.WorkflowId`, `WorkflowItemId` | `DiagramWorkflowBinding`, preserving raw IDs |
| `DiagramObjectSnapshot.PortalName`, `PairedPortalDiagramId`, `PairedPortalObjectId` | `DiagramPortal`, preserving raw target IDs and resolution status |
| `DiagramObjectSnapshot.ShapeKind` | `DiagramShape` |
| `DiagramObjectSnapshot.ImageDefinitionId`, `ImageName`, `ImageDataBase64`, `PastedImageFileName` | `DiagramImage`; decode bytes into `Asset` |
| `DiagramObjectSnapshot.HasEndArrow`, `IsLineLoose`, `LineStartX`, `LineStartY`, `LineEndX`, `LineEndY` | `DiagramLine` |
| `DiagramObjectSnapshot.IsTethered`, `LabelAnchorX`, `LabelAnchorY`, `LabelBoxLeft`, `LabelBoxTop`, `LabelBoxWidth`, `LabelBoxHeight` | `DiagramLabelGeometry` |
| `DiagramObjectMetadata.Link`, `DocumentationXaml`, `Queries` | Original/resolved link, exact documentation content, ordered owned queries |
| `QueryItem.QueryId`, `QueryNumber`, `CreatedDateUtc`, `Status`, `QueryDescription` | Contextually owned `QueryItem` |
| `WorkflowDocument.WorkflowId`, `WorkflowName`, `CreatedAtUtc`, `UpdatedAtUtc`, `AreMarkersVisible`, `Items` | `Workflow` and ordered items |
| `WorkflowItem.WorkflowItemId`, `MarkerDiagramObjectId`, `ItemNumber`, `ItemDescription`, `ItemDocumentationXaml`, `Queries` | `WorkflowItem`, raw/resolved marker relation, exact documentation, ordered owned queries |

### Workspace and saved workbenches

| Source properties | Destination or treatment |
| --- | --- |
| `WorkspaceState.LastFolderPath`, `CanvasZoom`, `ViewportHorizontalOffset`, `ViewportVerticalOffset` | `WorkspaceSession` |
| `WorkspaceState.UnloadedResourceIds`, `OpenDocuments` | Owned unloaded entries and window-state rows |
| `OpenDocumentState.FilePath`, `DisplayName`, `Left`, `Top`, `Width`, `Height`, `FontSize`, `HorizontalOffset`, `VerticalOffset` | `DocumentWindowState`, preserving original locator and resolved document separately |
| `OpenDocumentState.SpreadsheetFilters` | `DocumentWindowFilter`, keeping the original column-index key |
| `WorkbenchLibrary.Workbenches` | Ordered `Workbench` rows |
| `WorkbenchState.WorkbenchId`, `Name`, `IsDefaultForScope`, `CreatedAtUtc`, `UpdatedAtUtc`, `SavedAtUtc`, `ScopeId`, `ScopeName` | `Workbench`, preserving raw and resolved scope values |
| `WorkbenchState.IsCodeViewVisible`, `IsDiagramViewVisible`, `ActiveWorkspaceView`, `WorkspaceSplitOrientation`, `CodeViewMode`, `PinnedExplorerDetailTab`, `ReferenceConnectionLinesEnabled` | `WorkbenchViewState` |
| `WorkbenchState.CodeCanvasZoom`, `CodeViewportHorizontalOffset`, `CodeViewportVerticalOffset` | Code viewport fields in `WorkbenchViewState` |
| `WorkbenchState.ActiveDocumentPath`, `ActiveDiagramId`, `ActiveDiagramName`, `IsDiagramLocked`, `DiagramCanvasZoom`, `DiagramViewportHorizontalOffset`, `DiagramViewportVerticalOffset` | Active selection, raw locators/names, lock and diagram viewport fields in `WorkbenchViewState` |
| `WorkbenchState.UnloadedResourceIds`, `OpenDocuments`, `ReferenceConnectionLines` | Ordered owned child records |
| `WorkbenchState.ActiveDiagramSnapshot` | Exact separate `DiagramRevision` through `WorkbenchDiagramRevision`, not an alias to current diagram state |
| `ReferenceConnectionLineState.ConnectionId`, `SourceFilePath`, `SourceLineNumber`, `SourceStartColumnNumber`, `SourceEndColumnNumber`, `TargetFilePath`, `TargetLineNumber`, `TargetStartColumnNumber`, `TargetEndColumnNumber` | `ReferenceConnectionLine`, with raw/resolved endpoint documents and exact selection ranges |

### Preferences and image definitions

| Source properties | Destination or treatment |
| --- | --- |
| `AppSettings.CodeWindows`, `ReferenceHighlights`, `DiagramImages`, `KeyboardShortcuts`, `ResourceComparison`, `Diagnostics`, `Appearance` | Their respective relational preference/definition tables |
| `AppSettings.LoadMostRecentWorkbenchOnStartup` | `ApplicationPreference` |
| `AppearanceSettings.Theme` | `ApplicationPreference` |
| `ResourceComparisonSettings.IgnoreWhitespaceByDefault`, `IgnoreCaseByDefault` | `ApplicationPreference` |
| `DiagnosticsSettings.EnableInternalLogging` | `ApplicationPreference` |
| `CodeWindowSettings.DefaultBackcolor`, `BackcolorsByExtension` | Default preference and ordered `ExtensionAppearance` rows |
| `ExtensionBackcolorSetting.Extension`, `Backcolor`, `Language` | `ExtensionAppearance` |
| `ReferenceHighlightSettings.Styles` | Ordered `ReferenceStyle` rows |
| `ReferenceHighlightStyleSetting.Language`, `Kind`, `Foreground`, `IsBold`, `IsItalic`, `IsUnderline` | `ReferenceStyle` |
| `DiagramImageSettings.Images` | Ordered `DiagramImageDefinition` rows |
| `DiagramImageDefinition.Id`, `Name`, `Regex`, `MatchTarget`, `NameRegex`, `ContentRegex`, `ResourceTypeFilter`, `SortOrder`, `OriginalFileName`, `ImageDataBase64` | Image definition columns plus decoded asset reference |
| `KeyboardShortcutSettings.Version` | Keyboard preference version in `ApplicationPreference` |
| `PersistenceConnectionSettings.ConnectionString` | Remains in protected local bootstrap configuration; activate a new destination only after successful publication |

The following keyboard flags each map to one `InputPreference` entry with the exact original property name and boolean value:

- `EnableCanvasCtrlMousePanning`
- `EnableTabCtrlMouseScrolling`
- `EnableTabCtrlShiftMouseAutoscrolling`
- `EnableCodeShiftMouseAutoscrolling`
- `EnableCodeCtrlShiftMouseScrollbarLockedScrolling`
- `EnableCodeCanvasShiftMousePanning`
- `EnableCodeCanvasCtrlShiftMouseZooming`
- `EnableCodeViewCtrlPlusMinusNavigation`
- `EnableCtrlNumberViewSwitching`
- `EnableCodeTabCtrlASNavigation`
- `EnableDiagramCtrlQSidebarToggle`
- `EnableDiagramCtrlWWorkflowSidebar`
- `EnableDiagramShiftMousePanning`
- `EnableDiagramCtrlShiftMouseZooming`

Persisted derived getter values such as display labels, formatted multipart names, existence flags, and summary counts are recomputed from authoritative records. Computed getters may currently appear in JSON even though they are not writable model state. Migration must identify these explicitly rather than classify every unknown-looking JSON field as disposable. Transient WPF controls, open-tab view models, navigation state not saved by the stores, and edit-dialog enums are not new authoritative tables.

## 20. Source References

The current-format mappings above are grounded in these repository files:

- [SQL document schema and loader](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Storage/SqlServerDocumentStore.cs>)
- [Database metadata store interface](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Storage/IDatabaseMetadataStore.cs>)
- [Scope library](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/ScopeLibrary.cs>), [scope](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/Scope.cs>), [resource](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/ScopedResource.cs>), [virtual folder](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/VirtualFolder.cs>)
- [Snapshot library](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/DatabaseSnapshotLibrary.cs>), [metadata and captured rows](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/DatabaseMetadataSnapshot.cs>), [history models](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/DatabaseSnapshotHistory.cs>)
- [History reconstruction](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/DatabaseSnapshotHistoryService.cs>), [source capture SQL](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/DatabaseMetadataQueryService.cs>)
- [Document paths and table rendering](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/DatabaseDocumentService.cs>)
- [Diagram](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/DiagramDocument.cs>), [object](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/DiagramObjectSnapshot.cs>), [metadata and workflow](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/DiagramMetadata.cs>)
- [Workbench](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/WorkbenchState.cs>), [workspace](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/WorkspaceState.cs>), [window state](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/OpenDocumentState.cs>)
- [Settings](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/AppSettings.cs>), [code appearance](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/CodeWindowSettings.cs>), [reference appearance](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/ReferenceHighlightSettings.cs>), [image definitions](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Models/DiagramImageSettings.cs>)
- [Reference indexing](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/ScopeReferenceIndexService.cs>), [reference resolution](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/ScopeReferenceIndex.cs>), [Object Explorer search](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Services/FileTreeService.cs>)
- [Persistence packages](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Storage/PersistencePackageService.cs>), [connection bootstrap](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Storage/SqlServerConnectionOptions.cs>)
- [Startup and save integration](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/MainWindow.xaml.cs>), [current grid loading](</C:/Users/ciara/source/repos/Surf 2.0/Surf2/Controls/FloatingSpreadsheetWindow.xaml.cs>)
