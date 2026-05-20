using System.Text.Json;
using Surf2.Models;

namespace Surf2.Services;

public sealed class DatabaseMetadataImportService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DatabaseImportResult ParseClipboardResult(string clipboardText, string displayName)
    {
        var result = new DatabaseImportResult();
        string trimmedName = displayName.Trim();
        var snapshot = new DatabaseMetadataSnapshot
        {
            DisplayName = trimmedName,
            ImportedAtUtc = DateTimeOffset.UtcNow
        };

        Dictionary<string, List<ImportChunk>> chunksByPayload = ReadChunks(clipboardText, result.Warnings);
        foreach (IGrouping<string, ImportChunk> group in chunksByPayload.Values.SelectMany(chunk => chunk).GroupBy(chunk => chunk.PayloadKey))
        {
            List<ImportChunk> chunks = group.OrderBy(chunk => chunk.ChunkIndex).ToList();
            ImportChunk firstChunk = chunks[0];
            string payloadJson = string.Concat(chunks.Select(chunk => chunk.PayloadJsonChunk));

            try
            {
                AddPayload(snapshot, firstChunk.EntityType, firstChunk.EntityKey, payloadJson);
            }
            catch (JsonException ex)
            {
                result.Warnings.Add($"Could not parse {firstChunk.EntityType} '{firstChunk.EntityKey}': {ex.Message}");
            }
        }

        ApplyDataSetCounts(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.DisplayName))
        {
            snapshot.DisplayName = string.IsNullOrWhiteSpace(snapshot.DatabaseName)
                ? "Database Snapshot"
                : snapshot.DatabaseName;
        }

        result.Snapshot = snapshot;
        return result;
    }

    private static Dictionary<string, List<ImportChunk>> ReadChunks(string clipboardText, List<string> warnings)
    {
        var chunksByPayload = new Dictionary<string, List<ImportChunk>>(StringComparer.OrdinalIgnoreCase);
        string[] lines = clipboardText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string line in lines)
        {
            if (line.StartsWith("EntityType\tEntityKey\tChunkIndex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 5)
            {
                warnings.Add("Skipped a result row because it did not contain the expected five columns.");
                continue;
            }

            if (!int.TryParse(parts[2], out int chunkIndex) ||
                !int.TryParse(parts[3], out int chunkCount))
            {
                warnings.Add($"Skipped '{parts[0]}' row for '{parts[1]}' because its chunk metadata was invalid.");
                continue;
            }

            var chunk = new ImportChunk(
                parts[0].Trim(),
                parts[1].Trim(),
                chunkIndex,
                chunkCount,
                string.Join('\t', parts.Skip(4)));

            if (!chunksByPayload.TryGetValue(chunk.PayloadKey, out List<ImportChunk>? payloadChunks))
            {
                payloadChunks = [];
                chunksByPayload[chunk.PayloadKey] = payloadChunks;
            }

            payloadChunks.Add(chunk);
        }

        foreach (List<ImportChunk> payloadChunks in chunksByPayload.Values)
        {
            ImportChunk first = payloadChunks[0];
            int distinctChunkCount = payloadChunks.Select(chunk => chunk.ChunkIndex).Distinct().Count();
            if (distinctChunkCount != first.ChunkCount)
            {
                warnings.Add($"'{first.EntityType}' '{first.EntityKey}' expected {first.ChunkCount} chunk(s), but {distinctChunkCount} were found.");
            }
        }

        return chunksByPayload;
    }

    private static void AddPayload(DatabaseMetadataSnapshot snapshot, string entityType, string entityKey, string payloadJson)
    {
        switch (entityType)
        {
            case "Database":
                DatabasePayload? databasePayload = JsonSerializer.Deserialize<DatabasePayload>(payloadJson, SerializerOptions);
                if (databasePayload != null)
                {
                    snapshot.DatabaseName = databasePayload.DatabaseName;
                }

                break;

            case "SqlObject":
                SqlObjectPayload? objectPayload = JsonSerializer.Deserialize<SqlObjectPayload>(payloadJson, SerializerOptions);
                if (objectPayload != null)
                {
                    snapshot.Objects.Add(new SqlDatabaseObject
                    {
                        SchemaName = NormalizeSchema(objectPayload.SchemaName),
                        ObjectName = objectPayload.ObjectName,
                        TypeDescription = objectPayload.TypeDescription,
                        Kind = MapSqlObjectKind(objectPayload.TypeDescription),
                        Definition = objectPayload.Definition,
                        ParentSchemaName = objectPayload.ParentSchemaName,
                        ParentObjectName = objectPayload.ParentObjectName
                    });
                }

                break;

            case "Table":
                SqlTable? table = JsonSerializer.Deserialize<SqlTable>(payloadJson, SerializerOptions);
                if (table != null)
                {
                    table.SchemaName = NormalizeSchema(table.SchemaName);
                    snapshot.Tables.Add(table);
                }

                break;

            case "Column":
                SqlColumn? column = JsonSerializer.Deserialize<SqlColumn>(payloadJson, SerializerOptions);
                if (column != null)
                {
                    column.SchemaName = NormalizeSchema(column.SchemaName);
                    snapshot.Columns.Add(column);
                }

                break;

            case "PrimaryKey":
                SqlPrimaryKeyColumn? primaryKeyColumn = JsonSerializer.Deserialize<SqlPrimaryKeyColumn>(payloadJson, SerializerOptions);
                if (primaryKeyColumn != null)
                {
                    primaryKeyColumn.SchemaName = NormalizeSchema(primaryKeyColumn.SchemaName);
                    snapshot.PrimaryKeys.Add(primaryKeyColumn);
                }

                break;

            case "TableDataSet":
                SqlTableDataSet? dataSet = JsonSerializer.Deserialize<SqlTableDataSet>(payloadJson, SerializerOptions);
                if (dataSet != null)
                {
                    dataSet.SchemaName = NormalizeSchema(dataSet.SchemaName);
                    dataSet.ImportedAtUtc = snapshot.ImportedAtUtc;
                    snapshot.TableDataSets.Add(dataSet);
                }

                break;

            case "TableDataRow":
                using (JsonDocument document = JsonDocument.Parse(payloadJson))
                {
                    JsonElement row = document.RootElement.Clone();
                    (string schemaName, string tableName) = ParseTableKey(entityKey);
                    if (string.IsNullOrWhiteSpace(tableName))
                    {
                        break;
                    }

                    SqlTableDataSet rowDataSet = GetOrCreateDataSet(snapshot, NormalizeSchema(schemaName), tableName);
                    rowDataSet.Rows.Add(row);
                }

                break;
        }
    }

    private static void ApplyDataSetCounts(DatabaseMetadataSnapshot snapshot)
    {
        foreach (SqlTableDataSet dataSet in snapshot.TableDataSets)
        {
            if (dataSet.RowCount <= 0)
            {
                dataSet.RowCount = dataSet.Rows.Count;
            }

            SqlTable? table = snapshot.Tables.FirstOrDefault(candidate =>
                string.Equals(candidate.SchemaName, dataSet.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.TableName, dataSet.TableName, StringComparison.OrdinalIgnoreCase));

            if (table != null)
            {
                table.HasFullData = true;
                table.FullDataRowCount = dataSet.RowCount;
                table.FullDataImportedAtUtc = dataSet.ImportedAtUtc;
            }
        }
    }

    private static SqlTableDataSet GetOrCreateDataSet(DatabaseMetadataSnapshot snapshot, string schemaName, string tableName)
    {
        SqlTableDataSet? dataSet = snapshot.TableDataSets.FirstOrDefault(candidate =>
            string.Equals(candidate.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, tableName, StringComparison.OrdinalIgnoreCase));

        if (dataSet != null)
        {
            return dataSet;
        }

        dataSet = new SqlTableDataSet
        {
            SchemaName = schemaName,
            TableName = tableName,
            ImportedAtUtc = snapshot.ImportedAtUtc
        };
        snapshot.TableDataSets.Add(dataSet);
        return dataSet;
    }

    private static SqlDatabaseObjectKind MapSqlObjectKind(string typeDescription)
    {
        return typeDescription.Trim().ToUpperInvariant() switch
        {
            "SQL_STORED_PROCEDURE" => SqlDatabaseObjectKind.StoredProcedure,
            "VIEW" => SqlDatabaseObjectKind.View,
            "SQL_SCALAR_FUNCTION" => SqlDatabaseObjectKind.Function,
            "SQL_INLINE_TABLE_VALUED_FUNCTION" => SqlDatabaseObjectKind.Function,
            "SQL_TABLE_VALUED_FUNCTION" => SqlDatabaseObjectKind.Function,
            "SQL_TRIGGER" => SqlDatabaseObjectKind.Trigger,
            _ => SqlDatabaseObjectKind.Unknown
        };
    }

    private static string NormalizeSchema(string schemaName)
    {
        return string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName.Trim();
    }

    private static string GetJsonString(JsonElement row, string propertyName)
    {
        return row.ValueKind == JsonValueKind.Object &&
               row.TryGetProperty(propertyName, out JsonElement property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static (string SchemaName, string TableName) ParseTableKey(string entityKey)
    {
        int rowMarkerIndex = entityKey.IndexOf('#', StringComparison.Ordinal);
        if (rowMarkerIndex >= 0)
        {
            entityKey = entityKey[..rowMarkerIndex];
        }

        string[] parts = entityKey
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            1 => ("dbo", parts[0]),
            >= 2 => (parts[^2], parts[^1]),
            _ => ("dbo", string.Empty)
        };
    }

    private sealed record ImportChunk(
        string EntityType,
        string EntityKey,
        int ChunkIndex,
        int ChunkCount,
        string PayloadJsonChunk)
    {
        public string PayloadKey => $"{EntityType}|{EntityKey}";
    }

    private sealed class SqlObjectPayload
    {
        public string SchemaName { get; set; } = "dbo";

        public string ObjectName { get; set; } = string.Empty;

        public string TypeDescription { get; set; } = string.Empty;

        public string Definition { get; set; } = string.Empty;

        public string ParentSchemaName { get; set; } = string.Empty;

        public string ParentObjectName { get; set; } = string.Empty;
    }

    private sealed class DatabasePayload
    {
        public string DatabaseName { get; set; } = string.Empty;
    }
}

public sealed class DatabaseImportResult
{
    public DatabaseMetadataSnapshot? Snapshot { get; set; }

    public List<string> Warnings { get; } = [];
}
