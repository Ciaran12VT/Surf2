using System.Data;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Capture;

public sealed partial class RelationalCaptureStore
{
    // Explicit destination staging API: migration may call this while the
    // database is Migrating. It never initializes or writes a legacy source.
    public async Task<CaptureWriteHandle> CreateDataSetAsync(CaptureDataSetCreate request,
        CancellationToken cancellationToken = default)
    {
        _session.RejectValidationWrite();
        ArgumentNullException.ThrowIfNull(request);
        if (request.RevisionKey <= 0 || request.ExpectedActualRowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        var layout = new CaptureLayout(request.Columns);
        RequireWriteBudget(layout);
        await using var connection = await _session.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        long layoutKey = await FindOrCreateLayoutAsync(connection, transaction, layout, cancellationToken);
        var imported = RelationalSession.Parameter("@Imported", SqlDbType.DateTimeOffset, request.ImportedAtUtc);
        imported.Scale = 7;
        await using var command = Command(connection, transaction, """
INSERT surf.DataSet(RevisionKey, LayoutKey, ReportedRowCount, ExpectedActualRowCount, ImportedAtUtc, State, DisplayFormatVersion)
OUTPUT INSERTED.DataSetKey VALUES (@Revision, @Layout, @Reported, @Expected, @Imported, 'Writing', 1);
""", Key("@Revision", request.RevisionKey), Key("@Layout", layoutKey), Key("@Reported", request.ReportedRowCount),
            RelationalSession.Parameter("@Expected", SqlDbType.BigInt, request.ExpectedActualRowCount),
            imported);
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        long dataSetKey = (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("No dataset identity was returned."));
        await transaction.CommitAsync(cancellationToken);
        return new(_session.Epoch, dataSetKey, layoutKey);
    }

    private static async Task<long> FindOrCreateLayoutAsync(SqlConnection connection, SqlTransaction transaction,
        CaptureLayout layout, CancellationToken cancellationToken)
    {
        var candidates = new List<long>();
        await using (var find = Command(connection, transaction,
            "SELECT LayoutKey FROM surf.DataLayout WITH (UPDLOCK, HOLDLOCK) WHERE LayoutHash = @Hash ORDER BY LayoutKey;",
            RelationalSession.Parameter("@Hash", SqlDbType.Binary, layout.Hash, 32)))
        {
            using var cancellation = RelationalSession.CancelCommand(find, cancellationToken);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (candidates.Count == 64) throw new InvalidDataException("Unexpected captured-layout hash collision fanout.");
                candidates.Add(reader.GetInt64(0));
            }
        }
        foreach (long candidate in candidates)
        {
            var existing = await ReadLayoutAsync(connection, transaction, candidate, cancellationToken);
            if (layout.Columns.SequenceEqual(existing.Columns)) return candidate;
        }
        long key;
        await using (var insert = Command(connection, transaction, """
INSERT surf.DataLayout(LayoutHash, EncodingVersion, ColumnCount)
OUTPUT INSERTED.LayoutKey VALUES (@Hash, 1, @Count);
""", RelationalSession.Parameter("@Hash", SqlDbType.Binary, layout.Hash, 32),
            RelationalSession.Parameter("@Count", SqlDbType.Int, layout.Columns.Length)))
        {
            using var cancellation = RelationalSession.CancelCommand(insert, cancellationToken);
            key = (long)(await insert.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidDataException("No layout identity was returned."));
        }
        for (int i = 0; i < layout.Columns.Length; i++)
        {
            var column = layout.Columns[i];
            await using var insert = Command(connection, transaction, """
INSERT surf.DataColumn(LayoutKey, ColumnOrdinal, SourceName, SourceDataType, SourceMaxLength, SourcePrecision, SourceScale, SourceNullable, SourceOrdinal, SourceIdentity, EncodingPolicy)
VALUES (@Layout, @Ordinal, @Name, @Type, @Length, @Precision, @Scale, @Nullable, @SourceOrdinal, @Identity, 'JsonScalarToken-v1');
""", Key("@Layout", key), RelationalSession.Parameter("@Ordinal", SqlDbType.Int, i),
                Text("@Name", column.SourceName), Text("@Type", column.SourceDataType),
                RelationalSession.Parameter("@Length", SqlDbType.Int, column.SourceMaxLength),
                RelationalSession.Parameter("@Precision", SqlDbType.TinyInt, column.SourcePrecision),
                RelationalSession.Parameter("@Scale", SqlDbType.Int, column.SourceScale),
                RelationalSession.Parameter("@Nullable", SqlDbType.Bit, column.SourceNullable),
                RelationalSession.Parameter("@SourceOrdinal", SqlDbType.Int, column.SourceOrdinal),
                RelationalSession.Parameter("@Identity", SqlDbType.Bit, column.SourceIdentity));
            using var cancellation = RelationalSession.CancelCommand(insert, cancellationToken);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var ddl = Command(connection, transaction, layout.CreateTableSql(key)))
        {
            using var cancellation = RelationalSession.CancelCommand(ddl, cancellationToken);
            await ddl.ExecuteNonQueryAsync(cancellationToken);
        }
        return key;
    }

