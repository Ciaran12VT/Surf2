using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Surf2.Storage.Relational.Migration;

public static class LegacyProjectionReader
{
    public static async Task<T> HeaderAsync<T>(string filePath, long offset, string[] excludedCollections,
        CancellationToken cancellationToken = default) where T : class
    {
        await using var cursor = LegacySourceStage.OpenAt(filePath, offset);
        if (!await cursor.MoveNextAsync(cancellationToken))
        {
            throw new InvalidDataException("A source entity is missing.");
        }

        JsonElement header = await cursor.ReadHeaderAsync(new HashSet<string>(excludedCollections, StringComparer.Ordinal), cancellationToken);
        return LegacySchemaInspector.Deserialize<T>(header);
    }

    public static async Task ArrayPropertyAsync(string filePath, long offset, string property,
        Func<long, StreamingJsonCursor, CancellationToken, Task> process,
        CancellationToken cancellationToken = default)
    {
        await using var cursor = LegacySourceStage.OpenAt(filePath, offset);
        if (!await cursor.MoveNextAsync(cancellationToken))
        {
            throw new InvalidDataException("A source entity is missing.");
        }

        await cursor.ReadObjectAsync(async (name, value, ct) =>
        {
            if (name == property)
            {
                await value.ReadArrayAsync(process, ct);
            }
            else
            {
                await value.SkipValueAsync(ct);
            }
        }, cancellationToken);
    }

    public static async Task ValuePropertyAsync(string filePath, long offset, string property,
        Func<StreamingJsonCursor, CancellationToken, Task> process,
        CancellationToken cancellationToken = default)
    {
        await using var cursor = LegacySourceStage.OpenAt(filePath, offset);
        if (!await cursor.MoveNextAsync(cancellationToken)) throw new InvalidDataException("A source entity is missing.");
        await cursor.ReadObjectAsync(async (name, value, ct) =>
        {
            if (name == property) await process(value, ct);
            else await value.SkipValueAsync(ct);
        }, cancellationToken);
    }

    public static async IAsyncEnumerable<JsonElement> RowsAsync(string filePath, long offset, long skip = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        await using var cursor = LegacySourceStage.OpenAt(filePath, offset);
        if (!await cursor.MoveNextAsync(cancellationToken))
        {
            throw new InvalidDataException("A source dataset is missing.");
        }

        cursor.Require(JsonTokenType.StartObject);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            if (cursor.TokenType == JsonTokenType.EndObject)
            {
                yield break;
            }

            cursor.Require(JsonTokenType.PropertyName);
            string name = cursor.Value!;
            if (!await cursor.MoveNextAsync(cancellationToken))
            {
                throw new InvalidDataException("A dataset property is incomplete.");
            }

            if (name != "Rows")
            {
                await cursor.SkipValueAsync(cancellationToken);
                continue;
            }

            cursor.Require(JsonTokenType.StartArray);
            long ordinal = 0;
            while (await cursor.MoveNextAsync(cancellationToken) && cursor.TokenType != JsonTokenType.EndArray)
            {
                if (ordinal++ < skip)
                {
                    await cursor.SkipValueAsync(cancellationToken);
                }
                else
                {
                    yield return await cursor.ReadValueAsync(cancellationToken);
                }
            }

            cursor.Require(JsonTokenType.EndArray);
        }

        throw new InvalidDataException("A source dataset is incomplete.");
    }
}
