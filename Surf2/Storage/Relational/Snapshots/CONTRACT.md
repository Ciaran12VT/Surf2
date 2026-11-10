# Snapshot Storage Contract

Implemented storage contract, not a proposed runtime/UI integration. Namespace:
`Surf2.Storage.Relational.Snapshots`. No library load/save or JSON-payload facade.

## Installation and Ownership

`Schema/001.Snapshots.sql` executes as one command, without `GO`, after Core and
before Capture. Core must insert profile 1 before Snapshots runs. Only the schema
installer executes DDL. Runtime query/writer methods never initialize a database.

Capture references `surf.SnapshotResourceRevision.RevisionKey` (bigint PK).
The row also has `ResourceKey`, `SnapshotKey`, and `IsSealed`. Its composite FK
binds the logical resource to the same snapshot. `surf.DataSet` remains Capture's
ownership. Snapshot data revisions preserve their own schema/table spelling in
`surf.TableDataRevision` and a nullable companion SqlTable in
`surf.TableDataRevisionMetadata`; they never borrow the current metadata layout.

Mutable snapshot, resource, catalogue and history heads have SQL `rowversion`.
Tokens are eight-byte arrays, never timestamps or historical version numbers.
No definition, asset, row collection or history object is stored in JSON here.

## Incremental Migration

Construct `RelationalSnapshotWriter(openConnection, activeTransaction, contentStore)`.
It does not commit, publish format readiness, infer source identities, normalize
source values, or checkpoint. The coordinator records source-path identity maps
and checkpoints in that SAME transaction and reuses committed mapped keys on
resume. Methods are insert operations, not blind idempotent upserts. Root scalar
header prepasses and streamed source ownership are the coordinator's responsibility.

| Public writer method | Result / semantics |
| --- | --- |
| `CreateSnapshotAsync(SnapshotHeader, ct)` | bigint snapshot key; initially unpublished |
| `CreateResourceAsync(snapshotKey, kind?, schema, name, originalResourceKey, ordinal, ct)` | bigint logical resource key; supports history-only resources and duplicate entries |
| `InsertCurrentObjectAsync(snapshotKey, SqlDatabaseObject, ordinal, originalResourceKey?, ct)` | resource/revision handle; writes one definition through shared instance content store; seals and selects the revision |
| `InsertCurrentTableAsync(snapshotKey, SqlTable, ordinal, originalResourceKey?, ct)` | resource/revision handle; **unsealed**, ready for streamed children |
| `InsertObjectRevisionAsync(resourceKey, SqlDatabaseObject, ct)` | unsealed previous/new typed object revision |
| `InsertTableRevisionAsync(resourceKey, SqlTable, ct)` | unsealed previous/new metadata revision |
| `InsertColumnAsync(snapshotKey, tableRevisionKey?, SqlColumn, ordinal, currentCollectionOrdinal?, ct)` | typed child key; table may be null for orphan current entries |
| `InsertPrimaryKeyAsync(snapshotKey, tableRevisionKey?, SqlPrimaryKeyColumn, ordinal, currentCollectionOrdinal?, ct)` | typed key-column key, with explicit constraint identity |
| `InsertFullDataSelectionAsync(snapshotKey, exactName, ordinal, ct)` | selection key; retains unresolved names and duplicates |
| `InsertTableDataRevisionAsync(resourceKey, previousPayloadTable?, ct)` | unsealed data revision; names default to resource names |
| `InsertTableDataRevisionAsync(resourceKey, exactDatasetSchema, exactDatasetTable, previousPayloadTable?, ct)` | explicit dataset names, independent of companion table names |
| `InsertHistoryAsync(snapshotKey, originalSnapshotId, nextVersionNumber, ordinal, ct)` | history key; duplicate histories are retained in source order; does not normalize next-number value |
| `InsertVersionAsync(historyKey, SnapshotVersionHeader, ct)` | version key, preserving duplicate IDs/numbers, timestamp, initial flag and source order |
| `InsertChangeAsync(versionKey, resourceKey, SnapshotChangeHeader, ct)` | change key, preserving exact legacy identity/display/path/order and nullable previous revision |
| `SealRevisionAsync(revisionKey, ct)` | forbids subsequent metadata-child inserts; data sealing requires its Capture dataset to be Ready |
| `SetCurrentRevisionAsync(resourceKey, revisionKey?, ordinal, expectedRowVersion?, ct)` | resource rowversion; null revision explicitly removes the current resource |
| `SetHistoryNextVersionAsync(historyKey, nextNumber, expectedRowVersion, ct)` | history CAS, retaining intentional number gaps |
| `PublishSnapshotAsync(snapshotKey, latestVersionKey?, ct)` | snapshot token; validates sealed references, next numbering and first-history/latest source-stable version pointer; does NOT activate database format |

