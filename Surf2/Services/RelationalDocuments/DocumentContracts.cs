using System.Buffers.Binary;
using System.IO;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalDocuments;

public sealed record DocumentOpenLimits
{
    public long WarningBytes { get; init; } = 4 * 1024 * 1024;
    public long MaximumTextBytes { get; init; } = 16 * 1024 * 1024;
    public long CacheBytes { get; init; } = 32 * 1024 * 1024;
    public int CacheEntries { get; init; } = 32;
    public int ConcurrentReads { get; init; } = 4;
    public int MaximumMenuOccurrences { get; init; } = 4096;

    public void Validate()
    {
        if (WarningBytes < 1 || MaximumTextBytes < WarningBytes || MaximumTextBytes > 128 * 1024 * 1024 ||
            CacheBytes < 1 || CacheEntries < 1 || ConcurrentReads is < 1 or > 16 || MaximumMenuOccurrences is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(DocumentOpenLimits));
    }
}

// Window identity intentionally excludes the immutable revision and presentation alias.
public abstract record RelationalDocumentIdentity(Guid Epoch);
public sealed record SnapshotDocumentIdentity(Guid Epoch, long SnapshotKey, long ResourceKey, bool IsTableData)
    : RelationalDocumentIdentity(Epoch);
public sealed record PhysicalDocumentIdentity(Guid Epoch, string CanonicalPath) : RelationalDocumentIdentity(Epoch)
{
    public static PhysicalDocumentIdentity From(Guid epoch, string path) => new(epoch, Path.GetFullPath(path).ToUpperInvariant());
}

public sealed record PhysicalDocumentLocator : SessionLocator
{
    public PhysicalDocumentLocator(Guid epoch, string path, string fingerprint) : base(epoch)
    {
        PathHash = DocumentDigest.Parse(Convert.ToHexString(SnapshotIdentity.Hash(System.IO.Path.GetFullPath(path).ToUpperInvariant())));
        Fingerprint = DocumentDigest.Parse(fingerprint);
    }
    public DocumentDigest PathHash { get; }
    public DocumentDigest Fingerprint { get; }
}

public readonly record struct DocumentDigest(ulong A, ulong B, ulong C, ulong D)
{
    public static DocumentDigest Parse(string hex)
    {
        if (hex.Length != 64 || hex.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("A SHA-256 fingerprint is required.", nameof(hex));
        byte[] bytes = Convert.FromHexString(hex);
        return new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24)));
    }
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, A); BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], B);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], C); BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], D);
        return Convert.ToHexString(bytes);
    }
}

// The exact normalized physical path verifies digest collisions outside the value-only locator.
public sealed record DocumentTextKey(SessionLocator Locator, string RendererPolicy, string? PhysicalPath = null);
public sealed record RelationalDocumentAddress(RelationalDocumentIdentity Identity, string DocumentPath,
    string SyntaxPath, string DisplayName, string? TextFileNameSeed, SnapshotResourceLocator? Resource,
    ExplorerAddress? ExplorerAddress, SnapshotSummary? Snapshot = null);
public sealed record DocumentTextPlan(RelationalDocumentAddress Address, DocumentTextKey Key,
    long EstimatedTextBytes, TableMetadataDetails? Table = null);
public sealed record SelectedDocumentText(string Text, long EstimatedBytes);

public sealed class DocumentTargetException(SavedDocumentTargetState state, string message) : InvalidOperationException(message)
{
    public SavedDocumentTargetState TargetState { get; } = state;
}
public sealed class DocumentTextLimitException(long limitBytes) : InvalidOperationException(
    "The selected document exceeds its resident-text budget. No content was truncated.")
{
    public long LimitBytes { get; } = limitBytes;
}
public sealed class DocumentReferenceChangedException() : InvalidOperationException(
    "The reference index no longer describes this target. Refresh the index before navigating.");
