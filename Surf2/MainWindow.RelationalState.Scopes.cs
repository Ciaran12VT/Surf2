using System.IO;
using System.Windows;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

namespace Surf2;

public partial class MainWindow
{
    // An offscreen fixture can supply the same explicit confirmation without showing a modal window.
    internal Func<ExplorerResource, bool>? RelationalScopeResourceRemovalConfirmation { get; set; }
    private sealed record RelationalScopeCommandOwner(RelationalRuntime Runtime, StateEditSession<Scope> Edit,
        StateToken Token, Guid ExplorerOwner, long Selection);

    private void InitializeRelationalScopeResourceCommands()
    {
        RelationalScopeResourceAdditionHandler = _relationalScopeEdit == null ? null : AddRelationalScopeResourceAsync;
        RelationalScopeResourceRemovalHandler = _relationalScopeEdit == null ? null : RemoveRelationalScopeResourceAsync;
    }

    private RelationalScopeCommandOwner RequireRelationalScopeCommandOwner()
    {
        RequireRelationalWrite();
        var edit = _relationalScopeEdit ?? throw new InvalidOperationException("No selected scope was loaded.");
        var view = _relationalExplorerScope ?? throw new InvalidOperationException("Reload the selected scope before editing its resources.");
        var runtime = RelationalStateRuntime;
        if (edit.Status is not (StateEditStatus.Clean or StateEditStatus.Dirty) ||
            !AcceptsRelationalExplorer(_relationalExplorerOwner, runtime) || view.Context.RestrictDocumentKeys ||
            view.Context.Epoch != edit.Epoch || view.Context.ScopeKey != edit.SubjectKey ||
            !view.Context.ScopeVersion.Equals(Convert.ToHexString(edit.ExpectedToken.Version), StringComparison.OrdinalIgnoreCase) ||
            _activeScope == null || _activeScope.ScopeId != view.ScopeId || edit.Snapshot().ScopeId != view.ScopeId)
            throw new InvalidOperationException("This resource view is stale or read-only. Reload the selected scope before editing it.");
        return new(runtime, edit, edit.ExpectedToken, _relationalExplorerOwner, _relationalSelectionGeneration);
    }

    private void RequireRelationalScopeCommandOwner(RelationalScopeCommandOwner owner, CancellationToken ct)
    {
        RequireRelationalSelection(owner.Selection, ct);
        var current = RequireRelationalScopeCommandOwner();
        if (!ReferenceEquals(current.Runtime, owner.Runtime) || !ReferenceEquals(current.Edit, owner.Edit) ||
            current.ExplorerOwner != owner.ExplorerOwner || !SameRelationalOwner(current.Token, owner.Token))
            throw new OperationCanceledException("The selected scope changed.", ct);
    }

