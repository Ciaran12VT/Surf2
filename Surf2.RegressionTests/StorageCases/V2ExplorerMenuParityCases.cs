using System.Collections;
using System.IO;
using System.Windows.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;

public static partial class StorageRegressionSuite
{
    private static void VerifyV2ExplorerMenuParity(Surf2.MainWindow main, ExplorerScope scope,
        string folder, string file, Action<bool, string> check)
    {
        var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
        var registered = RuntimeWpfField<IDictionary>(main, "_relationalExplorerNodes");
        var temporary = new List<FileSystemNode>();
        var database = scope.Resources.First(resource => resource.Snapshot != null);
        var physical = scope.Resources.First(resource => resource.Path == folder);
        const string here = "New Virtual Folder Here", inside = "New Virtual Folder Inside", add = "Add Existing Resources";
        var root = Summary(folder, ExplorerNodeRole.Resource, ResourceKind.Folder, directory: true) with
        { ScopeResourceKey = physical.ScopeResourceKey, ScopeResourceId = physical.ResourceId, IsScopeResourceRoot = true };
        var db = Summary(database.Path, ExplorerNodeRole.Resource, ResourceKind.DatabaseSnapshot, directory: true) with
        { ScopeResourceKey = database.ScopeResourceKey, ScopeResourceId = database.ResourceId, IsScopeResourceRoot = true,
            SnapshotKey = database.Snapshot!.SnapshotKey };
        var fileNode = Summary(file, ExplorerNodeRole.PhysicalFile, ResourceKind.File);
        var category = db with { Role = ExplorerNodeRole.Category, IsScopeResourceRoot = false, Category = ExplorerCategory.Tables };
        var table = category with { Role = ExplorerNodeRole.DatabaseDocument, IsDirectory = false, IsVirtualDocument = true,
            SnapshotResourceKey = 1, SourceRevisionKey = 1, SchemaName = "dbo", ObjectName = "A", HasFullData = true };

        // The matrix is intentionally explicit: labels, separators and ordering are
        // observable master behavior, independently of the V2 menu implementation.
        using var trace = new StateAccessSqlTrace(runtime.Options.ConnectionString);
        try
        {
            Verify("loaded folder root", root, "Compare", "|", "Unload", "Remove From Scope", "|", here, inside, "|", add);
            Verify("unloaded folder root", root with { IsScopeResourceLoaded = false }, "Load", "Remove From Scope", "|", here, "|", add);
            Verify("nested physical directory", root with { IsScopeResourceRoot = false, Role = ExplorerNodeRole.PhysicalDirectory },
                "Compare", "|", here, inside, "|", add);
            Verify("physical file", fileNode, "Compare", "|", here, "|", add, "|", "Open Containing Folder");
            Verify("physical file root", fileNode with { IsScopeResourceRoot = true, ScopeResourceId = physical.ResourceId },
                "Compare", "|", "Unload", "Remove From Scope", "|", here, "|", add, "|", "Open Containing Folder");
            Verify("unloaded existing physical file", fileNode with { IsScopeResourceRoot = true,
                    ScopeResourceId = physical.ResourceId, IsScopeResourceLoaded = false },
                "Load", "Remove From Scope", "|", here, "|", add, "|", "Open Containing Folder");
            Verify("missing physical file", fileNode with { FullPath = Path.Combine(folder, "absent.cs"), Availability = ExplorerAvailability.Missing },
                "Compare", "|", here, "|", add);
            Verify("loaded database root", db, "Compare", "|", "History", "Export DB Data", "|", "Unload", "Remove From Scope", "|", here, inside, "|", add);
            Verify("unloaded database root", db with { IsScopeResourceLoaded = false }, "History", "Export DB Data", "|", "Load", "Remove From Scope", "|", here, "|", add);
            Verify("Tables category", category, "Compare", "|", here, inside, "|", add);
            Verify("table with captured data", table, "Compare Table Metadata", "Compare Table Data", "|", here, "|", add);
            Verify("table without captured data", table with { HasFullData = false }, "Compare Table Metadata", "|", here, "|", add);
            Verify("stored procedure", table with { Category = ExplorerCategory.Procedures, HasFullData = false }, "Compare", "|", here, "|", add);
            Verify("virtual folder", Summary("surf2://virtual-folder/parity", ExplorerNodeRole.VirtualFolder, ResourceKind.Folder, directory: true)
                with { VirtualFolderId = "parity" }, "Compare", "|", "Disband", "|", here, "|", add);
            Verify("diagram root", Summary(DiagramDocumentService.DiagramRootPath, ExplorerNodeRole.DiagramRoot, ResourceKind.Diagram, directory: true),
                here, inside, "|", add, "|", "New Diagram");
            Verify("diagram document", Summary("surf2://diagram/parity", ExplorerNodeRole.Diagram, ResourceKind.Diagram) with { IsVirtualDocument = true },
                here, "|", add);
            RuntimeWpfAssert(trace.Commands.Count == 0, check, "Object Explorer builds its master context menus from selected metadata without SQL reads");
        }
        finally
        {
            foreach (var node in temporary) registered.Remove(node);
        }

        void Verify(string name, ExplorerNodeSummary summary, params string[] expected)
        {
            var node = (FileSystemNode)RuntimeWpfInvoke(main, "CreateRelationalExplorerNode", summary)!;
            temporary.Add(node);
            var menu = (ContextMenu)RuntimeWpfInvoke(main, "CreateRelationalObjectExplorerContextMenu", node)!;
            RuntimeWpfAssert(MenuSignature(menu).SequenceEqual(expected), check,
                $"Object Explorer {name}: master menu labels, order, separators and node applicability");
            RuntimeWpfAssert(menu.Items.OfType<MenuItem>().All(item => item.IsEnabled && item.Items.Count == 0), check,
                $"Object Explorer {name}: commands are connected and contain no unexpected submenu");
        }

        static ExplorerNodeSummary Summary(string path, ExplorerNodeRole role, ResourceKind kind, bool directory = false) =>
            new("parity:" + Guid.NewGuid().ToString("N"), "parity", "parity", path, path,
                FileSystemNode.RootParentKey, FileSystemNode.RootParentKey, role, kind,
                directory ? FileSystemNodeIconKind.Folder : FileSystemNodeIconKind.File, ExplorerAvailability.Present,
                directory, false, false, true, ExplorerChildrenState.Loaded);
    }

    private static string[] MenuSignature(ItemsControl menu) => menu.Items.Cast<object>().Select(item => item switch
    {
        Separator => "|",
        MenuItem command => command.Header?.ToString() ?? "",
        _ => "<unexpected " + item.GetType().Name + ">"
    }).ToArray();
}
