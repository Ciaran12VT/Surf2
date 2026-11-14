using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RemoteFolders;
using Surf2.Services.SourceResources;
using Surf2.Storage;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Index;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckSourceUiAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
            { ApplicationName = "Surf2_Regression_StateAccess_SourceUi_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var main = new Surf2.MainWindow(runtime.Options);
        RuntimeWpfSetField(main, "_relational", runtime);
        // Selection defaults are exercised in memory. Never load or write the desktop's SourceSelections.json.
        var selections = typeof(ResourceWorkspace).GetField("_selection", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? priorSelections = selections.GetValue(null);
        selections.SetValue(null, new Dictionary<Guid, Guid>());
        string localRoot = Path.Combine(fixture.OwnedDirectory, "local"), otherRoot = Path.Combine(localRoot, "nested"),
            managedRoot = Path.Combine(fixture.OwnedDirectory, "managed"), blobRoot = Path.Combine(fixture.OwnedDirectory, "blob");
        foreach (string root in new[] { localRoot, otherRoot, managedRoot, blobRoot }) Directory.CreateDirectory(root);
        string localFile = Path.Combine(localRoot, "owned.cs"), otherFile = Path.Combine(otherRoot, "other.cs");
        await File.WriteAllTextAsync(localFile, "public class Owned { }");
        await File.WriteAllTextAsync(otherFile, "public class Other { }");
        await File.WriteAllTextAsync(Path.Combine(managedRoot, "owned.cs"), "public class Committed { }");
        await File.WriteAllTextAsync(Path.Combine(blobRoot, "item.txt"), "blob content");
        var local = new ScopedResource { Kind = ResourceKind.Folder, Path = localRoot, DisplayNameOverride = "Local checkout",
            Source = new() { Provider = SourceProvider.LocalFileSystem } };
        var direct = new ScopedResource { Kind = ResourceKind.Folder, Path = "https://example.invalid/team/repository.git", DisplayNameOverride = "Committed repository",
            Source = new() { Provider = SourceProvider.Git, Endpoint = "https://example.invalid/team/repository.git", Ref = "main" } };
        var other = new ScopedResource { Kind = ResourceKind.Folder, Path = otherRoot, DisplayNameOverride = "Independent nested resource" };
        var blob = new ScopedResource { Kind = ResourceKind.Folder, Path = "https://fixture.blob.core.windows.net/container", DisplayNameOverride = "Blob resource",
            Source = new() { Provider = SourceProvider.AzureBlob, Endpoint = "https://fixture.blob.core.windows.net", Container = "container", AuthenticationKind = "Anonymous" } };
        var model = new Scope { Name = "Source UI fixture" };
        // The alternative is deliberately added after unrelated containers to catch root-order drift on switching.
        model.Resources.Add(local); model.Resources.Add(other); model.Resources.Add(blob); model.Resources.Add(direct);
        model.EnsureResourceContainers(); direct.ContainerId = local.ContainerId; model.EnsureResourceContainers();
        var repository = model.ResourceContainers.Single(c => c.ContainerId == local.ContainerId);
        repository.Name = "Repository"; repository.DefaultResourceId = local.PublicId;
        ResourceWorkspace.Register(model); ResourceWorkspace.Admit(local); ResourceWorkspace.Admit(other);
        ResourceWorkspace.Admit(blob, Prepared(blobRoot, "item.txt"));
        RuntimeWpfSetField(main, "_activeScope", model);
        RuntimeWpfField<Dictionary<Guid, GitCheckoutInfo>>(main, "_gitCheckouts")[local.PublicId] =
            new(localRoot, "", "working-branch", new string('a', 40), new Dictionary<string, string> { ["origin"] = direct.Source!.Endpoint });
        var raw = new ExplorerScope(new IndexRequestContext(runtime.Session.Epoch, 1, 1, "scope", "snapshot", "diagram", true, [], [], false),
            model.ScopeId, model.Name, model.Resources.Select((member, index) => new ExplorerResource(index + 1, index, member.ResourceId,
                member.Kind, member.Path, member.DisplayName, true, true, null, null, null, null, false)).ToImmutableArray(), [], "en-IE");
        var opened = RuntimeWpfField<Dictionary<string, FloatingCodeWindow>>(main, "_openWindows");
        try
        {
            using var trace = new StateAccessSqlTrace(connection);
            var projected = ResourceWorkspace.Project(raw, model);
            var originalRootOrder = projected.Resources.Select(r => r.ContainerId).ToArray();
            RuntimeWpfSetField(main, "_relationalExplorerScope", projected);
            RuntimeWpfAssert(projected.Resources.Length == 3 && projected.Resources.Count(r => r.ContainerId == local.ContainerId) == 1 &&
                projected.Resources.Single(r => r.ContainerId == local.ContainerId).PublicId == local.PublicId, check,
                "Source UI projects one selected resource per container while retaining both Git alternatives");
            var localNode = Node(projected.Resources.Single(r => r.PublicId == local.PublicId));
            RuntimeWpfAssert(localNode.IconKind == FileSystemNodeIconKind.GitFolder && localNode.ToolTip?.Contains("working-branch") == true &&
                localNode.Children.First().IconKind == FileSystemNodeIconKind.SourceAction && localNode.Children.First().Name.Contains("Git", StringComparison.OrdinalIgnoreCase), check,
                "Local Git root uses its Git-folder icon, branch tooltip and first-child direct-source switch");
            var blobNode = Node(projected.Resources.Single(r => r.PublicId == blob.PublicId));
            RuntimeWpfAssert(blobNode.IconKind == FileSystemNodeIconKind.BlobStorage && blobNode.Children.First().Name.Contains("local folder", StringComparison.OrdinalIgnoreCase), check,
                "Standalone Blob root displays its Blob icon and first-child link-local-folder action");

            var ownedState = new OpenDocumentState { FilePath = localFile, PersistentSourcePath = ResourceWorkspace.Locator(local.PublicId, "owned.cs"), DisplayName = "Owned" };
            var otherState = new OpenDocumentState { FilePath = otherFile, PersistentSourcePath = ResourceWorkspace.Locator(other.PublicId, "other.cs"), DisplayName = "Other" };
            var owned = new FloatingCodeWindow(ownedState, "Owned", null); var unrelated = new FloatingCodeWindow(otherState, "Other", null);
            opened.Add(localFile, owned); opened.Add(otherFile, unrelated);
            var workspace = RuntimeWpfField<WorkspaceState>(main, "_workspaceState"); workspace.OpenDocuments.Add(ownedState); workspace.OpenDocuments.Add(otherState);
            RuntimeWpfInvoke(main, "AddWindowToCodeView", owned, ownedState, false);
            RuntimeWpfInvoke(main, "AddWindowToCodeView", unrelated, otherState, false);
            using var owner = new QueryLifetime(runtime.Session.Epoch, projected.Context.ScopeKey);
            using var request = owner.BeginRequest();
            long generation = (long)typeof(Surf2.MainWindow).GetField("_relationalDocumentContextGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            RuntimeWpfAssert((bool)RuntimeWpfInvoke(main, "CanPublishRelationalDocument", runtime, projected, owner, request, generation)!, check,
                "An in-flight source document can publish while its original scope and generation are current");
            var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> CompletePendingRead()
            {
                await releaseRead.Task;
                return (bool)RuntimeWpfInvoke(main, "CanPublishRelationalDocument", runtime, projected, owner, request, generation)!;
            }
            var pendingRead = CompletePendingRead();
            RuntimeWpfInvoke(main, "CloseResourceViews", local); ResourceWorkspace.Block(local, release: true);
            releaseRead.SetResult();
            long retiredGeneration = (long)typeof(Surf2.MainWindow).GetField("_relationalDocumentContextGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            RuntimeWpfAssert(!await pendingRead && retiredGeneration > generation && owner.IsCurrent(request.Stamp) && !opened.ContainsKey(localFile) && opened.ContainsKey(otherFile) &&
                !workspace.OpenDocuments.Contains(ownedState) && workspace.OpenDocuments.Contains(otherState), check,
                "Unloading closes only the concrete owner's windows and rejects late read publication, even with overlapping folders");
            try { _ = ResourceWorkspace.Resolve(ownedState.PersistentSourcePath!); throw new InvalidOperationException("Unloaded source resolved."); }
            catch (IOException) { RuntimeWpfAssert(true, check, "An unloaded UI resource's persistent file address cannot reopen its old physical copy"); }

            repository.DefaultResourceId = direct.PublicId;
            ResourceWorkspace.Admit(direct, Prepared(managedRoot, "owned.cs"));
            projected = ResourceWorkspace.Project(raw, model); RuntimeWpfSetField(main, "_relationalExplorerScope", projected);
            var directNode = Node(projected.Resources.Single(r => r.ContainerId == local.ContainerId));
            RuntimeWpfAssert(projected.Resources.Count(r => r.ContainerId == local.ContainerId) == 1 && directNode.IconKind == FileSystemNodeIconKind.GitRepository &&
                directNode.Children.First().Name.Contains("local folder", StringComparison.OrdinalIgnoreCase) && model.Resources.Count == 4, check,
                "Committed Git selection changes the single root icon and switch row while keeping inactive resources intact");
            RuntimeWpfAssert(projected.Resources.Select(r => r.ContainerId).SequenceEqual(originalRootOrder) &&
                projected.Resources[0].PublicId == direct.PublicId && projected.Resources[0].SortOrdinal == 3, check,
                "Switching to a later-added alternative preserves container order and its concrete storage ordinal");
            RuntimeWpfAssert(trace.Commands.Count == 0, check, "Source projection, action rows and unload fences perform no fixture SQL body reads");
        }
        finally
        {
            foreach (var member in model.Resources) { RuntimeWpfInvoke(main, "CloseResourceViews", member); ResourceWorkspace.Block(member, release: true); }
            ResourceWorkspace.ReleaseExcept(); selections.SetValue(null, priorSelections);
            await RuntimeWpfInvokeTaskAsync(main, "DrainRelationalStateQueriesAsync");
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            RuntimeWpfSetField(main, "_shutdownSaveCompleted", true); main.Close();
        }
        await fixture.VerifySourceUnchangedAsync(check);

        PreparedFolderSource Prepared(string root, string logical) => new(root,
            new("fixture", "fixture-revision", DateTimeOffset.UtcNow, [new(logical, logical, 1, DateTimeOffset.UtcNow, "fixture", "fixture-hash")]));
        FileSystemNode Node(ExplorerResource resource)
        {
            var summary = new ExplorerNodeSummary("resource:" + resource.PublicId, resource.Alias, resource.Alias, resource.Path,
                ResourceWorkspace.Locator(resource.PublicId, ""), FileSystemNode.RootParentKey, FileSystemNode.RootParentKey,
                ExplorerNodeRole.Resource, ResourceKind.Folder, FileSystemNodeIconKind.Folder, ExplorerAvailability.Present,
                true, false, true, resource.IsLoaded, ExplorerChildrenState.Loaded, resource.ScopeResourceKey, resource.ResourceId);
            return (FileSystemNode)RuntimeWpfInvoke(main, "CreateRelationalExplorerNode", summary)!;
        }
    }
}
