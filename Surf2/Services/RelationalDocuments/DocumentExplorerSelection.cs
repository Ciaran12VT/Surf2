using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalDocuments;

internal static class DocumentExplorerSelection
{
    internal static ExplorerResource SelectedMembership(ExplorerScope scope, ExplorerNodeSummary selected)
    {
        if (selected.IsDirectory || selected.ScopeResourceKey is not > 0 || !selected.IsScopeResourceLoaded ||
            selected.Availability != ExplorerAvailability.Present)
            throw Missing("The selected explorer document is not available in a loaded membership.");
        var member = scope.Resources.SingleOrDefault(r => r.ScopeResourceKey == selected.ScopeResourceKey);
        if (member == null || !member.IsLoaded)
            throw Missing("The selected explorer membership was removed or unloaded.");
        return member;
    }

    internal static ExplorerResource? SnapshotMembership(ExplorerScope scope, long snapshotKey, long? preferredKey)
    {
        if (preferredKey.HasValue)
        {
            var member = scope.Resources.SingleOrDefault(r => r.ScopeResourceKey == preferredKey && r.Snapshot?.SnapshotKey == snapshotKey);
            return member ?? throw Missing("The selected snapshot alias is no longer in this scope.");
        }
        return scope.Resources.FirstOrDefault(r => r.Snapshot?.SnapshotKey == snapshotKey && r.IsLoaded) ??
            scope.Resources.FirstOrDefault(r => r.Snapshot?.SnapshotKey == snapshotKey);
    }

    internal static void ValidateResource(ExplorerNodeSummary selected, ExplorerResource member, SnapshotResourceSummary current)
    {
        if (selected.Role != ExplorerNodeRole.DatabaseDocument || member.Kind != ResourceKind.DatabaseSnapshot ||
            member.Snapshot?.SnapshotKey != selected.SnapshotKey || selected.SnapshotKey != current.SnapshotKey ||
            selected.SnapshotResourceKey != current.ResourceKey)
            throw Missing("The selected resource does not belong to its explorer snapshot and membership.");
        if (selected.SourceRevisionKey.HasValue && selected.SourceRevisionKey != current.RevisionKey)
            throw new SnapshotConcurrencyException();
        bool categoryMatches = selected.Category switch
        {
            ExplorerCategory.Procedures => current.Kind == DatabaseVersionedResourceKind.StoredProcedure,
            ExplorerCategory.Views => current.Kind == DatabaseVersionedResourceKind.View,
            ExplorerCategory.Functions => current.Kind == DatabaseVersionedResourceKind.Function,
            ExplorerCategory.Triggers => current.Kind == DatabaseVersionedResourceKind.Trigger,
            ExplorerCategory.Tables => current.Kind is DatabaseVersionedResourceKind.TableMetadata or DatabaseVersionedResourceKind.TableData,
            _ => false
        };
        if (!categoryMatches) throw Missing("The selected resource is no longer in its explorer category.");
    }

    private static DocumentTargetException Missing(string message) => new(SavedDocumentTargetState.Missing, message);
}