    private async Task AddRelationalScopeResourceAsync(ExplorerResourceCandidate candidate, CancellationToken cancellationToken)
    {
        using var operation = BeginRelationalStateOperation(cancellationToken);
        var ct = operation.Token; var owner = RequireRelationalScopeCommandOwner();
        await _relationalOwnerCommands.WaitAsync(ct);
        try
        {
            RequireRelationalScopeCommandOwner(owner, ct);
            var origin = candidate.Origin;
            if (origin.Epoch != owner.Token.Epoch || origin.SourceKind is < 0 or > 2 || origin.AfterSourceKey < 0 ||
                candidate.SourceScopeKey == owner.Edit.SubjectKey ||
                (origin.SourceKind == 0) != candidate.SourceScopeKey.HasValue ||
                origin.SourceKind == 1 && !candidate.SnapshotKey.HasValue || origin.SourceKind == 2 && !candidate.DiagramKey.HasValue)
                throw new InvalidDataException("The chosen resource has an invalid source context. Refresh its catalogue.");
            var scope = StateCopies.Scope(_activeScope!, new(RelationalRuntimeStateLimits));
            var member = candidate.Header.CreateScopedResource();
            if (member.Kind is ResourceKind.File or ResourceKind.Folder && scope.Resources.Any(r => ExplorerResourceCatalogue.SameIdentity(r.Kind, r.Path, member.Kind, member.Path)))
                throw new InvalidOperationException("This physical resource is already in the selected scope.");
            var target = new RuntimeScopeResourceOrigin(origin.Epoch, origin.ScopeCatalogueVersion, origin.SnapshotCatalogueVersion,
                origin.DiagramCatalogueVersion, candidate.SourceScopeKey, candidate.SourceScopeResourceKey, candidate.SnapshotKey, candidate.DiagramKey);
            StateToken saved;
            try { saved = await owner.Runtime.StateStore.AddScopeResourceFromCatalogueAsync(scope, owner.Token, member, target, Guid.NewGuid(), ct); }
            catch (Exception error) when (error is not (StateConflictException or ArgumentException or InvalidDataException)) { _relationalStateCommandOutcomeUnknown = true; throw; }
            if (_relationalStateClosing || owner.Selection != _relationalSelectionGeneration || !ReferenceEquals(owner.Edit, _relationalScopeEdit)) return;
            var acknowledged = StateCopies.Scope(scope, new(RelationalRuntimeStateLimits)); acknowledged.Resources.Add(member);
            var live = StateCopies.Scope(_activeScope!, new(RelationalRuntimeStateLimits)); live.Resources.Add(member);
            await AcknowledgeRelationalScopeCommandAsync(owner, acknowledged, live, saved);
            if (!_relationalStateClosing) StatusText = "Added resource to the selected scope.";
            // The explorer caller reloads roots using its own context; do not cancel it here.
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private async Task RemoveRelationalScopeResourceAsync(ExplorerResource resource, CancellationToken cancellationToken)
    {
        using var operation = BeginRelationalStateOperation(cancellationToken);
        var ct = operation.Token; var owner = RequireRelationalScopeCommandOwner();
        await _relationalOwnerCommands.WaitAsync(ct);
        try
        {
            RequireRelationalScopeCommandOwner(owner, ct);
            var scope = StateCopies.Scope(_activeScope!, new(RelationalRuntimeStateLimits));
            if (scope.Resources.Count(r => r.ResourceId.Equals(resource.ResourceId, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException("This resource ID is duplicated. Remove the explicit occurrence in the Scopes editor instead.");
            if (resource.SortOrdinal < 0 || resource.SortOrdinal >= scope.Resources.Count ||
                !_relationalExplorerScope!.Resources.Any(r => r.ScopeResourceKey == resource.ScopeResourceKey && r.SortOrdinal == resource.SortOrdinal))
                throw new InvalidOperationException("The selected resource occurrence changed. Reload this scope.");
            int ordinal = checked((int)resource.SortOrdinal); var member = scope.Resources[ordinal];
            if (member.ResourceId != resource.ResourceId || member.Path != resource.Path || member.Kind != resource.Kind)
                throw new InvalidOperationException("The selected membership no longer matches its resource view.");
            bool confirmed = RelationalScopeResourceRemovalConfirmation?.Invoke(resource) ??
                MessageBox.Show(this, "Remove this resource from the selected scope? Its source and saved workbenches remain. Unambiguous links in the selected current diagram are cleared; unresolved links remain intact.",
                    "Remove Scope Resource", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!confirmed) return;
            RequireRelationalScopeCommandOwner(owner, ct);
            var diagramOwner = _relationalDiagramEdit;
            DiagramState? graph = null;
            var cleared = new List<(DiagramObjectSnapshot Object, string Original)>();
            if (diagramOwner != null && !_relationalDiagramDraft)
            {
                if (diagramOwner.Status is not (StateEditStatus.Clean or StateEditStatus.Dirty))
                    throw new InvalidOperationException("Reload or recover the selected diagram before removing a linked resource.");
                graph = CaptureRelationalDiagram();
                bool uniqueDiagramTarget = false;
                if (member.Kind == ResourceKind.Diagram && resource.DiagramKey.HasValue &&
                    graph.Document.Objects.Any(o => DiagramMetadataLinkTargetsResource(o.Metadata.Link, member)))
                {
                    try { uniqueDiagramTarget = (await FindRelationalDiagramAsync(member.Path, ct))?.Token.Key == resource.DiagramKey; }
                    catch (InvalidDataException) { /* Ambiguous raw links remain descriptors, never an arbitrary target. */ }
                    RequireRelationalScopeCommandOwner(owner, ct);
                }
                foreach (var item in graph.Document.Objects)
                {
                    string link = item.Metadata.Link;
                    bool matches = member.Kind == ResourceKind.DatabaseSnapshot
                        ? resource.Snapshot != null && DatabaseDocumentService.TryParseDocumentPath(ParseMetadataLinkTarget(link).Link, out var database) &&
                            ExplorerCompatibility.TryParseDatabaseIdentity(database, out Guid epoch, out long snapshot, out _) &&
                            epoch == owner.Token.Epoch && snapshot == resource.Snapshot.SnapshotKey
                        : (member.Kind != ResourceKind.Diagram || uniqueDiagramTarget) &&
                            scope.Resources.Count(r => r.Kind == member.Kind && r.Path.Equals(member.Path, StringComparison.OrdinalIgnoreCase)) == 1 &&
                            DiagramMetadataLinkTargetsResource(link, member);
                    if (!matches) continue;
                    cleared.Add((item, link)); item.Metadata.Link = string.Empty;
                }
                if (cleared.Count == 0) graph = null;
            }
            RuntimeScopeResourceRemovalPublication saved;
            try { saved = await owner.Runtime.StateStore.RemoveScopeResourceExpectedAsync(scope, owner.Token, ordinal, resource.ScopeResourceKey,
                graph, graph == null ? null : diagramOwner!.ExpectedToken, Guid.NewGuid(), ct); }
            catch (Exception error) when (error is not (StateConflictException or ArgumentException or InvalidDataException)) { _relationalStateCommandOutcomeUnknown = true; throw; }
            if (_relationalStateClosing || owner.Selection != _relationalSelectionGeneration || !ReferenceEquals(owner.Edit, _relationalScopeEdit)) return;
            var acknowledged = StateCopies.Scope(scope, new(RelationalRuntimeStateLimits)); acknowledged.Resources.RemoveAt(ordinal);
            var live = StateCopies.Scope(_activeScope!, new(RelationalRuntimeStateLimits));
            var liveMember = live.Resources.SingleOrDefault(r => r.ResourceId == member.ResourceId);
            if (liveMember != null) live.Resources.Remove(liveMember);
            if (graph != null && ReferenceEquals(diagramOwner, _relationalDiagramEdit))
            {
                _relationalDiagramEdit = NewRelationalDiagramEdit(graph, saved.Diagram!);
                _relationalSavedDiagram = StateCopies.Diagram(graph.Document, new(RelationalRuntimeStateLimits));
                foreach (var change in cleared)
                    if (_relationalSnapshotOccurrences.TryGetValue(change.Object, out var occurrence) && occurrence.Control.TryGetTarget(out var control))
                    {
                        var metadata = GetDiagramObjectMetadata(control);
                        if (metadata?.Link == change.Original) { metadata.Link = string.Empty; ApplyDiagramObjectMetadata(control, metadata); }
                    }
                await diagramOwner!.DisposeAsync();
                if (!_relationalStateClosing && owner.Selection == _relationalSelectionGeneration)
                    await BindPublishedRelationalDiagramRevisionAsync(saved.Diagram!, graph);
            }
            if (_relationalStateClosing || owner.Selection != _relationalSelectionGeneration) return;
            await AcknowledgeRelationalScopeCommandAsync(owner, acknowledged, live, saved.Scope);
            if (!_relationalStateClosing) StatusText = "Removed scope membership. Other diagrams and saved workbenches were retained.";
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private async Task AcknowledgeRelationalScopeCommandAsync(RelationalScopeCommandOwner owner, Scope acknowledged, Scope live, StateToken token)
    {
        var edit = NewRelationalScopeEdit(acknowledged, token);
        if (!SameRelationalScope(acknowledged, live)) edit.Replace(live);
        _relationalScopeEdit = edit; SetActiveScope(live);
        await owner.Edit.DisposeAsync();
    }
}
