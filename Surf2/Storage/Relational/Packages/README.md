# Relational Package Export Contract

`RelationalPackageExporter(RelationalSession, RelationalPackageLimits?)` exposes
`ExportAsync(destination, RelationalPackageExportOptions?, cancellationToken)`.
This slice exports only. It never provisions a database, changes database options,
takes the migration target lease, imports records, or executes saved definitions.

## Versions and entries

The ZIP contains `manifest.json`, `database/<schema>/<table>.rows.ndjson`, and
optional `local-files/<approved relative path>`. The exact identifiers are:

- `PackageIdentifier`: `Surf2.Relational.Package`
- `PackageVersion`: 2 (not legacy persistence package format 1)
- `ExporterVersion`: 1
- `RowEncodingVersion`: 1
- `DatabaseFormatIdentifier`: `Surf2.Relational`
- `DatabaseSchemaVersion`: 1
- `Consistency`: `SqlSnapshot-v1`, or explicitly selected `SqlSerializableBlocking-v1`
- `LocalFilePolicy`: `ExplicitCallerSelection-v1`

Manifest JSON uses the PascalCase property names in `PackageContracts.cs`.
Every table stream has its row count, uncompressed byte count, and lowercase
SHA-256 of the exact uncompressed entry bytes, including each trailing LF.
Local entries have uncompressed byte counts and SHA-256. The result additionally
returns the manifest SHA-256; a manifest cannot include its own checksum.
ZIP compressed sizes/CRC are not substitutes for these checksums.

The source database identity is provenance, not a connection string or the new
destination's identity. No server name, database name, connection settings,
credentials, or migration-run source paths are emitted as package metadata.
User-authored content is preserved, not scrubbed or logged.

## Table whitelist and selection

The fixed whitelist is `PackageTableCatalogue.Authoritative`. No discovery of
arbitrary user tables is allowed. Capture names are generated only from positive
`DataLayout.LayoutKey` values, never from captured source schema/table names.

Published snapshots, sealed revisions, and their Ready datasets are included.
Unpublished snapshots, unsealed revision payloads, Writing datasets, and unused
capture layouts are omitted. Snapshot-wide orphan/duplicate column/key entries
within published snapshots remain represented. TextContent/Asset include only
rows referenced by exported authoritative tables; derived index-only content and
unreferenced staging/garbage content are not exported.

All committed authoritative State tables are included. Migration bookkeeping,
SourceProvenance, SchemaMigration, StorageFormatInfo, all derived Index tables,
StateCatalogueGeneration, and SnapshotCatalogueHead are excluded. Storage format
information instead appears in the manifest. Install/import must reseed the
invalidation heads. ScopeCatalogueState is retained without its rowversion.

FK closure is checked against these exact selection predicates. A published row
referencing omitted/incomplete data aborts export instead of silently producing
a broken package. Sealed table-data revisions require Ready datasets; actual row
counts and contiguous zero-based RowOrdinal ranges are verified in the same SQL
snapshot. Generated capture streams retain raw scalar-token columns, binary
property descriptors, and separate DataValueException records without decoding
or normalizing JSON values.

## Schema and restore rules

Columns retain SQL column IDs, type, byte length, precision/scale, nullability,
collation/code page, identity seed/increment/observed last value, computed status,
and restore policy. Identity values in records and original numeric FK identities
are preserved. Each table records its PK column order and FK column pairs;
records are streamed in ascending SQL PK order. Collation remains source metadata;
import must explicitly verify compatible target definitions/code pages.

`StreamOrdinal` maps a column to its cell-array position. Omitted columns have
null StreamOrdinal/Encoding. `rowversion` columns (including `Version` and
`RowVersion`) have no exported values: regenerate fresh target tokens. Computed
columns also have no exported values: regenerate using the installed schema.
ComputedDefinition is bounded descriptive metadata, NEVER executable import DDL.

The eventual importer must install the exact supported schema in a separately
owned non-ready destination and hold `MigrationTargetLease`. Restore identities
explicitly and reseed at least to the exported counter/max restored value. Identity
allocator metadata is not versioned by SQL Server: the observed last counter may
include concurrent reservations or rolled-back inserts, unlike the pinned rows.
Preserving that higher reservation is safe and avoids key reuse; it is not claimed
to be a historical counter at snapshot start.

Update the installer's existing `UserProfile` key 1 rather than inserting a
duplicate seeded profile. Snapshot/state FK cycles require deferred loading:
disable appropriate installed constraints only in that non-ready owned target,
load in bounded batches, then enable every constraint using `WITH CHECK CHECK`,
verify trusted/enabled status and package hashes/counts, rebuild invalidation/Index
state, validate models, and publish Ready last. Do not execute SQL definitions
from the package or apply source owner revision tokens to the new database.

## Row encoding v1

Each row is one JSON array followed by LF. There is no entry header and no BOM.
Cells are ordered by StreamOrdinal. SQL NULL is JSON `null`; an empty non-null
string/binary cell instead has byteLength `"0"` and an empty chunks array.

