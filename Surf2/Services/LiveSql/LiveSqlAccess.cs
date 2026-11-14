using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Services.LiveSql;

public enum SqlAccessStatus { Qualified, InsufficientAccess, UnableToVerify, DefinitionUnavailable }
public sealed record LiveSqlObject(int ObjectId, string Schema, string Name, string Type, string? Definition, DateTime? ModifiedAt = null)
{
    public string QualifiedName => SqlName.FormatMultipartName(Schema, Name);
    public bool IsTable => Type == "U";
}
public sealed record SqlAccessQualification(SqlAccessStatus Status, string Message, IReadOnlyList<LiveSqlObject> Objects)
{
    public void RequireQualified()
    {
        if (Status != SqlAccessStatus.Qualified) throw new InvalidOperationException(Message);
    }
    public IReadOnlyList<LiveSqlObject> RequireObjects() { RequireQualified(); return Objects; }
}

/// <summary>Read-only qualification of the actual session. Never grants rights or invokes business routines.</summary>
public static class LiveSqlAccess
{
    public static async Task<SqlAccessQualification> QualifyAsync(SqlConnection connection, SqlTransaction? transaction = null,
        CancellationToken ct = default)
    {
        SqlCommand Command(string text) => new(text, connection, transaction) { CommandTimeout = 30 };
        using (var audit = Command("""
            SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION'),
              (SELECT COUNT_BIG(*) FROM sys.user_token),
              (SELECT COUNT_BIG(*) FROM sys.database_permissions p
               JOIN sys.user_token t ON t.principal_id=p.grantee_principal_id
               WHERE t.usage IN ('GRANT OR DENY','DENY ONLY') AND p.state='D'
                 AND p.class IN (0,1,3) AND p.permission_name IN ('VIEW DEFINITION','CONTROL','VIEW SECURITY DEFINITION'));
            """))
        {
            using var cancel = Cancel(audit, ct);
            using var reader = await audit.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.GetInt32(0) != 1 || reader.GetInt64(1) == 0)
                return new(SqlAccessStatus.UnableToVerify, "Cannot verify the complete database catalog. Database VIEW DEFINITION or equivalent verified access is required; no permissions were changed.", []);
            // Read raw permission rows for every active token, without joining filtered sys.objects.
            // DENY-only tokens must participate. Owner credentials are not a requirement.
            if (reader.GetInt64(2) != 0)
                return new(SqlAccessStatus.UnableToVerify, "Specific metadata denials prevent proving complete catalog visibility for this session.", []);
        }
        var objects = new List<LiveSqlObject>();
        using (var command = Command("""
            SELECT o.object_id,SCHEMA_NAME(o.schema_id),o.name,RTRIM(o.type),DATALENGTH(m.definition),
              HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+'.'+QUOTENAME(o.name),'OBJECT','VIEW DEFINITION'),
              o.modify_date,m.definition
            FROM sys.objects o LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
            WHERE o.is_ms_shipped=0 AND o.type IN ('U','P','PC','V','FN','IF','TF','FS','FT','TR','TA')
            ORDER BY o.object_id;
            """))
        {
            using var cancel = Cancel(command, ct);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            long bytes = 0;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (objects.Count >= 100000) return new(SqlAccessStatus.UnableToVerify, "The source exceeds the supported metadata object limit.", []);
                int id = reader.GetInt32(0); string schema = reader.GetString(1), name = reader.GetString(2), type = reader.GetString(3);
                long length = reader.IsDBNull(4) ? 0 : reader.GetInt64(4); bool definitionAccess = !reader.IsDBNull(5) && reader.GetInt32(5) == 1;
                DateTime modified = reader.GetDateTime(6); bytes += length;
                if (bytes > 32 * 1024 * 1024) return new(SqlAccessStatus.UnableToVerify, "Source definitions exceed the metadata budget.", []);
                if (length > 8 * 1024 * 1024) return new(SqlAccessStatus.DefinitionUnavailable, "A source definition exceeds the supported 8 MiB limit.", []);
                var item = new LiveSqlObject(id, schema, name, type, reader.IsDBNull(7) ? null : reader.GetString(7), modified);
                if (!definitionAccess)
                    return new(SqlAccessStatus.InsufficientAccess, "Cannot read the definition of " + item.QualifiedName + ".", []);
                if (!item.IsTable && item.Definition == null)
                    return new(SqlAccessStatus.DefinitionUnavailable, item.QualifiedName + " has an encrypted or unsupported definition. Additional permission may not make it readable.", []);
                objects.Add(item);
            }
        }
        foreach (var table in objects.Where(o => o.IsTable))
        {
            using var columns = Command("""
                SELECT name,HAS_PERMS_BY_NAME(@Table,'OBJECT','SELECT',name,'COLUMN')
                FROM sys.columns WHERE object_id=@Id ORDER BY column_id;
                """);
            columns.Parameters.AddWithValue("@Table", table.QualifiedName); columns.Parameters.AddWithValue("@Id", table.ObjectId);
            var names = new List<string>();
            using (var reader = await columns.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (reader.IsDBNull(1) || reader.GetInt32(1) != 1)
                        return new(SqlAccessStatus.InsufficientAccess, "SELECT is not available for " + table.QualifiedName + "." + reader.GetString(0) + ".", []);
                    names.Add(SqlName.Bracket(reader.GetString(0)));
                }
            if (names.Count == 0) return new(SqlAccessStatus.UnableToVerify, "The source catalog changed during qualification. Refresh and retry.", []);
            using var probe = Command("SELECT TOP (0) " + string.Join(',', names) + " FROM " + table.QualifiedName + ";");
            using var cancel = Cancel(probe, ct);
            try { using var reader = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false); }
            catch (SqlException e) { return new(SqlAccessStatus.InsufficientAccess, "Cannot read " + table.QualifiedName + ": " + e.Message, []); }
        }
        return new(SqlAccessStatus.Qualified, "Complete supported schema and table-column SELECT access verified.", objects);
    }

    internal static CancellationTokenRegistration Cancel(SqlCommand command, CancellationToken ct) =>
        ct.Register(() => { try { command.Cancel(); } catch (Exception) { } });
}
