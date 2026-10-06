using System.Data;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Access.State;

/// <summary>Small preference reads and child-targeted writes that preserve every unqueried sibling.</summary>
public sealed class StatePreferenceAccess
{
    private readonly RelationalSession _session;
    private readonly RelationalStateStore _store;
    private readonly RelationalContentStore _content = new();
    private readonly StateLimits _limits;
    public StatePreferenceAccess(RelationalSession session, StateLimits? limits = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session)); _limits = limits ?? new(); _limits.Validate();
        if (session.IsMigrationValidation) throw new ArgumentException("Runtime preference access cannot use a migration-validation session.", nameof(session));
        _store = new(session, _content, _limits);
    }
    public Guid Epoch => _session.Epoch;

    public Task<StateLoad<StateEditSession<StartupPreferences>>> LoadStartupPreferencesAsync(CancellationToken ct = default) =>
        SelectedStateAccess.LoadSelectedAsync("Preferences", 1, Epoch, async token =>
        {
            var summary = await _store.ReadPreferenceSummaryAsync(token).ConfigureAwait(false);
            return summary == null ? null : new SelectedState<StartupPreferences>(StartupPreferences.From(summary), summary.Token);
        }, CopyPreferences, SavePreferencesAsync, _store.ReadSettingsTokenAsync, ct);

    public Task<StateLoad<StateEditSession<ReferenceHighlightStyleSetting>>> LoadStyleAsync(long key, CancellationToken ct = default) =>
        LoadChildAsync("ReferenceStyle", key, StateMaps.Style, _store.ReadSettingsTokenAsync, ct);
    public Task<StateLoad<StateEditSession<ExtensionBackcolorSetting>>> LoadExtensionAppearanceAsync(long key, CancellationToken ct = default) =>
        LoadChildAsync("ExtensionAppearance", key, StateMaps.Extension, _store.ReadSettingsTokenAsync, ct);

    public Task<StateLoad<StateEditSession<DiagramImageDefinition>>> LoadImageAsync(long key, CancellationToken ct = default)
    {
        Positive(key);
        return SelectedStateAccess.LoadSelectedAsync("ImageDefinition", key, Epoch, token => ReadyAsync(async (c, t) =>
        {
            var image = await ReadImageAsync(c, t, key, null, token);
            return image == null ? null : new SelectedState<DiagramImageDefinition>(image.Definition, image.Owner);
        }, token), value => StateMaps.Image.Copy(value, new(_limits)),
            (value, owner, publication, token) => SaveImageAsync(key, value, owner, publication, token), _store.ReadSettingsTokenAsync, ct);
    }

    public Task<StateLoad<SelectedImageAsset>> ReadImageAssetAsync(long key, StateToken expectedOwner, CancellationToken ct = default)
    {
        Positive(key); Owner(expectedOwner);
        return LoadValueAsync(() => ReadyAsync(async (c, t) =>
        {
            var image = await ReadImageAsync(c, t, key, expectedOwner, ct);
            return image?.Bytes == null ? null : new SelectedImageAsset(key, image.Owner, image.MediaType!, image.Bytes);
        }, ct), ct);
    }

    public Task<StateLoad<PreferencePage<ImageDefinitionSummary>>> ListImagesAsync(int pageSize = 100,
        PreferenceCursor<ImageDefinitionSummary>? after = null, CancellationToken ct = default) =>
        ChildPageAsync(StateMaps.Image, pageSize, after, """
s.SortOrder,CONVERT(bit,CASE WHEN s.AssetKey IS NULL THEN 0 ELSE 1 END),
CONVERT(bigint,DATALENGTH(s.Id)),s.Id,CONVERT(bigint,DATALENGTH(s.Name)),s.Name,
CONVERT(bigint,DATALENGTH(s.ResourceTypeFilter)),s.ResourceTypeFilter
""", async (r, key, ordinal, budget, token) =>
        {
            int order = r.GetInt32(2); bool available = r.GetBoolean(3);
            string id = (await StateText.ReadAsync(r, 4, budget, token))!;
            string name = (await StateText.ReadAsync(r, 6, budget, token))!;
            string filter = (await StateText.ReadAsync(r, 8, budget, token))!;
            return new ImageDefinitionSummary(key, ordinal, id, name, order, filter, available);
        }, ct);

    public Task<StateLoad<PreferencePage<ReferenceStyleSummary>>> ListStylesAsync(int pageSize = 100,
        PreferenceCursor<ReferenceStyleSummary>? after = null, CancellationToken ct = default) =>
        ChildPageAsync(StateMaps.Style, pageSize, after, """
s.Kind,s.IsBold,s.IsItalic,s.IsUnderline,CONVERT(bigint,DATALENGTH(s.Language)),s.Language,
CONVERT(bigint,DATALENGTH(s.Foreground)),s.Foreground
""", async (r, key, ordinal, budget, token) =>
        {
            var kind = (ReferenceEntityKind)r.GetInt32(2);
            if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown reference style kind.");
            bool bold = r.GetBoolean(3), italic = r.GetBoolean(4), underline = r.GetBoolean(5);
            string language = (await StateText.ReadAsync(r, 6, budget, token))!;
            string foreground = (await StateText.ReadAsync(r, 8, budget, token))!;
            return new ReferenceStyleSummary(key, ordinal, language, kind, foreground, bold, italic, underline);
        }, ct);

    public Task<StateLoad<PreferencePage<ExtensionAppearanceSummary>>> ListExtensionAppearanceAsync(int pageSize = 100,
        PreferenceCursor<ExtensionAppearanceSummary>? after = null, CancellationToken ct = default) =>
        ChildPageAsync(StateMaps.Extension, pageSize, after, """
CONVERT(bigint,DATALENGTH(s.Extension)),s.Extension,CONVERT(bigint,DATALENGTH(s.Backcolor)),s.Backcolor,
CONVERT(bigint,DATALENGTH(s.Language)),s.Language
""", async (r, key, ordinal, budget, token) => new ExtensionAppearanceSummary(key, ordinal,
            (await StateText.ReadAsync(r, 2, budget, token))!, (await StateText.ReadAsync(r, 4, budget, token))!,
            (await StateText.ReadAsync(r, 6, budget, token))!), ct);

    // Same Updated/Saved/Created fallback and stable source-order tie as MainWindow's existing startup choice.
    public Task<StateLoad<WorkbenchSummary>> ReadMostRecentWorkbenchSummaryAsync(CancellationToken ct = default) =>
        LoadValueAsync(() => ReadyAsync(async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, """
SELECT TOP(1) s.WorkbenchKey,s.SortOrdinal,s.Version,s.PublicationId,s.IsDefaultForScope,s.CreatedAtUtc,s.UpdatedAtUtc,s.SavedAtUtc,s.EmbeddedDiagramRevisionKey,
CONVERT(bigint,DATALENGTH(s.WorkbenchId)),s.WorkbenchId,CONVERT(bigint,DATALENGTH(s.Name)),s.Name,
CONVERT(bigint,DATALENGTH(s.ScopeId)),s.ScopeId,CONVERT(bigint,DATALENGTH(s.ScopeName)),s.ScopeName
FROM surf.Workbench s WHERE s.ProfileKey=1
ORDER BY CASE WHEN s.UpdatedAtUtc<>@Unset THEN s.UpdatedAtUtc WHEN s.SavedAtUtc<>@Unset THEN s.SavedAtUtc ELSE s.CreatedAtUtc END DESC,
s.SortOrdinal,s.WorkbenchKey;
""", RelationalSession.Parameter("@Unset", SqlDbType.DateTimeOffset, default(DateTimeOffset)));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (!await reader.ReadAsync(ct)) return null;
            long key = reader.GetInt64(0), ordinal = reader.GetInt64(1);
            var owner = new StateToken(key, Epoch, (byte[])reader.GetValue(2), reader.GetGuid(3));
            bool isDefault = reader.GetBoolean(4);
            var created = reader.GetFieldValue<DateTimeOffset>(5); var updated = reader.GetFieldValue<DateTimeOffset>(6); var saved = reader.GetFieldValue<DateTimeOffset>(7);
            long? embedded = reader.IsDBNull(8) ? null : reader.GetInt64(8);
            var budget = new StateBudget(_limits); budget.Row();
            string id = (await StateText.ReadAsync(reader, 9, budget, ct))!;
            string name = (await StateText.ReadAsync(reader, 11, budget, ct))!;
            string scopeId = (await StateText.ReadAsync(reader, 13, budget, ct))!;
            string scopeName = (await StateText.ReadAsync(reader, 15, budget, ct))!;
            return new WorkbenchSummary(owner, ordinal, id, name, scopeId, scopeName, isDefault, created, updated, saved, embedded);
        }, ct), ct);

    private StartupPreferences CopyPreferences(StartupPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value.Input);
        _ = StateMaps.Settings.Copy(value.HeadModel(), new(_limits));
        return value with { Input = value.Input with { } };
    }

    private Task<StateLoad<StateEditSession<T>>> LoadChildAsync<T>(string kind, long key, StateRowMap<T> map,
        Func<CancellationToken, Task<StateToken?>> probe, CancellationToken ct) where T : class, new()
    {
        Positive(key);
        return SelectedStateAccess.LoadSelectedAsync(kind, key, Epoch, token => ReadyAsync(async (c, t) =>
        {
            var owner = await ReadOwnerAsync(c, t, token);
            if (owner == null) return null;
            await using var command = Command(c, t, $"SELECT {map.Projection()} FROM surf.[{map.Table}] s WHERE s.ProfileKey=1 AND s.[{map.Key}]=@Key;", Key(key));
            using var cancel = RelationalSession.CancelCommand(command, token);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            return await reader.ReadAsync(token) ? new SelectedState<T>(await map.ReadAsync(reader, 0, new(_limits), token), owner) : null;
        }, token), value => map.Copy(value, new(_limits)),
            (value, expected, publication, token) => SaveChildAsync(map, key, value, expected, publication, token), probe, ct);
    }

    private Task<StateLoad<PreferencePage<T>>> ChildPageAsync<T, TModel>(StateRowMap<TModel> map, int size, PreferenceCursor<T>? after,
        string projection, Func<SqlDataReader, long, long, StateBudget, CancellationToken, Task<T>> read, CancellationToken ct) where TModel : new()
    {
        if (size < 1 || size > _limits.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(size));
        if (after != null) Owner(after.Owner);
        return LoadValueAsync(() => ReadyAsync(async (c, t) =>
        {
            var owner = await ReadOwnerAsync(c, t, ct);
            if (owner == null) return null;
            if (after != null && !StateEditSession<StartupPreferences>.SameToken(owner, after.Owner)) throw new StateConflictException("preference catalogue generation");
            await using var command = Command(c, t, $"""
SELECT TOP (@Take) s.[{map.Key}],s.SortOrdinal,{projection} FROM surf.[{map.Table}] s
WHERE s.ProfileKey=1 AND (s.SortOrdinal>@Ordinal OR (s.SortOrdinal=@Ordinal AND s.[{map.Key}]>@Key))
ORDER BY s.SortOrdinal,s.[{map.Key}];
""", Key((long)size + 1, "@Take"), Key(after?.Ordinal ?? -1, "@Ordinal"), Key(after?.Key ?? 0));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            var items = new List<T>(); var budget = new StateBudget(_limits);
            PreferenceCursor<T>? last = null;
            while (await reader.ReadAsync(ct))
            {
                if (items.Count == size) return new PreferencePage<T>(owner, Array.AsReadOnly(items.ToArray()), last);
                budget.Row(); long key = reader.GetInt64(0), ordinal = reader.GetInt64(1);
                items.Add(await read(reader, key, ordinal, budget, ct)); last = new(owner, ordinal, key);
            }
            return new PreferencePage<T>(owner, Array.AsReadOnly(items.ToArray()), null);
        }, ct), ct);
    }

    private sealed record ImageRead(DiagramImageDefinition Definition, StateToken Owner, byte[]? Bytes, string? MediaType);
    private async Task<ImageRead?> ReadImageAsync(SqlConnection c, SqlTransaction t, long key, StateToken? expected, CancellationToken ct)
    {
        var owner = await ReadOwnerAsync(c, t, ct);
        if (owner == null) return null;
        if (expected != null && !StateEditSession<StartupPreferences>.SameToken(owner, expected)) throw new StateConflictException("image definition owner");
        var budget = new StateBudget(_limits); DiagramImageDefinition image; long? asset;
        await using (var command = Command(c, t, $"SELECT {StateMaps.Image.Projection()},s.AssetKey FROM surf.DiagramImageDefinition s WHERE s.ProfileKey=1 AND s.DiagramImageDefinitionKey=@Key;", Key(key)))
        {
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (!await reader.ReadAsync(ct)) return null;
            image = await StateMaps.Image.ReadAsync(reader, 0, budget, ct);
            int assetOrdinal = StateMaps.Image.Fields.Sum(f => f.Type == SqlDbType.NVarChar ? 2 : 1);
            asset = reader.IsDBNull(assetOrdinal) ? null : reader.GetInt64(assetOrdinal);
        }
        byte[]? inline = StateImages.Decode(image.ImageDataBase64, _limits, budget, ct);
        if (inline == null)
        {
            if (asset.HasValue) throw new InvalidDataException("A selected image asset has no original inline data.");
            return new(image, owner, null, null);
        }
        if (!asset.HasValue) throw new InvalidDataException("The selected image's asset is missing; saving is disabled until it is resolved.");
        await using var bytesCommand = Command(c, t, """
SELECT ByteCount,ContentHash,CONVERT(bigint,DATALENGTH(MediaType)),MediaType,CONVERT(bigint,DATALENGTH(Bytes)),Bytes
FROM surf.Asset WHERE AssetKey=@Key;
""", Key(asset.Value));
        using var bytesCancel = RelationalSession.CancelCommand(bytesCommand, ct);
        await using var bytesReader = await bytesCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        if (!await bytesReader.ReadAsync(ct)) throw new InvalidDataException("The selected image asset does not exist.");
        long declared = bytesReader.GetInt64(0); byte[] hash = bytesReader.GetFieldValue<byte[]>(1);
        string media = (await StateText.ReadAsync(bytesReader, 2, budget, ct))!;
        long length = bytesReader.GetInt64(4);
        if (length != declared || length != inline.LongLength) throw new InvalidDataException("Selected image byte lengths disagree.");
        budget.Asset(length); var bytes = new byte[checked((int)length)];
        await using var stream = bytesReader.GetStream(5); await stream.ReadExactlyAsync(bytes, ct);
        if (await stream.ReadAsync(new byte[1], ct) != 0 || !bytes.AsSpan().SequenceEqual(inline) || !SHA256.HashData(bytes).AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("Selected image bytes, base64 or hash disagree.");
        return new(image, owner, bytes, media);
    }

    private Task<StateToken> SavePreferencesAsync(StartupPreferences value, StateToken expected, Guid publication, CancellationToken ct)
    {
        WriteOwner(expected, publication); var model = CopyPreferences(value).HeadModel();
        return ReadyAsync(async (c, t) =>
        {
            await using var command = Command(c, t, $"""
UPDATE surf.ApplicationPreference SET {StateMaps.Settings.Assignments},PublicationId=@Publication
OUTPUT INSERTED.ProfileKey
WHERE ProfileKey=1 AND Version=@Version;
""", Version(expected), Publication(publication));
            await StateMaps.Settings.AddParametersAsync(command, model, _content, c, t, ct);
            using var cancel = RelationalSession.CancelCommand(command, ct);
            if (await command.ExecuteScalarAsync(ct) is not long) throw new StateConflictException("preferences");
            return (await ReadOwnerAsync(c, t, ct))!;
        }, ct);
    }

    private Task<StateToken> SaveChildAsync<T>(StateRowMap<T> map, long key, T value, StateToken expected, Guid publication, CancellationToken ct) where T : new()
    {
        WriteOwner(expected, publication); var copy = map.Copy(value, new(_limits));
        return ReadyAsync(async (c, t) =>
        {
            await BumpOwnerAsync(c, t, expected, publication, ct);
            await UpdateChildAsync(c, t, map, key, copy, ct);
            return (await ReadOwnerAsync(c, t, ct))!;
        }, ct);
    }

    private Task<StateToken> SaveImageAsync(long key, DiagramImageDefinition value, StateToken expected, Guid publication, CancellationToken ct)
    {
        WriteOwner(expected, publication); var budget = new StateBudget(_limits);
        var copy = StateMaps.Image.Copy(value, budget);
        byte[]? bytes = StateImages.Decode(copy.ImageDataBase64, _limits, budget, ct);
        return ReadyAsync(async (c, t) =>
        {
            await BumpOwnerAsync(c, t, expected, publication, ct);
            long? asset = bytes == null ? null : await _content.PutAssetAsync(c, t, bytes, StateImages.Validate(bytes, _limits, cancellationToken: ct), ct);
            await UpdateChildAsync(c, t, StateMaps.Image, key, copy, ct);
            await ExecuteAsync(c, t, "UPDATE surf.DiagramImageDefinition SET AssetKey=@Asset WHERE ProfileKey=1 AND DiagramImageDefinitionKey=@Key;", ct,
                Key(key), RelationalSession.Parameter("@Asset", SqlDbType.BigInt, asset));
            return (await ReadOwnerAsync(c, t, ct))!;
        }, ct);
    }

    private async Task UpdateChildAsync<T>(SqlConnection c, SqlTransaction t, StateRowMap<T> map, long key, T value, CancellationToken ct) where T : new()
    {
        await using var command = Command(c, t, $"UPDATE surf.[{map.Table}] SET {map.Assignments} OUTPUT INSERTED.[{map.Key}] WHERE ProfileKey=1 AND [{map.Key}]=@Key;", Key(key));
        await map.AddParametersAsync(command, value, _content, c, t, ct);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        if (await command.ExecuteScalarAsync(ct) is not long) throw new StateConflictException(map.Table);
    }
    private async Task BumpOwnerAsync(SqlConnection c, SqlTransaction t, StateToken expected, Guid publication, CancellationToken ct)
    {
        await using var command = Command(c, t, "UPDATE surf.ApplicationPreference SET PublicationId=@Publication OUTPUT INSERTED.ProfileKey WHERE ProfileKey=1 AND Version=@Version;", Version(expected), Publication(publication));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        if (await command.ExecuteScalarAsync(ct) is not long) throw new StateConflictException("preference owner");
    }
    private async Task<StateToken?> ReadOwnerAsync(SqlConnection c, SqlTransaction t, CancellationToken ct)
    {
        await using var command = Command(c, t, "SELECT Version,PublicationId FROM surf.ApplicationPreference WHERE ProfileKey=1;");
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new StateToken(1, Epoch, (byte[])reader.GetValue(0), reader.GetGuid(1)) : null;
    }
    private static async Task<StateLoad<T>> LoadValueAsync<T>(Func<Task<T?>> read, CancellationToken ct) where T : class
    {
        try
        {
            ct.ThrowIfCancellationRequested(); var value = await read().ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            return value == null ? StateLoad<T>.Missing() : StateLoad<T>.Ready(value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return StateLoad<T>.Cancelled(); }
        catch (Exception error) { return StateLoad<T>.Failed(error); }
    }
    private async Task<T> ReadyAsync<T>(Func<SqlConnection, SqlTransaction, Task<T>> operation, CancellationToken ct)
    {
        await _session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var c = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        T result = await operation(c, t).ConfigureAwait(false); await t.CommitAsync(ct).ConfigureAwait(false); return result;
    }
    private void Owner(StateToken token)
    { if (token.Key != 1 || token.Epoch != Epoch) throw new ArgumentException("The preference owner token belongs to another owner/epoch."); }
    private void WriteOwner(StateToken token, Guid publication)
    { _session.RejectValidationWrite(); Owner(token); if (publication == Guid.Empty) throw new ArgumentException("A publication identity is required."); }
    private static void Positive(long key) { if (key <= 0) throw new ArgumentOutOfRangeException(nameof(key)); }
    private static SqlParameter Key(long key, string name = "@Key") => RelationalSession.Parameter(name, SqlDbType.BigInt, key);
    private static SqlParameter Version(StateToken owner) => RelationalSession.Parameter("@Version", SqlDbType.Binary, owner.Version, 8);
    private static SqlParameter Publication(Guid publication) => RelationalSession.Parameter("@Publication", SqlDbType.UniqueIdentifier, publication);
    private static SqlCommand Command(SqlConnection c, SqlTransaction t, string sql, params SqlParameter[] parameters)
    { var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql; command.Parameters.AddRange(parameters); return command; }
    private static async Task ExecuteAsync(SqlConnection c, SqlTransaction t, string sql, CancellationToken ct, params SqlParameter[] parameters)
    { await using var command = Command(c, t, sql, parameters); using var cancel = RelationalSession.CancelCommand(command, ct); await command.ExecuteNonQueryAsync(ct); }
}