For a TableMetadata PreviousPayload: insert its SqlTable revision, append its
columns/keys in their payload-local order, seal, then put that revision in the
change header. Do not use `InsertCurrentTableAsync` for previous payloads.

For a TableData PreviousPayload: create its typed data revision; pass the
optional companion table and explicit dataset names; create Capture's dataset
against that revision, stream batches, complete it, then seal and insert the
change. `writer.Connection` and `writer.Transaction` let Capture enlist in the
same transaction. Capture retains reported count, actual count and timestamp.

Current Columns and PrimaryKeys are snapshot-wide arrays, not just table
children. `ordinal` is revision-local collection order; `currentCollectionOrdinal`
is the original root-array position. Retain source `SqlColumn.Ordinal` and
`KeyOrdinal` independently. A null currentCollectionOrdinal means history-only
or staged revision child. Orphan current entries use a null tableRevisionKey
and a present currentCollectionOrdinal. Never filter them out during migration.
Duplicated table names do not authorize assigning a child to an arbitrary table;
the coordinator must preserve root order and explicitly validate any ambiguous
ownership. Root ordered queries do not depend on table ownership.

## Queries and Targeted Saves

Construct `RelationalSnapshotStore(session, contentStore)`. Every runtime method
requires the shared session's Ready format guard; migration uses the writer directly.

- `ListSnapshotsAsync`, `GetSnapshotAsync`: catalogue scalars and owner token.
- `ListResourcesAsync`, `ListObjectsAsync`, `ListTablesAsync`: current or selected
  version metadata; no definition/content text, dataset rows or previous payloads.
- `ResolveResourceAsync(resourceKey, versionKey?, ct)`: one identity/revision mapping
  or null when absent; validates version ownership.
- `FindResourceKeysAsync(snapshotKey, exactOriginalKey, ct)`: exact, hash-assisted
  lookup returning all matches rather than silently resolving ambiguity.
- `ReadObjectAsync(revisionKey, ct)`: one complete definition and its metadata,
  using SequentialAccess and GetStream over the raw varbinary-converted definition; explicit
  selected-text and metadata budgets apply before string/buffer allocation.
- `ReadTableMetadataAsync(revisionKey, ct)`: one sealed metadata aggregate, with
  ordered columns/keys. It does not read rows or any table-data revision.
- `ReadHistoricalTableMetadataAsync(snapshotKey, versionKey, resourceKey, ct)`:
  the selected version's EFFECTIVE table scalars and replayed root children.
  Use this, not a raw revision read, for historical table opening.
- `OpenHistoricalSnapshotAsync(snapshotKey, versionKey, ct)`: disposable SQL-temp
  identity/metadata projection. `ReadPageAsync(collection)` and `StreamAsync(collection)`
  expose all six reconstructed root collections; Header.ImportedAtUtc is the
  selected version's CreatedAtUtc. The plan never materializes definitions or rows.
- `ReadTableDataMetadataAsync(revisionKey, ct)`: exact dataset names and nullable
  companion table. Pass the same revision to Capture for descriptor/rows.
- `ListCurrentColumnsAsync`, `ListCurrentPrimaryKeysAsync`,
  `ListFullDataSelectionsAsync`: paged original root collection order.
- `GetHistoryAsync`, `ListVersionsAsync`, `ListChangesAsync`: scalar summaries,
  counts and nullable previous revision references only.
  Default history/version listings select the first history by `(SortOrdinal, HistoryKey)`.
  Explicit selected-version contexts expose `HistoryKey` and replay only that history;
  historical cursors bind both the version and history owner.
- `GetHistoryByKeyAsync(historyKey, ct)`: one exact history summary, or null if
  its owning snapshot is absent/unpublished. `ListHistoriesAsync(snapshotKey,
  pageSize, cursor, ct)` pages every history in source order.
- `ListHistoryVersionsAsync(snapshotKey, historyKey, pageSize, cursor, ct)`:
  versions of that explicit published owner only, in source order. A mismatched
  owner throws; the query name `history-versions:{historyKey}` binds continuation
  to that history, preventing cursor reuse across histories or default listings.
- `ResolveVersionKeyAsync(snapshotKey, originalVersionId, ct)`: first source-order
  `OrdinalIgnoreCase` ID match in the first history, or null. An exact hash hit only
  bounds the incremental ID scan; it never overrides an earlier case-insensitive
  match. No definition, rows or history payload is read.
