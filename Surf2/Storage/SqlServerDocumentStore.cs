using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage;

public sealed class SqlServerDocumentStore
{
    private const string SchemaName = "app";
    private const string TableName = "Surf2Documents";
    private const char DocumentHydrationKeySeparator = '\u001f';
    private static readonly SemaphoreSlim InitializationLock = new(1, 1);
    private static readonly HashSet<string> InitializedConnectionStrings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object HydratedDocumentsLock = new();
    private static readonly HashSet<string> HydratedDocuments = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _connectionString;

    public SqlServerDocumentStore()
        : this(SqlServerConnectionOptions.CreateDefault())
    {
    }

    public SqlServerDocumentStore(SqlServerConnectionOptions options)
    {
        _connectionString = options.ConnectionString;
    }

    public async Task<T?> LoadAsync<T>(string documentKey, CancellationToken cancellationToken = default)
        where T : class
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureLegacyProviderAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT PayloadJson
FROM [{SchemaName}].[{TableName}]
WHERE DocumentKey = @DocumentKey;
""";
        command.Parameters.Add(new SqlParameter("@DocumentKey", documentKey));

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value)
        {
            MarkDocumentHydrated(documentKey);
            return null;
        }

        if (result is not string payload || string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidDataException($"Document '{documentKey}' has an empty SQL payload.");
        }

        T? value = JsonSerializer.Deserialize<T>(payload, SerializerOptions);
        if (value == null)
        {
            throw new InvalidDataException($"Document '{documentKey}' could not be deserialized from SQL.");
        }

        MarkDocumentHydrated(documentKey);
        return value;
    }

    public async Task SaveAsync<T>(string documentKey, T value, CancellationToken cancellationToken = default)
        where T : class
    {
        await EnsureInitializedAsync(cancellationToken);
        if (!IsDocumentHydrated(documentKey))
        {
            throw new InvalidOperationException(
                $"Refusing to save document '{documentKey}' before it has been successfully loaded in this session.");
        }

        string payload = JsonSerializer.Serialize(value, SerializerOptions);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureLegacyProviderAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
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
        command.Parameters.Add(new SqlParameter("@DocumentKey", documentKey));
        command.Parameters.Add(new SqlParameter("@PayloadJson", payload) { Size = -1 });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var documentStore = new SqlServerDocumentStore(SqlServerConnectionOptions.FromConnectionString(connectionString));
        await documentStore.EnsureInitializedAsync(cancellationToken);
    }

    private void MarkDocumentHydrated(string documentKey)
    {
        lock (HydratedDocumentsLock)
        {
            HydratedDocuments.Add(CreateHydrationKey(documentKey));
        }
    }

    private bool IsDocumentHydrated(string documentKey)
    {
        lock (HydratedDocumentsLock)
        {
            return HydratedDocuments.Contains(CreateHydrationKey(documentKey));
        }
    }

    private string CreateHydrationKey(string documentKey)
    {
        return string.Concat(_connectionString, DocumentHydrationKeySeparator, documentKey);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (InitializedConnectionStrings.Contains(_connectionString))
        {
            return;
        }

        await InitializationLock.WaitAsync(cancellationToken);
        try
        {
            if (InitializedConnectionStrings.Contains(_connectionString))
            {
                return;
            }

            await EnsureDatabaseExistsAsync(cancellationToken);
            await EnsureSchemaExistsAsync(cancellationToken);
            InitializedConnectionStrings.Add(_connectionString);
        }
        finally
        {
            InitializationLock.Release();
        }
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        string databaseName = string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? "Surf2"
            : builder.InitialCatalog;

        builder.InitialCatalog = "master";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
IF DB_ID(@DatabaseName) IS NULL
BEGIN
    DECLARE @Sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName);
    EXEC (@Sql);
END;
""";
        command.Parameters.Add(new SqlParameter("@DatabaseName", databaseName));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaExistsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
IF OBJECT_ID(N'surf.StorageFormatInfo', N'U') IS NOT NULL
    THROW 51011, 'The legacy provider cannot initialize a relational or incomplete migration database.', 1;
IF SCHEMA_ID(N'{SchemaName}') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [{SchemaName}]');
END;

IF OBJECT_ID(N'[{SchemaName}].[{TableName}]', N'U') IS NULL
BEGIN
    CREATE TABLE [{SchemaName}].[{TableName}]
    (
        DocumentKey nvarchar(128) NOT NULL
            CONSTRAINT PK_{TableName} PRIMARY KEY,
        PayloadJson nvarchar(max) NOT NULL,
        UpdatedAtUtc datetime2(3) NOT NULL
            CONSTRAINT DF_{TableName}_UpdatedAtUtc DEFAULT SYSUTCDATETIME()
    );
END;
""";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureLegacyProviderAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
IF OBJECT_ID(N'surf.StorageFormatInfo', N'U') IS NOT NULL
    THROW 51011, 'The legacy provider cannot read or write a relational or incomplete migration database.', 1;
""";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
