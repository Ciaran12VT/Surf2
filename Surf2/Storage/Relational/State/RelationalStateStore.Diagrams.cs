using System.Data;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    public async Task<DiagramState> PrepareDiagramImportAsync(SqlConnection connection, SqlTransaction transaction,
        DiagramDocument diagram, string pastedImageDirectory,
        Func<string, CancellationToken, Task<byte[]?>>? frozenPngReader = null, CancellationToken cancellationToken = default)
    {
        if (transaction.Connection != connection || connection.State != ConnectionState.Open)
            throw new ArgumentException("Image preparation requires the caller's open connection and transaction.");
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Diagram(diagram, budget);
        var images = await StateImages.ReadPastedAsync(copy, pastedImageDirectory, _limits, budget,
            (id, ct) => DefinitionAssetAsync(connection, transaction, id, budget, ct), cancellationToken, frozenPngReader);
        return new(copy, images.Pasted, images.Fallbacks);
    }

    public async Task<StateToken> ImportDiagramAsync(SqlConnection connection, SqlTransaction transaction,
        DiagramState diagram, long sourceOrdinal, Guid publicationId, CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId, sourceOrdinal);
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Diagram(diagram.Document, budget);
        var pasted = StateCopies.PastedImages(copy, diagram.PastedImages, budget);
        var fallbacks = StateCopies.FallbackImages(copy, pasted, diagram.PastedImageFallbacks, budget);
        var token = await ImportUnitAsync(connection, transaction,
            () => InsertDiagramAsync(connection, transaction, copy, pasted, fallbacks, sourceOrdinal, publicationId, budget, cancellationToken), cancellationToken);
        return token.WithWarnings(ImageWarnings("Diagram", fallbacks));
    }

    public async Task<StateToken> ImportDiagramAsync(SqlConnection connection, SqlTransaction transaction,
        DiagramDocument diagram, long sourceOrdinal, string pastedImageDirectory, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId, sourceOrdinal);
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Diagram(diagram, budget);
        var images = await StateImages.ReadPastedAsync(copy, pastedImageDirectory, _limits, budget,
            (id, ct) => DefinitionAssetAsync(connection, transaction, id, budget, ct), cancellationToken);
        var token = await ImportUnitAsync(connection, transaction,
            () => InsertDiagramAsync(connection, transaction, copy, images.Pasted, images.Fallbacks, sourceOrdinal, publicationId, budget, cancellationToken), cancellationToken);
        return token.WithWarnings(ImageWarnings("Diagram", images.Fallbacks));
    }

    public Task<StateToken> CreateDiagramAsync(DiagramState diagram, long sortOrdinal, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        _session.RejectValidationWrite();
        if (sortOrdinal < 0 || publicationId == Guid.Empty) throw new ArgumentException("Invalid ordinal or publication.");
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Diagram(diagram.Document, budget);
        var pasted = StateCopies.PastedImages(copy, diagram.PastedImages, budget);
        var fallbacks = StateCopies.FallbackImages(copy, pasted, diagram.PastedImageFallbacks, budget);
        return ReadyAsync((c, t) => InsertDiagramAsync(c, t, copy, pasted, fallbacks, sortOrdinal, publicationId, budget, cancellationToken), cancellationToken);
    }

    private async Task<StateToken> InsertDiagramAsync(SqlConnection connection, SqlTransaction transaction,
        DiagramDocument diagram, IReadOnlyDictionary<int, byte[]> pasted, IReadOnlyDictionary<int, PastedImageFallback> fallbacks,
        long ordinal, Guid publication,
        StateBudget budget, CancellationToken ct)
    {
        await BumpAsync(connection, transaction, 1, publication, ct);
        await using var command = Command(connection, transaction, """
INSERT surf.Diagram(ProfileKey, DiagramId, Name, SortOrdinal, PublicationId)
OUTPUT INSERTED.DiagramKey VALUES (1, @Id, @Name, @Ordinal, @Publication);
""", Text(diagram.DiagramId, "@Id"), Text(diagram.Name, "@Name"), Key(ordinal, "@Ordinal"), Publication(publication));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        long key = (long)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidDataException("Missing diagram identity."));
        long revision = await WriteDiagramRevisionAsync(connection, transaction, diagram, pasted, fallbacks, key, null, publication, budget, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE surf.Diagram SET CurrentRevisionKey=@Revision WHERE DiagramKey=@Key;", ct, Key(key), Key(revision, "@Revision"));
        return await TokenAsync(connection, transaction, "Diagram", "DiagramKey", key, ct);
    }

    public Task<StateToken> SaveDiagramAsync(DiagramState diagram, StateToken expected, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        Expected(expected, publicationId);
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Diagram(diagram.Document, budget);
        var pasted = StateCopies.PastedImages(copy, diagram.PastedImages, budget);
        var fallbacks = StateCopies.FallbackImages(copy, pasted, diagram.PastedImageFallbacks, budget);
        return ReadyAsync(async (connection, transaction) =>
        {
            await BumpAsync(connection, transaction, 1, publicationId, cancellationToken);
            await using (var command = Command(connection, transaction, """
UPDATE surf.Diagram SET DiagramId=@Id, Name=@Name, PublicationId=@Publication
OUTPUT INSERTED.DiagramKey
WHERE DiagramKey=@Key AND ProfileKey=1 AND Version=@Version;
""", Key(expected.Key), Text(copy.DiagramId, "@Id"), Text(copy.Name, "@Name"), Publication(publicationId),
                RelationalSession.Parameter("@Version", SqlDbType.Binary, expected.Version, 8)))
            {
                using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
                if (await command.ExecuteScalarAsync(cancellationToken) is not long) throw new StateConflictException("diagram");
            }
            long revision = await WriteDiagramRevisionAsync(connection, transaction, copy, pasted, fallbacks, expected.Key,
                null, publicationId, budget, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "UPDATE surf.Diagram SET CurrentRevisionKey=@Revision WHERE DiagramKey=@Key;", cancellationToken,
                Key(expected.Key), Key(revision, "@Revision"));
            return await TokenAsync(connection, transaction, "Diagram", "DiagramKey", expected.Key, cancellationToken);
        }, cancellationToken);
    }

    private async Task<long> WriteDiagramRevisionAsync(SqlConnection connection, SqlTransaction transaction,
        DiagramDocument diagram, IReadOnlyDictionary<int, byte[]> pasted, IReadOnlyDictionary<int, PastedImageFallback> fallbacks,
        long? diagramKey, long? workbenchKey,
        Guid publication, StateBudget budget, CancellationToken ct)
    {
        await NormalizeDiagramDocumentLinksAsync(connection, transaction, diagram, diagramKey, workbenchKey, ct);
        long revision = await InsertAsync(connection, transaction, StateMaps.Diagram, diagram, ct,
            ("DiagramKey", SqlDbType.BigInt, diagramKey), ("WorkbenchKey", SqlDbType.BigInt, workbenchKey),
            ("Origin", SqlDbType.Int, diagramKey.HasValue ? 0 : 1), ("PublicationId", SqlDbType.UniqueIdentifier, publication));
        var objectKeys = new List<long>();
        var workflowKeys = new List<long>();
        var itemKeys = new List<long[]>();
        for (int i = 0; i < diagram.Objects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = diagram.Objects[i];
            byte[]? inline = StateImages.Decode(item.ImageDataBase64, _limits, budget, ct);
            long? inlineKey = inline == null ? null : await _content.PutAssetAsync(connection, transaction, inline,
                StateImages.Validate(inline, _limits, cancellationToken: ct), ct);
            long? pastedKey = null;
            long? fallbackKey = null;
            var resolution = PastedImageResolution.None;
            if (!string.IsNullOrWhiteSpace(item.PastedImageFileName))
            {
                if (pasted.TryGetValue(i, out byte[]? bytes))
                {
                    resolution = PastedImageResolution.CapturedLocal;
                    pastedKey = await _content.PutAssetAsync(connection, transaction, bytes, StateImages.Validate(bytes, _limits, true, ct), ct);
                }
                else if (fallbacks.TryGetValue(i, out var fallback))
                {
                    resolution = fallback.Resolution;
                    if (resolution == PastedImageResolution.MissingUseInline && (inline == null || !inline.AsSpan().SequenceEqual(fallback.Bytes)))
                        throw new InvalidDataException("Pasted fallback disagrees with the object's inline bytes.");
                    if (resolution == PastedImageResolution.MissingUseDefinition && string.IsNullOrWhiteSpace(item.ImageDefinitionId))
                        throw new InvalidDataException("Pasted definition fallback has no original definition ID.");
                    fallbackKey = await _content.PutAssetAsync(connection, transaction, fallback.Bytes,
                        StateImages.Validate(fallback.Bytes, _limits, cancellationToken: ct), ct);
                }
                else throw new InvalidDataException($"Missing pasted image or fallback at ordinal {i}.");
            }
            long key = await InsertAsync(connection, transaction, StateMaps.Object, item, ct,
                ("DiagramRevisionKey", SqlDbType.BigInt, revision), ("SortOrdinal", SqlDbType.BigInt, (long)i),
                ("ImageAssetKey", SqlDbType.BigInt, inlineKey), ("PastedAssetKey", SqlDbType.BigInt, pastedKey),
                ("PastedImageResolution", SqlDbType.Int, (int)resolution), ("PastedFallbackAssetKey", SqlDbType.BigInt, fallbackKey));
            objectKeys.Add(key);
            for (int q = 0; q < item.Metadata.Queries.Count; q++)
                await InsertAsync(connection, transaction, StateMaps.Query, item.Metadata.Queries[q], ct,
                    ("DiagramRevisionKey", SqlDbType.BigInt, revision), ("DiagramObjectKey", SqlDbType.BigInt, key),
                    ("SortOrdinal", SqlDbType.BigInt, (long)q));
        }
        for (int w = 0; w < diagram.Workflows.Count; w++)
        {
            var workflow = diagram.Workflows[w];
            long workflowKey = await InsertAsync(connection, transaction, StateMaps.Workflow, workflow, ct,
                ("DiagramRevisionKey", SqlDbType.BigInt, revision), ("SortOrdinal", SqlDbType.BigInt, (long)w));
            workflowKeys.Add(workflowKey);
            var keys = new long[workflow.Items.Count];
            itemKeys.Add(keys);
            for (int i = 0; i < workflow.Items.Count; i++)
            {
                var item = workflow.Items[i];
                long itemKey = await InsertAsync(connection, transaction, StateMaps.Item, item, ct,
                    ("DiagramRevisionKey", SqlDbType.BigInt, revision), ("WorkflowKey", SqlDbType.BigInt, workflowKey),
                    ("SortOrdinal", SqlDbType.BigInt, (long)i));
                keys[i] = itemKey;
                for (int q = 0; q < item.Queries.Count; q++)
                    await InsertAsync(connection, transaction, StateMaps.Query, item.Queries[q], ct,
                        ("DiagramRevisionKey", SqlDbType.BigInt, revision), ("WorkflowItemKey", SqlDbType.BigInt, itemKey),
                        ("SortOrdinal", SqlDbType.BigInt, (long)q));
            }
        }
        await WriteRevisionRelationshipsAsync(connection, transaction, diagram, revision, objectKeys, workflowKeys, itemKeys, ct);
        return revision;
    }

    public Task<SelectedState<DiagramState>?> ReadDiagramAsync(long diagramKey, CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            var budget = new StateBudget(_limits);
            StateToken token;
            long revision;
            await using (var command = Command(connection, transaction,
                "SELECT Version, PublicationId, CurrentRevisionKey FROM surf.Diagram WHERE DiagramKey=@Key AND ProfileKey=1;", Key(diagramKey)))
            {
                using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return null;
                token = new(diagramKey, _session.Epoch, (byte[])reader.GetValue(0), reader.GetGuid(1));
                if (reader.IsDBNull(2)) throw new InvalidDataException("A published diagram has no revision.");
                revision = reader.GetInt64(2);
            }
            return new SelectedState<DiagramState>(await ReadRevisionAsync(connection, transaction, revision, budget, cancellationToken), token);
        }, cancellationToken);

    public Task<DiagramState> ReadDiagramRevisionAsync(long revisionKey, CancellationToken cancellationToken = default) =>
        ReadyAsync((c,t) => ReadRevisionAsync(c,t,revisionKey,new StateBudget(_limits),cancellationToken), cancellationToken);

    private async Task<DiagramState> ReadRevisionAsync(SqlConnection connection, SqlTransaction transaction, long revision,
        StateBudget budget, CancellationToken ct)
    {
        DiagramDocument diagram;
        await using (var command = Command(connection, transaction,
            $"SELECT {StateMaps.Diagram.Projection()} FROM surf.DiagramRevision s WHERE s.DiagramRevisionKey=@Key;", Key(revision)))
        {
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("The requested diagram revision is missing.");
            diagram = await StateMaps.Diagram.ReadAsync(reader, 0, budget, ct);
        }
        var objects = await ChildrenAsync(connection, transaction, StateMaps.Object, "DiagramRevisionKey", revision, budget, ct);
        var images = await ReadDiagramAssetsAsync(connection, transaction, revision, budget, ct);
        var objectQueries = await ReadRevisionChildrenAsync(connection, transaction, StateMaps.Query,
            StateMaps.Object.Table, StateMaps.Object.Key, revision, budget, ct);
        var pasted = new Dictionary<int, byte[]>();
        var fallbacks = new Dictionary<int, PastedImageFallback>();
        for (int i = 0; i < objects.Count; i++)
        {
            var (key, value) = objects[i];
            if (!images.Bindings.TryGetValue(key, out var binding)) throw new InvalidDataException("Missing diagram object.");
            var (inlineKey, pastedKey, resolution, fallbackKey) = binding;
            if (inlineKey.HasValue) value.ImageDataBase64 = StateImages.RestoreBase64(value.ImageDataBase64, images.Bytes[inlineKey.Value]);
            else if (!string.IsNullOrWhiteSpace(value.ImageDataBase64)) throw new InvalidDataException("An inline image asset is unresolved.");
            if (pastedKey.HasValue)
            {
                if (string.IsNullOrWhiteSpace(value.PastedImageFileName) || resolution != PastedImageResolution.CapturedLocal || fallbackKey.HasValue)
                    throw new InvalidDataException("Pasted asset resolution is inconsistent.");
                byte[] bytes = images.Bytes[pastedKey.Value];
                StateImages.Validate(bytes, _limits, true, ct);
                pasted.Add(i, bytes);
            }
            else if (fallbackKey.HasValue)
            {
                if (string.IsNullOrWhiteSpace(value.PastedImageFileName) ||
                    resolution is not (PastedImageResolution.MissingUseInline or PastedImageResolution.MissingUseDefinition))
                    throw new InvalidDataException("Pasted fallback resolution is inconsistent.");
                byte[] bytes = images.Bytes[fallbackKey.Value];
                StateImages.Validate(bytes, _limits, cancellationToken: ct);
                if (resolution == PastedImageResolution.MissingUseInline &&
                    !Convert.FromBase64String(value.ImageDataBase64).AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("Inline fallback and source image disagree.");
                if (resolution == PastedImageResolution.MissingUseDefinition && string.IsNullOrWhiteSpace(value.ImageDefinitionId))
                    throw new InvalidDataException("Definition fallback has no original definition ID.");
                fallbacks.Add(i, new(resolution, bytes));
            }
            else if (!string.IsNullOrWhiteSpace(value.PastedImageFileName) || resolution != PastedImageResolution.None)
                throw new InvalidDataException($"Unresolved pasted image at ordinal {i}.");
            value.Metadata.Queries = objectQueries.TryGetValue(key, out var queries)
                ? queries.Select(query => query.Value).ToList() : [];
            diagram.Objects.Add(value);
        }
        var workflows = await ChildrenAsync(connection, transaction, StateMaps.Workflow, "DiagramRevisionKey", revision, budget, ct);
        var workflowItems = await ReadRevisionChildrenAsync(connection, transaction, StateMaps.Item,
            StateMaps.Workflow.Table, StateMaps.Workflow.Key, revision, budget, ct);
        var workflowQueries = await ReadRevisionChildrenAsync(connection, transaction, StateMaps.Query,
            StateMaps.Item.Table, StateMaps.Item.Key, revision, budget, ct);
        foreach (var (key, value) in workflows)
        {
            if (workflowItems.TryGetValue(key, out var items))
            {
                foreach (var (itemKey, _, item) in items)
                {
                    item.Queries = workflowQueries.TryGetValue(itemKey, out var queries)
                        ? queries.Select(query => query.Value).ToList() : [];
                    value.Items.Add(item);
                }
            }
            diagram.Workflows.Add(value);
        }
        return new(diagram, pasted, fallbacks);
    }

    private async Task<Dictionary<long, List<(long Key, long Ordinal, T Value)>>> ReadRevisionChildrenAsync<T>(
        SqlConnection connection, SqlTransaction transaction, StateRowMap<T> map, string parentTable,
        string parentKey, long revision, StateBudget budget, CancellationToken ct) where T : new()
    {
        // The revision indexes select parents and the owner indexes select each parent's children.
        // Sort the bounded groups in memory so SQL can stream the text projections without a sort.
        await using var command = Command(connection, transaction, $"""
SELECT TOP (@Limit) s.[{parentKey}],s.[{map.Key}],s.SortOrdinal,{map.Projection()}
FROM surf.[{parentTable}] p WITH (FORCESEEK)
JOIN surf.[{map.Table}] s WITH (FORCESEEK) ON s.[{parentKey}]=p.[{parentKey}]
WHERE p.DiagramRevisionKey=@Revision AND s.[{parentKey}] IS NOT NULL
OPTION (LOOP JOIN, FORCE ORDER);
""", Key(revision, "@Revision"), RelationalSession.Parameter("@Limit", SqlDbType.BigInt, (long)_limits.MaximumRows + 1));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var groups = new Dictionary<long, List<(long Key, long Ordinal, T Value)>>();
        while (await reader.ReadAsync(ct))
        {
            long owner = reader.GetInt64(0), key = reader.GetInt64(1), ordinal = reader.GetInt64(2);
            var value = await map.ReadAsync(reader, 3, budget, ct);
            if (!groups.TryGetValue(owner, out var rows)) groups.Add(owner, rows = []);
            rows.Add((key, ordinal, value));
        }
        foreach (var rows in groups.Values)
        {
            ct.ThrowIfCancellationRequested();
            rows.Sort(static (left, right) =>
            {
                int order = left.Ordinal.CompareTo(right.Ordinal);
                return order != 0 ? order : left.Key.CompareTo(right.Key);
            });
        }
        return groups;
    }

    private sealed record DiagramImageBinding(long? Inline, long? Pasted, PastedImageResolution Resolution, long? Fallback);
    private sealed record DiagramAssets(Dictionary<long, DiagramImageBinding> Bindings, Dictionary<long, byte[]> Bytes);

    private async Task<DiagramAssets> ReadDiagramAssetsAsync(SqlConnection connection, SqlTransaction transaction,
        long revision, StateBudget budget, CancellationToken ct)
    {
        var bindings = new Dictionary<long, DiagramImageBinding>();
        var assets = new Dictionary<long, byte[]>();
        await using var command = Command(connection, transaction, """
SELECT DiagramObjectKey,ImageAssetKey,PastedAssetKey,PastedImageResolution,PastedFallbackAssetKey
FROM surf.DiagramObject WHERE DiagramRevisionKey=@Revision;
SELECT a.AssetKey,a.ByteCount,a.ContentHash,CONVERT(bigint,DATALENGTH(a.Bytes)),a.Bytes
FROM surf.Asset a
WHERE a.AssetKey IN (
    SELECT ImageAssetKey FROM surf.DiagramObject WHERE DiagramRevisionKey=@Revision
    UNION SELECT PastedAssetKey FROM surf.DiagramObject WHERE DiagramRevisionKey=@Revision
    UNION SELECT PastedFallbackAssetKey FROM surf.DiagramObject WHERE DiagramRevisionKey=@Revision);
""", Key(revision, "@Revision"));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
            bindings.Add(reader.GetInt64(0), new(reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2), (PastedImageResolution)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long key = reader.GetInt64(0), length = reader.GetInt64(1);
            byte[] hash = reader.GetFieldValue<byte[]>(2);
            if (length != reader.GetInt64(3)) throw new InvalidDataException("Asset length is inconsistent.");
            budget.Asset(length);
            byte[] bytes = new byte[checked((int)length)];
            await using var stream = reader.GetStream(4);
            await stream.ReadExactlyAsync(bytes, ct);
            if (await stream.ReadAsync(new byte[1], ct) != 0 || !SHA256.HashData(bytes).AsSpan().SequenceEqual(hash))
                throw new InvalidDataException("Stored image bytes do not match their immutable identity.");
            StateImages.Validate(bytes, _limits, cancellationToken: ct);
            assets.Add(key, bytes);
        }
        return new(bindings, assets);
    }

    private async Task<byte[]> ReadAssetAsync(SqlConnection connection, SqlTransaction transaction, long key,
        StateBudget budget, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT ByteCount, DATALENGTH(Bytes), Bytes FROM surf.Asset WHERE AssetKey=@Key;", Key(key));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidDataException("Missing state asset.");
        long length = reader.GetInt64(0);
        if (length != reader.GetInt64(1)) throw new InvalidDataException("Asset length is inconsistent.");
        budget.Asset(length);
        var bytes = new byte[checked((int)length)];
        await using var stream = reader.GetStream(2);
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private async Task<string> RestoreInlineAssetAsync(SqlConnection connection, SqlTransaction transaction, string encoded,
        long? key, StateBudget budget, CancellationToken ct)
    {
        if (!key.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(encoded)) throw new InvalidDataException("An inline image asset is unresolved.");
            return encoded;
        }
        byte[] bytes = await ReadAssetAsync(connection, transaction, key.Value, budget, ct);
        StateImages.Validate(bytes, _limits, cancellationToken: ct);
        return StateImages.RestoreBase64(encoded, bytes);
    }

    private async Task<byte[]?> DefinitionAssetAsync(SqlConnection connection, SqlTransaction transaction, string id,
        StateBudget budget, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        long? asset = null;
        // Preserve legacy first-match/OrdinalIgnoreCase semantics, including duplicate definition IDs.
        await using (var command = Command(connection, transaction,
            "SELECT TOP (@Limit) CONVERT(bigint, DATALENGTH(Id)), Id, AssetKey FROM surf.DiagramImageDefinition WHERE ProfileKey=1 ORDER BY SortOrdinal;",
            Key((long)_limits.MaximumRows + 1, "@Limit")))
        {
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            var lookupBudget = new StateBudget(_limits);
            while (await reader.ReadAsync(ct))
            {
                lookupBudget.Row();
                string candidate = (await StringAsync(reader, 0, lookupBudget, ct))!;
                if (!string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase)) continue;
                asset = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                break;
            }
        }
        return asset.HasValue ? await ReadAssetAsync(connection, transaction, asset.Value, budget, ct) : null;
    }

    private static IReadOnlyList<StateImportWarning> ImageWarnings(string kind, IReadOnlyDictionary<int, PastedImageFallback> images) =>
        images.Select(pair => new StateImportWarning(kind, pair.Key,
            pair.Value.Resolution == PastedImageResolution.MissingUseInline ? "MissingPastedImageUsedInline" : "MissingPastedImageUsedDefinition")).ToArray();
}
