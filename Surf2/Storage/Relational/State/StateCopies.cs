using System.IO;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

// Capture mutable UI models before the first await. Do not call legacy Clone/EnsureDefaults: they normalize data.
internal static class StateCopies
{
    public static Scope Scope(Scope source, StateBudget budget)
    {
        source.EnsureResourceContainers();
        foreach(var container in source.ResourceContainers)
        {
            var members=source.Resources.Where(r=>r.ContainerId==container.ContainerId).Take(2).ToArray();
            if(members is not [var member])continue;
            // Keep singleton aliases working as before. A shared container name is independent
            // of which alternative is active; unloaded snapshot metadata must not replace a
            // previously resolved friendly label with its raw snapshot identifier.
            if(!string.IsNullOrWhiteSpace(member.DisplayNameOverride) ||
                member.Kind is ResourceKind.File or ResourceKind.Folder or ResourceKind.LiveDatabase ||
                member.Kind==ResourceKind.DatabaseSnapshot&&member.SnapshotDisplayName!=null)
                container.Name=member.DisplayName;
        }
        var value = StateMaps.Scope.Copy(source, budget);
        foreach (var resource in source.Resources)
        {
            var copy = StateMaps.Resource.Copy(resource, budget);
            PortableResourcePath.Validate(copy.Path);
            copy.Source = resource.Source?.Copy();
            if (copy.Source == null && copy.Kind is ResourceKind.Folder or ResourceKind.File)
            {
                resource.Source = new ResourceSourceDefinition { Endpoint = resource.Path };
                copy.Source = resource.Source.Copy();
            }
            else if(copy.Source?.Provider==SourceProvider.LocalFileSystem && copy.Source.Endpoint!=copy.Path)
            {
                // A concrete member can be rebound independently of copies in other scopes.
                copy.Source.Endpoint=copy.Path;copy.Source.SourceId=Guid.NewGuid();copy.Source.ConfigurationRevision=1;
                resource.Source=copy.Source.Copy();
            }
            value.Resources.Add(copy);
        }
        foreach (var container in source.ResourceContainers)
            value.ResourceContainers.Add(new ResourceContainer { ContainerId=container.ContainerId,
                Name=String(container.Name,budget), DefaultResourceId=container.DefaultResourceId });
        foreach (var folder in source.VirtualFolders)
        {
            var copy = StateMaps.Folder.Copy(folder, budget);
            PortableResourcePath.Validate(copy.ParentNodeKey);
            foreach (string member in folder.ChildNodeKeys) { PortableResourcePath.Validate(member); copy.ChildNodeKeys.Add(String(member, budget)); }
            value.VirtualFolders.Add(copy);
        }
        return value;
    }

    public static DiagramDocument Diagram(DiagramDocument source, StateBudget budget)
    {
        var value = StateMaps.Diagram.Copy(source, budget);
        foreach (var item in source.Objects)
        {
            var copy = StateMaps.Object.Copy(item, budget);
            PortableResourcePath.Validate(copy.Metadata.Link);
            copy.Metadata.Queries = item.Metadata.Queries.Select(q => StateMaps.Query.Copy(q, budget)).ToList();
            value.Objects.Add(copy);
        }
        foreach (var workflow in source.Workflows)
        {
            var copy = StateMaps.Workflow.Copy(workflow, budget);
            foreach (var item in workflow.Items)
            {
                var child = StateMaps.Item.Copy(item, budget);
                child.Queries = item.Queries.Select(q => StateMaps.Query.Copy(q, budget)).ToList();
                copy.Items.Add(child);
            }
            value.Workflows.Add(copy);
        }
        return value;
    }

    public static OpenDocumentState Window(OpenDocumentState source, StateBudget budget)
    {
        var value = StateMaps.Window.Copy(source, budget);
        if(source.PersistentSourcePath!=null)value.FilePath=String(source.PersistentSourcePath,budget);
        PortableResourcePath.Validate(value.FilePath);
        foreach (var filter in source.SpreadsheetFilters)
            value.SpreadsheetFilters.Add(filter.Key, String(filter.Value, budget));
        return value;
    }

