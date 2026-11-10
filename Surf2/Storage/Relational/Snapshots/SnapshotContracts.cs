using System.Buffers.Binary;
using System.Security.Cryptography;
using Surf2.Models;

namespace Surf2.Storage.Relational.Snapshots;

public sealed record SnapshotHeader(string SnapshotId, string DisplayName, string DatabaseName,
    DateTimeOffset ImportedAtUtc, long SortOrdinal, long UserKey = 1);
public sealed record SnapshotSummary(long SnapshotKey, Guid PublicId, string SnapshotId, string DisplayName,
    string DatabaseName, DateTimeOffset ImportedAtUtc, long SortOrdinal, long? CurrentVersionKey, byte[] RowVersion);
public sealed record SnapshotResourceSummary(long ResourceKey, Guid PublicId, long SnapshotKey,
    DatabaseVersionedResourceKind? Kind, string SchemaName, string ObjectName, string OriginalResourceKey,
    long RevisionKey, long SortOrdinal, byte[] RowVersion, TableRevisionSource TableSource = TableRevisionSource.Metadata);
public sealed record ObjectSummary(SnapshotResourceSummary Resource, SqlDatabaseObjectKind ObjectKind,
    string TypeDescription, string ParentSchemaName, string ParentObjectName);
public sealed record TableSummary(SnapshotResourceSummary Resource, bool HasFullData,
    long FullDataRowCount, DateTimeOffset? FullDataImportedAtUtc);
public sealed record SnapshotHistorySummary(long HistoryKey, long SnapshotKey, string SnapshotId,
    int NextVersionNumber, long SortOrdinal, byte[] RowVersion);
public sealed record SnapshotVersionHeader(string VersionId, string VersionName, int VersionNumber,
    DateTimeOffset CreatedAtUtc, bool IsInitial, long SortOrdinal);
public sealed record SnapshotVersionSummary(long VersionKey, long SnapshotKey, string VersionId,
    string VersionName, int VersionNumber, DateTimeOffset CreatedAtUtc, bool IsInitial, long SortOrdinal,
    long ChangeCount);
public sealed record SnapshotChangeHeader(DatabaseVersionedResourceKind Kind,
    DatabaseSnapshotResourceChangeKind ChangeKind, string ResourceKey, string DisplayName,
    string RelativePath, long SortOrdinal, long? PreviousRevisionKey);
public sealed record SnapshotChangeSummary(long ChangeKey, long VersionKey, long ResourceKey,
    DatabaseVersionedResourceKind Kind, DatabaseSnapshotResourceChangeKind ChangeKind,
    string OriginalResourceKey, string DisplayName, string RelativePath, long SortOrdinal,
    long? PreviousRevisionKey);
public sealed record SnapshotRevisionHandle(long ResourceKey, long RevisionKey);
public sealed record OrderedColumn(long ColumnRevisionKey, long SortOrdinal, SqlColumn Column);
public sealed record OrderedPrimaryKey(long KeyColumnKey, long SortOrdinal, SqlPrimaryKeyColumn Column);
public sealed record FullDataSelection(long SelectionKey, long SortOrdinal, string TableName);
public sealed record TableMetadataDetails(long RevisionKey, SqlTable Table,
    IReadOnlyList<OrderedColumn> Columns, IReadOnlyList<OrderedPrimaryKey> PrimaryKeys);
public sealed record TableDataMetadata(long RevisionKey, SqlTable? Table, string SchemaName, string TableName);

// Cursors are tied to a particular owner and immutable version, or mutable head
// token. The store validates them before issuing the next page.
public sealed record SnapshotCursor(long OwnerKey, long? VersionKey, byte[] HeadToken,
    long SortOrdinal, long EntryKey, int? Kind = null, Guid Epoch = default, string Query = "");
public sealed record SnapshotPage<T>(IReadOnlyList<T> Items, SnapshotCursor? Next, Guid Epoch);

public sealed class SnapshotConcurrencyException() : InvalidOperationException(
    "The snapshot changed. Reload its metadata before publishing or continuing this page.");

public static class SnapshotIdentity
{
    public static DatabaseVersionedResourceKind? ResourceKind(SqlDatabaseObjectKind kind) => kind switch
    {
        SqlDatabaseObjectKind.Unknown => null,
        SqlDatabaseObjectKind.StoredProcedure => DatabaseVersionedResourceKind.StoredProcedure,
        SqlDatabaseObjectKind.View => DatabaseVersionedResourceKind.View,
        SqlDatabaseObjectKind.Function => DatabaseVersionedResourceKind.Function,
        SqlDatabaseObjectKind.Trigger => DatabaseVersionedResourceKind.Trigger,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string LegacyResourceKey(DatabaseVersionedResourceKind kind, string schema, string name) =>
        $"{kind}|{SqlName.FormatPlainMultipartName(schema, name)}";

    // Hash raw UTF-16 code units, including unmatched surrogates. No encoder
    // replacement, case folding, normalization, delimiter ambiguity or trimming.
    public static byte[] Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[1024];
        for (int start = 0; start < value.Length; start += buffer.Length / 2)
        {
            int count = Math.Min(buffer.Length / 2, value.Length - start);
            for (int i = 0; i < count; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(i * 2, 2), value[start + i]);
            hash.AppendData(buffer[..(count * 2)]);
        }
        return hash.GetHashAndReset();
    }

    public static byte[] NameHash(string schema, string name) =>
        Hash($"{schema.Length}:{schema}{name.Length}:{name}");
}