- `ExecuteMutationAsync(snapshotKey, expectedSnapshotRowVersion, callback, ct)`:
  opens a transaction, CAS-checks the owner, invokes the incremental writer,
  validates sealed references, commits, and returns the new snapshot token.
  Its writer methods reject owners outside this selected snapshot. The exposed
  connection/transaction remain trusted enlistment hooks, not a SQL security sandbox. Ordinary runtime callers
  must not bypass this CAS wrapper by constructing a migration writer directly.
  Migration-validation sessions reject this runtime mutation before opening SQL;
  read APIs and caller-transaction migration writers are unchanged.

`UpdateSnapshotHeaderAsync` updates only catalogue scalars; `RemoveCurrentColumnEntryAsync`,
`RemoveCurrentPrimaryKeyEntryAsync`, and `RemoveFullDataSelectionAsync` perform
explicit owner-scoped collection removals. No mutation infers deletion from a
partially loaded view. Revision children are retained. Replace an object/table by
creating its new revision and changing its existing resource head, not by inserting
another current resource. Runtime multi-record publication belongs in the CAS wrapper.

Pages accept 1..1000 entries, also bounded to 8 MiB estimated Unicode metadata.
Large individual metadata entries fail explicitly rather than truncate. Page
cursors bind owner, selected version, query kind/filter, connection Epoch and
head rowversion. Current pages use short RepeatableRead transactions and stable
`(SortOrdinal, EntryKey)` ordering. No reader or transaction survives page return.
Changing a head invalidates continuation rather than mixing generations.

### Read Allocation Limits

Listing, single-summary, selected object metadata, data companion metadata and
selected table-detail reads use an 8 MiB metadata budget. SQL projects a leading
bigint byte charge (`256 + DATALENGTH` of every returned string) BEFORE any LOB
field. Sequential readers validate that charge before `GetString`/mapping. A
selected table charges its header plus every column/key to one cumulative budget;
the fixed child charge bounds even arbitrarily many empty-name children. Historical
work-plan scans and replay headers use the same preallocation policy. Historical
summary synthesis also charges generated legacy keys before allocation, and enriched
historical pages share an aggregate budget rather than accumulating unbounded details.

Normal catalogue pages can end early with a valid continuation when their next
entry would exceed the remaining budget. An individually oversized entry, selected
aggregate or enriched historical page throws `SnapshotReadLimitException` with its
`LimitBytes`; it never returns partial/truncated data. Reduce the requested historical
page size after an enriched-page budget rejection. Limits are read policy, not schema
constraints or authorization to discard source content during migration.

The existing two-argument `RelationalSnapshotStore(session, content)` constructor
is retained. Its selected-definition default is **16 MiB of SQL UTF-16 bytes**.
The additive three-argument constructor accepts `SnapshotReadLimits` with
`SelectedDefinitionBytes` from 1 byte to the existing **128 MiB** single-content
storage ceiling. This explicitly separates resident selected-document policy from
the migration/storage ceiling. A model validator intentionally examining every
supported definition should opt into 128 MiB; runtime composition must review the
selected-document budget before rollout. No current runtime composition is changed.

`ReadObjectAsync` checks authoritative `DATALENGTH(Text)` from the same query before
opening its binary stream or allocating a character buffer; it does not trust saved
ByteCount/CharacterCount fields. SQL projects `CONVERT(varbinary(max), Text)` and the
reader manually copies little-endian UTF-16 code units, without an encoding decoder
or SqlClient text-reader fallback. Unpaired surrogates are authoritative code units,
not replacement characters. Reads fill a validated exact-size character buffer
using a fixed 16 KiB binary buffer, verify declared length and stream end, and then
create the exact string.
Mismatched/truncated streams fail, including odd Unicode byte counts. The character
buffer and final string are TWO bounded selected-text copies; caller/editor copies,
SQL work-table memory, transport packets and metadata object overhead are additional
owners. These byte limits are not a measured total-process memory guarantee.

Commands are canceled before rejecting unread oversized LOBs or abandoning unused
page remainders; readers, transactions and connections are still disposed. Actual
SqlClient cancellation/disposal behavior remains an integration gate, not established
by the pure guard tests. Ready/pinned-connection guards remain
`RequireReadyAsync` followed by `OpenAsync`; changed database identity is not remapped.

## Field Preservation

All name/ID/description/path strings use nvarchar(max). Lookup hashes cover raw
UTF-16LE code units; multipart names are length-delimited. Hash matches always
require exact spelling and DATALENGTH comparison before identity binding.
No collation-based case folding silently rewrites authoritative names.

