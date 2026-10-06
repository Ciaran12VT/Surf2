using System.Data;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Win32.SafeHandles;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Packages;

namespace Surf2.Services.RelationalDocuments;

/// <summary>Read-only format-1 export for an explicitly retained legacy source.</summary>
public static class LegacyPersistenceExport
{
    private const long MaximumArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 16L * 1024 * 1024 * 1024;
    private const int MaximumFiles = 4_094;

    public static Task<PersistenceExportResult> ExportAsync(string packagePath, string connectionString,
        string appDataDirectory, bool replaceExisting = false, bool allowBlockingConsistentFallback = false,
        CancellationToken ct = default)
    {
        var options = SqlServerConnectionOptions.FromConnectionString(connectionString);
        string destination = Path.GetFullPath(packagePath), localRoot = Path.GetFullPath(appDataDirectory);
        // Compression and filesystem traversal run off the dispatcher. SQL and copies
        // remain asynchronous; no legacy document or local-file collection is loaded.
        return Task.Run(() => ExportCoreAsync(destination, options, localRoot, replaceExisting,
            allowBlockingConsistentFallback, ct), ct);
    }

    private static async Task<PersistenceExportResult> ExportCoreAsync(string destination,
        SqlServerConnectionOptions options, string localRoot, bool replace, bool allowBlocking, CancellationToken ct)
    {
        var source = await new PersistenceFormatProbe().ProbeAsync(options.ConnectionString, ct).ConfigureAwait(false);
        if (source.Format != PersistenceFormat.Legacy)
            throw new InvalidDataException("A known legacy database is required for format-1 export.");
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool ownsTemporary = false;
        PersistenceExportResult? result = null;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsTemporary = true;
                using var compressedBudget = new WriteBudgetStream(file, MaximumArchiveBytes);
                using (var archive = new ZipArchive(compressedBudget, ZipArchiveMode.Create, leaveOpen: true))
                {
                    int documents; long expanded;
                    var entry = archive.CreateEntry("database/surf2-documents.json", CompressionLevel.Optimal);
                    await using (var output = entry.Open())
                    using (var budget = new WriteBudgetStream(output, MaximumArchiveBytes))
                    {
                        documents = await WriteDocumentsAsync(budget, options, source, allowBlocking, ct).ConfigureAwait(false);
                        expanded = budget.BytesWritten;
                    }
                    int localFiles = 0;
                    var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string path in EnumerateLocalFiles(localRoot, destination, temporary, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++localFiles > MaximumFiles) throw new InvalidDataException("The legacy export exceeds its file-count budget.");
                        string entryName = LocalEntry(Path.GetRelativePath(localRoot, path)) ??
                            throw new InvalidDataException("The local export selection changed.");
                        if (!localNames.Add(entryName)) throw new InvalidDataException("Local package paths collide after normalization.");
                        var local = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        ValidatePinnedLocalFile(input, path);
                        long length = input.Length;
                        if (length > MaximumArchiveBytes || length > MaximumExpandedBytes - expanded)
                            throw new InvalidDataException("The legacy export exceeds its expanded-size budget.");
                        await using var output = local.Open();
                        using var budget = new WriteBudgetStream(output, Math.Min(length, MaximumExpandedBytes - expanded));
                        await input.CopyToAsync(budget, 64 * 1024, ct).ConfigureAwait(false);
                        if (budget.BytesWritten != length || input.Length != length)
                            throw new InvalidDataException("A local source changed during export.");
                        expanded += budget.BytesWritten;
                    }
                    var manifest = new PersistencePackageManifest
                    {
                        FormatVersion = 1, ExportedAtUtc = DateTimeOffset.UtcNow, AppName = "Surf2",
                        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
                        DatabaseName = source.DatabaseName, DocumentCount = documents, LocalFileCount = localFiles
                    };
                    var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    await using (var output = manifestEntry.Open())
                    using (var budget = new WriteBudgetStream(output, MaximumExpandedBytes - expanded))
                        await JsonSerializer.SerializeAsync(budget, manifest, cancellationToken: ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    result = new PersistenceExportResult(documents, localFiles);
                }
                await file.FlushAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                // Only the completed archive can replace a user-selected destination.
                // Publication happens after all handles on the temporary file close.
            }
            ct.ThrowIfCancellationRequested();
            if (replace) File.Replace(temporary, destination, destinationBackupFileName: null);
            else File.Move(temporary, destination);
            ownsTemporary = false;
            return result ?? throw new InvalidOperationException("The export did not produce a result.");
        }
        finally
        {
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<int> WriteDocumentsAsync(Stream output, SqlServerConnectionOptions options,
        PersistenceFormatResult expected, bool allowBlocking, CancellationToken ct)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        int isolation;
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = """
SELECT CONVERT(nvarchar(256),SERVERPROPERTY('ServerName')), CONVERT(int,DB_ID()), DB_NAME(),
       CONVERT(int,snapshot_isolation_state) FROM sys.databases WHERE database_id=DB_ID();
""";
            using var cancel = RelationalSession.CancelCommand(identity, ct);
            await using var reader = await identity.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidDataException("The legacy source identity is unavailable.");
            var actual = new PersistenceFormatResult(PersistenceFormat.Legacy, reader.GetString(2),
                reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt32(1));
            if (!PersistenceFormatProbe.SameDatabase(expected, actual) || expected.DatabaseName != actual.DatabaseName)
                throw new InvalidDataException("The legacy source identity changed.");
            isolation = reader.GetInt32(3);
        }
        if (isolation != 1 && !allowBlocking) throw new InvalidOperationException("Consistent blocking export requires authorization.");
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            isolation == 1 ? IsolationLevel.Snapshot : IsolationLevel.Serializable, ct).ConfigureAwait(false);
        int count = 0;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
