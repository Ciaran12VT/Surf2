using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    public async Task<StateToken> ImportSettingsAsync(SqlConnection connection, SqlTransaction transaction,
        AppSettings settings, Guid publicationId, CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId);
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Settings(settings, budget);
        return await ImportUnitAsync(connection, transaction, async () =>
        {
            await InsertAsync(connection, transaction, StateMaps.Settings, copy, cancellationToken,
                ("ProfileKey", SqlDbType.BigInt, 1L), ("PublicationId", SqlDbType.UniqueIdentifier, publicationId));
            await SettingsChildrenAsync(connection, transaction, copy, budget, cancellationToken);
            return await TokenAsync(connection, transaction, "ApplicationPreference", "ProfileKey", 1, cancellationToken);
        }, cancellationToken);
    }

    public Task<StateToken> CreateSettingsAsync(AppSettings settings, Guid publicationId, CancellationToken cancellationToken = default)
    {
        _session.RejectValidationWrite();
        var copy = StateCopies.Settings(settings, new StateBudget(_limits));
        return ReadyAsync((c,t) => ImportSettingsAsync(c,t,copy,publicationId,cancellationToken), cancellationToken);
    }

    public Task<StateToken> SaveSettingsAsync(AppSettings settings, StateToken expected, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        Expected(expected, publicationId);
        if (expected.Key != 1) throw new ArgumentException("Invalid profile token.");
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Settings(settings, budget);
        return ReadyAsync(async (connection, transaction) =>
        {
            await UpdateAsync(connection, transaction, StateMaps.Settings, copy, expected, publicationId, cancellationToken);
            await ExecuteAsync(connection, transaction, """
DELETE FROM surf.ExtensionAppearance WHERE ProfileKey=1;
DELETE FROM surf.ReferenceStyle WHERE ProfileKey=1;
DELETE FROM surf.DiagramImageDefinition WHERE ProfileKey=1;
""", cancellationToken);
            await SettingsChildrenAsync(connection, transaction, copy, budget, cancellationToken);
            return await TokenAsync(connection, transaction, "ApplicationPreference", "ProfileKey", 1, cancellationToken);
        }, cancellationToken);
    }

    private async Task SettingsChildrenAsync(SqlConnection connection, SqlTransaction transaction, AppSettings settings,
        StateBudget budget, CancellationToken ct)
    {
        for (int i = 0; i < settings.CodeWindows.BackcolorsByExtension.Count; i++)
            await InsertAsync(connection, transaction, StateMaps.Extension, settings.CodeWindows.BackcolorsByExtension[i], ct,
                ("ProfileKey", SqlDbType.BigInt, 1L), ("SortOrdinal", SqlDbType.BigInt, (long)i));
        for (int i = 0; i < settings.ReferenceHighlights.Styles.Count; i++)
            await InsertAsync(connection, transaction, StateMaps.Style, settings.ReferenceHighlights.Styles[i], ct,
                ("ProfileKey", SqlDbType.BigInt, 1L), ("SortOrdinal", SqlDbType.BigInt, (long)i));
        for (int i = 0; i < settings.DiagramImages.Images.Count; i++)
        {
            var image = settings.DiagramImages.Images[i];
            byte[]? bytes = StateImages.Decode(image.ImageDataBase64, _limits, budget, ct);
            long? asset = bytes == null ? null : await _content.PutAssetAsync(connection, transaction, bytes,
                StateImages.Validate(bytes, _limits, cancellationToken: ct), ct);
            await InsertAsync(connection, transaction, StateMaps.Image, image, ct,
                ("ProfileKey", SqlDbType.BigInt, 1L), ("SortOrdinal", SqlDbType.BigInt, (long)i), ("AssetKey", SqlDbType.BigInt, asset));
        }
    }

    public Task<SelectedState<AppSettings>?> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            var budget = new StateBudget(_limits);
            var selected = await HeadAsync(connection, transaction, StateMaps.Settings, 1, budget, cancellationToken);
            if (selected == null) return null;
            selected.Value.CodeWindows.BackcolorsByExtension = (await ChildrenAsync(connection, transaction, StateMaps.Extension,
                "ProfileKey", 1, budget, cancellationToken)).Select(v => v.Value).ToList();
            selected.Value.ReferenceHighlights.Styles = (await ChildrenAsync(connection, transaction, StateMaps.Style,
                "ProfileKey", 1, budget, cancellationToken)).Select(v => v.Value).ToList();
            foreach (var (key, image) in await ChildrenAsync(connection, transaction, StateMaps.Image, "ProfileKey", 1, budget, cancellationToken))
            {
                long? asset;
                await using (var command = Command(connection, transaction,
                    "SELECT AssetKey FROM surf.DiagramImageDefinition WHERE DiagramImageDefinitionKey=@Key;", Key(key)))
                {
                    using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
                    object? value = await command.ExecuteScalarAsync(cancellationToken);
                    asset = value is long number ? number : null;
                }
                await VerifyInlineAssetAsync(connection, transaction, image.ImageDataBase64, asset, budget, cancellationToken);
                selected.Value.DiagramImages.Images.Add(image);
            }
            return selected;
        }, cancellationToken);
}
