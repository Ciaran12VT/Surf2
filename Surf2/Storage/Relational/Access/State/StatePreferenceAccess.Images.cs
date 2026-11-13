using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Access.State;

public sealed partial class StatePreferenceAccess
{
    // The settings grid needs its visible text columns, but never the full image payload.
    public Task<StateLoad<PreferencePage<ImageDefinitionSummary>>> ListImageSettingsAsync(int pageSize = 100,
        PreferenceCursor<ImageDefinitionSummary>? after = null, CancellationToken ct = default) =>
        ChildPageAsync(StateMaps.Image, pageSize, after, """
CONVERT(bit,CASE WHEN s.AssetKey IS NULL THEN 0 ELSE 1 END),
CONVERT(bigint,DATALENGTH(s.Id)),s.Id,CONVERT(bigint,DATALENGTH(s.Name)),s.Name,
CONVERT(bigint,DATALENGTH(s.ResourceTypeFilter)),s.ResourceTypeFilter,
CONVERT(bigint,DATALENGTH(nr.Text)),nr.Text,CONVERT(bigint,DATALENGTH(cr.Text)),cr.Text,
CONVERT(bigint,DATALENGTH(lr.Text)),lr.Text,CONVERT(bigint,DATALENGTH(s.MatchTarget)),s.MatchTarget,
CONVERT(bigint,DATALENGTH(s.OriginalFileName)),s.OriginalFileName,
CONVERT(int,(SELECT COUNT_BIG(*) FROM surf.DiagramImageDefinition priorImage WHERE priorImage.ProfileKey=1
AND (priorImage.SortOrdinal<s.SortOrdinal OR (priorImage.SortOrdinal=s.SortOrdinal AND priorImage.DiagramImageDefinitionKey<=s.DiagramImageDefinitionKey)))),
(SELECT COUNT(*) FROM surf.DiagramImageDefinition WHERE ProfileKey=1)
""", async (r, key, ordinal, budget, token) =>
        {
            bool available = r.GetBoolean(2);
            string id = (await StateText.ReadAsync(r, 3, budget, token))!;
            string name = (await StateText.ReadAsync(r, 5, budget, token))!;
            string filter = (await StateText.ReadAsync(r, 7, budget, token))!;
            string nameRegex = await StateText.ReadAsync(r, 9, budget, token) ?? "";
            string contentRegex = await StateText.ReadAsync(r, 11, budget, token) ?? "";
            string legacyRegex = await StateText.ReadAsync(r, 13, budget, token) ?? "";
            string target = (await StateText.ReadAsync(r, 15, budget, token))!;
            string fileName = (await StateText.ReadAsync(r, 17, budget, token))!;
            if (string.IsNullOrWhiteSpace(nameRegex) && string.IsNullOrWhiteSpace(contentRegex))
            {
                if (DiagramImageDefinition.NormalizeMatchTarget(target) == DiagramImageDefinition.ContentMatchTarget) contentRegex = legacyRegex;
                else nameRegex = legacyRegex;
            }
            return new ImageDefinitionSummary(key, ordinal, id, name, r.GetInt32(19), filter, available, nameRegex, contentRegex, fileName, r.GetInt32(20));
        }, ct, """
LEFT JOIN surf.TextContent nr ON nr.ContentKey=s.NameRegexContentKey
LEFT JOIN surf.TextContent cr ON cr.ContentKey=s.ContentRegexContentKey
LEFT JOIN surf.TextContent lr ON lr.ContentKey=s.RegexContentKey
""");

    /// <summary>Reads a bounded set of selected image assets in one query, without loading definition text.</summary>
    public Task<IReadOnlyDictionary<long, SelectedImageAsset>> ReadImageAssetsAsync(IReadOnlyCollection<long> keys,
        StateToken expectedOwner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys); Owner(expectedOwner);
        long[] selected = keys.Distinct().ToArray();
        if (selected.Length > _limits.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(keys));
        foreach (long key in selected) Positive(key);
        return ReadyAsync<IReadOnlyDictionary<long, SelectedImageAsset>>(async (connection, transaction) =>
        {
            var owner = await ReadOwnerAsync(connection, transaction, ct);
            if (owner == null || !StateEditSession<StartupPreferences>.SameToken(owner, expectedOwner))
                throw new StateConflictException("image asset owner");
            var result = new Dictionary<long, SelectedImageAsset>();
            if (selected.Length == 0) return result;
            await using var command = Command(connection, transaction, """
SELECT s.DiagramImageDefinitionKey,a.ByteCount,a.ContentHash,
CONVERT(bigint,DATALENGTH(a.MediaType)),a.MediaType,CONVERT(bigint,DATALENGTH(a.Bytes)),a.Bytes
FROM OPENJSON(@Keys) WITH ([Key] bigint '$') k
JOIN surf.DiagramImageDefinition s ON s.DiagramImageDefinitionKey=k.[Key] AND s.ProfileKey=1
JOIN surf.Asset a ON a.AssetKey=s.AssetKey;
""", RelationalSession.Parameter("@Keys", SqlDbType.NVarChar, JsonSerializer.Serialize(selected), -1));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            var budget = new StateBudget(_limits);
            while (await reader.ReadAsync(ct))
            {
                budget.Row(); long key = reader.GetInt64(0), declared = reader.GetInt64(1);
                byte[] hash = reader.GetFieldValue<byte[]>(2);
                string media = (await StateText.ReadAsync(reader, 3, budget, ct))!;
                long length = reader.GetInt64(5);
                if (length != declared) throw new InvalidDataException("Selected image byte lengths disagree.");
                budget.Asset(length); var bytes = new byte[checked((int)length)];
                await using var stream = reader.GetStream(6);
                await stream.ReadExactlyAsync(bytes, ct);
                if (await stream.ReadAsync(new byte[1], ct) != 0 || !SHA256.HashData(bytes).AsSpan().SequenceEqual(hash))
                    throw new InvalidDataException("Selected image bytes and hash disagree.");
                StateImages.Validate(bytes, _limits, cancellationToken: ct);
                result.Add(key, new SelectedImageAsset(key, owner, media, bytes));
            }
            return result;
        }, ct);
    }
}
