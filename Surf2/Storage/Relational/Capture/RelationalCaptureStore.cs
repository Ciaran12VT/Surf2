using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Capture;

public sealed partial class RelationalCaptureStore
{
    private readonly RelationalSession _session;
    private readonly CaptureLimits _limits;

    public RelationalCaptureStore(RelationalSession session, CaptureLimits? limits = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _limits = limits ?? new();
        _limits.Validate();
    }

    private void ValidateHandle(CaptureWriteHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.Epoch != _session.Epoch || handle.DataSetKey <= 0 || handle.LayoutKey <= 0)
            throw new ArgumentException("The write handle belongs to another connection epoch or has invalid keys.", nameof(handle));
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql,
        params SqlParameter[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return command;
    }

    private static SqlParameter Key(string name, long value) => RelationalSession.Parameter(name, SqlDbType.BigInt, value);
    private static SqlParameter Text(string name, string? value) => RelationalSession.Parameter(name, SqlDbType.NVarChar, value, -1);

    private static async Task<CaptureLayout> ReadLayoutAsync(SqlConnection connection, SqlTransaction? transaction,
        long layoutKey, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
SELECT ColumnCount, EncodingVersion, LayoutHash FROM surf.DataLayout WHERE LayoutKey = @Layout;
SELECT ColumnOrdinal, SourceName, SourceDataType, SourceMaxLength, SourcePrecision, SourceScale, SourceNullable,
       EncodingPolicy, SourceOrdinal, SourceIdentity FROM surf.DataColumn WHERE LayoutKey = @Layout ORDER BY ColumnOrdinal;
""", Key("@Layout", layoutKey));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Captured layout not found.");
        int count = reader.GetInt32(0);
        if (count is < 0 or > CaptureLimits.MaximumColumns || reader.GetInt32(1) != CaptureLayout.EncodingVersion)
            throw new InvalidDataException("Unsupported captured layout.");
        byte[] hash = reader.GetFieldValue<byte[]>(2);
        await reader.NextResultAsync(cancellationToken);
        var columns = new List<CaptureColumnDefinition>(count);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (columns.Count >= count || reader.GetInt32(0) != columns.Count || reader.GetString(7) != "JsonScalarToken-v1")
                throw new InvalidDataException("Invalid captured column catalogue.");
            columns.Add(new(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetByte(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetBoolean(9)));
        }
        if (columns.Count != count) throw new InvalidDataException("Incomplete captured column catalogue.");
        var layout = new CaptureLayout(columns);
        if (!layout.Hash.AsSpan().SequenceEqual(hash)) throw new InvalidDataException("Captured layout hash mismatch.");
        return layout;
    }

    private static CaptureDataSetSummary ReadSummary(SqlDataReader reader) => new(reader.GetInt64(0),
        reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
        reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), reader.GetInt32(7));

    private const string SummaryProjection = "DataSetKey, RevisionKey, LayoutKey, ReportedRowCount, ActualRowCount, ImportedAtUtc, State, DisplayFormatVersion";
}
