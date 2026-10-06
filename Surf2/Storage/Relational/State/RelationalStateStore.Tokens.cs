using System.Data;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    // Recovery probes need neither selected content nor a catalogue-wide search.
    public Task<StateToken?> ReadScopeTokenAsync(long key, CancellationToken ct = default) =>
        ReadOwnerTokenAsync("Scope", "ScopeKey", key, ct);
    public Task<StateToken?> ReadDiagramTokenAsync(long key, CancellationToken ct = default) =>
        ReadOwnerTokenAsync("Diagram", "DiagramKey", key, ct);
    public Task<StateToken?> ReadWorkbenchTokenAsync(long key, CancellationToken ct = default) =>
        ReadOwnerTokenAsync("Workbench", "WorkbenchKey", key, ct);
    public Task<StateToken?> ReadWorkspaceTokenAsync(CancellationToken ct = default) =>
        ReadOwnerTokenAsync("WorkspaceSession", "WorkspaceSessionKey", null, ct);
    public Task<StateToken?> ReadSettingsTokenAsync(CancellationToken ct = default) =>
        ReadOwnerTokenAsync("ApplicationPreference", "ProfileKey", 1, ct);
    public Task<StateToken?> ReadScopeSelectionTokenAsync(CancellationToken ct = default) =>
        ReadOwnerTokenAsync("ScopeCatalogueState", "ProfileKey", 1, ct);

    private Task<StateToken?> ReadOwnerTokenAsync(string table, string keyColumn, long? key, CancellationToken ct) =>
        ReadyAsync(async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction,
                $"SELECT [{keyColumn}], Version, PublicationId FROM surf.[{table}] WHERE ProfileKey=1" +
                (key.HasValue ? $" AND [{keyColumn}]=@Key;" : ";"), key.HasValue ? [Key(key.Value)] : []);
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new StateToken(reader.GetInt64(0), _session.Epoch, (byte[])reader.GetValue(1), reader.GetGuid(2));
        }, ct);
}
