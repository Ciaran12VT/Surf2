using System.Data;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services.RelationalDocuments;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    private sealed record LinkRepairColumn(string Table, string Key, string Column, string From,
        string DiagramOwner = "NULL", string WorkbenchOwner = "NULL", string WorkspaceOwner = "NULL",
        string TargetState = "0", string SnapshotBinding = "NULL", string ResourceBinding = "NULL");
    private sealed record LinkRepairRow(long Key, string Link, long? DiagramOwner, long? WorkbenchOwner,
        long? WorkspaceOwner, SavedDocumentTargetState TargetState, long? SnapshotBinding, long? ResourceBinding);

    /// <summary>
    /// Repair only session-address reference fields already persisted in this database. Exact numeric
    /// owner/resource/kind matches are required; unresolved values remain available for manual repair.
    /// The transaction changes no captured SQL definitions, rows, or snapshot history.
    /// </summary>
    public Task<DocumentLinkRepairResult> RepairStoredDocumentLinksAsync(CancellationToken ct = default)
    {
        _session.RejectValidationWrite();
        return ReadyAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, """
DECLARE @Result int;
EXEC @Result=sys.sp_getapplock @Resource=N'Surf2.DurableDocumentLinkRepair',
 @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
IF @Result<0 THROW 51000,'Could not acquire the saved document link repair lock.',1;
""", ct);
            long inspected = 0, repaired = 0, unresolved = 0;
            var diagrams = new HashSet<long>();
            var workbenches = new HashSet<long>();
            var workspaces = new HashSet<long>();
            var mappings = new Dictionary<(long, long, DatabaseVersionedResourceKind), string?>();
            LinkRepairColumn[] columns =
            [
                new("DiagramObject", "DiagramObjectKey", "MetadataLink",
                    "surf.DiagramObject s JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=s.DiagramRevisionKey",
                    DiagramOwner: "r.DiagramKey", WorkbenchOwner: "r.WorkbenchKey"),
                // Inspect saved window bindings before their paths are rewritten. Conflicting or
                // explicitly unresolved window occurrences must not redirect the active document.
                new("Workbench", "WorkbenchKey", "ActiveDocumentPath", """
surf.Workbench s OUTER APPLY (
 SELECT COUNT_BIG(*) Matches,MIN(w.TargetState) MinState,MAX(w.TargetState) MaxState,
 MIN(w.BoundSnapshotKey) SnapshotMin,MAX(w.BoundSnapshotKey) SnapshotMax,
 MIN(w.BoundResourceKey) ResourceMin,MAX(w.BoundResourceKey) ResourceMax
 FROM surf.DocumentWindowState w WHERE w.WorkbenchKey=s.WorkbenchKey AND w.FilePath=s.ActiveDocumentPath
) binding
""", WorkbenchOwner: "s.WorkbenchKey", TargetState: """
CASE WHEN binding.Matches=0 THEN 0
 WHEN binding.MinState<>binding.MaxState OR binding.SnapshotMin<>binding.SnapshotMax
 OR binding.ResourceMin<>binding.ResourceMax THEN 3 ELSE binding.MinState END
""", SnapshotBinding: "binding.SnapshotMin", ResourceBinding: "binding.ResourceMin"),
                new("DocumentWindowState", "DocumentWindowKey", "FilePath", "surf.DocumentWindowState s",
                    WorkbenchOwner: "s.WorkbenchKey", WorkspaceOwner: "s.WorkspaceSessionKey", TargetState: "s.TargetState",
                    SnapshotBinding: "s.BoundSnapshotKey", ResourceBinding: "s.BoundResourceKey"),
                new("ReferenceConnectionLine", "ReferenceConnectionLineKey", "SourceFilePath", "surf.ReferenceConnectionLine s",
                    WorkbenchOwner: "s.WorkbenchKey", TargetState: "s.SourceTargetState",
                    SnapshotBinding: "s.SourceBoundSnapshotKey", ResourceBinding: "s.SourceBoundResourceKey"),
                new("ReferenceConnectionLine", "ReferenceConnectionLineKey", "TargetFilePath", "surf.ReferenceConnectionLine s",
                    WorkbenchOwner: "s.WorkbenchKey", TargetState: "s.TargetTargetState",
                    SnapshotBinding: "s.TargetBoundSnapshotKey", ResourceBinding: "s.TargetBoundResourceKey")
            ];
            foreach (var column in columns)
            {
                long after = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = await ReadLinkRepairPageAsync(connection, transaction, column, after, ct);
                    if (rows.Count == 0) break;
                    foreach (var row in rows)
                    {
                        after = row.Key;
                        if (!await HasTemporaryDocumentAddressAsync(row.Link)) continue;
                        inspected++;
                        if (row.TargetState is SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous)
                        { unresolved++; continue; }
                        string normalized = await NormalizeStoredLinkAsync(connection, transaction, row.Link,
                            allowPreviousSession: true, preserveUnresolved: true, row.SnapshotBinding, row.ResourceBinding, ct, mappings);
                        if (normalized == row.Link) { unresolved++; continue; }
                        await ExecuteAsync(connection, transaction,
                            $"UPDATE surf.[{column.Table}] SET [{column.Column}]=@Link WHERE [{column.Key}]=@Key;",
                            ct, Key(row.Key), Text(normalized, "@Link"));
                        repaired++;
                        if (row.DiagramOwner.HasValue) diagrams.Add(row.DiagramOwner.Value);
                        if (row.WorkbenchOwner.HasValue) workbenches.Add(row.WorkbenchOwner.Value);
                        if (row.WorkspaceOwner.HasValue) workspaces.Add(row.WorkspaceOwner.Value);
                    }
                }
            }
            if (repaired > 0)
            {
                Guid publication = Guid.NewGuid();
                await PublishLinkRepairOwnersAsync(connection, transaction, "Diagram", "DiagramKey", diagrams, publication, ct);
                await PublishLinkRepairOwnersAsync(connection, transaction, "Workbench", "WorkbenchKey", workbenches, publication, ct);
                await PublishLinkRepairOwnersAsync(connection, transaction, "WorkspaceSession", "WorkspaceSessionKey", workspaces, publication, ct);
                if (diagrams.Count > 0) await BumpAsync(connection, transaction, 1, publication, ct);
                if (workbenches.Count > 0) await BumpAsync(connection, transaction, 2, publication, ct);
            }
            return new DocumentLinkRepairResult(inspected, repaired, unresolved);
        }, ct);
    }

    private async Task<List<LinkRepairRow>> ReadLinkRepairPageAsync(SqlConnection connection, SqlTransaction transaction,
        LinkRepairColumn column, long after, CancellationToken ct)
    {
        // Column/table expressions are the fixed declarations above; user link text is never SQL.
        await using var command = Command(connection, transaction, $"""
SELECT TOP(128) s.[{column.Key}],{column.DiagramOwner},{column.WorkbenchOwner},{column.WorkspaceOwner},
 {column.TargetState},{column.SnapshotBinding},{column.ResourceBinding},
 CONVERT(bigint,DATALENGTH(s.[{column.Column}])),s.[{column.Column}]
FROM {column.From}
WHERE s.[{column.Key}]>@After
 AND (s.[{column.Column}] LIKE N'%db://@%' OR s.[{column.Column}] LIKE N'%surf-search:%')
ORDER BY s.[{column.Key}];
""", Key(after, "@After"));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var rows = new List<LinkRepairRow>();
        var budget = new StateBudget(_limits);
        long retainedBytes = 0;
        while (await reader.ReadAsync(ct))
        {
            long key = reader.GetInt64(0);
            long? diagram = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            long? workbench = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            long? workspace = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            var state = (SavedDocumentTargetState)reader.GetInt32(4);
            long? snapshot = reader.IsDBNull(5) ? null : reader.GetInt64(5);
            long? resource = reader.IsDBNull(6) ? null : reader.GetInt64(6);
            long bytes = reader.GetInt64(7);
            if (rows.Count > 0 && bytes > _limits.MaximumAggregateBytes - retainedBytes) break;
            budget.Text(bytes);
            using var text = reader.GetTextReader(8);
            string link = await text.ReadToEndAsync(ct);
            if (checked((long)link.Length * 2) != bytes)
                throw new InvalidDataException("Saved document link length is inconsistent.");
            retainedBytes += bytes;
            rows.Add(new(key, link, diagram, workbench, workspace, state, snapshot, resource));
        }
        return rows;
    }

    private async Task PublishLinkRepairOwnersAsync(SqlConnection connection, SqlTransaction transaction,
        string table, string key, HashSet<long> owners, Guid publication, CancellationToken ct)
    {
        if (owners.Count == 0) return;
        await ExecuteAsync(connection, transaction, $"""
UPDATE s SET PublicationId=@Publication FROM surf.[{table}] s
JOIN OPENJSON(@Owners) WITH ([Key] bigint '$') owners ON owners.[Key]=s.[{key}]
WHERE s.ProfileKey=1;
""", ct, Publication(publication), Text(JsonSerializer.Serialize(owners), "@Owners"));
    }

    private static async Task<bool> HasTemporaryDocumentAddressAsync(string link)
    {
        bool contains = false;
        await DurableDocumentLink.RewriteAsync(link, path =>
        {
            contains |= path.StartsWith("db://@", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(path);
        });
        return contains;
    }
}