    public static WorkspaceState Workspace(WorkspaceState source, StateBudget budget)
    {
        var value = StateMaps.Workspace.Copy(source, budget);
        PortableResourcePath.Validate(value.LastFolderPath);
        value.UnloadedResourceIds = source.UnloadedResourceIds.Select(id => String(id, budget)).ToList();
        foreach (var window in source.OpenDocuments) value.OpenDocuments.Add(Window(window, budget));
        return value;
    }

    public static WorkbenchState Workbench(WorkbenchState source, StateBudget budget)
    {
        var value = StateMaps.Workbench.Copy(source, budget);
        PortableResourcePath.Validate(value.ActiveDocumentPath);
        value.UnloadedResourceIds = source.UnloadedResourceIds.Select(id => String(id, budget)).ToList();
        value.OpenDocuments = source.OpenDocuments.Select(w => Window(w, budget)).ToList();
        value.ReferenceConnectionLines = source.ReferenceConnectionLines.Select(c => StateMaps.Connection.Copy(c, budget)).ToList();
        foreach(var line in value.ReferenceConnectionLines) { PortableResourcePath.Validate(line.SourceFilePath); PortableResourcePath.Validate(line.TargetFilePath); }
        value.ActiveDiagramSnapshot = source.ActiveDiagramSnapshot == null ? null : Diagram(source.ActiveDiagramSnapshot, budget);
        return value;
    }

    public static AppSettings Settings(AppSettings source, StateBudget budget)
    {
        var value = StateMaps.Settings.Copy(source, budget);
        value.CodeWindows.BackcolorsByExtension = source.CodeWindows.BackcolorsByExtension.Select(v => StateMaps.Extension.Copy(v, budget)).ToList();
        value.ReferenceHighlights.Styles = source.ReferenceHighlights.Styles.Select(v => StateMaps.Style.Copy(v, budget)).ToList();
        value.DiagramImages.Images = source.DiagramImages.Images.Select(v => StateMaps.Image.Copy(v, budget)).ToList();
        return value;
    }

    public static IReadOnlyDictionary<int, byte[]> PastedImages(DiagramDocument? diagram,
        IReadOnlyDictionary<int, byte[]> images, StateBudget budget)
    {
        var result = new Dictionary<int, byte[]>();
        foreach (var pair in images)
        {
            if (diagram == null || pair.Key < 0 || pair.Key >= diagram.Objects.Count ||
                string.IsNullOrWhiteSpace(diagram.Objects[pair.Key].PastedImageFileName))
                throw new InvalidDataException("Pasted-image bytes do not belong to a selected object.");
            budget.Asset(pair.Value.LongLength);
            result.Add(pair.Key, pair.Value.ToArray());
        }
        return result;
    }

    public static IReadOnlyDictionary<int, PastedImageFallback> FallbackImages(DiagramDocument? diagram,
        IReadOnlyDictionary<int, byte[]> pasted, IReadOnlyDictionary<int, PastedImageFallback>? images, StateBudget budget)
    {
        var result = new Dictionary<int, PastedImageFallback>();
        foreach (var pair in images ?? new Dictionary<int, PastedImageFallback>())
        {
            if (diagram == null || pair.Key < 0 || pair.Key >= diagram.Objects.Count ||
                string.IsNullOrWhiteSpace(diagram.Objects[pair.Key].PastedImageFileName) || pasted.ContainsKey(pair.Key) ||
                pair.Value.Resolution is not (PastedImageResolution.MissingUseInline or PastedImageResolution.MissingUseDefinition))
                throw new InvalidDataException("Invalid or ambiguous pasted-image fallback.");
            budget.Asset(pair.Value.Bytes.LongLength);
            result.Add(pair.Key, new(pair.Value.Resolution, pair.Value.Bytes.ToArray()));
        }
        if (diagram != null)
            for (int i = 0; i < diagram.Objects.Count; i++)
                if (!string.IsNullOrWhiteSpace(diagram.Objects[i].PastedImageFileName) && !pasted.ContainsKey(i) && !result.ContainsKey(i))
                    throw new InvalidDataException($"Missing pasted-image bytes or fallback for object ordinal {i}.");
        return result;
    }

    private static string String(string text, StateBudget budget)
    {
        ArgumentNullException.ThrowIfNull(text);
        budget.Row();
        budget.Text(checked((long)text.Length * 2));
        return text;
    }
}
