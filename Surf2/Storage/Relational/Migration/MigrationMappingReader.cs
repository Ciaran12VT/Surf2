using System.Data;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Migration;

internal sealed record MigrationMapping(string SourceIdentity, string EntityKind, long DestinationKey);

// The validator holds the exclusive migration lease: both mappings and cached absences are immutable.
internal sealed class MigrationMappingReader(RelationalSession session, Guid migrationIdentity)
{
    private const int BlockSize = 128, MaximumBlocks = 8;
    private const long MaximumMetadataBytes = 8L * 1024 * 1024;
    private readonly RelationalSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly LinkedList<Block> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _metadataBytes;

    internal async Task<MigrationMapping?> FindAsync(string document, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        GuardInputLength(document, path);
        string identity = Identity(document, path);
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (TryCached(identity, out var mapping))
            {
                ct.ThrowIfCancellationRequested();
                return mapping;
            }

            Block block = CreateBlock(document, path);
            await LoadAsync(block, ct);
            ct.ThrowIfCancellationRequested();
            Remember(block);
            return block.Mappings[identity];
        }
        catch (SqlException) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(Block block, CancellationToken ct)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await _session.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, migrationIdentity));
        var names = new List<string>(block.Mappings.Count);
        foreach (string identity in block.Mappings.Keys)
        {
            byte[] hash = Hash(identity);
            if (!expected.TryAdd(Convert.ToHexString(hash), identity))
                throw new InvalidDataException("A migration identity hash collision was detected.");
            string name = "@Hash" + names.Count.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.Add(RelationalSession.Parameter(name, SqlDbType.Binary, hash, 32));
        }
        // Lengths precede the LOB under SequentialAccess, so corrupt rows cannot allocate unbounded strings.
        command.CommandText = """
SELECT SourceIdentityHash,DATALENGTH(SourceIdentity),DATALENGTH(EntityKind),DestinationKey,SourceIdentity,EntityKind
FROM surf.MigrationIdentityMap
WHERE MigrationIdentity=@Migration AND SourceIdentityHash IN (
""" + string.Join(",", names) + ");";
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            byte[] hash = (byte[])reader.GetValue(0);
            if (!expected.TryGetValue(Convert.ToHexString(hash), out string? identity))
                throw new InvalidDataException("An unexpected migration identity hash was returned.");
            GuardRowLengths(identity, reader.GetInt64(1), reader.GetInt32(2));
            long key = reader.GetInt64(3);
            string storedIdentity = reader.GetString(4), kind = reader.GetString(5);
            block.Add(ValidateMapping(identity, hash, storedIdentity, kind, key));
        }
        ct.ThrowIfCancellationRequested();
    }

    private bool TryCached(string identity, out MigrationMapping? mapping)
    {
        for (var node = _cache.First; node != null; node = node.Next)
        {
            if (!node.Value.Mappings.TryGetValue(identity, out mapping)) continue;
            _cache.Remove(node);
            _cache.AddFirst(node);
            return true;
        }
        mapping = null;
        return false;
    }

    private void Remember(Block block)
    {
        while (_cache.Count >= MaximumBlocks || _metadataBytes > MaximumMetadataBytes - block.MetadataBytes)
        {
            var last = _cache.Last ?? throw new InvalidDataException("Migration mapping metadata exceeds its budget.");
            _metadataBytes -= last.Value.MetadataBytes;
            _cache.RemoveLast();
        }
        _cache.AddFirst(block);
        _metadataBytes += block.MetadataBytes;
    }

    private static Block CreateBlock(string document, string path) => new(CreateIdentities(document, path));

    private static string[] CreateIdentities(string document, string path)
    {
        GuardInputLength(document, path);
        if (!TrySiblingRange(path, out int start, out int length, out long ordinal) ||
            256L + BlockSize * (512L + 2L * (document.Length + (long)path.Length + 64)) > MaximumMetadataBytes)
            return [Identity(document, path)];

        long first = ordinal - ordinal % BlockSize;
        int count = (int)Math.Min(BlockSize - 1L, long.MaxValue - first) + 1;
        string prefix = path[..start], suffix = path[(start + length)..];
        var result = new string[count];
        for (int i = 0; i < count; i++)
        {
            string sibling = prefix + (first + i).ToString(CultureInfo.InvariantCulture) + suffix;
            result[i] = Identity(document, sibling);
        }
        return result;
    }

    private static bool TrySiblingRange(string path, out int start, out int length, out long ordinal)
    {
        start = length = 0; ordinal = 0;
        Span<Range> segments = stackalloc Range[8];
        int count = 0, position = 0;
        while (position < path.Length)
        {
            if (count == segments.Length) return false;
            int slash = path.IndexOf('/', position);
            int end = slash < 0 ? path.Length : slash;
            if (end == position || end == path.Length - 1 && slash >= 0) return false;
            segments[count++] = position..end;
            position = end + 1;
        }
        if (count < 2) return false;

        // Recognize only converter paths. Unrecognized paths retain their exact single-lookup semantics.
        int lastNumber;
        if (Segment(path, segments[0], "snapshots"))
        {
            if (count == 2 || count == 3 && Segment(path, segments[2], "publish")) lastNumber = 1;
            else
            {
                if (count is not (4 or 5) ||
                    !(Segment(path, segments[2], "objects") || Segment(path, segments[2], "tables") ||
                      Segment(path, segments[2], "columns") || Segment(path, segments[2], "keys") ||
                      Segment(path, segments[2], "selections") || Segment(path, segments[2], "datasets"))) return false;
                if (count == 5 && (!Segment(path, segments[4], "seal") ||
                    !(Segment(path, segments[2], "tables") || Segment(path, segments[2], "datasets")))) return false;
                lastNumber = 3;
            }
        }
        else if (Segment(path, segments[0], "histories"))
        {
            if (count == 2) lastNumber = 1;
            else
            {
                if (count < 4 || !Segment(path, segments[2], "versions")) return false;
                if (count == 4) lastNumber = 3;
                else
                {
                    if (count < 6 || !Segment(path, segments[4], "changes")) return false;
                    if (count == 7 && !(Segment(path, segments[6], "resource") || Segment(path, segments[6], "previous"))) return false;
                    if (count == 8 && !(Segment(path, segments[6], "previous") && Segment(path, segments[7], "seal"))) return false;
                    lastNumber = 5;
                }
            }
        }
        else return false;

        for (int i = 1; i <= lastNumber; i += 2)
        {
            ReadOnlySpan<char> number = path.AsSpan()[segments[i]];
            if (number.IsEmpty || number.Length > 1 && number[0] == '0' ||
                !long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)) return false;
        }
        (start, length) = segments[lastNumber].GetOffsetAndLength(path.Length);
        return true;
    }

    private static bool Segment(string path, Range segment, string expected) => path.AsSpan()[segment].SequenceEqual(expected);
    private static string Identity(string document, string path) =>
        FormattableString.Invariant($"{document.Length}:{document}{path.Length}:{path}");
    private static byte[] Hash(string identity) => SHA256.HashData(Encoding.UTF8.GetBytes(identity));

    private static void GuardInputLength(string document, string path)
    {
        if (768L + 2L * (document.Length + (long)path.Length + 32) > MaximumMetadataBytes)
            throw new InvalidDataException("Migration mapping metadata exceeds its budget.");
    }

    private static void GuardRowLengths(string identity, long identityBytes, int kindBytes)
    {
        if (identityBytes != 2L * identity.Length || kindBytes < 0 || kindBytes > 256 || kindBytes % 2 != 0)
            throw new InvalidDataException("Migration mapping identity/kind lengths do not match their bounds.");
    }

    private static MigrationMapping ValidateMapping(string expected, byte[] hash, string identity, string kind, long key)
    {
        if (!string.Equals(expected, identity, StringComparison.Ordinal) || hash.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(hash, Hash(identity)))
            throw new InvalidDataException("A migration identity hash collision or mismatch was detected.");
        return new(expected, kind, key);
    }

    private sealed class Block
    {
        internal Dictionary<string, MigrationMapping?> Mappings { get; } = new(StringComparer.Ordinal);
        internal long MetadataBytes { get; }

        internal Block(string[] identities)
        {
            // Includes null slots, dictionary storage, mapping records and the maximum 128-character kind.
            MetadataBytes = 256L + identities.Sum(identity => 512L + 2L * identity.Length);
            if (MetadataBytes > MaximumMetadataBytes)
                throw new InvalidDataException("Migration mapping metadata exceeds its budget.");
            foreach (string identity in identities)
                if (!Mappings.TryAdd(identity, null)) throw new InvalidDataException("Duplicate migration mapping request.");
        }

        internal void Add(MigrationMapping mapping)
        {
            if (!Mappings.TryGetValue(mapping.SourceIdentity, out var prior) || prior != null)
                throw new InvalidDataException("An unexpected or duplicate migration mapping was returned.");
            Mappings[mapping.SourceIdentity] = mapping;
        }
    }
}