| Source | Authoritative storage |
| --- | --- |
| Snapshot ID, display/database names, import timestamp, original order | DatabaseSnapshot |
| Every SqlDatabaseObject property except Definition | DatabaseObjectRevision; Kind retains Unknown too |
| Exact Definition | TextContent FK, stored by RelationalContentStore without document normalization |
| Every SqlTable property | TableMetadataRevision, or independent TableDataRevisionMetadata for payload companion |
| Every SqlColumn property | TableColumnRevision, with separate source ordinal and collection ordinal |
| Every SqlPrimaryKeyColumn property | PrimaryKeyColumn, including exact constraint spelling per entry |
| FullDataTableNames entries | FullDataTableSelection, no resolution prerequisite |
| Dataset schema/table names | TableDataRevision, not inferred from current metadata |
| Dataset reported count, import timestamp, rows | Delegated to Capture's dataset/row catalogue |
| History snapshot ID, next number, collection order | SnapshotHistory |
| Every version scalar and collection order | SnapshotVersion |
| Every change scalar, collection order, nullable PreviousPayload | SnapshotChange; typed previous revision and children, never new-version content |

All model timestamps are datetimeoffset(7) and preserve offsets. Reported negative
or inconsistent row counts are retained, not clamped. Keys/order/counts are bigint;
source enum codes, model int ordinals, max length and scale remain their exact int
types; NumericPrecision is tinyint. No floating-point property exists in this slice.
Computed FullName/Counts are not persisted as competing authoritative values.

## Reverse History

All versioned resource/object/table listings and resource resolution execute
`HistoricalSnapshotReplay`, not an independent earliest-change shortcut.
Construction copies current root identities/scalars into session-local SQL work
tables, then streams ONLY the selected version's history, ordered by
`(VersionNumber DESC, Version.SortOrdinal, VersionKey, Change.SortOrdinal, ChangeKey)`.
The complete tuple is the keyset continuation. Equal-number versions remain in
source order with all changes of one version before the next; surrogate keys only
break equal source ordinals. Duplicate histories and duplicate version numbers
are legal, retained entries rather than uniqueness failures. The current version
pointer selects the first history's maximum number and its first source-order tie;
an empty first history does not fall through to a later history.
Client batches are limited to 128 entries and 8 MiB of estimated scalar metadata.
Source snapshot rowversion is checked before/after construction and between pages;
concurrent publication fails rather than yielding a mixed source generation.
No transaction remains open while a caller consumes the constructed plan.

The exact legacy operations are applied to the projection:

- Added uses the original legacy locator parser, including malformed-locator no-op.
- Object payload removes ALL kind/schema/name matches, then appends its object.
- Metadata removal deletes all matching tables, columns, keys and datasets, plus
  all matching full-data selections. A metadata payload then appends its table
  and its literal child arrays in their original order, even nonmatching child names.
- Data payload removes all matching datasets and appends its previous data revision.
  It updates ONLY the first matching table, or appends its optional companion if
  none exists. Flags become true, count becomes Max(reported count, actual rows),
  and import timestamp becomes the dataset timestamp. The existing table's position
  does not change. A newly appended companion is updated even when its names
  differ from the dataset. A null companion never invents a table.
- Added data rollback removes matching datasets, clears ONLY the first table's
  full-data fields, and removes all matching selections. Data restore conditionally
  appends one plain multipart selection, preserving existing duplicates and spelling.
- Null Modified/Deleted previous payloads remain no-ops.

Matching uses .NET OrdinalIgnoreCase in bounded batches, not SQL collation or
trimmed/folded names. The SQL work plan stores sequence positions explicitly;
RemoveAll+append therefore changes each reconstructed collection exactly as the
legacy service does. Orphan root children survive unless their literal names match
an actual removal operation. Table detail selection uses the replayed ROOT child
collections, not merely a revision ownership FK. No full snapshot/row clone exists.

`SnapshotResourceSummary.TableSource=DataCompanion` identifies a resurrected table
whose immutable source revision belongs to a TableData resource. Its ResourceKey
is that source resource key, not an invented persisted metadata resource; use the
selected-version table-read method or the context entry to open it. Data revisions
remain independent and resolve through Capture using their original revision keys.

The context owns one pooled connection and its temp tables. Dispose it at the end
of a bounded query/streaming operation, including early enumeration exit; never
retain it on a WPF control. Disposal drops work tables before returning the connection.
Legacy page methods construct/dispose a plan per request; multi-page jobs can reuse
one explicit context instead. Work-table/scan costs scale with catalogue size and
rollback count; benchmark those costs before adding a derived cache or accelerators.

## Validation Hooks and Remaining Gates

