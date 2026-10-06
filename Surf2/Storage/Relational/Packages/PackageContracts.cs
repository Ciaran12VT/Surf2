using System.IO;

namespace Surf2.Storage.Relational.Packages;

public static class RelationalPackageFormat
{
    public const string Identifier = "Surf2.Relational.Package";
    public const int PackageVersion = 2;
    public const int ExporterVersion = 1;
    public const int RowEncodingVersion = 1;
    public const string ManifestEntry = "manifest.json";
}

public sealed record RelationalPackageLimits
{
    public int ChunkBytes { get; init; } = 16 * 1024;
    public long MaxCellBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxRowBytes { get; init; } = 256L * 1024 * 1024;
    public long MaxEntryBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public long MaxPackageBytes { get; init; } = 256L * 1024 * 1024 * 1024;
    public long MaxLocalFileBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxEntries { get; init; } = 4096;
    public int MaxTables { get; init; } = 512;
    public int MaxColumnsPerTable { get; init; } = 256;
    public int MaxTotalColumns { get; init; } = 32768;
    public int MaxManifestBytes { get; init; } = 16 * 1024 * 1024;
    public int CommandTimeoutSeconds { get; init; } = 120;

    internal void Validate()
    {
        if (ChunkBytes is < 1024 or > 64 * 1024 || MaxCellBytes is <= 0 or > int.MaxValue ||
            MaxRowBytes < MaxCellBytes || MaxRowBytes > 8L * 1024 * 1024 * 1024 ||
            MaxEntryBytes <= 0 || MaxPackageBytes < MaxEntryBytes || MaxLocalFileBytes <= 0 ||
            MaxLocalFileBytes > MaxEntryBytes || MaxEntries is <= 0 or > 16384 ||
            MaxTables is <= 0 or > 4096 || MaxTables > MaxEntries ||
            MaxColumnsPerTable is <= 0 or > 1024 || MaxTotalColumns is <= 0 or > 131072 ||
            MaxManifestBytes is <= 0 or > 128 * 1024 * 1024 || CommandTimeoutSeconds is <= 0 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(RelationalPackageLimits), "Invalid streaming package budget.");
    }
}

// Explicit extension hook, not a directory crawler. The caller applies its
// user inclusion rules and supplies a pinned, read-only stream. Export owns it.
public sealed record RelationalPackageLocalFile(string RelativePath, long ByteCount,
    Func<CancellationToken, ValueTask<Stream>> OpenPinnedReadAsync);

public sealed record RelationalPackageExportOptions(bool ReplaceExisting = false,
    IAsyncEnumerable<RelationalPackageLocalFile>? LocalFiles = null,
    bool AllowBlockingConsistentFallback = false);

public sealed record RelationalPackageColumn(int SqlColumnId, string Name, string SqlType,
    int MaxLengthBytes, byte Precision, byte Scale, bool Nullable, string? Collation,
    int? CodePage, bool Identity, string? IdentitySeed, string? IdentityIncrement,
    string? IdentityLastValue, bool Computed, string? ComputedDefinition, bool RowVersion,
    int? StreamOrdinal, string? Encoding, string RestorePolicy);

public sealed record RelationalPackageForeignKey(string Name, string ReferencedSchema,
    string ReferencedTable, IReadOnlyList<string> Columns, IReadOnlyList<string> ReferencedColumns);

public sealed record RelationalPackageTable(string Schema, string Name,
    IReadOnlyList<RelationalPackageColumn> Columns, IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<RelationalPackageForeignKey> ForeignKeys, string RowSelection);

public sealed record RelationalPackageTableStream(string Entry, RelationalPackageTable Table,
    long RowCount, long ByteCount, string Sha256);

public sealed record RelationalPackageLocalStream(string Entry, long ByteCount, string Sha256);

public sealed record RelationalPackageManifest(string PackageIdentifier, int PackageVersion,
    int ExporterVersion, int RowEncodingVersion, string DatabaseFormatIdentifier,
    int DatabaseSchemaVersion, int MinimumReaderVersion, int MinimumWriterVersion,
    Guid SourceDatabaseIdentity, DateTimeOffset SourceCreatedAtUtc, DateTimeOffset ExportedAtUtc,
    string Consistency, string LocalFilePolicy, IReadOnlyList<RelationalPackageTableStream> Tables,
    IReadOnlyList<RelationalPackageLocalStream> LocalFiles);

public sealed record RelationalPackageExportResult(string Destination, RelationalPackageManifest Manifest,
    string ManifestSha256, long UncompressedBytes);

public sealed class RelationalPackageLimitException(string message) : InvalidOperationException(message);
