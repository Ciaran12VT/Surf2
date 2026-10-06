using System.Data;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Packages;

public sealed partial class RelationalPackageExporter
{
    private readonly RelationalSession _session;
    private readonly RelationalPackageLimits _limits;

    public RelationalPackageExporter(RelationalSession session, RelationalPackageLimits? limits = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _limits = limits ?? new();
        _limits.Validate();
    }

    public async Task<RelationalPackageExportResult> ExportAsync(string destination,
        RelationalPackageExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var settings = options ?? new();
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        string final = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(final) ?? throw new ArgumentException("A package destination directory is required.", nameof(destination));
        if (Directory.Exists(final)) throw new IOException("The package destination is a directory.");
        if (!settings.ReplaceExisting && File.Exists(final)) throw new IOException("The package destination already exists.");
        string pending = Path.Combine(directory, "." + Path.GetFileName(final) + "." + Guid.NewGuid().ToString("N") + ".pending");
        bool owned = false;
        RelationalPackageExportResult? result = null;
        try
        {
            await _session.RequireReadyAsync(ct);
            var budget = new PackageBudget(_limits);
            var tables = new List<RelationalPackageTableStream>();
            var localFiles = new List<RelationalPackageLocalStream>();
            SourceHeader source;
            IsolationLevel isolation;
            DateTimeOffset exportedAt = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(directory);
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                _limits.ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                owned = true;
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await using (var connection = await _session.OpenAsync(ct))
                    {
                        isolation = await SelectIsolationAsync(connection, settings.AllowBlockingConsistentFallback, ct);
                        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(isolation, ct);
                        source = await ReadReadyHeaderAsync(connection, transaction, ct);
                        await ValidateCompleteCapturesAsync(connection, transaction, ct);
                        foreach (var spec in PackageTableCatalogue.Authoritative)
                            await ExportTableAsync(spec);
                        long after = 0;
                        while (true)
                        {
                            PackageTableSpec? capture = await NextCaptureAsync(connection, transaction, after, ct);
                            if (capture == null) break;
                            await ValidateCaptureCountsAsync(connection, transaction, capture, ct);
                            await ExportTableAsync(capture);
                            after = capture.CaptureLayoutKey!.Value;
                        }
                        await transaction.CommitAsync(ct);

                        async Task ExportTableAsync(PackageTableSpec spec)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (tables.Count >= _limits.MaxTables) throw new RelationalPackageLimitException("Package table catalogue budget exceeded.");
                            var metadata = await ReadMetadataAsync(connection, transaction, spec, budget, ct);
                            await ValidateReferencesAsync(connection, transaction, spec, metadata, ct);
                            tables.Add(await WriteTableAsync(connection, transaction, spec, metadata, archive, budget, ct));
                        }
                    }
                    // Release the SQL snapshot/version-store retention before caller-selected files.
                    if (settings.LocalFiles != null)
                        await foreach (var selected in settings.LocalFiles.WithCancellation(ct).ConfigureAwait(false))
                            localFiles.Add(await WriteLocalFileAsync(selected, archive, budget, ct));
                    var manifest = new RelationalPackageManifest(RelationalPackageFormat.Identifier, RelationalPackageFormat.PackageVersion,
                        RelationalPackageFormat.ExporterVersion, RelationalPackageFormat.RowEncodingVersion,
                        PersistenceFormatProbe.FormatIdentifier, PersistenceFormatProbe.SupportedSchemaVersion,
                        source.MinimumReader, source.MinimumWriter, source.Identity, source.CreatedAt, exportedAt,
                        isolation == IsolationLevel.Snapshot ? "SqlSnapshot-v1" : "SqlSerializableBlocking-v1",
                        "ExplicitCallerSelection-v1", tables.AsReadOnly(), localFiles.AsReadOnly());
                    budget.AddEntry(RelationalPackageFormat.ManifestEntry);
                    var entry = archive.CreateEntry(RelationalPackageFormat.ManifestEntry, CompressionLevel.Fastest);
                    await using var output = entry.Open();
                    using var hash = new PackageHashingStream(output, budget, _limits.MaxManifestBytes);
                    await JsonSerializer.SerializeAsync(hash, manifest, cancellationToken: ct);
                    await hash.FlushAsync(ct);
                    result = new(final, manifest, hash.Finish(), budget.Bytes);
                }
                ct.ThrowIfCancellationRequested();
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            if (settings.ReplaceExisting && File.Exists(final)) File.Replace(pending, final, null);
            else File.Move(pending, final, overwrite: false);
            owned = false;
            return result ?? throw new InvalidOperationException("Package manifest was not completed.");
        }
        catch (Exception error)
        {
            if (owned)
            {
                try { File.Delete(pending); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { error.Data["PendingPackageCleanupFailed"] = pending; }
            }
            if (error is SqlException { Number: 3951 or 3952 or 3961 })
                throw new InvalidOperationException("The consistent snapshot export could not complete. Confirm snapshot isolation is enabled and retry without concurrent schema changes.", error);
            throw;
        }
    }

    private sealed record SourceHeader(Guid Identity, DateTimeOffset CreatedAt, int MinimumReader, int MinimumWriter);

    private async Task<IsolationLevel> SelectIsolationAsync(SqlConnection connection, bool allowBlocking, CancellationToken ct)
    {
        await using var command = Command(connection, null, "SELECT CONVERT(int,snapshot_isolation_state) FROM sys.databases WHERE database_id=DB_ID();");
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        object? state = await command.ExecuteScalarAsync(ct);
        return PackageIsolation.Select(state is int known ? known : null, allowBlocking);
    }

    private async Task<SourceHeader> ReadReadyHeaderAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
