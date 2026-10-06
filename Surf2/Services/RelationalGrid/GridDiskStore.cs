using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Surf2.Services.RelationalGrid;

internal readonly record struct GridRowReference(long Ordinal, long Offset, int Length, int CellCount)
{
    internal const int Size = 24;
    internal byte[] Encode()
    {
        byte[] bytes = new byte[Size];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, Ordinal);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), Offset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), CellCount);
        return bytes;
    }
    internal static GridRowReference Decode(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]),
        BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]));
}

internal sealed class GridAppendIndex : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly GridOwnedWorkspace _workspace;
    private readonly FileStream _writer;
    private TaskCompletionSource _changed = NewSignal();
    private readonly TaskCompletionSource<long> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _written;
    private long _published;
    private bool _finished;
    internal string Path { get; }
    internal Task<long> Completion => _completion.Task;
    internal GridCount Count { get { lock (_gate) return new(_published, _finished && _completion.Task.IsCompletedSuccessfully); } }

    internal GridAppendIndex(GridOwnedWorkspace workspace)
    {
        _workspace = workspace;
        _writer = workspace.Create(".index");
        Path = _writer.Name;
        _ = _completion.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal async Task AppendAsync(GridRowReference row, CancellationToken ct)
    {
        _workspace.Reserve(Path, GridRowReference.Size);
        await _writer.WriteAsync(row.Encode(), ct).ConfigureAwait(false);
        _written = checked(_written + 1);
    }

    internal async Task PublishAsync(CancellationToken ct)
    {
        await _writer.FlushAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _published = _written;
            var signal = _changed;
            _changed = NewSignal();
            signal.TrySetResult();
        }
    }

    internal async Task FinishAsync(CancellationToken ct)
    {
        await PublishAsync(ct).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        lock (_gate) { _finished = true; _completion.TrySetResult(_published); _changed.TrySetResult(); }
    }

    internal void Fail(Exception error)
    {
        lock (_gate) { _finished = true; _completion.TrySetException(error); _changed.TrySetResult(); }
    }

    internal async Task<long> WaitForAsync(long position, CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (_finished && !_completion.Task.IsCompletedSuccessfully) signal = _completion.Task;
                else if (_published > position || _finished) return _published;
                else signal = _changed.Task;
            }
            await signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    internal async IAsyncEnumerable<GridRowReference> StreamAsync(long skip,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var reader = OpenReader(Path);
        long position = skip;
        while (true)
        {
            long available = await WaitForAsync(position, ct).ConfigureAwait(false);
            if (position >= available) yield break;
            while (position < available)
            {
                ct.ThrowIfCancellationRequested();
                yield return await ReadReferenceAsync(reader, position++, ct).ConfigureAwait(false);
            }
        }
    }

    internal static FileStream OpenReader(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);

    internal static async ValueTask<GridRowReference> ReadReferenceAsync(FileStream reader, long position, CancellationToken ct)
    {
        byte[] bytes = new byte[GridRowReference.Size];
        reader.Position = checked(position * GridRowReference.Size);
        await reader.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return GridRowReference.Decode(bytes);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask DisposeAsync()
    {
        List<Exception> failures = [];
        try { await _writer.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { _workspace.Delete(Path); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Grid index cleanup failed.", failures);
    }
}

internal sealed class GridDiskStore : IAsyncDisposable
{
    private readonly GridOwnedWorkspace _workspace;
    private readonly GridLimits _limits;
    private readonly FileStream _writer;
    internal GridAppendIndex Index { get; }
    internal string RowsPath { get; }

    internal GridDiskStore(GridOwnedWorkspace workspace, GridLimits limits)
    {
        _workspace = workspace;
        _limits = limits;
        _writer = workspace.Create(".rows");
        RowsPath = _writer.Name;
        try { Index = new(workspace); }
        catch
        {
            _writer.Dispose();
            workspace.Delete(RowsPath);
            throw;
        }
    }

    internal async Task AppendAsync(long ordinal, string[] cells, CancellationToken ct)
    {
        if (ordinal < 0) throw new InvalidDataException("Negative source row identity.");
        GridValues.Validate(cells, _limits);
        using var memory = new MemoryStream(checked(4 + cells.Sum(s => 4 + s.Length * 2)));
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(cells.Length);
            foreach (string cell in cells) GridValues.WriteString(writer, cell);
        }
        int length = checked((int)memory.Length);
        long offset = _writer.Position;
        _workspace.Reserve(RowsPath, length);
        await _writer.WriteAsync(memory.GetBuffer().AsMemory(0, length), ct).ConfigureAwait(false);
        await Index.AppendAsync(new(ordinal, offset, length, cells.Length), ct).ConfigureAwait(false);
    }

    internal async Task PublishAsync(CancellationToken ct)
    {
        await _writer.FlushAsync(ct).ConfigureAwait(false);
        await Index.PublishAsync(ct).ConfigureAwait(false);
    }

    internal async Task FinishAsync(CancellationToken ct)
    {
        await _writer.FlushAsync(ct).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        await Index.FinishAsync(ct).ConfigureAwait(false);
    }

    internal async ValueTask<string[]> ReadAsync(FileStream reader, GridRowReference row, int columnCount, CancellationToken ct)
    {
        if (row.Ordinal < 0 || row.Offset < 0 || row.Length < 4 || row.Length > _limits.MaxRowBytes || row.CellCount < 0 || row.CellCount > _limits.MaxColumns)
            throw new InvalidDataException("Invalid owned grid row index.");
        byte[] bytes = new byte[row.Length];
        reader.Position = row.Offset;
        await reader.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        using var memory = new MemoryStream(bytes, writable: false);
        using var decoder = new BinaryReader(memory, Encoding.UTF8);
        int count = decoder.ReadInt32();
        if (count != row.CellCount || count > columnCount) throw new InvalidDataException("Grid row/layout mismatch.");
        var cells = new string[columnCount];
        for (int i = 0; i < count; i++) cells[i] = GridValues.ReadString(decoder, _limits);
        for (int i = count; i < cells.Length; i++) cells[i] = string.Empty;
        if (memory.Position != memory.Length) throw new InvalidDataException("Trailing owned grid row bytes.");
        GridValues.Validate(cells, _limits);
        return cells;
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception> failures = [];
        try { await _writer.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { await Index.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { _workspace.Delete(RowsPath); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Grid row-store cleanup failed.", failures);
    }
}
