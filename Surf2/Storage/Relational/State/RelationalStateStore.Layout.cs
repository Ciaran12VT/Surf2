using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    public async Task<StateToken> ImportWorkspaceAsync(SqlConnection connection, SqlTransaction transaction,
        WorkspaceState workspace, Guid publicationId, CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId);
        var copy = StateCopies.Workspace(workspace, new StateBudget(_limits));
        return await ImportUnitAsync(connection, transaction, async () =>
        {
            long key = await InsertAsync(connection, transaction, StateMaps.Workspace, copy, cancellationToken,
                ("ProfileKey", SqlDbType.BigInt, 1L), ("PublicationId", SqlDbType.UniqueIdentifier, publicationId));
            await LayoutAsync(connection, transaction, "WorkspaceSessionKey", key, copy.OpenDocuments, copy.UnloadedResourceIds, cancellationToken);
            return await TokenAsync(connection, transaction, "WorkspaceSession", "WorkspaceSessionKey", key, cancellationToken);
        }, cancellationToken);
    }

    public Task<StateToken> CreateWorkspaceAsync(WorkspaceState workspace, Guid publicationId, CancellationToken cancellationToken = default)
    {
        _session.RejectValidationWrite();
        var copy = StateCopies.Workspace(workspace, new StateBudget(_limits));
        return ReadyAsync((c,t) => ImportWorkspaceAsync(c,t,copy,publicationId,cancellationToken), cancellationToken);
    }

    public Task<StateToken> SaveWorkspaceAsync(WorkspaceState workspace, StateToken expected, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        Expected(expected, publicationId);
        var copy = StateCopies.Workspace(workspace, new StateBudget(_limits));
        return ReadyAsync(async (connection, transaction) =>
        {
            await UpdateAsync(connection, transaction, StateMaps.Workspace, copy, expected, publicationId, cancellationToken);
            await ClearLayoutAsync(connection, transaction, "WorkspaceSessionKey", expected.Key, cancellationToken);
            await LayoutAsync(connection, transaction, "WorkspaceSessionKey", expected.Key, copy.OpenDocuments, copy.UnloadedResourceIds, cancellationToken);
            return await TokenAsync(connection, transaction, "WorkspaceSession", "WorkspaceSessionKey", expected.Key, cancellationToken);
        }, cancellationToken);
    }

    public Task<SelectedState<WorkspaceState>?> ReadWorkspaceAsync(CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            var budget = new StateBudget(_limits);
            var selected = await HeadAsync(connection, transaction, StateMaps.Workspace, null, budget, cancellationToken);
            if (selected == null) return null;
            var (windows, unloaded) = await ReadLayoutAsync(connection, transaction, "WorkspaceSessionKey", selected.Token.Key, budget, cancellationToken);
            foreach (var window in windows) selected.Value.OpenDocuments.Add(window);
            selected.Value.UnloadedResourceIds = unloaded;
            return selected;
        }, cancellationToken);

    public async Task<StateToken> ImportWorkbenchAsync(SqlConnection connection, SqlTransaction transaction,
        WorkbenchAggregate workbench, long sourceOrdinal, Guid publicationId, CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId, sourceOrdinal);
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Workbench(workbench.Workbench, budget);
        var pasted = StateCopies.PastedImages(copy.ActiveDiagramSnapshot, workbench.PastedImages, budget);
        var fallbacks = StateCopies.FallbackImages(copy.ActiveDiagramSnapshot, pasted, workbench.PastedImageFallbacks, budget);
        var token = await ImportUnitAsync(connection, transaction,
            () => InsertWorkbenchAsync(connection, transaction, copy, pasted, fallbacks, sourceOrdinal, publicationId, budget, cancellationToken), cancellationToken);
        return token.WithWarnings(ImageWarnings("Workbench", fallbacks));
    }

    public async Task<StateToken> ImportWorkbenchAsync(SqlConnection connection, SqlTransaction transaction,
        WorkbenchState workbench, long sourceOrdinal, string pastedImageDirectory, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId, sourceOrdinal);
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Workbench(workbench, budget);
        var images = await StateImages.ReadPastedAsync(copy.ActiveDiagramSnapshot, pastedImageDirectory, _limits, budget,
            (id, ct) => DefinitionAssetAsync(connection, transaction, id, budget, ct), cancellationToken);
        var token = await ImportUnitAsync(connection, transaction,
            () => InsertWorkbenchAsync(connection, transaction, copy, images.Pasted, images.Fallbacks, sourceOrdinal, publicationId, budget, cancellationToken), cancellationToken);
        return token.WithWarnings(ImageWarnings("Workbench", images.Fallbacks));
    }

    public Task<StateToken> CreateWorkbenchAsync(WorkbenchAggregate workbench, long sortOrdinal, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        _session.RejectValidationWrite();
        if (sortOrdinal < 0 || publicationId == Guid.Empty) throw new ArgumentException("Invalid ordinal or publication.");
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Workbench(workbench.Workbench, budget);
        var pasted = StateCopies.PastedImages(copy.ActiveDiagramSnapshot, workbench.PastedImages, budget);
        var fallbacks = StateCopies.FallbackImages(copy.ActiveDiagramSnapshot, pasted, workbench.PastedImageFallbacks, budget);
        return ReadyAsync((c,t) => InsertWorkbenchAsync(c,t,copy,pasted,fallbacks,sortOrdinal,publicationId,budget,cancellationToken), cancellationToken);
    }

    private async Task<StateToken> InsertWorkbenchAsync(SqlConnection connection, SqlTransaction transaction,
        WorkbenchState workbench, IReadOnlyDictionary<int, byte[]> pasted, IReadOnlyDictionary<int, PastedImageFallback> fallbacks,
        long ordinal, Guid publication,
        StateBudget budget, CancellationToken ct)
    {
        await BumpAsync(connection, transaction, 2, publication, ct);
        long key = await InsertAsync(connection, transaction, StateMaps.Workbench, workbench, ct,
            ("ProfileKey", SqlDbType.BigInt, 1L), ("SortOrdinal", SqlDbType.BigInt, ordinal),
            ("PublicationId", SqlDbType.UniqueIdentifier, publication));
        await WorkbenchChildrenAsync(connection, transaction, key, workbench, pasted, fallbacks, publication, budget, ct);
        return await TokenAsync(connection, transaction, "Workbench", "WorkbenchKey", key, ct);
    }

    public Task<StateToken> SaveWorkbenchAsync(WorkbenchAggregate workbench, StateToken expected, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        Expected(expected, publicationId);
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Workbench(workbench.Workbench, budget);
        var pasted = StateCopies.PastedImages(copy.ActiveDiagramSnapshot, workbench.PastedImages, budget);
        var fallbacks = StateCopies.FallbackImages(copy.ActiveDiagramSnapshot, pasted, workbench.PastedImageFallbacks, budget);
        return ReadyAsync(async (connection, transaction) =>
        {
            await BumpAsync(connection, transaction, 2, publicationId, cancellationToken);
            await UpdateAsync(connection, transaction, StateMaps.Workbench, copy, expected, publicationId, cancellationToken);
            await ClearLayoutAsync(connection, transaction, "WorkbenchKey", expected.Key, cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM surf.ReferenceConnectionLine WHERE WorkbenchKey=@Key;",
                cancellationToken, Key(expected.Key));
            await WorkbenchChildrenAsync(connection, transaction, expected.Key, copy, pasted, fallbacks, publicationId, budget, cancellationToken);
            return await TokenAsync(connection, transaction, "Workbench", "WorkbenchKey", expected.Key, cancellationToken);
        }, cancellationToken);
    }

    private async Task WorkbenchChildrenAsync(SqlConnection connection, SqlTransaction transaction, long key,
        WorkbenchState workbench, IReadOnlyDictionary<int, byte[]> pasted, IReadOnlyDictionary<int, PastedImageFallback> fallbacks,
        Guid publication, StateBudget budget, CancellationToken ct)
    {
        await LayoutAsync(connection, transaction, "WorkbenchKey", key, workbench.OpenDocuments, workbench.UnloadedResourceIds, ct);
        for (int i = 0; i < workbench.ReferenceConnectionLines.Count; i++)
            await InsertAsync(connection, transaction, StateMaps.Connection, workbench.ReferenceConnectionLines[i], ct,
                ("WorkbenchKey", SqlDbType.BigInt, key), ("SortOrdinal", SqlDbType.BigInt, (long)i));
        long? revision = workbench.ActiveDiagramSnapshot == null ? null : await WriteDiagramRevisionAsync(connection, transaction,
            workbench.ActiveDiagramSnapshot, pasted, fallbacks, null, key, publication, budget, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE surf.Workbench SET EmbeddedDiagramRevisionKey=@Revision WHERE WorkbenchKey=@Key;", ct,
            Key(key), RelationalSession.Parameter("@Revision", SqlDbType.BigInt, revision));
        await ResolveWorkbenchScopeCoreAsync(connection, transaction, key, workbench.ScopeId, ct);
    }

    public Task<SelectedState<WorkbenchAggregate>?> ReadWorkbenchAsync(long workbenchKey, CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            var budget = new StateBudget(_limits);
            var selected = await HeadAsync(connection, transaction, StateMaps.Workbench, workbenchKey, budget, cancellationToken);
            if (selected == null) return null;
            var (windows, unloaded) = await ReadLayoutAsync(connection, transaction, "WorkbenchKey", workbenchKey, budget, cancellationToken);
            selected.Value.OpenDocuments = windows;
            selected.Value.UnloadedResourceIds = unloaded;
            selected.Value.ReferenceConnectionLines = (await ChildrenAsync(connection, transaction, StateMaps.Connection,
                "WorkbenchKey", workbenchKey, budget, cancellationToken)).Select(c => c.Value).ToList();
            long? revision;
            await using (var command = Command(connection, transaction,
                "SELECT EmbeddedDiagramRevisionKey FROM surf.Workbench WHERE WorkbenchKey=@Key;", Key(workbenchKey)))
            {
                using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
                object? value = await command.ExecuteScalarAsync(cancellationToken);
                revision = value is long number ? number : null;
            }
            IReadOnlyDictionary<int, byte[]> pasted = new Dictionary<int, byte[]>();
            IReadOnlyDictionary<int, PastedImageFallback>? fallbacks = null;
            if (revision.HasValue)
            {
                var diagram = await ReadRevisionAsync(connection, transaction, revision.Value, budget, cancellationToken);
                selected.Value.ActiveDiagramSnapshot = diagram.Document;
                pasted = diagram.PastedImages;
                fallbacks = diagram.PastedImageFallbacks;
            }
            return new SelectedState<WorkbenchAggregate>(new(selected.Value, pasted, fallbacks), selected.Token);
        }, cancellationToken);

    private async Task LayoutAsync(SqlConnection connection, SqlTransaction transaction, string ownerColumn, long owner,
        IEnumerable<OpenDocumentState> windows, IEnumerable<string> unloaded, CancellationToken ct)
    {
        long ordinal = 0;
        foreach (var window in windows)
        {
            long key = await InsertAsync(connection, transaction, StateMaps.Window, window, ct,
                (ownerColumn, SqlDbType.BigInt, owner), ("SortOrdinal", SqlDbType.BigInt, ordinal++));
            long filterOrdinal = 0;
            foreach (var filter in window.SpreadsheetFilters)
                await ExecuteAsync(connection, transaction, """
INSERT surf.DocumentWindowFilter(DocumentWindowKey, SortOrdinal, ColumnIndex, FilterText) VALUES (@Key, @Ordinal, @Column, @Text);
""", ct, Key(key), Key(filterOrdinal++, "@Ordinal"), RelationalSession.Parameter("@Column", SqlDbType.Int, filter.Key), Text(filter.Value, "@Text"));
        }
        ordinal = 0;
        foreach (var resource in unloaded)
            await ExecuteAsync(connection, transaction,
                $"INSERT surf.WorkspaceUnloadedResource([{ownerColumn}], SortOrdinal, ResourceId) VALUES (@Key, @Ordinal, @Id);",
                ct, Key(owner), Key(ordinal++, "@Ordinal"), Text(resource, "@Id"));
    }

    private Task ClearLayoutAsync(SqlConnection connection, SqlTransaction transaction, string ownerColumn,
        long owner, CancellationToken ct) => ExecuteAsync(connection, transaction, $"""
DELETE f FROM surf.DocumentWindowFilter f JOIN surf.DocumentWindowState w ON w.DocumentWindowKey=f.DocumentWindowKey WHERE w.[{ownerColumn}]=@Key;
DELETE FROM surf.DocumentWindowState WHERE [{ownerColumn}]=@Key;
DELETE FROM surf.WorkspaceUnloadedResource WHERE [{ownerColumn}]=@Key;
""", ct, Key(owner));

    private async Task<(List<OpenDocumentState> Windows, List<string> Unloaded)> ReadLayoutAsync(SqlConnection connection,
        SqlTransaction transaction, string ownerColumn, long owner, StateBudget budget, CancellationToken ct)
    {
        var windows = new List<OpenDocumentState>();
        foreach (var (key, window) in await ChildrenAsync(connection, transaction, StateMaps.Window, ownerColumn, owner, budget, ct))
        {
            await using var command = Command(connection, transaction,
                "SELECT TOP (@Limit) ColumnIndex, CONVERT(bigint, DATALENGTH(FilterText)), FilterText FROM surf.DocumentWindowFilter WHERE DocumentWindowKey=@Key ORDER BY SortOrdinal;",
                Key(key), Key((long)_limits.MaximumRows + 1, "@Limit"));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                budget.Row();
                int column = reader.GetInt32(0);
                window.SpreadsheetFilters.Add(column, (await StringAsync(reader, 1, budget, ct))!);
            }
            windows.Add(window);
        }
        var unloaded = new List<string>();
        await using (var command = Command(connection, transaction,
            $"SELECT TOP (@Limit) CONVERT(bigint, DATALENGTH(ResourceId)), ResourceId FROM surf.WorkspaceUnloadedResource WHERE [{ownerColumn}]=@Key ORDER BY SortOrdinal;",
            Key(owner), Key((long)_limits.MaximumRows + 1, "@Limit")))
        {
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                budget.Row();
                unloaded.Add((await StringAsync(reader, 0, budget, ct))!);
            }
        }
        return (windows, unloaded);
    }
}
