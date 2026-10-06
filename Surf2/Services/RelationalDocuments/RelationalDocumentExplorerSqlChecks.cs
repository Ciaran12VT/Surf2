using Surf2.Models;
using Surf2.Services.RelationalExplorer;

namespace Surf2.Services.RelationalDocuments;

/// <summary>Read-only hook for parent-owned duplicate-resource / second-alias SQL fixtures.</summary>
public static class RelationalDocumentExplorerSqlChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(RelationalRuntime runtime, ExplorerScope scope,
        ExplorerNodeSummary firstDuplicate, ExplorerNodeSummary clickedDuplicate, ExplorerNodeSummary secondAlias,
        string? expectedClickedText = null, CancellationToken ct = default)
    {
        if (firstDuplicate.SnapshotKey != clickedDuplicate.SnapshotKey || firstDuplicate.SnapshotResourceKey == clickedDuplicate.SnapshotResourceKey ||
            firstDuplicate.SchemaName != clickedDuplicate.SchemaName || firstDuplicate.ObjectName != clickedDuplicate.ObjectName ||
            clickedDuplicate.SnapshotResourceKey != secondAlias.SnapshotResourceKey || clickedDuplicate.ScopeResourceKey == secondAlias.ScopeResourceKey)
            throw new ArgumentException("The fixture requires duplicate-named resources and a second membership for the clicked resource.");
        await using var documents = new RelationalDocumentService(runtime);
        var passed = new List<string>();
        var first = await documents.ResolveExplorerNodeAsync(scope, firstDuplicate, ct);
        var clicked = await documents.ResolveExplorerNodeAsync(scope, clickedDuplicate, ct);
        var alias = await documents.ResolveExplorerNodeAsync(scope, secondAlias, ct);
        Check(first.Resource?.ResourceKey == firstDuplicate.SnapshotResourceKey && clicked.Resource?.ResourceKey == clickedDuplicate.SnapshotResourceKey &&
            first.Identity != clicked.Identity && first.DocumentPath != clicked.DocumentPath,
            "SQL clicked duplicate resources resolve distinct keys and window identities without name lookup", passed);
        Check(alias.Identity == clicked.Identity && alias.DocumentPath == clicked.DocumentPath &&
            alias.Resource == clicked.Resource && alias.ExplorerAddress?.Node.ScopeResourceKey == secondAlias.ScopeResourceKey,
            "SQL second alias changes presentation ancestry, never document or immutable text identity", passed);
        var selectedMembership = scope.Resources.Single(r => r.ScopeResourceKey == secondAlias.ScopeResourceKey);
        string aliasName = ExplorerCompatibility.ResourceDisplayName(selectedMembership);
        Check(alias.ExplorerAddress != null && alias.ExplorerAddress.Hierarchy.Contains(aliasName) &&
            alias.TextFileNameSeed == alias.ExplorerAddress.TextFileNameSeed && alias.TextFileNameSeed != clicked.TextFileNameSeed,
            "SQL TXT filename ancestry follows the clicked second alias, including effective virtual-folder placement", passed);
        await documents.ValidateAddressAsync(alias, scope, ct);
        passed.Add("Prepared clicked address validates its exact authoritative revision and current scope");
        if (expectedClickedText != null)
        {
            var plan = await documents.DescribeTextAsync(alias, ct);
            var text = await documents.ReadTextAsync(plan, ct);
            Check(text.Text == expectedClickedText, "Selected duplicate definition body comes from the clicked resource, not its sibling", passed);
        }
        try
        {
            _ = await documents.ResolveAsync(clicked.ExplorerAddress!.CanonicalLocator, scope, ct: ct);
            throw new InvalidOperationException("An ambiguous name locator unexpectedly selected a duplicate resource.");
        }
        catch (DocumentTargetException error) when (error.TargetState == SavedDocumentTargetState.Ambiguous)
        { passed.Add("Unbound duplicate name locators remain explicitly ambiguous while typed clicks work"); }
        return passed.AsReadOnly();
    }

    private static void Check(bool valid, string label, List<string> passed)
    { if (!valid) throw new InvalidOperationException(label); passed.Add(label); }
}