    public async Task<CaptureWriteProgress> GetWriteProgressAsync(CaptureWriteHandle handle,
        CancellationToken cancellationToken = default)
    {
        ValidateHandle(handle);
        await using var connection = await _session.OpenAsync(cancellationToken);
        return await ReadProgressAsync(connection, null, handle, cancellationToken);
    }

    // Narrow crash recovery lookup, including staged datasets in a Migrating
    // destination. Caller must validate its frozen source fingerprint and all
    // import metadata before skipping Progress.NextRowOrdinal source rows.
    public async Task<CaptureImportState?> FindForRevisionForImportAsync(long revisionKey,
        CancellationToken cancellationToken = default)
    {
        if (revisionKey <= 0) throw new ArgumentOutOfRangeException(nameof(revisionKey));
        await using var connection = await _session.OpenAsync(cancellationToken);
        CaptureDataSetSummary summary;
        CaptureWriteProgress progress;
        await using (var command = Command(connection, null,
            $"SELECT {SummaryProjection}, ExpectedActualRowCount FROM surf.DataSet WHERE RevisionKey = @Revision;", Key("@Revision", revisionKey)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            summary = ReadSummary(reader);
            progress = new(summary.ActualRowCount, summary.State, reader.IsDBNull(8) ? null : reader.GetInt64(8));
        }
        var layout = await ReadLayoutAsync(connection, null, summary.LayoutKey, cancellationToken);
        return new(new(_session.Epoch, summary.DataSetKey, summary.LayoutKey), summary, progress, Array.AsReadOnly(layout.Columns));
    }

    private static async Task<CaptureWriteProgress> ReadProgressAsync(SqlConnection connection,
        SqlTransaction? transaction, CaptureWriteHandle handle, CancellationToken cancellationToken)
    {
        string locking = transaction == null ? "" : " WITH (UPDLOCK, HOLDLOCK)";
        await using var command = Command(connection, transaction, $"""
SELECT ActualRowCount, State, ExpectedActualRowCount FROM surf.DataSet{locking}
WHERE DataSetKey = @DataSet AND LayoutKey = @Layout;
""", Key("@DataSet", handle.DataSetKey), Key("@Layout", handle.LayoutKey));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Captured dataset not found.");
        return new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    // Each committed batch advances ActualRowCount atomically. On error/cancel,
    // callers must reread progress before replaying any source rows. Ready rows
    // are immutable, and expectedNextRowOrdinal rejects concurrent/replayed writes.
    public async Task<long> AppendRowsAsync(CaptureWriteHandle handle, IAsyncEnumerable<JsonElement> rows,
        long expectedNextRowOrdinal, CancellationToken cancellationToken = default)
    {
        _session.RejectValidationWrite();
        ValidateHandle(handle);
        ArgumentNullException.ThrowIfNull(rows);
        if (expectedNextRowOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(expectedNextRowOrdinal));
        CaptureLayout layout;
        await using (var connection = await _session.OpenAsync(cancellationToken))
            layout = await ReadLayoutAsync(connection, null, handle.LayoutKey, cancellationToken);
        RequireWriteBudget(layout);
        var batch = new List<EncodedCaptureRow>();
        long overhead = CaptureBulkBatch.OverheadBytes(layout);
        long bytes = overhead;
        long next = expectedNextRowOrdinal;
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = CaptureRowCodec.Encode(layout, row, _limits);
            if (encoded.EstimatedBytes + overhead > _limits.WriteBatchBytes)
                throw new CaptureLimitException("One row plus bulk-table overhead exceeds the write-batch memory budget.");
            if (batch.Count > 0 && (batch.Count >= _limits.WriteBatchRows || bytes + encoded.EstimatedBytes > _limits.WriteBatchBytes))
            {
                next = await WriteBatchAsync(handle, layout, batch, next, cancellationToken);
                batch.Clear();
                bytes = overhead;
            }
            batch.Add(encoded);
            bytes += encoded.EstimatedBytes;
            if (batch.Count == _limits.WriteBatchRows || bytes == _limits.WriteBatchBytes)
            {
                next = await WriteBatchAsync(handle, layout, batch, next, cancellationToken);
                batch.Clear();
                bytes = overhead;
            }
        }
        if (batch.Count > 0) next = await WriteBatchAsync(handle, layout, batch, next, cancellationToken);
        else
        {
            var progress = await GetWriteProgressAsync(handle, cancellationToken);
            if (progress.State != "Writing" || progress.NextRowOrdinal != next)
                throw new InvalidOperationException("Dataset is not writable at the expected ordinal.");
        }
        return next;
    }

    private void RequireWriteBudget(CaptureLayout layout)
    {
        long minimum = CaptureLimits.GetMinimumWriteBatchBytes(layout.Columns.Length);
        if (_limits.WriteBatchBytes < minimum)
            throw new CaptureLimitException($"The {layout.Columns.Length}-column layout needs a write budget of at least {minimum} bytes for its bulk tables and one minimal row; configured {_limits.WriteBatchBytes} bytes. Read/page budgets need not change.");
    }

    private async Task<long> WriteBatchAsync(CaptureWriteHandle handle, CaptureLayout layout,
        IReadOnlyList<EncodedCaptureRow> batch, long next, CancellationToken cancellationToken)
    {
        using var tables = new CaptureBulkBatch(handle, layout, batch, next, _limits);
        long end = tables.EndOrdinal;
        await using var connection = await _session.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var progress = await ReadProgressAsync(connection, transaction, handle, cancellationToken);
        if (progress.State != "Writing" || progress.NextRowOrdinal != next)
            throw new InvalidOperationException("Dataset is not writable at the expected ordinal.");
        if (progress.ExpectedActualRowCount is long expected && end > expected)
            throw new InvalidDataException("More source rows than the expected actual count.");
        // Both bulk copies and the row checkpoint share this external transaction.
        // Disposal rolls back partial copies on failure/cancellation, never publishing
        // a row prefix without its exceptions. Source names never enter SQL mappings.
        await BulkCopyAsync(connection, transaction, CaptureLayout.QualifiedTable(handle.LayoutKey), tables.Rows, cancellationToken);
        if (tables.Exceptions.Rows.Count > 0)
            await BulkCopyAsync(connection, transaction, "[surf].[DataValueException]", tables.Exceptions, cancellationToken);
        await using var update = Command(connection, transaction,
            "UPDATE surf.DataSet SET ActualRowCount = @Next WHERE DataSetKey = @DataSet AND State = 'Writing';",
            Key("@Next", end), Key("@DataSet", handle.DataSetKey));
        using var updateCancellation = RelationalSession.CancelCommand(update, cancellationToken);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new DBConcurrencyException("Dataset write was not advanced.");
        await transaction.CommitAsync(cancellationToken);
        return end;
    }

    private static async Task BulkCopyAsync(SqlConnection connection, SqlTransaction transaction,
        string destination, DataTable table, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls, transaction)
        {
            DestinationTableName = destination,
            BatchSize = table.Rows.Count,
            EnableStreaming = true
        };
        foreach (DataColumn column in table.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(table, cancellationToken);
    }

    public Task<CaptureDataSetSummary> CompleteDataSetAsync(CaptureWriteHandle handle,
        CancellationToken cancellationToken = default) => CompleteDataSetCoreAsync(handle, null, cancellationToken);

    public Task<CaptureDataSetSummary> CompleteDataSetAsync(CaptureWriteHandle handle, long expectedActualRowCount,
        CancellationToken cancellationToken = default) => CompleteDataSetCoreAsync(handle, expectedActualRowCount, cancellationToken);

    private async Task<CaptureDataSetSummary> CompleteDataSetCoreAsync(CaptureWriteHandle handle,
        long? expectedActualRowCount, CancellationToken cancellationToken)
    {
        _session.RejectValidationWrite();
        ValidateHandle(handle);
        if (expectedActualRowCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedActualRowCount));
        await using var connection = await _session.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var progress = await ReadProgressAsync(connection, transaction, handle, cancellationToken);
        long expected = expectedActualRowCount ?? progress.ExpectedActualRowCount
            ?? throw new InvalidOperationException("Supply the expected actual source-row count at creation or completion.");
        if (expected != progress.NextRowOrdinal ||
            (progress.ExpectedActualRowCount is long initialExpected && initialExpected != expected))
            throw new InvalidDataException("Source rows do not match the expected actual count.");
        if (progress.State == "Writing")
        {
            await using (var verify = Command(connection, transaction, $"""
SELECT COUNT_BIG(*), MIN(RowOrdinal), MAX(RowOrdinal) FROM {CaptureLayout.QualifiedTable(handle.LayoutKey)} WHERE DataSetKey = @DataSet;
""", Key("@DataSet", handle.DataSetKey)))
            {
                using var cancellation = RelationalSession.CancelCommand(verify, cancellationToken);
                await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                long count = reader.GetInt64(0);
                if (count != progress.NextRowOrdinal || (count > 0 && (reader.GetInt64(1) != 0 || reader.GetInt64(2) != count - 1)))
                    throw new InvalidDataException("Captured row count or contiguous ordinals failed validation.");
            }
            await using var publish = Command(connection, transaction,
                "UPDATE surf.DataSet SET State = 'Ready', ExpectedActualRowCount = @Expected WHERE DataSetKey = @DataSet AND State = 'Writing';",
                Key("@DataSet", handle.DataSetKey), Key("@Expected", expected));
            using var publishCancellation = RelationalSession.CancelCommand(publish, cancellationToken);
            if (await publish.ExecuteNonQueryAsync(cancellationToken) != 1) throw new DBConcurrencyException("Dataset publication failed.");
        }
        await using var summaryCommand = Command(connection, transaction,
            $"SELECT {SummaryProjection} FROM surf.DataSet WHERE DataSetKey = @DataSet;", Key("@DataSet", handle.DataSetKey));
        using var summaryCancellation = RelationalSession.CancelCommand(summaryCommand, cancellationToken);
        CaptureDataSetSummary summary;
        await using (var reader = await summaryCommand.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            summary = ReadSummary(reader);
        }
        await transaction.CommitAsync(cancellationToken);
        return summary;
    }
}