IF OBJECT_ID(N'surf.StorageFormatInfo',N'U') IS NOT NULL
   OR (SELECT COUNT_BIG(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'app.Surf2Documents',N'U')) <> 3
   OR (SELECT COUNT_BIG(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'app.Surf2Documents',N'U') AND is_nullable=0 AND
       ((name=N'DocumentKey' AND TYPE_NAME(system_type_id)=N'nvarchar' AND max_length=256) OR
        (name=N'PayloadJson' AND TYPE_NAME(system_type_id)=N'nvarchar' AND max_length=-1) OR
        (name=N'UpdatedAtUtc' AND TYPE_NAME(system_type_id)=N'datetime2'))) <> 3
    THROW 50000, 'The legacy source structure changed.', 1;
SELECT CONVERT(bigint,DATALENGTH(DocumentKey)), DocumentKey, CONVERT(bigint,DATALENGTH(PayloadJson)),
       UpdatedAtUtc, PayloadJson FROM app.Surf2Documents ORDER BY DocumentKey;
""";
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartArray();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (++count > 10_000 || reader.GetInt64(0) > 256)
                    throw new InvalidDataException("The legacy document catalogue exceeds its budget.");
                string key = reader.GetString(1); long bytes = reader.GetInt64(2); DateTime updated = reader.GetDateTime(3);
                writer.WriteStartObject(); writer.WriteString("DocumentKey", key); writer.WritePropertyName("PayloadJson");
                using (var text = reader.GetTextReader(4))
                    await WriteTextValueAsync(writer, text, bytes, ct).ConfigureAwait(false);
                writer.WriteString("UpdatedAtUtc", updated); writer.WriteEndObject();
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
            writer.WriteEndArray(); await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return count;
    }

    internal static async Task WriteTextValueAsync(Utf8JsonWriter writer, TextReader input, long declaredBytes, CancellationToken ct)
    {
        if (declaredBytes < 0 || (declaredBytes & 1) != 0 || declaredBytes > int.MaxValue)
            throw new InvalidDataException("The legacy text size is invalid.");
        var buffer = new char[16 * 1024 + 1]; int carry = 0; long readBytes = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), ct).ConfigureAwait(false);
            bool final = read == 0; int length = carry + read;
            readBytes += (long)read * 2;
            if (readBytes > declaredBytes) throw new InvalidDataException("The legacy text size changed.");
            carry = !final && length != 0 && char.IsHighSurrogate(buffer[length - 1]) ? 1 : 0;
            length -= carry;
            ValidateUtf16(buffer.AsSpan(0, length));
            writer.WriteStringValueSegment(buffer.AsSpan(0, length), final);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            if (final)
            {
                if (readBytes != declaredBytes) throw new InvalidDataException("The legacy text size changed.");
                return;
            }
            if (carry != 0) buffer[0] = buffer[length];
        }
    }

    private static void ValidateUtf16(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i == text.Length || !char.IsLowSurrogate(text[i]))
                    throw new InvalidDataException("Format-1 cannot encode an unpaired UTF-16 surrogate losslessly.");
            }
            else if (char.IsLowSurrogate(text[i]))
                throw new InvalidDataException("Format-1 cannot encode an unpaired UTF-16 surrogate losslessly.");
        }
    }

    internal static string? LocalEntry(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string path = relativePath.Replace('\\', '/');
        if (path.Length > 4096) throw new InvalidDataException("The local package path exceeds its size budget.");
        string[] components = path.Split('/'); bool excluded = false;
        for (int i = 0; i < components.Length; i++)
        {
            string component = components[i];
            bool credentials = component.StartsWith("connection-settings", StringComparison.OrdinalIgnoreCase);
            if (credentials ||
                component.Equals("InternalLogs", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("Migrations", StringComparison.OrdinalIgnoreCase) ||
                component.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                component.EndsWith(".pending", StringComparison.OrdinalIgnoreCase) ||
                component.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                component.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)) excluded = true;
            // The shared policy rejects bootstrap names. Prefix ONLY for validation
            // of excluded components, retaining all unsafe characters/path segments.
            if (credentials) components[i] = "_" + component;
        }
        string entry = PackageLocalFiles.Entry(string.Join('/', components));
        return excluded ? null : entry;
    }

    internal static IEnumerable<string> EnumerateLocalFiles(string root, string destination, string temporary, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;
        int entries = 0; long nameBytes = 0;
        foreach (string path in Walk(root, 0)) yield return path;

        IEnumerable<string> Walk(string directory, int depth)
        {
            if (depth > 128 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A local source directory cannot be traversed safely.");
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(path, destination, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, temporary, StringComparison.OrdinalIgnoreCase)) continue;
                if (++entries > 10_000 || (nameBytes += (long)path.Length * 2) > 16 * 1024 * 1024)
                    throw new InvalidDataException("The local source catalogue exceeds its metadata budget.");
                // Denied subtrees are never enumerated or opened. Validate remaining
                // names with the same policy as explicitly selected format-2 files.
                if (LocalEntry(Path.GetRelativePath(root, path)) == null) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Local source reparse points require an explicit export selection.");
                if ((attributes & FileAttributes.Directory) != 0)
                { foreach (string child in Walk(path, depth + 1)) yield return child; }
                else yield return path;
            }
        }
    }

    private static void ValidatePinnedLocalFile(FileStream file, string expectedPath)
    {
        var ancestor = new FileInfo(expectedPath).Directory;
        while (ancestor != null)
        {
            if ((File.GetAttributes(ancestor.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Redirected local source paths are not exported.");
            ancestor = ancestor.Parent;
        }
        if ((File.GetAttributes(expectedPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Redirected local source files are not exported.");
        if (!OperatingSystem.IsWindows()) return;
        var final = new StringBuilder(32_768);
        uint length = GetFinalLocalPath(file.SafeFileHandle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity)
            throw new InvalidDataException("The pinned local source path could not be verified.");
        string actual = final.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pinned local source path changed or was redirected.");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalLocalPath(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    internal sealed class WriteBudgetStream(Stream inner, long maximum) : Stream
    {
        internal long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        private void Reserve(int count)
        {
            if (count > maximum - BytesWritten) throw new InvalidDataException("The legacy export exceeds its byte budget.");
            BytesWritten += count;
        }
        public override void Write(byte[] buffer, int offset, int count) { Reserve(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Reserve(buffer.Length); inner.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Reserve(buffer.Length); await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
