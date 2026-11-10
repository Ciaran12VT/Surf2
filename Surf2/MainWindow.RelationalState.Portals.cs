using System.IO;
using System.Windows;
using System.Windows.Controls;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

namespace Surf2;

public partial class MainWindow
{
    private sealed record RelationalPortalChoice(DiagramSummary? Summary, string Name);
    private sealed record RelationalPortalSource(long Key, StateToken Token, string OriginalId, string ObjectId);
    private RelationalPortalSource? _relationalPortalSource;

    private static DiagramObjectSnapshot RequireUniqueRelationalPortal(DiagramDocument graph, string objectId)
    {
        var matches = graph.Objects.Where(o => o.ObjectType == DiagramObjectType.Portal && string.Equals(o.Id, objectId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("The portal object ID is missing or ambiguous within the selected diagram revision.");
    }
    private DiagramPortalControl RequireUniqueRelationalPortalControl(string objectId)
    {
        var matches = DiagramCanvas.Children.OfType<DiagramPortalControl>().Where(p => string.Equals(p.DiagramObjectId, objectId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("The portal object ID is missing or ambiguous within the open diagram.");
    }

    private async Task BeginRelationalPortalPairingAsync(DiagramPortalControl source)
    {
        try
        {
            using var operation = BeginRelationalStateOperation(); RequireRelationalWrite();
            if (TryBlockDiagramObjectEditWhenLocked("pair portals")) return;
            _ = RequireUniqueRelationalPortalControl(source.DiagramObjectId);
            if (string.IsNullOrWhiteSpace(_activeDiagramId)) { StatusText = "Save the diagram before pairing portals."; return; }
            var scope = _relationalScopeEdit ?? throw new InvalidOperationException("Select a scope before pairing portals.");
            var targets = await RelationalStateRuntime.StateStore.ReadRuntimeScopeTargetsAsync(scope.ExpectedToken, _relationalStateLifetime.Token);
            var scopedKeys = targets.Where(t => t.DiagramKey.HasValue).Select(t => t.DiagramKey!.Value).ToHashSet();
            var picker = new RelationalQueryPickerWindow<RelationalPortalChoice>("Portal Target Diagram", async (cursor, ct) =>
            {
                var page = await RelationalStateRuntime.State.ListDiagramsAsync(100, cursor as StateCatalogueCursor<DiagramSummary>, ct);
                var choices = page.Items.Where(d => scopedKeys.Contains(d.Token.Key) && d.Token.Key != _relationalDiagramEdit?.SubjectKey)
                    .Select(d => new RelationalPortalChoice(d, d.Name)).ToList();
                if (cursor == null) choices.Insert(0, new(null, CurrentDiagramName + " (open workspace)"));
                return new RelationalPickerPage<RelationalPortalChoice>(choices, page.Next);
            }, d => d.Name) { Owner = this };
            picker.LinkOwnerLifetime(operation.Token);
            bool? accepted;
            try { accepted = picker.ShowDialog(); } finally { await picker.DrainQueriesAsync(); }
            operation.Token.ThrowIfCancellationRequested();
            if (accepted != true || picker.SelectedSummary == null) return;
            var choice = picker.SelectedSummary;
            if (choice.Summary != null)
            {
                if (_relationalDiagramEdit == null || _relationalDiagramDraft)
                    throw new InvalidOperationException("Cross-diagram pairing needs a current diagram owner. Save the embedded Workbench graph as a new diagram first.");
                var sourceSummary = await FindRelationalDiagramAsync(_activeDiagramId!, _relationalStateLifetime.Token);
                var targetSummary = await FindRelationalDiagramAsync(choice.Summary.DiagramId, _relationalStateLifetime.Token);
                if (sourceSummary?.Token.Key != _relationalDiagramEdit.SubjectKey || targetSummary?.Token.Key != choice.Summary.Token.Key)
                    throw new InvalidDataException("The selected original diagram IDs do not identify unique portal contexts.");
                await SaveRelationalDiagramAsync(_relationalStateLifetime.Token);
                _relationalPortalSource = new(_relationalDiagramEdit.SubjectKey, _relationalDiagramEdit.ExpectedToken, _activeDiagramId!, source.DiagramObjectId);
                await LoadRelationalDiagramAsync(choice.Summary, _relationalStateLifetime.Token);
                if (_relationalDiagramEdit?.SubjectKey != choice.Summary.Token.Key) { _relationalPortalSource = null; return; }
            }
            else _relationalPortalSource = _relationalDiagramEdit == null ? null : new(_relationalDiagramEdit.SubjectKey,
                _relationalDiagramEdit.ExpectedToken, _activeDiagramId!, source.DiagramObjectId);
            if (_relationalStateClosing) return;
            var available = GetCurrentUnpairedPortalPickerItems(choice.Summary == null ? source.DiagramObjectId : null)
                .Where(p => DiagramCanvas.Children.OfType<DiagramPortalControl>().Count(c => string.Equals(c.DiagramObjectId, p.PortalObjectId, StringComparison.OrdinalIgnoreCase)) == 1).ToList();
            var action = new PortalTargetChoiceWindow(CurrentDiagramName, available.Count > 0) { Owner = this };
            if (action.ShowDialog() != true) return;
            string sourceId = _relationalPortalSource?.OriginalId ?? _activeDiagramId!;
            if (action.Choice == PortalTargetChoice.SelectExisting)
            {
                var portals = new PortalPickerWindow(available, CurrentDiagramName) { Owner = this };
                if (portals.ShowDialog() == true && !string.IsNullOrWhiteSpace(portals.SelectedPortalObjectId))
                    await PairRelationalPortalsAsync(sourceId, source.DiagramObjectId, _activeDiagramId!, portals.SelectedPortalObjectId);
            }
            else if (action.Choice == PortalTargetChoice.PlaceNew)
            {
                string? name = PromptForPortalName(source.PortalName, "New portal name", _activeDiagramId);
                if (!string.IsNullOrWhiteSpace(name)) ArmPortalPlacement(name, new PendingPortalPairPlacement(sourceId, source.DiagramObjectId));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "pair portals"); }
    }

    private async Task<bool> PairRelationalPortalsAsync(string firstId, string firstObjectId, string secondId, string secondObjectId)
    {
        try
        {
            using var operation = BeginRelationalStateOperation(); RequireRelationalWrite();
            var current = CaptureRelationalDiagram(); var target = RequireUniqueRelationalPortal(current.Document, secondObjectId);
            if (!string.Equals(secondId, current.Document.DiagramId, StringComparison.Ordinal)) throw new InvalidDataException("The pairing target is not the selected diagram.");
            if (string.Equals(firstId, secondId, StringComparison.OrdinalIgnoreCase))
            {
                var source = RequireUniqueRelationalPortal(current.Document, firstObjectId);
                if (ReferenceEquals(source, target)) throw new InvalidOperationException("A portal cannot pair with itself.");
                source.PairedPortalDiagramId = secondId; source.PairedPortalObjectId = secondObjectId;
                target.PairedPortalDiagramId = firstId; target.PairedPortalObjectId = firstObjectId;
                RequireUniqueRelationalPortalControl(firstObjectId).ApplyPairing(secondId, secondObjectId, ResolveRelationalPortalAddress(secondId, secondObjectId));
                RequireUniqueRelationalPortalControl(secondObjectId).ApplyPairing(firstId, firstObjectId, ResolveRelationalPortalAddress(firstId, firstObjectId));
                await SaveRelationalPortalWorkspaceAsync();
            }
            else
            {
                long generation = _relationalSelectionGeneration;
                var active = _relationalDiagramEdit ?? throw new InvalidOperationException("No target diagram is selected.");
                await _relationalOwnerCommands.WaitAsync(operation.Token);
                try
                {
                RequireRelationalSelection(generation, operation.Token);
                if (!ReferenceEquals(active, _relationalDiagramEdit)) throw new OperationCanceledException("The selected portal owner changed.");
                var pending = _relationalPortalSource ?? throw new InvalidOperationException("The source diagram owner was not selected for this pairing.");
                if (pending.OriginalId != firstId || pending.ObjectId != firstObjectId || _relationalDiagramEdit == null)
                    throw new InvalidDataException("The pending portal owner context changed.");
                await using var first = RequireRelationalLoad(await RelationalStateRuntime.State.LoadDiagramAsync(pending.Key, _relationalStateLifetime.Token), "source diagram");
                RequireRelationalSelection(generation, operation.Token);
                if (!SameRelationalOwner(first.ExpectedToken, pending.Token)) throw new StateConflictException("source diagram pairing");
                var a = first.Snapshot(); var source = RequireUniqueRelationalPortal(a.Document, firstObjectId);
                source.PairedPortalDiagramId = secondId; source.PairedPortalObjectId = secondObjectId;
                target.PairedPortalDiagramId = firstId; target.PairedPortalObjectId = firstObjectId;
                RuntimeDiagramPairPublication published;
                try { published = await RelationalStateRuntime.StateStore.SaveDiagramPairAsync(a, first.ExpectedToken, current, active.ExpectedToken, Guid.NewGuid(), _relationalStateLifetime.Token); }
                catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
                if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(active, _relationalDiagramEdit)) return true;
                _relationalDiagramEdit = NewRelationalDiagramEdit(current, published.Second); await active.DisposeAsync();
                RequireUniqueRelationalPortalControl(secondObjectId).ApplyPairing(firstId, firstObjectId, firstObjectId + " @ " + a.Document.Name);
                _relationalSavedDiagram = StateCopies.Diagram(current.Document, new(RelationalRuntimeStateLimits));
                await BindPublishedRelationalDiagramRevisionAsync(published.Second, current);
                }
                finally { _relationalOwnerCommands.Release(); }
            }
            _relationalPortalSource = null; return true;
        }
        catch (Exception error) { ReportRelationalStateFailure(error, "publish portal pairing"); return false; }
    }

    private async Task SaveRelationalPortalWorkspaceAsync()
    {
        using var operation = BeginRelationalStateOperation();
        if (_relationalDiagramEdit != null && !_relationalDiagramDraft) { await SaveRelationalDiagramAsync(_relationalStateLifetime.Token); return; }
        var edit = _relationalWorkbenchEdit ?? throw new InvalidOperationException("Save the diagram before publishing portal relationships.");
        long generation = _relationalSelectionGeneration;
        await _relationalOwnerCommands.WaitAsync(operation.Token);
        try
        {
        RequireRelationalSelection(generation, operation.Token);
        if (!ReferenceEquals(edit, _relationalWorkbenchEdit)) throw new OperationCanceledException("The selected Workbench changed.");
        var original = edit.Snapshot().Workbench; var captured = CaptureRelationalWorkbench(original, original.IsDefaultForScope);
        edit.Replace(captured);
        try { await edit.SaveAsync(_relationalStateLifetime.Token); }
        catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
        if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(edit, _relationalWorkbenchEdit)) return;
        var summary = await RelationalStateRuntime.StateStore.ReadWorkbenchScopeTargetAsync(edit.SubjectKey, _relationalStateLifetime.Token);
        if (summary == null) throw new InvalidDataException("Published Workbench owner is missing.");
        // The independent embedded revision is retrieved through a summary page, never the current diagram library.
        RuntimeWorkbenchCursor? cursor = null;
        long count = 0; long? revision = null;
        do
        {
            var page = await RelationalStateRuntime.StateStore.ListRecentWorkbenchesAsync(100, cursor, _relationalStateLifetime.Token);
            count += page.Items.Count;
            if (count > RelationalRuntimeStateLimits.MaximumRows) throw new InvalidDataException("The selected Workbench revision lookup exceeds its metadata budget.");
            var selected = page.Items.FirstOrDefault(w => w.Token.Key == edit.SubjectKey);
            if (selected != null) { revision = selected.EmbeddedDiagramRevisionKey; break; }
            cursor = page.Next;
        } while (cursor != null);
        if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(edit, _relationalWorkbenchEdit)) return;
        _relationalActiveDiagramRevisionKey = revision;
        if (captured.Workbench.ActiveDiagramSnapshot != null) BindPublishedRelationalOccurrences(captured.Workbench.ActiveDiagramSnapshot);
        if (original.IsDefaultForScope)
        {
            _relationalDefaultWorkbenchScope = _relationalScopeEdit?.SubjectKey;
            _relationalDefaultWorkbenchComparison = CreateRelationalWorkbenchComparisonKey(captured.Workbench);
        }
        RememberRelationalScopeSwitchWorkbenchBaseline(captured.Workbench);
        await RefreshRelationalWorkbenchCatalogueAsync(_relationalStateLifetime.Token);
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private async Task NavigateRelationalPortalAsync(DiagramPortalControl portal)
    {
        try
        {
            using var operation = BeginRelationalStateOperation();
            long generation = _relationalSelectionGeneration;
            _ = RequireUniqueRelationalPortalControl(portal.DiagramObjectId);
            if (!_relationalActiveDiagramRevisionKey.HasValue || !_relationalControlOccurrences.TryGetValue(portal, out var occurrence) || !occurrence.SourceOrdinal.HasValue)
                throw new InvalidOperationException("Save this portal in its selected owner before navigating its target.");
            if (occurrence.Original.PairedPortalDiagramId != portal.PairedPortalDiagramId || occurrence.Original.PairedPortalObjectId != portal.PairedPortalObjectId)
            {
                var captured = _relationalSavedDiagram ?? _relationalWorkbenchEdit?.Snapshot().Workbench.ActiveDiagramSnapshot;
                if (captured == null || occurrence.SourceOrdinal.Value >= captured.Objects.Count ||
                    captured.Objects[occurrence.SourceOrdinal.Value].PairedPortalDiagramId != portal.PairedPortalDiagramId ||
                    captured.Objects[occurrence.SourceOrdinal.Value].PairedPortalObjectId != portal.PairedPortalObjectId)
                    throw new InvalidOperationException("Save the changed portal target before navigating it.");
            }
            var target = await RelationalStateRuntime.StateStore.ReadRuntimePortalTargetAsync(_relationalActiveDiagramRevisionKey.Value,
                occurrence.SourceOrdinal.Value, _relationalStateLifetime.Token);
            RequireRelationalSelection(generation, operation.Token);
            if (target?.Resolution != StateLinkResolution.Resolved) throw new InvalidDataException("The saved portal target is missing, ambiguous, or in a different revision context. Its original IDs were retained.");
            if (_relationalStateClosing) return;
            if (target.RevisionKey == _relationalActiveDiagramRevisionKey)
            {
                var control = DiagramCanvas.Children.OfType<DiagramPortalControl>().SingleOrDefault(p =>
                    _relationalControlOccurrences.TryGetValue(p, out var o) && o.SourceOrdinal == target.ObjectOrdinal);
                if (control == null) throw new InvalidDataException("The saved embedded target occurrence is unavailable in the open graph.");
                ScrollDiagramObjectIntoView(control.DiagramObjectId); return;
            }
            if (!target.DiagramKey.HasValue) throw new InvalidDataException("The saved portal has no logical target owner.");
            var memberships = await RelationalStateRuntime.StateStore.ReadRuntimeScopeTargetsAsync(_relationalScopeEdit?.ExpectedToken ?? throw new InvalidOperationException("No scope is selected."), _relationalStateLifetime.Token);
            if (!memberships.Any(m => m.DiagramKey == target.DiagramKey)) throw new InvalidDataException("The resolved portal diagram is not in the selected scope.");
            await using var selected = RequireRelationalLoad(await RelationalStateRuntime.State.LoadDiagramAsync(target.DiagramKey.Value, _relationalStateLifetime.Token), "portal target diagram");
            var model = selected.Snapshot(); _ = RequireUniqueRelationalPortal(model.Document, portal.PairedPortalObjectId);
            long revision = await RelationalStateRuntime.StateStore.ReadCurrentDiagramRevisionKeyAsync(selected.ExpectedToken, _relationalStateLifetime.Token);
            RequireRelationalSelection(generation, operation.Token);
            await LoadRelationalDiagramAsync(new(selected.ExpectedToken, 0, model.Document.DiagramId, model.Document.Name, revision,
                model.Document.CreatedAtUtc, model.Document.UpdatedAtUtc), _relationalStateLifetime.Token);
            if (_relationalDiagramEdit?.SubjectKey == target.DiagramKey && !_relationalStateClosing) ScrollDiagramObjectIntoView(portal.PairedPortalObjectId);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "navigate portal target"); }
    }

    private async Task ClearRelationalPairedPortalAsync(DiagramPortalControl portal)
    {
        if (!portal.IsPaired) return;
        // Never mutate an arbitrary reciprocal raw-ID match. Missing/ambiguous endpoints remain descriptors.
        using var operation = BeginRelationalStateOperation(); RequireRelationalWrite();
        _ = RequireUniqueRelationalPortalControl(portal.DiagramObjectId);
        if (string.Equals(portal.PairedPortalDiagramId, _activeDiagramId, StringComparison.OrdinalIgnoreCase))
        {
            var target = RequireUniqueRelationalPortalControl(portal.PairedPortalObjectId);
            target.ClearPairing(); portal.ClearPairing(); await SaveRelationalPortalWorkspaceAsync(); return;
        }
        var sourceEdit = _relationalDiagramEdit ?? throw new InvalidOperationException("Cross-diagram unpairing needs a current diagram owner, not an embedded Workbench copy.");
        long generation = _relationalSelectionGeneration;
        await _relationalOwnerCommands.WaitAsync(operation.Token);
        try
        {
        RequireRelationalSelection(generation, operation.Token);
        if (!ReferenceEquals(sourceEdit, _relationalDiagramEdit)) throw new OperationCanceledException("The selected portal owner changed.");
        var summary = await FindRelationalDiagramAsync(portal.PairedPortalDiagramId, _relationalStateLifetime.Token) ?? throw new InvalidDataException("The paired diagram is missing. The original pairing was retained.");
        await using var targetEdit = RequireRelationalLoad(await RelationalStateRuntime.State.LoadDiagramAsync(summary, _relationalStateLifetime.Token), "reciprocal portal diagram");
        RequireRelationalSelection(generation, operation.Token);
        var a = CaptureRelationalDiagram(); var source = RequireUniqueRelationalPortal(a.Document, portal.DiagramObjectId);
        var b = targetEdit.Snapshot(); var reciprocal = RequireUniqueRelationalPortal(b.Document, portal.PairedPortalObjectId);
        if (!string.Equals(reciprocal.PairedPortalDiagramId, a.Document.DiagramId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reciprocal.PairedPortalObjectId, portal.DiagramObjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reciprocal endpoint no longer targets this portal. No pairing was changed.");
        source.PairedPortalDiagramId = source.PairedPortalObjectId = reciprocal.PairedPortalDiagramId = reciprocal.PairedPortalObjectId = string.Empty;
        RuntimeDiagramPairPublication published;
        try { published = await RelationalStateRuntime.StateStore.SaveDiagramPairAsync(a, sourceEdit.ExpectedToken, b, targetEdit.ExpectedToken, Guid.NewGuid(), _relationalStateLifetime.Token); }
        catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
        if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(sourceEdit, _relationalDiagramEdit)) return;
        portal.ClearPairing(); _relationalDiagramEdit = NewRelationalDiagramEdit(a, published.First); await sourceEdit.DisposeAsync();
        _relationalSavedDiagram = StateCopies.Diagram(a.Document, new(RelationalRuntimeStateLimits)); await BindPublishedRelationalDiagramRevisionAsync(published.First, a);
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private string ResolveRelationalPortalAddress(string diagramId, string objectId)
    {
        if (string.Equals(diagramId, _activeDiagramId, StringComparison.OrdinalIgnoreCase))
        {
            var portals = DiagramCanvas.Children.OfType<DiagramPortalControl>().Where(p => string.Equals(p.DiagramObjectId, objectId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (portals.Length == 1) return portals[0].PortalName + " @ " + CurrentDiagramName;
        }
        return objectId + " @ " + diagramId;
    }
}