- `integer-string-v1`: invariant base-10 JSON string, including bigint extremes.
- `boolean-v1`: JSON boolean.
- `sql-decimal-words-v1`: object with numeric `precision`, `scale`, boolean
  `positive`, and `words`: four eight-digit uppercase hex strings, least-significant
  32-bit magnitude word first. Rebuild SqlDecimal without going through CLR decimal.
- `money-string-v1`: invariant decimal string with exactly four fractional digits.
- `float64-bits-v1` / `float32-bits-v1`: sixteen/eight uppercase hex digits for
  IEEE bit patterns. Preserve negative zero; these are not decimal approximations.
- `guid-string-v1`: Guid D string.
- `date-string-v1`: `yyyy-MM-dd`.
- `datetime-string-v1`: `yyyy-MM-ddTHH:mm:ss.fffffff`, no invented timezone.
- `datetimeoffset-string-v1`: invariant O format retaining ticks and offset.
- `time-string-v1`: invariant TimeSpan c format, retaining ticks.
- `utf16le-chunks-v1`, `sql-codepage-chunks-v1`, `binary-chunks-v1`: object
  `{ "byteLength": "<integer>", "chunks": ["<base64>", ...] }`. Decode each
  base64 chunk independently and concatenate its bytes. Chunk boundaries may
  bisect UTF-16 code units/surrogate pairs and are not text boundaries. Unicode
  values use the exact SQL `CONVERT(varbinary(max), value)` UTF-16LE bytes, including
  unpaired code units. ANSI values retain their SQL collation/code-page bytes;
  binary values retain every byte. Fixed-length padding is retained.

Each byte stream has its projected SQL DATALENGTH checked against the cell limit
before opening it, and against actual streamed EOF. Scalars use typed SqlClient
accessors, including SqlDecimal for precision 38. Unsupported types, alias types,
hidden/encrypted columns, untrusted FKs, or unexpected generated capture columns
fail explicitly. There is no universal EAV or whole-library JSON representation.

Import must parse incrementally; JSONL does not authorize allocating a whole
potentially large row/line. Buffers, source-cell and source-row payload bytes,
entry/package uncompressed bytes, entries, tables, columns and manifest size are
bounded by RelationalPackageLimits. Scalar row accounting reserves 256 bytes per
cell; chunked row accounting uses actual source-byte lengths, not base64 lengths.
Entry/package budgets independently bound actual encoded output.

## Consistency, cancellation, local files

Export requires Ready twice: the normal session guard and an exact Ready marker
inside the transaction, so a migration-validation session cannot export Converting
data. Normally `ALLOW_SNAPSHOT_ISOLATION` must already be ON. The exporter uses one
SNAPSHOT transaction for all SQL metadata, filtering, counts, and streams. RCSI
alone is statement-level and insufficient. There is no NOLOCK or silent fallback.

The explicit `AllowBlockingConsistentFallback` option defaults false. If snapshot
isolation is unavailable at initial inspection, true permits one SERIALIZABLE
transaction instead. This can block source writes for the whole database export,
cause deadlocks, and should require deliberate provider/UI consent. The manifest
records that mode. Snapshot mode remains preferred when available. Never switch
isolation after streaming starts or retry arbitrary SQL failures in blocking mode.
Owned SQL fixtures should enable snapshot isolation before initialization; enabling
it on a newly provisioned destination is an installer decision, not an export write.

Row-versioned reads avoid long shared data locks, but retain source row versions
and acquire ordinary schema stability locks. Concurrent DDL may abort snapshot
access; retry without schema changes. Large exports require enough server version
store space. The SQL transaction/connection are released before local-file hooks.
See [SQL snapshot isolation](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/snapshot-isolation-sql-server)
and [SqlClient streaming](https://learn.microsoft.com/en-us/sql/connect/ado-net/sqlclient-streaming-support).

Local files are opt-in only; no implicit AppData traversal is performed. The
parent supplies its explicitly approved inclusion set as an async sequence of
RelationalPackageLocalFile records, applying legacy inclusion/backup decisions.
OpenPinnedReadAsync must return a read-only, stable stream with the declared length;
export owns/disposes it. A Windows FileStream opened with FileAccess.Read and
FileShare.Read is appropriate. The parent owns physical-root/reparse-path validation
and must not include the destination/pending file or bootstrap settings under an
alias. Paths must be safe relative paths; known connection-settings names/backups,
traversal, rooted/ADS paths, Windows reserved names and ZIP case collisions fail.
Local files are pinned individually, not claimed to share SQL's snapshot time.

Output uses a GUID-owned CreateNew pending file beside the destination. Only after
all streams, checksums, manifest, ZIP directory and disk flush complete is it renamed
to the final path. Existing targets require explicit ReplaceExisting; replacement
uses File.Replace. Cancellation/failure disposes readers/transactions/streams and
deletes only this operation's pending file, never an existing target or source.
If filesystem permissions/locks prevent cleanup, PendingPackageCleanupFailed is
attached to the original exception so the parent can report that owned leftover.
