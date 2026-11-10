using System.IO;

namespace Surf2.Storage.Relational.Packages;

/// <summary>Imports database state only. Optional local files are verified, never restored.</summary>
public sealed record RelationalPackageImportOptions
{
    public Guid? ImportIdentity { get; init; }
    public string? ExpectedManifestSha256 { get; init; }
    public int BatchRows { get; init; } = 128;
    // Includes DataTable schema/storage reservation and all retained row payloads.
    // The default accommodates the format's 64 MiB single-cell limit with overhead.
    public long BatchBytes { get; init; } = 128 * 1024 * 1024;

    internal void Validate()
    {
        if (ImportIdentity == Guid.Empty || BatchRows is < 1 or > 1024 ||
            BatchBytes is < 65536 or > 256L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(RelationalPackageImportOptions));
        if (ExpectedManifestSha256 != null) PackageRowDecoding.Hash(ExpectedManifestSha256);
    }
}

public sealed record RelationalPackageImportResult(Guid ImportIdentity, Guid DatabaseIdentity,
    Guid SourceDatabaseIdentity, string ManifestSha256, long ImportedRows, long CapturedRows,
    int VerifiedSkippedLocalFiles, long VerifiedUncompressedBytes)
{
    public string LocalFilePolicy => "VerifyAndSkip-v1";
    public bool Activated => false;
}

/// <summary>A failed installed destination is deliberately not resumable; select a new Empty target.</summary>
public sealed class RelationalPackageImportException(string message) : IOException(message);
