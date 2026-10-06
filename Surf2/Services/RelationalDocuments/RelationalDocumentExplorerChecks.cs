using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalDocuments;

/// <summary>Pure clicked-identity and alias guards; no SQL, files or WPF.</summary>
public static class RelationalDocumentExplorerChecks
{
    public static Task<IReadOnlyList<string>> RunPureAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var passed = new List<string>();
        Guid epoch = Guid.Parse("471a78eb-daca-4186-a54d-8855e195ef03");
        var snapshot = new SnapshotSummary(1, Guid.Empty, "original", "Database", "Database", DateTimeOffset.MinValue, 0, null, []);
        var first = new ExplorerResource(20, 0, "original-membership", ResourceKind.DatabaseSnapshot,
            "original", "FirstAlias", true, true, snapshot, null, null, null, false);
        var second = first with { ScopeResourceKey = 22, SortOrdinal = 1, Alias = "SecondAlias" };
        var scope = new ExplorerScope(new IndexRequestContext(epoch, 1, 1, "scope", "snapshot", "diagram", true, [], [], false),
            "scope", "Scope", [first, second], [], "en-IE");
        var selected = new ExplorerNodeSummary("database:22:4", "dbo.Duplicate", "dbo.Duplicate",
            "db://original/object/StoredProcedure/dbo.Duplicate.sql", "same-legacy-node-key", "category", "category",
            ExplorerNodeRole.DatabaseDocument, ResourceKind.DatabaseSnapshot, FileSystemNodeIconKind.File,
            ExplorerAvailability.Present, false, true, false, true, ExplorerChildrenState.Loaded,
            ScopeResourceKey: 22, SnapshotKey: 1, SnapshotResourceKey: 4, SourceRevisionKey: 104,
            Category: ExplorerCategory.Procedures, SchemaName: "dbo", ObjectName: "Duplicate");
        var current = new SnapshotResourceSummary(4, Guid.Empty, 1, DatabaseVersionedResourceKind.StoredProcedure,
            "dbo", "Duplicate", "original-resource", 104, 1, []);
        var member = DocumentExplorerSelection.SelectedMembership(scope, selected);
        Check(member.ScopeResourceKey == 22 && member.Alias == "SecondAlias", "Clicked second alias beats the first loaded membership", passed);
        DocumentExplorerSelection.ValidateResource(selected, member, current);
        passed.Add("Clicked duplicate binds its resource key rather than its indistinguishable legacy path or name");
        Check(DocumentExplorerSelection.SnapshotMembership(scope, 1, 22) == second,
            "Prepared snapshot address retains the preferred alias", passed);
        Check(DocumentExplorerSelection.SnapshotMembership(scope, 1, null) == first,
            "Default unbound address selection keeps existing first-loaded behavior", passed);
        Expect<DocumentTargetException>(() => DocumentExplorerSelection.ValidateResource(selected, member, current with { ResourceKey = 2, RevisionKey = 102 }));
        passed.Add("Same-schema/name sibling identity cannot replace the clicked duplicate");
        Expect<SnapshotConcurrencyException>(() => DocumentExplorerSelection.ValidateResource(selected, member, current with { RevisionKey = 105 }));
        passed.Add("A stale clicked revision requires refreshed metadata instead of a substituted head");
        Expect<DocumentTargetException>(() => DocumentExplorerSelection.SnapshotMembership(scope, 1, 99));
        Expect<DocumentTargetException>(() => DocumentExplorerSelection.SnapshotMembership(scope, 2, 22));
        passed.Add("Missing or wrong-owner preferred aliases reject instead of falling back to the first alias");
        Expect<DocumentTargetException>(() => DocumentExplorerSelection.SelectedMembership(scope with { Resources = [first, second with { IsLoaded = false }] }, selected));
        Expect<DocumentTargetException>(() => DocumentExplorerSelection.SelectedMembership(scope, selected with { IsScopeResourceLoaded = false }));
        passed.Add("Unloaded clicked memberships reject even when another alias remains loaded");
        Expect<DocumentTargetException>(() => DocumentExplorerSelection.ValidateResource(selected, member, current with { Kind = DatabaseVersionedResourceKind.View }));
        passed.Add("Selected category and resource kind must agree");
        Expect<DocumentTargetException>(() => DocumentExplorerSelection.ValidateResource(selected, member, current with { SnapshotKey = 2 }));
        passed.Add("Snapshot ownership remains part of typed clicked identity");
        var physical = second with { Kind = ResourceKind.Folder, Snapshot = null, Path = @"C:\fixtures", Alias = "PhysicalSecondAlias" };
        var file = selected with { Role = ExplorerNodeRole.PhysicalFile, ResourceKind = ResourceKind.File, FullPath = @"C:\fixtures\source.cs",
            SnapshotKey = null, SnapshotResourceKey = null, SourceRevisionKey = null, Category = null };
        Check(DocumentExplorerSelection.SelectedMembership(scope with { Resources = [physical] }, file) == physical &&
            file.FullPath == @"C:\fixtures\source.cs", "Physical alias selection leaves the actual source path separate from ephemeral occurrence keys", passed);
        return Task.FromResult<IReadOnlyList<string>>(passed.AsReadOnly());
    }

    private static void Check(bool success, string label, List<string> passed)
    { if (!success) throw new InvalidOperationException(label); passed.Add(label); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("The clicked explorer identity guard did not reject an invalid selection.");
    }
}
