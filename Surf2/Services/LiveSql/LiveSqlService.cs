using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services.RemoteFolders;

namespace Surf2.Services.LiveSql;

public sealed record LiveSqlParameter(string Name, SqlDbType Type, object? Value, int Size = 0, byte Precision = 0, byte Scale = 0);
public sealed record LiveSqlQueryLimits(int MaximumRows = 10000, long MaximumBytes = 64 * 1024 * 1024,
    int MaximumCellBytes = 4 * 1024 * 1024, int TimeoutSeconds = 30)
{
    public void Validate()
    {
        if (MaximumRows is < 1 or > 100000 || MaximumBytes is < 1024 or > 256 * 1024 * 1024 ||
            MaximumCellBytes is < 64 or > 4 * 1024 * 1024 || TimeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(LiveSqlQueryLimits));
    }
}
public sealed record LiveSqlColumn(int Ordinal, string Name, string SqlType);
public sealed record LiveSqlCell(string Kind, string? Value)
{
    public string Display => Kind == "Null" ? "NULL" : Kind == "Binary" ? "0x" + Convert.ToHexString(Convert.FromBase64String(Value!)) : Value ?? "";
    public static int Compare(LiveSqlCell a, LiveSqlCell b)
    {
        if (a.Kind == "Null" || b.Kind == "Null") return a.Kind == b.Kind ? 0 : a.Kind == "Null" ? -1 : 1;
        if (a.Kind == b.Kind && a.Kind == "Decimal") return SqlDecimal.Parse(a.Value!).CompareTo(SqlDecimal.Parse(b.Value!));
        if (a.Kind == b.Kind && a.Kind == "Float") return double.Parse(a.Value!, CultureInfo.InvariantCulture).CompareTo(double.Parse(b.Value!, CultureInfo.InvariantCulture));
        if (a.Kind == b.Kind && a.Kind == "Binary") return Convert.FromBase64String(a.Value!).AsSpan().SequenceCompareTo(Convert.FromBase64String(b.Value!));
        if (a.Kind == b.Kind && a.Kind == "DateTimeOffset") return DateTimeOffset.Parse(a.Value!, CultureInfo.InvariantCulture).CompareTo(DateTimeOffset.Parse(b.Value!, CultureInfo.InvariantCulture));
        if (a.Kind == b.Kind && a.Kind == "DateTime") return DateTime.Parse(a.Value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).CompareTo(DateTime.Parse(b.Value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        if (a.Kind == b.Kind && a.Kind == "Time") return TimeSpan.Parse(a.Value!, CultureInfo.InvariantCulture).CompareTo(TimeSpan.Parse(b.Value!, CultureInfo.InvariantCulture));
        return StringComparer.Ordinal.Compare(a.Value, b.Value);
    }
}

/// <summary>A bounded typed result owned by this desktop; no source SQL connection survives execution.</summary>
public sealed class LiveSqlResult : IAsyncDisposable
{
    private readonly FileStream _file;
    private readonly List<(long Offset, int Length)> _rows = [];
    private readonly SemaphoreSlim _gate = new(1);
    public Guid Id { get; } = Guid.NewGuid();
    public IReadOnlyList<LiveSqlColumn> Columns { get; internal set; } = [];
    public int RowCount => _rows.Count;
    public long Bytes { get; private set; }
    public string Status { get; internal set; } = "Complete";
    public TimeSpan Elapsed { get; internal set; }
    public bool IsPartial => Status != "Complete";
    private LiveSqlResult(string directory, long reservation)
    {
        Directory.CreateDirectory(directory);
        _file = new(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try { _file.SetLength(reservation); }
        catch { _file.Dispose(); throw; }
    }
    internal static async Task<LiveSqlResult> CreateAsync(string sourceIdentity, long maximumBytes, CancellationToken ct)
    {
        var cache = RemoteFolderServices.Default.Cache;
        using var operation = await cache.AcquireOperationAsync(ct).ConfigureAwait(false);
        var settings = cache.ReadSettings();
        string directory = Path.Combine(cache.Root, "query-results", FolderSourcePaths.Hash(sourceIdentity));
        if (FolderCacheStore.DirectoryBytes(cache.Root) > settings.TotalBytes - maximumBytes ||
            FolderCacheStore.DirectoryBytes(directory) > settings.PerSourceBytes - maximumBytes)
            throw new FolderCapacityException("This query's result limit would exceed Local Cache storage limits. Close an existing result, lower its byte limit, or adjust Local Cache settings.");
        FolderCacheStore.RequireDriveSpace(cache.Root, maximumBytes, settings.FreeSpaceReserveBytes);
        Directory.CreateDirectory(directory); FolderSourcePaths.RequireNoReparseAncestors(cache.Root, directory);
        // Reserving the bounded result length makes concurrent queries participate in the shared disk budget.
        return new(directory, maximumBytes);
    }
    internal void Complete() => _file.SetLength(Bytes);
    internal async Task<bool> AppendAsync(LiveSqlCell[] cells, long budget, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(cells);
        if (Bytes + bytes.Length > budget) return false;
        long offset = _file.Position;
        await _file.WriteAsync(bytes, ct).ConfigureAwait(false);
        _rows.Add((offset, bytes.Length)); Bytes += bytes.Length; return true;
    }
    public async Task<LiveSqlCell[]> ReadRowAsync(int ordinal, CancellationToken ct = default)
    {
        if (ordinal < 0 || ordinal >= _rows.Count) throw new ArgumentOutOfRangeException(nameof(ordinal));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var row = _rows[ordinal]; var bytes = new byte[row.Length]; _file.Position = row.Offset;
            await _file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LiveSqlCell[]>(bytes)!;
        }
        finally { _gate.Release(); }
    }
    public async Task ExportAsync(string path, CancellationToken ct = default)
    {
        // JSON preserves NULL, ordinal identity, duplicate names, exact numbers and binary bytes.
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject(); writer.WriteString("Format", "Surf2.SqlQueryResult"); writer.WriteNumber("Version", 1);
        writer.WriteString("Status", Status); writer.WritePropertyName("Columns"); JsonSerializer.Serialize(writer, Columns);
        writer.WriteStartArray("Rows");
        for (int i = 0; i < RowCount; i++) { ct.ThrowIfCancellationRequested(); JsonSerializer.Serialize(writer, await ReadRowAsync(i, ct).ConfigureAwait(false)); }
        writer.WriteEndArray(); writer.WriteEndObject(); await writer.FlushAsync(ct).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync() { await _gate.WaitAsync(); try { await _file.DisposeAsync(); } finally { _gate.Release(); } }
}

public sealed class LiveSqlService
{
    private readonly string _connectionString;
    public string Server { get; }
    public string Database { get; }
    public LiveSqlService(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog) || builder.AttachDBFilename.Length != 0)
            throw new ArgumentException("Select an existing server and database.");
        builder.ApplicationName = "Surf2 live SQL";
        _connectionString = builder.ConnectionString; Server = builder.DataSource; Database = builder.InitialCatalog;
    }
    public async Task<SqlAccessQualification> QualifyAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
            return await QualifyConnectionAsync(connection, ct).ConfigureAwait(false);
        }
        catch (SqlException) { SourceResources.LiveSourceWorkspace.RejectService(this, "The live SQL connection cannot be verified. Reload to reconnect."); throw; }
    }
    private async Task<SqlAccessQualification> QualifyConnectionAsync(SqlConnection connection, CancellationToken ct)
    {
        var result = await LiveSqlAccess.QualifyAsync(connection, ct: ct).ConfigureAwait(false);
        if (result.Status != SqlAccessStatus.Qualified) SourceResources.LiveSourceWorkspace.RejectService(this, result.Message);
        return result;
    }
    public async Task<DatabaseOrigin> OriginAsync(Guid? sourceId = null, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        return await DatabaseOriginQueryService.ReadAsync(connection, sourceId: sourceId, ct: ct).ConfigureAwait(false);
    }
    public async Task<string> ReadDefinitionAsync(LiveSqlObject item, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        var access = await QualifyConnectionAsync(connection, ct).ConfigureAwait(false); access.RequireQualified();
        var current = access.Objects.SingleOrDefault(o => o.Schema == item.Schema && o.Name == item.Name && o.Type == item.Type)
            ?? throw new InvalidOperationException("The source object no longer exists. Refresh the resource.");
        if (current.ObjectId != item.ObjectId) throw new InvalidOperationException("The source object was replaced. Refresh and reopen it to view its current definition.");
        if (!current.IsTable) return current.Definition!;
        return await ReadTableDefinitionAsync(connection, current, ct).ConfigureAwait(false);
    }
    // An indexing pass qualifies once, consumes the captured definition inventory, and requalifies before publication.
    internal async Task<string> ReadQualifiedDefinitionAsync(LiveSqlObject item, CancellationToken ct)
    {
        if (!item.IsTable) return item.Definition ?? throw new InvalidOperationException("The qualified definition is unavailable.");
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        return await ReadTableDefinitionAsync(connection, item, ct).ConfigureAwait(false);
    }
    private static async Task<string> ReadTableDefinitionAsync(SqlConnection connection, LiveSqlObject current, CancellationToken ct)
    {
        using var command = new SqlCommand("""
            IF ISNULL(OBJECT_ID(@Name,N'U'),-1)<>@Id THROW 51101,'The live table changed during the read; refresh the resource.',1;
            SELECT c.name,TYPE_NAME(c.user_type_id),c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity
            FROM sys.columns c WHERE c.object_id=@Id ORDER BY c.column_id;
            SELECT i.name,c.name,ic.key_ordinal FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE i.object_id=@Id AND i.is_primary_key=1 ORDER BY ic.key_ordinal;
            """, connection) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("@Id", current.ObjectId); command.Parameters.AddWithValue("@Name", current.QualifiedName);
        using var cancel = LiveSqlAccess.Cancel(command, ct);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false); var lines = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string type = reader.GetString(1); short length = reader.GetInt16(2);
            string suffix = type is "varchar" or "char" or "varbinary" or "binary" or "nvarchar" or "nchar"
                ? "(" + (length == -1 ? "max" : (type.StartsWith('n') ? length / 2 : length).ToString(CultureInfo.InvariantCulture)) + ")"
                : type is "decimal" or "numeric" ? $"({reader.GetByte(3)},{reader.GetByte(4)})" : "";
            lines.Add("    " + SqlName.Bracket(reader.GetString(0)) + " " + type + suffix + (reader.GetBoolean(6) ? " IDENTITY" : "") + (reader.GetBoolean(5) ? " NULL" : " NOT NULL"));
        }
        if (lines.Count == 0) throw new InvalidOperationException("The table columns are no longer visible. Refresh to verify source access.");
        await reader.NextResultAsync(ct).ConfigureAwait(false); var keys = new List<string>(); string keyName = "";
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) { keyName = reader.GetString(0); keys.Add(SqlName.Bracket(reader.GetString(1))); }
        if (keys.Count != 0) lines.Add("    CONSTRAINT " + SqlName.Bracket(keyName) + " PRIMARY KEY (" + string.Join(", ", keys) + ")");
        return "-- Live schema metadata (informational; not a complete CREATE script)\nCREATE TABLE " + current.QualifiedName + " (\n" + string.Join(",\n", lines) + "\n);";
    }
    public async Task<LiveSqlResult> QueryAsync(string sql, IReadOnlyList<LiveSqlParameter>? parameters = null,
        LiveSqlQueryLimits? limits = null, CancellationToken ct = default)
    {
        LiveSqlQueryPolicy.Validate(sql); limits ??= new(); limits.Validate();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await LiveSqlResult.CreateAsync(Server + "/" + Database, limits.MaximumBytes, ct).ConfigureAwait(false);
        bool accessVerified = false;
        try
        {
            await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
            (await QualifyConnectionAsync(connection, ct).ConfigureAwait(false)).RequireQualified();
            accessVerified = true;
            using var command = new SqlCommand(sql, connection) { CommandTimeout = limits.TimeoutSeconds };
            foreach (var value in parameters ?? [])
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(value.Name, "^@[A-Za-z_][A-Za-z0-9_]{0,127}$")) throw new ArgumentException("Invalid parameter name.");
                command.Parameters.Add(new SqlParameter(value.Name, value.Type) { Value = value.Value ?? DBNull.Value,
                    Size = value.Size, Precision = value.Precision, Scale = value.Scale });
            }
            using var cancel = LiveSqlAccess.Cancel(command, ct);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, ct).ConfigureAwait(false);
            if (reader.FieldCount > 1024) throw new InvalidOperationException("The query returned too many columns.");
            result.Columns = Enumerable.Range(0, reader.FieldCount).Select(i => new LiveSqlColumn(i, reader.GetName(i), reader.GetDataTypeName(i))).ToArray();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (result.RowCount == limits.MaximumRows) { result.Status = "Partial: row limit"; command.Cancel(); break; }
                var cells = new LiveSqlCell[reader.FieldCount];
                try
                {
                    long rowBytes = 2;
                    for (int i = 0; i < cells.Length; i++)
                    {
                        cells[i] = await ReadCellAsync(reader, i, limits.MaximumCellBytes, ct).ConfigureAwait(false);
                        rowBytes += JsonSerializer.SerializeToUtf8Bytes(cells[i]).LongLength + (i == 0 ? 0 : 1);
                        if (rowBytes > limits.MaximumBytes - result.Bytes) throw new NotSupportedException("row byte limit");
                    }
                    if (!await result.AppendAsync(cells, limits.MaximumBytes, ct).ConfigureAwait(false)) { result.Status = "Partial: byte limit"; command.Cancel(); break; }
                }
                catch (NotSupportedException error) { result.Status = "Partial: " + error.Message; command.Cancel(); break; }
            }
            result.Complete(); result.Elapsed = clock.Elapsed; return result;
        }
        catch (SqlException error) when (!accessVerified || error.Errors.Cast<SqlError>().Any(e => e.Number is 229 or 230 or 297 or 916 or 942 or 4060 or 18456))
        {
            SourceResources.LiveSourceWorkspace.RejectService(this, "SQL access changed during the query. Reload the resource to verify access.");
            await result.DisposeAsync(); throw;
        }
        catch { await result.DisposeAsync(); throw; }
    }
    private static async Task<LiveSqlCell> ReadCellAsync(SqlDataReader reader, int i, int maximumBytes, CancellationToken ct)
    {
        if (await reader.IsDBNullAsync(i, ct).ConfigureAwait(false)) return new("Null", null);
        string type = reader.GetDataTypeName(i).ToLowerInvariant();
        if (type is "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" or "xml")
        {
            using var text = reader.GetTextReader(i); var buffer = new char[4096]; var builder = new StringBuilder(); int n;
            while ((n = await text.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            { if ((builder.Length + n) * 2L > maximumBytes) throw new NotSupportedException("cell byte limit"); builder.Append(buffer, 0, n); }
            return new("Text", builder.ToString());
        }
        if (type is "binary" or "varbinary" or "image" or "timestamp" or "rowversion")
        {
            using var stream = reader.GetStream(i); using var bytes = new MemoryStream(); var buffer = new byte[4096]; int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            { if (bytes.Length + n > maximumBytes) throw new NotSupportedException("cell byte limit"); bytes.Write(buffer, 0, n); }
            return new("Binary", Convert.ToBase64String(bytes.ToArray()));
        }
        if (type is "decimal" or "numeric") return new("Decimal", reader.GetSqlDecimal(i).ToString());
        object value = reader.GetValue(i);
        return value switch
        {
            byte or short or int or long or decimal => new("Decimal", Convert.ToString(value, CultureInfo.InvariantCulture)),
            double number => new("Float", number.ToString("R", CultureInfo.InvariantCulture)),
            float number => new("Float", number.ToString("R", CultureInfo.InvariantCulture)),
            bool flag => new("Boolean", flag ? "true" : "false"),
            Guid id => new("Guid", id.ToString("D")),
            DateTime date => new("DateTime", date.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset date => new("DateTimeOffset", date.ToString("O", CultureInfo.InvariantCulture)),
            TimeSpan time => new("Time", time.ToString("c", CultureInfo.InvariantCulture)),
            _ => throw new NotSupportedException("unsupported SQL type " + type)
        };
    }
}