`SnapshotContractChecks.Run()` returns 19 deterministic passing-case labels, or
throws. It covers exact hashes, existing enum mapping, reverse modified/deleted/
re-added states, gaps, history-only resources, no-op null payloads, within-version
ordering, and payload-free summary shapes. `ParseSql(script)` and
`ParseStoreQueries()` return ScriptDom syntax diagnostics without connecting to SQL.

`await SnapshotContractChecks.RunHistoryAsync()` compares the PRODUCTION replay
program against the actual legacy DatabaseSnapshotHistoryService.ReconstructSnapshot
for every target of 15 tiny fixture groups. It verifies full source fields and all
collection sequences, including mixed version/change interleavings, metadata/data
order reversal, duplicate first-table semantics, child/selection removals, table
resurrection, null/differently named companions, null/malformed changes, number gaps,
case/trailing-space matching, original column/key order, and offsets/subsecond times.
Additional fixtures cover duplicate histories with explicit/default ownership,
duplicate numbers without change interleaving (including a 128-change page boundary),
and duplicate/case-colliding IDs whose first source match differs from an exact hash hit.
Only these tiny test oracles construct graphs/rows; the SQL provider does not.

`await SnapshotContractChecks.RunReadLimitsAsync(ct)` exposes 16 pure checks for
definition policy/ceiling, exact/oversized metadata lengths, cumulative/empty-child
budgets, pre-read oversize rejection, odd lengths, one-byte/odd-sized binary chunks,
literal UTF-16LE bytes, all 65,536 code units, buffer boundaries, partial/extra-byte
and short/long stream rejection, empty definitions and preallocation cancellation.
The raw binary-reader additions have not been executed in this agent; the parent
owns the fresh build and pure/SQL rerun. The read-only
`RelationalDocumentRawTextSqlChecks.RunAsync` hook in Services/RelationalDocuments
covers actual current/previous SQL reads and current-document/historical previews.

The shared SQL regression harness should exercise these contracts in a newly
owned disposable fixture database only:

1. Execute all installer scripts as single commands; check FK ownership and
   bigint keys, null/orphan metadata, duplicate entries and names over 128 characters.
2. Import metadata one entity/child per checkpoint transaction; cancel before and
   after each commit. Mapped retries must not create duplicate rows.
3. Store exact definitions, offsets/subsecond timestamps, negative reported counts,
   duplicate/out-of-order source ordinals, empty and duplicate selections, and
   non-GUID IDs. Compare every typed source field and root collection position.
4. Use different Capture layouts for different data revisions of one table. Sealing
   missing/incomplete datasets must fail; metadata revision reads must not read rows.
5. Compare SQL context pages and selected table reads to the legacy oracle for every
   fixture version, including cross-resource table effects and all root collection order.
6. Attempt cross-snapshot previous references and columns/keys, unknown kinds
   and invalid next numbers. They must fail without
   publication. Modified/Deleted null previous references must remain no-ops.
7. Read one definition/table revision, metadata-only history pages and current
   root collections. Trace SQL to prove definitions/rows are not selected by lists.
8. Race owner-token saves and paging, reconnect with overlapping numeric keys,
   cancel SQL and exit early. Check stale cursors, rollback and disposal. No blind
   retry after an unknown commit outcome; reread the owner and operation mapping.
9. Import multiple histories for one snapshot and duplicate version numbers/IDs,
   including reverse physical insertion order, an empty first history and tied
   versions crossing 128 changes. Compare explicit and default history APIs and
   replay against the oracle, reject cross-history cursors, and verify publication
   chooses only the first history's latest first-source tie.
10. Query a single oversized nvarchar metadata entry, an over-budget table child
    aggregate, definitions on both sides of the configured selected limit, and
    byte-limited pages. Verify rejection before LOB allocation, exact content with
    an explicitly raised valid budget, correct sequential mappings and continuations,
    and real cancellation/connection reuse after unread-row rejection.

Preflight must explicitly reject unsupported null required scalars, unmappable
enum values, ambiguous snapshot-ID owner bindings,
missing history owners, conflicting resource identity maps, and non-standard
payload unions (e.g. an object change also carrying a dataset). Preserve protected
source evidence; do not silently discard unsupported extra payload fields.
The shared content store's single-definition size/encoding policy still applies.
One selected metadata aggregate is intentionally materialized within its 8 MiB
budget; use the ordered child-page/stream APIs for an exceptionally large table.
Actual SQL work-table execution, paging/cancellation/disposal and query plans remain
integration/scale gates. Graph-wide replay and source-order equivalence are checked
against the real legacy service by the owned fixtures; compilation/SQL parsing alone
do not establish the SQL backend's execution or performance characteristics.
