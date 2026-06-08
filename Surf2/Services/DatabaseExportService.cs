using System.IO;
using System.IO.Compression;
using System.Text;
using Surf2.Models;

namespace Surf2.Services;

public sealed class DatabaseExportService(DatabaseDocumentService databaseDocumentService)
{
    private const string StoredProceduresFolder = "Stored Procedures";
    private const string ViewsFolder = "Views";
    private const string FunctionsFolder = "Functions";
    private const string TriggersFolder = "Triggers";
    private const string TablesFolder = "Tables";
    private const string TableDataFolder = "Table Data";

    public DatabaseExportResult Export(DatabaseMetadataSnapshot snapshot, string zipFilePath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? directoryPath = Path.GetDirectoryName(zipFilePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (File.Exists(zipFilePath))
        {
            File.Delete(zipFilePath);
        }

        var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int textFileCount = 0;
        int csvFileCount = 0;

        using ZipArchive archive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create);
        AddFolderEntry(archive, usedEntryNames, StoredProceduresFolder);
        AddFolderEntry(archive, usedEntryNames, ViewsFolder);
        AddFolderEntry(archive, usedEntryNames, FunctionsFolder);
        AddFolderEntry(archive, usedEntryNames, TriggersFolder);
        AddFolderEntry(archive, usedEntryNames, TablesFolder);
        AddFolderEntry(archive, usedEntryNames, TableDataFolder);

        textFileCount += AddObjectEntries(archive, usedEntryNames, snapshot, StoredProceduresFolder, SqlDatabaseObjectKind.StoredProcedure);
        textFileCount += AddObjectEntries(archive, usedEntryNames, snapshot, ViewsFolder, SqlDatabaseObjectKind.View);
        textFileCount += AddObjectEntries(archive, usedEntryNames, snapshot, FunctionsFolder, SqlDatabaseObjectKind.Function);
        textFileCount += AddObjectEntries(archive, usedEntryNames, snapshot, TriggersFolder, SqlDatabaseObjectKind.Trigger);
        textFileCount += AddTableDefinitionEntries(archive, usedEntryNames, snapshot);
        csvFileCount += AddTableDataEntries(archive, usedEntryNames, snapshot);

        return new DatabaseExportResult(zipFilePath, textFileCount, csvFileCount);
    }

    private int AddObjectEntries(
        ZipArchive archive,
        HashSet<string> usedEntryNames,
        DatabaseMetadataSnapshot snapshot,
        string folderName,
        SqlDatabaseObjectKind kind)
    {
        int count = 0;
        foreach (SqlDatabaseObject databaseObject in snapshot.Objects
                     .Where(item => item.Kind == kind)
                     .OrderBy(item => item.SchemaName)
                     .ThenBy(item => item.ObjectName))
        {
            string fullName = SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName);
            string content = string.IsNullOrWhiteSpace(databaseObject.Definition)
                ? $"-- No definition imported for {databaseObject.FullName}.{Environment.NewLine}"
                : databaseObject.Definition;

            AddTextEntry(archive, usedEntryNames, folderName, fullName, ".txt", content);
            count++;
        }

        return count;
    }

    private int AddTableDefinitionEntries(
        ZipArchive archive,
        HashSet<string> usedEntryNames,
        DatabaseMetadataSnapshot snapshot)
    {
        int count = 0;
        foreach (SqlTable table in snapshot.Tables
                     .OrderBy(item => item.SchemaName)
                     .ThenBy(item => item.TableName))
        {
            string fullName = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
            string content = databaseDocumentService.CreateTableDocument(snapshot, table);
            AddTextEntry(archive, usedEntryNames, TablesFolder, fullName, ".txt", content);
            count++;
        }

        return count;
    }

    private int AddTableDataEntries(
        ZipArchive archive,
        HashSet<string> usedEntryNames,
        DatabaseMetadataSnapshot snapshot)
    {
        int count = 0;
        foreach (SqlTableDataSet dataSet in snapshot.TableDataSets
                     .OrderBy(item => item.SchemaName)
                     .ThenBy(item => item.TableName))
        {
            SqlTable table = snapshot.Tables.FirstOrDefault(candidate =>
                string.Equals(candidate.SchemaName, dataSet.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.TableName, dataSet.TableName, StringComparison.OrdinalIgnoreCase)) ?? new SqlTable
                {
                    SchemaName = dataSet.SchemaName,
                    TableName = dataSet.TableName,
                    HasFullData = true,
                    FullDataRowCount = Math.Max(dataSet.RowCount, dataSet.Rows.Count),
                    FullDataImportedAtUtc = dataSet.ImportedAtUtc
                };

            string fullName = SqlName.FormatPlainMultipartName(dataSet.SchemaName, dataSet.TableName);
            string content = databaseDocumentService.CreateTableDataDocument(snapshot, table, dataSet);
            AddTextEntry(archive, usedEntryNames, TableDataFolder, fullName, ".csv", content);
            count++;
        }

        return count;
    }

    private static void AddFolderEntry(ZipArchive archive, HashSet<string> usedEntryNames, string folderName)
    {
        string entryName = $"{folderName}/";
        if (usedEntryNames.Add(entryName))
        {
            archive.CreateEntry(entryName);
        }
    }

    private static void AddTextEntry(
        ZipArchive archive,
        HashSet<string> usedEntryNames,
        string folderName,
        string fileNameSeed,
        string extension,
        string content)
    {
        string entryName = CreateUniqueEntryName(usedEntryNames, folderName, fileNameSeed, extension);
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string CreateUniqueEntryName(
        HashSet<string> usedEntryNames,
        string folderName,
        string fileNameSeed,
        string extension)
    {
        string fileName = CreateSafeFileName(fileNameSeed, extension);
        string entryName = $"{folderName}/{fileName}";
        if (usedEntryNames.Add(entryName))
        {
            return entryName;
        }

        string baseName = fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;

        for (int index = 2; ; index++)
        {
            entryName = $"{folderName}/{baseName} ({index}){extension}";
            if (usedEntryNames.Add(entryName))
            {
                return entryName;
            }
        }
    }

    private static string CreateSafeFileName(string fileNameSeed, string extension)
    {
        string fileName = string.IsNullOrWhiteSpace(fileNameSeed)
            ? "resource"
            : fileNameSeed.Trim();

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        fileName = fileName.Trim('.', ' ');
        if (fileName.Length == 0)
        {
            fileName = "resource";
        }

        if (fileName.Length > 120)
        {
            fileName = fileName[..120].Trim('.', ' ');
        }

        return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? fileName
            : $"{fileName}{extension}";
    }
}

public sealed record DatabaseExportResult(string ZipFilePath, int TextFileCount, int CsvFileCount)
{
    public int TotalFileCount => TextFileCount + CsvFileCount;
}
