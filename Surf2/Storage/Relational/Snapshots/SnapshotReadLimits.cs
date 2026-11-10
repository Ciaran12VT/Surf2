using System.IO;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Snapshots;

public sealed record SnapshotReadLimits
{
    public const long DefaultDefinitionBytes = 16L * 1024 * 1024;
    public const long MaximumDefinitionBytes = RelationalContentStore.MaximumSingleContentBytes;
    public long SelectedDefinitionBytes { get; init; } = DefaultDefinitionBytes;

    internal void Validate()
    {
        if (SelectedDefinitionBytes <= 0 || SelectedDefinitionBytes > MaximumDefinitionBytes)
            throw new ArgumentOutOfRangeException(nameof(SelectedDefinitionBytes));
    }
}

public sealed class SnapshotReadLimitException(long limitBytes) : InvalidOperationException(
    $"The selected snapshot value exceeds its {limitBytes}-byte read budget. No stored value was truncated.")
{
    public long LimitBytes { get; } = limitBytes;
}

internal sealed class SnapshotMetadataBudget
{
    internal long Bytes { get; private set; }
    internal void Add(long bytes, SqlCommand? command = null)
    {
        SnapshotReadGuard.RequireBytes(bytes, RelationalSnapshotStore.MaximumMetadataPageBytes, command);
        if (bytes > RelationalSnapshotStore.MaximumMetadataPageBytes - Bytes)
        {
            SnapshotReadGuard.StopReading(command);
            throw new SnapshotReadLimitException(RelationalSnapshotStore.MaximumMetadataPageBytes);
        }
        Bytes += bytes;
    }
}

internal static class SnapshotReadGuard
{
    internal static void RequireBytes(long bytes, long maximum, SqlCommand? command = null)
    {
        if (bytes >= 0 && bytes <= maximum) return;
        StopReading(command);
        if (bytes < 0) throw new InvalidDataException("Invalid snapshot byte length.");
        throw new SnapshotReadLimitException(maximum);
    }

    internal static void StopReading(SqlCommand? command)
    {
        if (command == null) return;
        // Cancel before reader disposal so a rejected LOB/unused page remainder
        // does not have to be drained merely to return the pooled connection.
        using var cancel = RelationalSession.CancelCommand(command, new CancellationToken(canceled: true));
    }

    internal static async Task<string> ReadUtf16Async(Stream reader, long sqlBytes, long maximum, CancellationToken ct, SqlCommand? command = null)
    {
        ct.ThrowIfCancellationRequested();
        RequireBytes(sqlBytes, maximum, command);
        if ((sqlBytes & 1) != 0) { StopReading(command); throw new InvalidDataException("Invalid SQL Unicode byte length."); }
        int count = checked((int)(sqlBytes / 2));
        // Exact, validated allocation: no ReadToEnd/StringBuilder growth before
        // a limit check. The final string is a second bounded selected-text copy.
        char[] buffer = GC.AllocateUninitializedArray<char>(count);
        byte[] bytes = new byte[16 * 1024];
        int offset = 0;
        while (offset < count)
        {
            ct.ThrowIfCancellationRequested();
            int characters = Math.Min(bytes.Length / 2, count - offset);
            int requiredBytes = characters * 2;
            int received = 0;
            while (received < requiredBytes)
            {
                ct.ThrowIfCancellationRequested();
                int read = await reader.ReadAsync(bytes.AsMemory(received, requiredBytes - received), ct).ConfigureAwait(false);
                if (read == 0) { StopReading(command); throw new InvalidDataException("Snapshot text changed or ended before its declared length."); }
                received += read;
            }
            // SQL's raw nvarchar representation is UTF-16LE code units, not an
            // encoded Unicode scalar stream. Never normalize unpaired surrogates.
            for (int i = 0; i < characters; i++)
                buffer[offset + i] = (char)(bytes[i * 2] | bytes[i * 2 + 1] << 8);
            offset += characters;
        }
        if (await reader.ReadAsync(bytes.AsMemory(0, 1), ct).ConfigureAwait(false) != 0)
        {
            StopReading(command);
            throw new InvalidDataException("Snapshot text exceeds its declared length.");
        }
        ct.ThrowIfCancellationRequested();
        return new string(buffer);
    }
}
