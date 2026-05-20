using Microsoft.Data.SqlClient;

namespace Surf2.Storage;

public sealed class SqlServerConnectionOptions
{
    public const string EnvironmentVariableName = "SURF2_CONNECTION_STRING";
    public const string DefaultConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=Surf2;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10";

    private SqlServerConnectionOptions(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public string DatabaseName
    {
        get
        {
            var builder = new SqlConnectionStringBuilder(ConnectionString);
            return string.IsNullOrWhiteSpace(builder.InitialCatalog)
                ? "Surf2"
                : builder.InitialCatalog;
        }
    }

    public static SqlServerConnectionOptions CreateDefault()
    {
        string? environmentConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        string connectionString = !string.IsNullOrWhiteSpace(environmentConnectionString)
            ? environmentConnectionString
            : new LocalConnectionSettingsStore().Load().ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = DefaultConnectionString;
        }

        return FromConnectionString(connectionString);
    }

    public static SqlServerConnectionOptions FromConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);

        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            builder.InitialCatalog = "Surf2";
        }

        if (!ContainsKeyword(connectionString, "TrustServerCertificate") &&
            !ContainsKeyword(connectionString, "Trust Server Certificate"))
        {
            builder.TrustServerCertificate = true;
        }

        if (!ContainsKeyword(connectionString, "Connect Timeout") &&
            !ContainsKeyword(connectionString, "Connection Timeout"))
        {
            builder.ConnectTimeout = 10;
        }

        return new SqlServerConnectionOptions(builder.ConnectionString);
    }

    private static bool ContainsKeyword(string connectionString, string keyword)
    {
        return connectionString.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
