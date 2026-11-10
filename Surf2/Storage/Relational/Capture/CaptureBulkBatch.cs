using System.Data;
using System.IO;

namespace Surf2.Storage.Relational.Capture;

internal sealed class CaptureBulkBatch : IDisposable
{
    internal DataTable Rows { get; } = new("CapturedRows");
    internal DataTable Exceptions { get; } = new("CapturedExceptions");
    internal long EndOrdinal { get; }

    // Reserve schema objects and the DataTable storage arrays' minimum capacity
    // even when a batch has only one row; token/record costs are in row estimates.
    internal static long OverheadBytes(CaptureLayout layout) => CaptureLimits.BulkTableOverheadBytes(layout.Columns.Length);

    internal CaptureBulkBatch(CaptureWriteHandle handle, CaptureLayout layout,
        IReadOnlyList<EncodedCaptureRow> batch, long next, CaptureLimits limits)
    {
        try
        {
            if (next < 0) throw new ArgumentOutOfRangeException(nameof(next));
            if (batch.Count == 0 || batch.Count > limits.WriteBatchRows)
                throw new CaptureLimitException("The bulk batch exceeds the write row budget or is empty.");
            EndOrdinal = checked(next + batch.Count);
            long bytes = OverheadBytes(layout);
            int exceptionCount = 0;
            foreach (var row in batch)
            {
                bytes = checked(bytes + row.EstimatedBytes);
                if (row.EstimatedBytes <= 0 || bytes > limits.WriteBatchBytes)
                    throw new CaptureLimitException("The bulk batch exceeds the write memory budget.");
                if (row.ScalarTokens.Length != layout.Columns.Length || row.Exceptions.Length > limits.MaxExceptionsPerRow)
                    throw new InvalidDataException("An encoded bulk row does not match the bounded layout.");
                exceptionCount = checked(exceptionCount + row.Exceptions.Length);
            }
            Rows.MinimumCapacity = batch.Count;
            Add(Rows, "DataSetKey", typeof(long));
            Add(Rows, "LayoutKey", typeof(long));
            Add(Rows, "RowOrdinal", typeof(long));
            Add(Rows, "RowKind", typeof(byte));
            Add(Rows, "EstimatedBytes", typeof(long));
            Add(Rows, "PropertyOrder", typeof(byte[]));
            for (int i = 0; i < layout.Columns.Length; i++)
                Add(Rows, CaptureLayout.ColumnName(i), typeof(string), allowNull: true);
            Exceptions.MinimumCapacity = exceptionCount;
            Add(Exceptions, "DataSetKey", typeof(long));
            Add(Exceptions, "RowOrdinal", typeof(long));
            Add(Exceptions, "PropertyOrdinal", typeof(int));
            Add(Exceptions, "ValueKind", typeof(byte));
            Add(Exceptions, "RawToken", typeof(string));

            Rows.BeginLoadData();
            Exceptions.BeginLoadData();
            foreach (var row in batch)
            {
                var values = new object[6 + layout.Columns.Length];
                values[0] = handle.DataSetKey;
                values[1] = handle.LayoutKey;
                values[2] = next;
                values[3] = (byte)row.Kind;
                values[4] = row.EstimatedBytes;
                values[5] = row.PropertyOrder;
                for (int i = 0; i < row.ScalarTokens.Length; i++)
                    values[6 + i] = (object?)row.ScalarTokens[i] ?? DBNull.Value;
                Rows.Rows.Add(values);
                foreach (var exception in row.Exceptions)
                    Exceptions.Rows.Add(handle.DataSetKey, next, exception.PropertyOrdinal, (byte)exception.Kind, exception.RawToken);
                next++;
            }
            Rows.EndLoadData();
            Exceptions.EndLoadData();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static void Add(DataTable table, string name, Type type, bool allowNull = false) =>
        table.Columns.Add(new DataColumn(name, type) { AllowDBNull = allowNull });

    public void Dispose()
    {
        Exceptions.Dispose();
        Rows.Dispose();
    }
}
