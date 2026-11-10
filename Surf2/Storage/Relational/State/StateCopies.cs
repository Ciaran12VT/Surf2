using System.IO;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

// Capture mutable UI models before the first await. Do not call legacy Clone/EnsureDefaults: they normalize data.
internal static class StateCopies
{
    public static Scope Scope(Scope source, StateBudget budget)
    {
        var value = StateMaps.Scope.Copy(source, budget);
        foreach (var resource in source.Resources) value.Resources.Add(StateMaps.Resource.Copy(resource, budget));
        foreach (var folder in source.VirtualFolders)
        {
            var copy = StateMaps.Folder.Copy(folder, budget);
            foreach (string member in folder.ChildNodeKeys) copy.ChildNodeKeys.Add(String(member, budget));
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
        foreach (var filter in source.SpreadsheetFilters)
            value.SpreadsheetFilters.Add(filter.Key, String(filter.Value, budget));
        return value;
    }

    public static WorkspaceState Workspace(WorkspaceState source, StateBudget budget)
    {
        var value = StateMaps.Workspace.Copy(source, budget);
        value.UnloadedResourceIds = source.UnloadedResourceIds.Select(id => String(id, budget)).ToList();
        foreach (var window in source.OpenDocuments) value.OpenDocuments.Add(Window(window, budget));
        return value;
    }

    public static WorkbenchState Workbench(WorkbenchState source, StateBudget budget)
    {
        var value = StateMaps.Workbench.Copy(source, budget);
        value.UnloadedResourceIds = source.UnloadedResourceIds.Select(id => String(id, budget)).ToList();
        value.OpenDocuments = source.OpenDocuments.Select(w => Window(w, budget)).ToList();
        value.ReferenceConnectionLines = source.ReferenceConnectionLines.Select(c => StateMaps.Connection.Copy(c, budget)).ToList();
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
