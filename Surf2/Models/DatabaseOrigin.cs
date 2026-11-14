using System.Text.Json;

namespace Surf2.Models;

/// <summary>Evidence describing a capture's source, never authentication material.</summary>
public sealed record DatabaseOrigin
{
    public int Version { get; init; } = 1;
    public string Engine { get; init; } = "Unknown";
    public string Server { get; init; } = "";
    public string Database { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string IdentifierKind { get; init; } = "";
    public string Identifier { get; init; } = "";
    public string Availability { get; init; } = "Unknown";
    public string Acquisition { get; init; } = "Manual";
    public DateTimeOffset? ObservedAtUtc { get; init; }
    public Guid? RegisteredSourceId { get; init; }
    public bool UserConfirmed { get; init; }
    public DatabaseOrigin? OriginalEvidence { get; init; }
    public bool IsComplete => Engine != "Unknown" && !string.IsNullOrWhiteSpace(Server) &&
        !string.IsNullOrWhiteSpace(Database) && !string.IsNullOrWhiteSpace(IdentifierKind) && !string.IsNullOrWhiteSpace(Identifier);

    public DatabaseOrigin Validate()
    {
        if (Version != 1) throw new FormatException("Unsupported database origin version.");
        foreach (var text in new[] { Engine, Server, Database, Endpoint, IdentifierKind, Identifier, Availability, Acquisition })
            if (text == null || text.Length > 2048 || text.Any(char.IsControl)) throw new FormatException("Invalid database origin field.");
        if (Endpoint.Contains(';') || Endpoint.Contains('@') || Endpoint.Contains('?'))
            throw new FormatException("Enter a server address only, without credentials or a connection string.");
        if (IdentifierKind == "database_guid" && Identifier.Length != 0 && !Guid.TryParse(Identifier, out _))
            throw new FormatException("The SQL Server database identifier must be a GUID.");
        if (OriginalEvidence?.OriginalEvidence != null) throw new FormatException("Origin evidence nesting is too deep.");
        return this;
    }

    public string ToJson() => JsonSerializer.Serialize(Validate());
    public static DatabaseOrigin? FromJson(string? json) => string.IsNullOrWhiteSpace(json) ? null :
        json.Length > 32768 ? throw new FormatException("Database origin is too large.") :
        (JsonSerializer.Deserialize<DatabaseOrigin>(json, new JsonSerializerOptions { MaxDepth = 8 }) ??
            throw new FormatException("Invalid database origin.")).Validate();

    public bool AgreesWith(DatabaseOrigin other)
    {
        if (!IsComplete || !other.IsComplete) return false;
        static bool Equal(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        return Equal(Engine, other.Engine) && Equal(Server, other.Server) && Equal(Database, other.Database) &&
            Equal(IdentifierKind, other.IdentifierKind) && Equal(Identifier, other.Identifier) &&
            (Endpoint.Length == 0 || other.Endpoint.Length == 0 || Equal(Endpoint, other.Endpoint));
    }
    public bool ConflictsWith(DatabaseOrigin other)
    {
        static bool Different(string a, string b) => a.Length != 0 && b.Length != 0 &&
            !string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        return Engine != "Unknown" && other.Engine != "Unknown" && Different(Engine, other.Engine) ||
            Different(Server, other.Server) || Different(Database, other.Database) || Different(Endpoint, other.Endpoint) ||
            Different(IdentifierKind, other.IdentifierKind) || Different(Identifier, other.Identifier);
    }
}
