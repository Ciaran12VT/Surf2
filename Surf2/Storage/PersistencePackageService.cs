using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage;

public sealed class PersistencePackageService
{
    private const int CurrentFormatVersion = 1;
    private const string ManifestEntryName = "manifest.json";
    private const string DocumentsEntryName = "database/surf2-documents.json";
    private const string LocalFilesRoot = "local-files/";
    private const string ConnectionSettingsFileName = "connection-settings.json";
    private const string SchemaName = "app";
    private const string TableName = "Surf2Documents";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<PersistenceExportResult> ExportAsync(
        string packagePath,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        string normalizedConnectionString = SqlServerConnectionOptions.FromConnectionString(connectionString).ConnectionString;
        await SqlServerDocumentStore.TestConnectionAsync(normalizedConnectionString, cancellationToken);

        List<PersistenceDocumentExport> documents = await LoadDocumentsAsync(normalizedConnectionString, cancellationToken);
        string appDataDirectory = GetSurfAppDataDirectory();
        string packageFullPath = Path.GetFullPath(packagePath);
        List<string> localFiles = Directory.Exists(appDataDirectory)
            ? Directory.EnumerateFiles(appDataDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFullPath(path), packageFullPath, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

        string? packageDirectory = Path.GetDirectoryName(packageFullPath);
        if (!string.IsNullOrWhiteSpace(packageDirectory))
        {
            Directory.CreateDirectory(packageDirectory);
        }

        if (File.Exists(packageFullPath))
        {
            File.Delete(packageFullPath);
        }

        int exportedLocalFileCount = 0;
        await using FileStream packageStream = File.Create(packageFullPath);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create);

        await WriteJsonEntryAsync(archive, DocumentsEntryName, documents, cancellationToken);

        foreach (string localFile in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(appDataDirectory, localFile);
            string entryName = $"{LocalFilesRoot}{relativePath.Replace('\\', '/')}";
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            await using Stream entryStream = entry.Open();
            await using FileStream inputStream = new(
                localFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            await inputStream.CopyToAsync(entryStream, cancellationToken);
            exportedLocalFileCount++;
        }

        var manifest = new PersistencePackageManifest
        {
            FormatVersion = CurrentFormatVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            AppName = "Surf2",
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
            DatabaseName = SqlServerConnectionOptions.FromConnectionString(normalizedConnectionString).DatabaseName,
            DocumentCount = documents.Count,
            LocalFileCount = exportedLocalFileCount
        };
        await WriteJsonEntryAsync(archive, ManifestEntryName, manifest, cancellationToken);

        return new PersistenceExportResult(documents.Count, exportedLocalFileCount);
    }

    public async Task<PersistenceImportResult> ImportAsync(
        string packagePath,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        string normalizedConnectionString = SqlServerConnectionOptions.FromConnectionString(connectionString).ConnectionString;
        await SqlServerDocumentStore.TestConnectionAsync(normalizedConnectionString, cancellationToken);

        await using FileStream packageStream = File.OpenRead(packagePath);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);

        ZipArchiveEntry documentsEntry = archive.GetEntry(DocumentsEntryName)
            ?? throw new InvalidDataException("The selected file is not a Surf2 persistence export.");

        List<PersistenceDocumentExport> documents;
        await using (Stream documentsStream = documentsEntry.Open())
        {
            documents = await JsonSerializer.DeserializeAsync<List<PersistenceDocumentExport>>(
                    documentsStream,
                    SerializerOptions,
                    cancellationToken)
                ?? [];
        }

        await SaveDocumentsAsync(normalizedConnectionString, documents, cancellationToken);
        (int restoredLocalFiles, int skippedLocalFiles) = await RestoreLocalFilesAsync(archive, cancellationToken);

        return new PersistenceImportResult(documents.Count, restoredLocalFiles, skippedLocalFiles);
    }

    private static async Task<List<PersistenceDocumentExport>> LoadDocumentsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var documents = new List<PersistenceDocumentExport>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT DocumentKey, PayloadJson, UpdatedAtUtc
FROM [{SchemaName}].[{TableName}]
ORDER BY DocumentKey;
""";

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new PersistenceDocumentExport
            {
                DocumentKey = reader.GetString(0),
                PayloadJson = reader.GetString(1),
                UpdatedAtUtc = reader.GetDateTime(2)
            });
        }

        return documents;
    }

    private static async Task SaveDocumentsAsync(
        string connectionString,
        IEnumerable<PersistenceDocumentExport> documents,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (PersistenceDocumentExport document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(document.DocumentKey) ||
                    string.IsNullOrWhiteSpace(document.PayloadJson))
                {
                    continue;
                }

                await using SqlCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
UPDATE [{SchemaName}].[{TableName}]
SET PayloadJson = @PayloadJson,
    UpdatedAtUtc = SYSUTCDATETIME()
WHERE DocumentKey = @DocumentKey;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [{SchemaName}].[{TableName}] (DocumentKey, PayloadJson)
    VALUES (@DocumentKey, @PayloadJson);
END;
""";
                command.Parameters.Add(new SqlParameter("@DocumentKey", document.DocumentKey));
                command.Parameters.Add(new SqlParameter("@PayloadJson", document.PayloadJson) { Size = -1 });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<(int RestoredCount, int SkippedCount)> RestoreLocalFilesAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        string appDataDirectory = GetSurfAppDataDirectory();
        string appDataRoot = Path.GetFullPath(appDataDirectory);
        string connectionSettingsPath = Path.GetFullPath(Path.Combine(appDataRoot, ConnectionSettingsFileName));
        Directory.CreateDirectory(appDataRoot);

        int restoredCount = 0;
        int skippedCount = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(LocalFilesRoot, StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            string relativePath = entry.FullName[LocalFilesRoot.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                skippedCount++;
                continue;
            }

            string targetPath = Path.GetFullPath(Path.Combine(appDataRoot, relativePath));
            if (!IsPathInsideDirectory(targetPath, appDataRoot) ||
                string.Equals(targetPath, connectionSettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                continue;
            }

            string? directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using Stream entryStream = entry.Open();
            await using FileStream outputStream = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await entryStream.CopyToAsync(outputStream, cancellationToken);
            restoredCount++;
        }

        return (restoredCount, skippedCount);
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        string normalizedDirectory = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : $"{directory}{Path.DirectorySeparatorChar}";

        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken);
    }

    private static string GetSurfAppDataDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Surf2");
    }
}

public sealed class PersistencePackageManifest
{
    public int FormatVersion { get; set; }

    public DateTimeOffset ExportedAtUtc { get; set; }

    public string AppName { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = string.Empty;

    public int DocumentCount { get; set; }

    public int LocalFileCount { get; set; }
}

public sealed class PersistenceDocumentExport
{
    public string DocumentKey { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}

public sealed record PersistenceExportResult(int DocumentCount, int LocalFileCount);

public sealed record PersistenceImportResult(int DocumentCount, int RestoredLocalFileCount, int SkippedLocalFileCount);