SELECT Singleton,FormatIdentifier,SchemaVersion,MinimumReaderVersion,MinimumWriterVersion,State,DatabaseIdentity,CreatedAtUtc FROM surf.StorageFormatInfo;
""");
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetByte(0) != 1 || reader.GetString(1) != PersistenceFormatProbe.FormatIdentifier ||
            reader.GetInt32(2) != PersistenceFormatProbe.SupportedSchemaVersion || reader.GetInt32(3) != 1 || reader.GetInt32(4) != 1 ||
            reader.GetString(5) != "Ready" || reader.GetGuid(6) == Guid.Empty)
            throw new InvalidOperationException("Package export requires the exact supported Ready format inside the pinned snapshot.");
        var result = new SourceHeader(reader.GetGuid(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt32(3), reader.GetInt32(4));
        if (await reader.ReadAsync(ct)) throw new InvalidDataException("The source format marker is not unique.");
        return result;
    }

    private async Task<PackageTableSpec?> NextCaptureAsync(SqlConnection connection, SqlTransaction transaction, long after, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, $"""
SELECT TOP(1) l.LayoutKey,l.ColumnCount,l.EncodingVersion FROM surf.DataLayout l
WHERE l.LayoutKey>@After AND EXISTS (SELECT 1 FROM surf.DataSet d WHERE d.LayoutKey=l.LayoutKey AND ({PackageTableCatalogue.DataSet("d")}))
ORDER BY l.LayoutKey;
""", RelationalSession.Parameter("@After", SqlDbType.BigInt, after));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetInt32(2) != 1) throw new InvalidDataException("Unsupported captured layout encoding in package source.");
        return PackageTableCatalogue.Capture(reader.GetInt64(0), reader.GetInt32(1));
    }

    private async Task<RelationalPackageLocalStream> WriteLocalFileAsync(RelationalPackageLocalFile selected,
        ZipArchive archive, PackageBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(selected.OpenPinnedReadAsync);
        string path = PackageLocalFiles.Entry(selected.RelativePath);
        if (selected.ByteCount < 0 || selected.ByteCount > _limits.MaxLocalFileBytes)
            throw new RelationalPackageLimitException("Selected local file exceeds the package local-file limit.");
        budget.AddEntry(path);
        await using var input = await selected.OpenPinnedReadAsync(ct);
        if (input == null || !input.CanRead || input.CanWrite) throw new InvalidDataException("The local-file hook must provide a pinned read-only stream.");
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        await using var output = entry.Open();
        using var hash = new PackageHashingStream(output, budget, _limits.MaxEntryBytes);
        byte[] buffer = new byte[_limits.ChunkBytes];
        long remaining = selected.ByteCount;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
            if (read == 0) throw new InvalidDataException("Selected local file ended before its pinned length.");
            await hash.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), ct) != 0) throw new InvalidDataException("Selected local file changed beyond its pinned length.");
        await hash.FlushAsync(ct);
        return new(path, hash.Bytes, hash.Finish());
    }
}
