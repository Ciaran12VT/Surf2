using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage;

public sealed class SqlServerDocumentStore
{
    private const string SchemaName = "app";
    private const string TableName = "Surf2Documents";
    private static readonly SemaphoreSlim InitializationLock = new(1, 1);
    private static readonly HashSet<string> InitializedConnectionStrings = new(StringComparer.OrdinalIgnoreCase);

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

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT PayloadJson
FROM [{SchemaName}].[{TableName}]
WHERE DocumentKey = @DocumentKey;
""";
        command.Parameters.Add(new SqlParameter("@DocumentKey", documentKey));

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string payload && !string.IsNullOrWhiteSpace(payload)
            ? JsonSerializer.Deserialize<T>(payload, SerializerOptions)
            : null;
    }

    public async Task SaveAsync<T>(string documentKey, T value, CancellationToken cancellationToken = default)
        where T : class
    {
        await EnsureInitializedAsync(cancellationToken);

        string payload = JsonSerializer.Serialize(value, SerializerOptions);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

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
}
