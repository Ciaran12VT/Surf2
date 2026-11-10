using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunResourceManagementChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        await fixture.MarkTestFixtureReadyAsync();
        var store = new RelationalStateStore(new RelationalSession(fixture.DestinationConnectionString));
        var source = new Scope { Name = "Existing resources" };
        source.Resources.Add(new() { Kind = ResourceKind.Folder, Path = @"C:\resource-fixture\folder", DisplayNameOverride = "Folder alias", IncludeChildren = false });
        source.Resources.Add(new() { Kind = ResourceKind.File, Path = @"C:\resource-fixture\same.txt" });
        source.Resources.Add(new() { Kind = ResourceKind.File, Path = @"C:\resource-fixture\first.txt", DisplayNameOverride = "First alias", DetailsOverride = "First details" });
        source.Resources.Add(new() { Kind = ResourceKind.File, Path = @"C:\resource-fixture\second.txt" });
        source.Resources.Add(new() { Kind = ResourceKind.Folder, Path = @"C:\resource-fixture\already-folder" });
        var sourceToken = await store.CreateScopeAsync(source, 0, Guid.NewGuid());
        var target = new Scope { Name = "Target" };
        target.Resources.Add(new() { Kind = ResourceKind.File, Path = @"C:\RESOURCE-FIXTURE\SAME.TXT" });
        target.Resources.Add(new() { Kind = ResourceKind.Folder, Path = "C:/RESOURCE-FIXTURE/already-folder/" });
        var targetToken = await store.CreateScopeAsync(target, 1, Guid.NewGuid());
        var duplicates = new Scope { Name = "Later copies of existing resources" };
        duplicates.Resources.Add(new() { Kind = ResourceKind.Folder, Path = "c:/RESOURCE-FIXTURE/folder///",
            DisplayNameOverride = "Later folder alias", IncludeChildren = true });
        duplicates.Resources.Add(new() { Kind = ResourceKind.File, Path = "c:/RESOURCE-FIXTURE/FIRST.TXT",
            DisplayNameOverride = "Later file alias", DetailsOverride = "Later details" });
        await store.CreateScopeAsync(duplicates, 2, Guid.NewGuid());
        var folders = await store.ListPhysicalResourceChoicesAsync(ResourceKind.Folder, targetToken.Key);
        check(folders.Items.Count == 1 && folders.Items[0].Resource.Path == source.Resources[0].Path &&
            folders.Items[0].Resource.DisplayNameOverride == "Folder alias" && !folders.Items[0].Resource.IncludeChildren,
            "Existing Folder deduplicates slash/case variants, excludes current equivalents, and preserves the first membership's alias and child preference");
        var first = await store.ListPhysicalResourceChoicesAsync(ResourceKind.File, targetToken.Key, pageSize: 1);
        check(first.Items.Count == 1 && first.Next != null && first.Items[0].Resource.Path == source.Resources[2].Path &&
            first.Items[0].Resource.DetailsOverride == "First details" && first.Items[0].ScopeName == source.Name,
            "Existing File excludes references already in this scope and pages other references with source labels");
        var second = await store.ListPhysicalResourceChoicesAsync(ResourceKind.File, targetToken.Key, 1, first.Next);
        check(second.Items.Count == 1 && second.Next == null && second.Items[0].Resource.Path == source.Resources[3].Path,
            "Existing File continuation returns the next reference without skipping or repeating rows");
        var files = await store.ListPhysicalResourceChoicesAsync(ResourceKind.File, targetToken.Key);
        check(files.Items.Count == 2 && files.Items[0].Resource.DisplayNameOverride == "First alias" &&
            files.Items[0].Resource.DetailsOverride == "First details" && files.Items[0].ScopeName == source.Name,
            "Existing physical choices deduplicate across source scopes before paging while retaining first-source metadata");
        source.Name = "Renamed source";
        await store.SaveScopeAsync(source, sourceToken, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => store.ListPhysicalResourceChoicesAsync(ResourceKind.File, targetToken.Key, 1, first.Next),
            check, "A changed resource catalogue rejects stale picker continuation");

        var diagram = new DiagramDocument { Name = "New empty diagram" };
        int diagramMembershipOrdinal = target.Resources.Count;
        target.Resources.Add(new() { Kind = ResourceKind.Diagram, Path = diagram.DiagramId, DisplayNameOverride = diagram.Name });
        var catalogue = await store.ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue.Diagrams);
        var created = await store.CreateDiagramInScopeAsync(new(diagram, new Dictionary<int, byte[]>()), target,
            targetToken, catalogue, Guid.NewGuid());
        var saved = Required(await store.ReadDiagramAsync(created.Diagram.Key), "new scope diagram");
        check(saved.Value.Document.Name == diagram.Name && saved.Value.Document.Objects.Count == 0 &&
            (await store.ReadRuntimeScopeTargetsAsync(created.Scope)).Single(t => t.ResourceOrdinal == diagramMembershipOrdinal).DiagramKey == created.Diagram.Key,
            "New Diagram publishes an empty saved diagram and its exact scope membership together");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}
