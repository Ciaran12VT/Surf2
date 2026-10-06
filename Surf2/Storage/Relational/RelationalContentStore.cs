using System.Data;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational;

public sealed class RelationalContentStore
{
    // Hash exact SQL nvarchar bytes, including trailing whitespace and line endings.
    public const int MaximumSingleContentBytes = 128 * 1024 * 1024;

    public async Task<long> PutTextAsync(SqlConnection connection, SqlTransaction? transaction,
        string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        long byteCount = checked((long)text.Length * 2);
        if (byteCount > MaximumSingleContentBytes)
        {
            throw new InvalidDataException("A single text value exceeds the supported conversion limit.");
        }

        byte[] bytes = new byte[checked((int)byteCount)];
        long lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), text[i]);
            if (text[i] == '\n') lines++;
        }
        byte[] hash = SHA256.HashData(bytes);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT TOP (1) ContentKey FROM surf.TextContent
WHERE ContentHash = @Hash AND ByteCount = @Bytes
  AND CONVERT(varbinary(max), Text) = @Exact;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Hash", SqlDbType.Binary, hash, 32));
        command.Parameters.Add(RelationalSession.Parameter("@Bytes", SqlDbType.BigInt, byteCount));
        command.Parameters.Add(RelationalSession.Parameter("@Exact", SqlDbType.VarBinary, bytes, -1));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        object? existing = await command.ExecuteScalarAsync(cancellationToken);
        if (existing != null && existing != DBNull.Value)
        {
            return (long)existing;
        }

        command.CommandText = """
INSERT surf.TextContent(ContentHash, HashEncodingVersion, CharacterCount, ByteCount, LineCount, Text)
OUTPUT INSERTED.ContentKey VALUES (@Hash, 1, @Characters, @Bytes, @Lines, CONVERT(nvarchar(max), @Exact));
""";
        command.Parameters.Clear();
        command.Parameters.Add(RelationalSession.Parameter("@Hash", SqlDbType.Binary, hash, 32));
        command.Parameters.Add(RelationalSession.Parameter("@Characters", SqlDbType.BigInt, (long)text.Length));
        command.Parameters.Add(RelationalSession.Parameter("@Bytes", SqlDbType.BigInt, byteCount));
        command.Parameters.Add(RelationalSession.Parameter("@Lines", SqlDbType.BigInt, lines));
        command.Parameters.Add(RelationalSession.Parameter("@Exact", SqlDbType.VarBinary, bytes, -1));
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Text content identity was not returned."));
    }

    public async Task<long> PutAssetAsync(SqlConnection connection, SqlTransaction? transaction,
        byte[] bytes, string mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (bytes.Length > MaximumSingleContentBytes || mediaType.Length > 128)
        {
            throw new InvalidDataException("An asset exceeds the supported conversion limit.");
        }

        byte[] hash = SHA256.HashData(bytes);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT TOP (1) AssetKey FROM surf.Asset
WHERE ContentHash = @Hash AND ByteCount = @Count AND Bytes = @Bytes;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Hash", SqlDbType.Binary, hash, 32));
        command.Parameters.Add(RelationalSession.Parameter("@Count", SqlDbType.BigInt, (long)bytes.Length));
        command.Parameters.Add(RelationalSession.Parameter("@Bytes", SqlDbType.VarBinary, bytes, -1));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        object? existing = await command.ExecuteScalarAsync(cancellationToken);
        if (existing != null && existing != DBNull.Value)
        {
            return (long)existing;
        }

        command.CommandText = """
INSERT surf.Asset(ContentHash, ByteCount, MediaType, Bytes)
OUTPUT INSERTED.AssetKey VALUES (@Hash, @Count, @MediaType, @Bytes);
""";
        command.Parameters.Add(RelationalSession.Parameter("@MediaType", SqlDbType.NVarChar, mediaType, 128));
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Asset identity was not returned."));
    }

    public async Task<string> ReadTextAsync(RelationalSession session, long key, CancellationToken cancellationToken = default)
    {
        await using var connection = await session.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ByteCount, CONVERT(varbinary(max), Text) FROM surf.TextContent WHERE ContentKey = @Key;";
        command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, key));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("The requested text revision is missing.");
        }

        long byteCount = reader.GetInt64(0);
        if (byteCount < 0 || byteCount > MaximumSingleContentBytes || byteCount % 2 != 0)
            throw new InvalidDataException("The selected text exceeds its supported read limit.");
        await using var stream = reader.GetStream(1);
        byte[] bytes = new byte[checked((int)byteCount)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
            throw new InvalidDataException("The selected text length does not match its immutable metadata.");
        char[] chars = new char[bytes.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        }
        return new string(chars);
    }

    public async Task CopyAssetAsync(RelationalSession session, long key, Stream output, CancellationToken cancellationToken = default)
    {
        await using var connection = await session.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Bytes FROM surf.Asset WHERE AssetKey = @Key;";
        command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, key));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("The requested asset is missing.");
        }

        await using var bytes = reader.GetStream(0);
        await bytes.CopyToAsync(output, cancellationToken);
    }
}
