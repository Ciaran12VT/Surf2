using Surf2.Models;

namespace Surf2.Storage.Relational.State;

internal sealed record LocalWorkflowBinding(int ObjectOrdinal, int? WorkflowOrdinal, int? ItemOrdinal,
    StateLinkResolution WorkflowResolution, StateLinkResolution ItemResolution);
internal sealed record LocalItemMarker(int WorkflowOrdinal, int ItemOrdinal, int? ObjectOrdinal, StateLinkResolution Resolution);
internal sealed record LocalPortalTarget(int ObjectOrdinal, int? TargetObjectOrdinal, string? LogicalDiagramId, StateLinkResolution Resolution);
internal sealed record LocalFolderLink(int FolderOrdinal, int? MemberOrdinal, int? TargetOrdinal, int? ResourceOrdinal, StateLinkResolution Resolution);

// Only this selected graph supplies candidates. Duplicate raw IDs never acquire an arbitrary FK.
internal static class StateRelationshipResolver
{
    private const string FolderPrefix = "surf2://virtual-folder/";
    private sealed class IdIndex
    {
        private readonly Dictionary<string, int?> _ids = new(StringComparer.OrdinalIgnoreCase);
        public IdIndex(IEnumerable<string> ids)
        {
            int ordinal = 0;
            foreach (string id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id) && !_ids.TryAdd(id, ordinal)) _ids[id] = null;
                ordinal++;
            }
        }
        public (int? Ordinal, StateLinkResolution Resolution) Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return (null, StateLinkResolution.None);
            if (!_ids.TryGetValue(id, out int? ordinal)) return (null, StateLinkResolution.Missing);
            return ordinal.HasValue ? (ordinal, StateLinkResolution.Resolved) : (null, StateLinkResolution.Ambiguous);
        }
    }

    public static (List<LocalWorkflowBinding> Bindings, List<LocalItemMarker> Markers) Workflows(DiagramDocument diagram)
    {
        var workflows = new IdIndex(diagram.Workflows.Select(w => w.WorkflowId));
        var items = diagram.Workflows.Select(w => new IdIndex(w.Items.Select(i => i.WorkflowItemId))).ToArray();
        var objects = new IdIndex(diagram.Objects.Select(o => o.Id));
        var bindings = new List<LocalWorkflowBinding>();
        var byObject = new Dictionary<int, LocalWorkflowBinding>();
        for (int o = 0; o < diagram.Objects.Count; o++)
        {
            var value = diagram.Objects[o];
            if (value.ObjectType != DiagramObjectType.WorkflowMarker && string.IsNullOrWhiteSpace(value.WorkflowId) &&
                string.IsNullOrWhiteSpace(value.WorkflowItemId)) continue;
            var (workflow, workflowStatus) = workflows.Find(value.WorkflowId);
            int? item = null;
            StateLinkResolution itemStatus;
            if (value.ObjectType != DiagramObjectType.WorkflowMarker)
            {
                workflow = null;
                workflowStatus = StateLinkResolution.ContextMismatch;
                itemStatus = StateLinkResolution.ContextMismatch;
            }
            else if (workflow.HasValue)
                (item, itemStatus) = items[workflow.Value].Find(value.WorkflowItemId);
            else
                itemStatus = string.IsNullOrWhiteSpace(value.WorkflowItemId) ? StateLinkResolution.None :
                    workflowStatus == StateLinkResolution.Ambiguous ? StateLinkResolution.Ambiguous : StateLinkResolution.Missing;
            if (value.ObjectType == DiagramObjectType.WorkflowMarker)
            {
                if (workflowStatus == StateLinkResolution.None) workflowStatus = StateLinkResolution.Missing;
                if (itemStatus == StateLinkResolution.None) itemStatus = StateLinkResolution.Missing;
            }
            var binding = new LocalWorkflowBinding(o, workflow, item, workflowStatus, itemStatus);
            bindings.Add(binding);
            byObject.Add(o, binding);
        }
        var markers = new List<LocalItemMarker>();
        for (int w = 0; w < diagram.Workflows.Count; w++)
            for (int i = 0; i < diagram.Workflows[w].Items.Count; i++)
            {
                var (target, status) = objects.Find(diagram.Workflows[w].Items[i].MarkerDiagramObjectId);
                if (target.HasValue)
                {
                    if (!byObject.TryGetValue(target.Value, out var binding) || binding.WorkflowOrdinal != w || binding.ItemOrdinal != i ||
                        binding.WorkflowResolution != StateLinkResolution.Resolved || binding.ItemResolution != StateLinkResolution.Resolved)
                    {
                        target = null;
                        status = binding?.WorkflowResolution == StateLinkResolution.Ambiguous || binding?.ItemResolution == StateLinkResolution.Ambiguous
                            ? StateLinkResolution.Ambiguous : StateLinkResolution.ContextMismatch;
                    }
                }
                markers.Add(new(w, i, target, status));
            }
        return (bindings, markers);
    }

    public static List<LocalPortalTarget> Portals(DiagramDocument diagram)
    {
        var objects = new IdIndex(diagram.Objects.Select(o => o.Id));
        var targets = new List<LocalPortalTarget>();
        for (int o = 0; o < diagram.Objects.Count; o++)
        {
            var value = diagram.Objects[o];
            if (value.ObjectType != DiagramObjectType.Portal && string.IsNullOrWhiteSpace(value.PairedPortalDiagramId) &&
                string.IsNullOrWhiteSpace(value.PairedPortalObjectId)) continue;
            if (value.ObjectType != DiagramObjectType.Portal)
                targets.Add(new(o, null, null, StateLinkResolution.ContextMismatch));
            else if (string.IsNullOrWhiteSpace(value.PairedPortalDiagramId) && string.IsNullOrWhiteSpace(value.PairedPortalObjectId))
                targets.Add(new(o, null, null, StateLinkResolution.None));
            else if (string.IsNullOrWhiteSpace(value.PairedPortalDiagramId) || string.IsNullOrWhiteSpace(value.PairedPortalObjectId))
                targets.Add(new(o, null, null, StateLinkResolution.Missing));
            else if (string.Equals(value.PairedPortalDiagramId, diagram.DiagramId, StringComparison.OrdinalIgnoreCase))
            {
                var (target, status) = objects.Find(value.PairedPortalObjectId);
                if (target.HasValue && diagram.Objects[target.Value].ObjectType != DiagramObjectType.Portal)
                {
                    target = null;
                    status = StateLinkResolution.ContextMismatch;
                }
                targets.Add(new(o, target, null, status));
            }
            else targets.Add(new(o, null, value.PairedPortalDiagramId, StateLinkResolution.Missing));
        }
        return targets;
    }

    public static List<LocalFolderLink> Folders(Scope scope)
    {
        // Match the model's literal node-key format without initializing FileSystemNode's WPF geometry cache.
        var folders = new IdIndex(scope.VirtualFolders.Select(f => FolderPrefix + f.VirtualFolderId));
        var resources = new IdIndex(scope.Resources.Select(r => r.Kind == ResourceKind.Folder ? r.Path : ""));
        var result = new List<LocalFolderLink>();
        for (int f = 0; f < scope.VirtualFolders.Count; f++)
        {
            var folder = scope.VirtualFolders[f];
            Add(folder.ParentNodeKey, null);
            for (int m = 0; m < folder.ChildNodeKeys.Count; m++) Add(folder.ChildNodeKeys[m], m);
            void Add(string raw, int? member)
            {
                var (target, status) = raw.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase)
                    ? folders.Find(raw) : (null, StateLinkResolution.None);
                int? resource = null;
                if (!raw.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(raw, "__root__", StringComparison.OrdinalIgnoreCase))
                {
                    (resource, status) = resources.Find(raw);
                    if (status == StateLinkResolution.Missing) status = StateLinkResolution.None;
                }
                if (target == f) { target = null; status = StateLinkResolution.ContextMismatch; }
                result.Add(new(f, member, target, resource, status));
            }
        }
        return result;
    }
}
