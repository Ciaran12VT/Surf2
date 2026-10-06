using System.IO;
using Surf2.Models;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Access.State;

/// <summary>Pure, opt-in checks. Does not open a connection, read files, decode images, or change runtime state.</summary>
public static class StateAccessContractChecks
{
    private sealed class ProbeModel { public string Text { get; set; } = string.Empty; }
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token; var passed = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("State access contract failed: " + name); passed.Add(name); }
        static ProbeModel Copy(ProbeModel value) => new() { Text = value.Text };
        Guid epoch = Guid.NewGuid();
        StateToken Token(byte version, Guid? publication = null) => new(7, epoch, new byte[] { 0, 0, 0, 0, 0, 0, 0, version }, publication ?? Guid.NewGuid());
        var initial = Token(1);
        var largeTypes = new[] { typeof(AppSettings), typeof(DiagramDocument), typeof(WorkbenchState), typeof(DiagramState), typeof(WorkbenchAggregate), typeof(byte[]) };
        foreach (var type in new[] { typeof(DiagramSummary), typeof(WorkbenchSummary), typeof(StartupPreferences), typeof(ImageDefinitionSummary), typeof(ReferenceStyleSummary) })
            Check(type.GetProperties().All(property => !largeTypes.Contains(property.PropertyType)), type.Name + " excludes aggregate/image payloads");
        Check(typeof(StateEditSession<ProbeModel>).GetConstructors().Length == 0, "Writable sessions have no public construction from default models/tokens");
        var keyboard = new KeyboardShortcutSettings { Version = 1, EnableDiagramCtrlShiftMouseZooming = false, EnableCodeCanvasCtrlShiftMouseZooming = false };
        var settings = new AppSettings(); var image = new DiagramImageDefinition { Id = "same", ImageDataBase64 = "not-decoded-by-startup" };
        var style = new ReferenceHighlightStyleSetting { Language = "C#" }; settings.DiagramImages.Images.Add(image); settings.ReferenceHighlights.Styles.Add(style);
        var images = settings.DiagramImages.Images; var styles = settings.ReferenceHighlights.Styles; var extensions = settings.CodeWindows.BackcolorsByExtension;
        var preferences = new StartupPreferences("dArK", true, true, false, true, "raw-color", StateInputPreferences.From(keyboard));
        preferences.ApplyToRuntime(settings);
        Check(ReferenceEquals(images, settings.DiagramImages.Images) && ReferenceEquals(styles, settings.ReferenceHighlights.Styles) && ReferenceEquals(extensions, settings.CodeWindows.BackcolorsByExtension),
            "Applying scalar startup preferences preserves unqueried image/style/extension collections");
        Check(ReferenceEquals(image, settings.DiagramImages.Images[0]) && image.ImageDataBase64 == "not-decoded-by-startup", "Startup neither normalizes nor decodes image data");
        Check(settings.Appearance.Theme == "dArK" && settings.KeyboardShortcuts.Version == 1 && !settings.KeyboardShortcuts.EnableDiagramCtrlShiftMouseZooming &&
            !settings.KeyboardShortcuts.EnableCodeCanvasCtrlShiftMouseZooming, "Raw appearance/input version and false flags are preserved");
        byte[] generation = new byte[8]; generation[7] = 3;
        var cursor = new StateCatalogueCursor<DiagramSummary>(new(epoch, 1, 7, generation)); generation[7] = 99;
        var native = cursor.Native; native.Generation[7] = 44;
        Check(cursor.Native.Generation[7] == 3, "Catalogue cursors detach mutable generation arrays");

        int writes = 0;
        Task<StateToken> NeverSave(ProbeModel model, StateToken owner, Guid publication, CancellationToken token)
        { writes++; return Task.FromResult(Token(2, publication)); }
        var missing = await SelectedStateAccess.LoadSelectedAsync<ProbeModel>("Probe", 7, epoch, _ => Task.FromResult<SelectedState<ProbeModel>?>(null), Copy, NeverSave, _ => Task.FromResult<StateToken?>(initial), ct);
        Check(missing.Status == StateLoadStatus.Missing && missing.Value == null && writes == 0, "Missing load has no default value/write capability");
        var failed = await SelectedStateAccess.LoadSelectedAsync<ProbeModel>("Probe", 7, epoch, _ => Task.FromException<SelectedState<ProbeModel>?>(new IOException("fixture read failure")), Copy, NeverSave, _ => Task.FromResult<StateToken?>(initial), ct);
        Check(failed.Status == StateLoadStatus.Failed && failed.Value == null && writes == 0, "Failed load has no default value/write capability");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var load = await SelectedStateAccess.LoadSelectedAsync<ProbeModel>("Probe", 7, epoch, _ => throw new InvalidOperationException("Should not read"), Copy, NeverSave,
                _ => Task.FromResult<StateToken?>(initial), cancelled.Token);
            Check(load.Status == StateLoadStatus.Cancelled && load.Value == null && writes == 0, "Cancelled load has no write capability or read side effect");
        }
        var wrongEpoch = await SelectedStateAccess.LoadSelectedAsync<ProbeModel>("Probe", 7, Guid.NewGuid(),
            _ => Task.FromResult<SelectedState<ProbeModel>?>(new(new(), initial)), Copy, NeverSave, _ => Task.FromResult<StateToken?>(initial), ct);
        Check(wrongEpoch.Status == StateLoadStatus.Failed && wrongEpoch.Value == null, "Cross-epoch loads cannot become writable sessions");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new List<string>(); StateToken observed = initial;
        await using (var edit = new StateEditSession<ProbeModel>("Probe", 7, new(new() { Text = "loaded" }, initial), Copy, async (model, expected, publication, token) =>
        {
            persisted.Add(model.Text); Check(expected.PublicationId == observed.PublicationId, "Serialized writes use the latest acknowledged expected owner");
            if (persisted.Count == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            observed = Token((byte)(persisted.Count + 1), publication); return observed;
        }, _ => Task.FromResult<StateToken?>(observed)))
        {
            Check((await edit.SaveAsync(ct)).Disposition == StateSaveDisposition.NoChanges && persisted.Count == 0, "Clean save performs no persistence write");
            var mutable = new ProbeModel { Text = "first" }; edit.Replace(mutable); mutable.Text = "caller mutation";
            Task<StateSaveResult> first = edit.SaveAsync(ct); await entered.Task.WaitAsync(ct);
            Task<StateSaveResult> redundant = edit.SaveAsync(ct);
            edit.Replace(new() { Text = "newer" }); release.TrySetResult();
            var saved = await first;
            Check(saved.HasNewerChanges && edit.IsDirty && edit.Status == StateEditStatus.Dirty && edit.Snapshot().Text == "newer", "Delayed save cannot clear or replace newer edits");
            Check((await redundant).Disposition == StateSaveDisposition.NoChanges && persisted.SequenceEqual(new[] { "first" }), "Duplicate queued saves coalesce by captured generation");
            var snapshot = edit.Snapshot(); snapshot.Text = "detached read";
            await edit.SaveAsync(ct);
            Check(persisted.SequenceEqual(new[] { "first", "newer" }) && !edit.IsDirty && edit.Snapshot().Text == "newer", "Detached models and selected-generation saves");
        }
        observed = initial; writes = 0;
        await using (var edit = new StateEditSession<ProbeModel>("Probe", 7, new(new(), initial), Copy, (model, expected, publication, token) =>
        {
            writes++; observed = Token(2, publication); throw new IOException("fixture lost commit acknowledgement");
        }, _ => Task.FromResult<StateToken?>(observed)))
        {
            edit.Replace(new() { Text = "one" });
            try { await edit.SaveAsync(ct); throw new InvalidOperationException("Expected save failure"); } catch (IOException) { }
            Check(edit.Status == StateEditStatus.OutcomeUnknown && edit.PendingPublicationId == observed.PublicationId && edit.IsDirty, "Lost acknowledgement retains dirty generation and publication identity");
            bool retryRejected = false;
            try { await edit.SaveAsync(ct); }
            catch (InvalidOperationException) { retryRejected = true; }
            Check(retryRejected && writes == 1, "Unknown outcomes reject blind retries");
            edit.Replace(new() { Text = "newer" });
            Check(await edit.RecoverAsync(ct) == StateRecoveryDisposition.Committed && edit.IsDirty && edit.Snapshot().Text == "newer", "Publication recovery marks only the acknowledged generation clean");
        }
        observed = initial; writes = 0;
        await using (var edit = new StateEditSession<ProbeModel>("Probe", 7, new(new(), initial), Copy, (model, expected, publication, token) =>
        {
            writes++; if (writes == 1) throw new IOException("fixture no commit");
            observed = Token(2, publication); return Task.FromResult(observed);
        }, _ => Task.FromResult<StateToken?>(observed)))
        {
            edit.Replace(new() { Text = "one" });
            try { await edit.SaveAsync(ct); throw new InvalidOperationException("Expected failure"); } catch (IOException) { }
            Check(await edit.RecoverAsync(ct) == StateRecoveryDisposition.NotCommitted && edit.IsDirty, "Unchanged owner proves a pending publication did not commit");
            await edit.SaveAsync(ct); Check(writes == 2 && !edit.IsDirty, "Explicit retry only after recovery");
        }
        await using (var edit = new StateEditSession<ProbeModel>("Probe", 7, new(new(), initial), Copy,
            (_, _, _, _) => throw new StateConflictException("fixture"), _ => Task.FromResult<StateToken?>(initial)))
        {
            edit.Replace(new() { Text = "one" });
            try { await edit.SaveAsync(ct); throw new InvalidOperationException("Expected conflict"); } catch (StateConflictException) { }
            Check(edit.Status == StateEditStatus.Conflict && edit.IsDirty && edit.PendingPublicationId == null, "Conflicts preserve edits and require a reload");
            Check(await edit.RecoverAsync(ct) == StateRecoveryDisposition.NoPendingSave && edit.Status == StateEditStatus.Conflict, "Recovery cannot erase a real conflict");
        }
        var closed = new StateEditSession<ProbeModel>("Probe", 7, new(new(), initial), Copy, NeverSave, _ => Task.FromResult<StateToken?>(initial));
        await closed.DisposeAsync();
        try { closed.Snapshot(); throw new InvalidOperationException("Expected closed session"); }
        catch (ObjectDisposedException) { passed.Add("Logical close releases models and rejects further use"); }
        return passed.AsReadOnly();
    }
}
