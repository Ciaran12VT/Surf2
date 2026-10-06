using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

internal sealed class RelationalScopePickerWindow : RelationalQueryPickerWindow<ScopeSummary>
{
    private readonly RelationalRuntime _runtime;
    private readonly TextBox _name = new(), _description = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _resources = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false };
    private readonly Button _save = CommandButton("Save", "SurfSaveButtonStyle"), _delete = CommandButton("Delete", "RelationalDangerButtonStyle");
    private readonly Button _editDatabase = CommandButton("Edit Database");
    private StateEditSession<Scope>? _edit;
    private Scope? _model;
    private bool _busy;
    public HashSet<long> ChangedScopeKeys { get; } = [];
    public HashSet<long> DeletedScopeKeys { get; } = [];

    public RelationalScopePickerWindow(RelationalRuntime runtime, bool manage = true) : base("Scopes", async (cursor, ct) =>
    {
        var page = await runtime.State.ListScopesAsync(100, cursor as StateCatalogueCursor<ScopeSummary>, ct);
        return new(page.Items, page.Next);
    }, s => s.Name)
    {
        _runtime = runtime;
        if (!manage) return;
        Width = 1000; Details.Width = 520;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(80), GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            Details.RowDefinitions.Add(new() { Height = height });
        AddDetail(new TextBlock { Text = "Name", Margin = new(0, 0, 0, 4) }, 0); AddDetail(_name, 1);
        AddDetail(new TextBlock { Text = "Description", Margin = new(0, 8, 0, 4) }, 2); AddDetail(_description, 3);
        var resourceCommands = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 4) };
        Button folder = CommandButton("Add Folder"), file = CommandButton("Add File"), database = CommandButton("Add Database"), remove = CommandButton("Remove", "RelationalDangerButtonStyle");
        Button existingSnapshot = CommandButton("Add Snapshot"), existingDiagram = CommandButton("Add Diagram");
        foreach (var command in new[] { folder, file, database, _editDatabase, existingSnapshot, existingDiagram, remove }) resourceCommands.Children.Add(command);
        _editDatabase.IsEnabled = false;
        _resources.SelectionChanged += (_, _) => _editDatabase.IsEnabled = !_busy && _resources.SelectedItem is ScopedResource { Kind: ResourceKind.DatabaseSnapshot };
        AddDetail(resourceCommands, 4);
        _resources.Columns.Add(new DataGridTextColumn { Header = "Resource", Binding = new Binding(nameof(ScopedResource.Path)), IsReadOnly = true, Width = new(1, DataGridLengthUnitType.Star) });
        _resources.Columns.Add(new DataGridTextColumn { Header = "Alias", Binding = new Binding(nameof(ScopedResource.DisplayNameOverride)), Width = 130 });
        _resources.Columns.Add(new DataGridCheckBoxColumn { Header = "Children", Binding = new Binding(nameof(ScopedResource.IncludeChildren)), Width = 70 });
        AddDetail(_resources, 5);
        Button create = CommandButton("New"), merge = CommandButton("Merge Scope");
        Commands.Children.Add(create); Commands.Children.Add(_save); Commands.Children.Add(_delete); Commands.Children.Add(merge);
        _save.IsEnabled = _delete.IsEnabled = false; Details.IsEnabled = false;
        _save.Click += async (_, _) => await RunAsync(async () => { await SaveFieldsAsync(Lifetime); await ReloadAsync(); });
        create.Click += async (_, _) => await RunAsync(async () =>
        {
            await SaveFieldsAsync(Lifetime);
            var prompt = new RenameResourceWindow("New Scope") { Owner = this, Title = "New Scope" };
            if (prompt.ShowDialog() != true) return;
            string name = prompt.ResourceName;
            var catalogue = await _runtime.StateStore.ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue.Scopes, Lifetime);
            var token = await _runtime.StateStore.CreateScopeExpectedAsync(new() { Name = name.Trim() }, catalogue, Guid.NewGuid(), Lifetime);
            ChangedScopeKeys.Add(token.Key); await ReloadAsync();
        });
        _delete.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_edit == null || MessageBox.Show(this, "Delete this scope? Saved workbenches will be retained.", "Delete Scope",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            long key = _edit.SubjectKey;
            await _runtime.StateStore.DeleteScopeExpectedAsync(_edit.ExpectedToken, Guid.NewGuid(), Lifetime);
            DeletedScopeKeys.Add(key); await _edit.DisposeAsync(); _edit = null; _model = null; await ReloadAsync();
        });
        folder.Click += (_, _) =>
        {
            if (_model == null) return; var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog(this) == true) _model.Resources.Add(new() { Kind = ResourceKind.Folder, Path = dialog.FolderName });
        };
        file.Click += (_, _) =>
        {
            if (_model == null) return; var dialog = new OpenFileDialog { Multiselect = true };
            if (dialog.ShowDialog(this) == true) foreach (string path in dialog.FileNames)
                _model.Resources.Add(new() { Kind = ResourceKind.File, Path = Path.GetFullPath(path) });
        };
        remove.Click += (_, _) => { if (_model != null && _resources.SelectedItem is ScopedResource r) _model.Resources.Remove(r); };
        database.Click += async (_, _) => await RunAsync(() => CaptureDatabaseAsync(replaceSelected: false));
        _editDatabase.Click += async (_, _) => await RunAsync(() => CaptureDatabaseAsync(replaceSelected: true));
        existingSnapshot.Click += async (_, _) => await RunAsync(AddExistingSnapshotAsync);
        existingDiagram.Click += async (_, _) => await RunAsync(AddExistingDiagramAsync);
        merge.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_model == null || _edit == null) return;
            await SaveFieldsAsync(Lifetime);
            var picker = new RelationalScopePickerWindow(runtime, manage: false) { Owner = this };
            picker.LinkOwnerLifetime(Lifetime);
            bool? accepted;
            try { accepted = picker.ShowDialog(); } finally { await picker.DrainQueriesAsync(); }
            Lifetime.ThrowIfCancellationRequested();
            var selected = picker.SelectedSummary; var owner = _edit; var model = _model;
            if (accepted != true || selected == null || owner == null || model == null || selected.Token.Key == owner.SubjectKey) return;
            var merged = await runtime.StateStore.MergeScopeExpectedAsync(model, owner.ExpectedToken,
                selected.Token, Guid.NewGuid(), Lifetime);
            ChangedScopeKeys.Add(merged.Token.Key);
            await ReloadScopeOwnerAsync(merged.Token, merged.Scope);
        });
    }

    private async Task CaptureDatabaseAsync(bool replaceSelected)
    {
        if (_edit == null || _model == null) return;
        var selected = _resources.SelectedItem as ScopedResource;
        if (replaceSelected && selected?.Kind != ResourceKind.DatabaseSnapshot) return;
        await SaveFieldsAsync(Lifetime);
        SnapshotSummary? replacement = null;
        if (replaceSelected)
        {
            int ordinal = _model.Resources.IndexOf(selected!);
            var targets = await _runtime.StateStore.ReadRuntimeScopeTargetsAsync(_edit.ExpectedToken, Lifetime);
            var target = targets.SingleOrDefault(t => t.ResourceOrdinal == ordinal);
            if (target?.SnapshotKey == null) throw new InvalidDataException("The selected database membership is missing or unresolved. Add a specific existing snapshot instead of guessing from its original ID.");
            replacement = await _runtime.Snapshots.GetSnapshotAsync(target.SnapshotKey.Value, Lifetime)
                ?? throw new InvalidDataException("The selected database snapshot is unavailable. Its scope membership was retained.");
        }
        Lifetime.ThrowIfCancellationRequested();
        var capture = new RelationalDatabaseCaptureWindow(_runtime, _edit.Snapshot(), _edit.ExpectedToken, replacement) { Owner = this };
        using var cancellation = Lifetime.Register(() => _ = Dispatcher.InvokeAsync(() => _ = capture.CloseAndDrainAsync()));
        bool? accepted;
        try { accepted = capture.ShowDialog(); } finally { await capture.CloseCompletion; }
        if (capture.RequiresReload)
        {
            long key = _edit.SubjectKey; ChangedScopeKeys.Add(key);
            await _edit.DisposeAsync(); _edit = null; _model = null;
            if (!IsLogicallyClosed) { _resources.ItemsSource = null; SetStatus("Capture publication needs recovery. Reload this scope before editing it."); }
            return;
        }
        Lifetime.ThrowIfCancellationRequested();
        if (accepted != true || capture.Result == null) return;
        ChangedScopeKeys.Add(capture.Result.UpdatedScopeToken.Key);
        await ReloadScopeOwnerAsync(capture.Result.UpdatedScopeToken, capture.Result.UpdatedScope);
    }

    private async Task AddExistingSnapshotAsync()
    {
        if (_edit == null || _model == null) return;
        await SaveFieldsAsync(Lifetime);
        var picker = new RelationalQueryPickerWindow<SnapshotSummary>("Existing Database Snapshots", async (cursor, ct) =>
        {
            var page = await _runtime.Snapshots.ListSnapshotsAsync(pageSize: 100, cursor: cursor as SnapshotCursor, ct: ct);
            return new(page.Items, page.Next);
        }, s => s.DisplayName) { Owner = this };
        picker.LinkOwnerLifetime(Lifetime);
        bool? accepted;
        try { accepted = picker.ShowDialog(); } finally { await picker.DrainQueriesAsync(); }
        Lifetime.ThrowIfCancellationRequested();
        if (accepted != true || picker.SelectedSummary == null) return;
        var selected = picker.SelectedSummary;
        var scope = StateCopies.Scope(_model, new(new StateLimits()));
        int ordinal = scope.Resources.Count;
        scope.Resources.Add(new() { Kind = ResourceKind.DatabaseSnapshot, Path = selected.SnapshotId,
            DisplayNameOverride = selected.DisplayName, DetailsOverride = selected.DatabaseName });
        var expectedTarget = new SnapshotRuntimeToken(_runtime.Snapshots.Epoch, selected.SnapshotKey, selected.RowVersion);
        var token = await _runtime.StateStore.SaveScopeWithSnapshotTargetAsync(scope, _edit.ExpectedToken, ordinal, expectedTarget, Guid.NewGuid(), Lifetime);
        ChangedScopeKeys.Add(token.Key); await ReloadScopeOwnerAsync(token, scope);
    }

    private async Task AddExistingDiagramAsync()
    {
        if (_edit == null || _model == null) return;
        await SaveFieldsAsync(Lifetime);
        var picker = new RelationalQueryPickerWindow<DiagramSummary>("Existing Diagrams", async (cursor, ct) =>
        {
            var page = await _runtime.State.ListDiagramsAsync(100, cursor as StateCatalogueCursor<DiagramSummary>, ct);
            return new(page.Items, page.Next);
        }, d => d.Name) { Owner = this };
        picker.LinkOwnerLifetime(Lifetime);
        bool? accepted;
        try { accepted = picker.ShowDialog(); } finally { await picker.DrainQueriesAsync(); }
        Lifetime.ThrowIfCancellationRequested();
        if (accepted != true || picker.SelectedSummary == null) return;
        var selected = picker.SelectedSummary;
        var scope = StateCopies.Scope(_model, new(new StateLimits()));
        int ordinal = scope.Resources.Count;
        scope.Resources.Add(new() { Kind = ResourceKind.Diagram, Path = selected.DiagramId, DisplayNameOverride = selected.Name });
        var token = await _runtime.StateStore.SaveScopeWithDiagramTargetAsync(scope, _edit.ExpectedToken, ordinal, selected, Guid.NewGuid(), Lifetime);
        ChangedScopeKeys.Add(token.Key); await ReloadScopeOwnerAsync(token, scope);
    }

    private async Task ReloadScopeOwnerAsync(StateToken expected, Scope model)
    {
        var loaded = Require(await _runtime.State.LoadScopeAsync(new ScopeSummary(expected, 0, model.ScopeId, model.Name), Lifetime));
        if (IsLogicallyClosed || Lifetime.IsCancellationRequested) { await loaded.DisposeAsync(); Lifetime.ThrowIfCancellationRequested(); return; }
        var old = _edit; _edit = loaded; _model = loaded.Snapshot();
        if (old != null) await old.DisposeAsync();
        ReplaceSelectedSummary(new(expected, SelectedSummary?.SortOrdinal ?? 0, _model.ScopeId, _model.Name));
        _name.Text = _model.Name; _description.Text = _model.Description; _resources.ItemsSource = _model.Resources;
    }

    private void AddDetail(UIElement control, int row) { Grid.SetRow(control, row); Details.Children.Add(control); }
    protected override async Task OnSelectedAsync(ScopeSummary? selected, CancellationToken ct)
    {
        if (Details.Width == 0 || double.IsNaN(Details.Width)) return;
        await SaveFieldsAsync(ct);
        if (_edit != null) await _edit.DisposeAsync(); _edit = null; _model = null;
        Details.IsEnabled = _save.IsEnabled = _delete.IsEnabled = false;
        _name.Text = _description.Text = string.Empty; _resources.ItemsSource = null;
        if (selected == null) return;
        var edit = Require(await _runtime.State.LoadScopeAsync(selected, ct));
        if (IsLogicallyClosed || ct.IsCancellationRequested) { await edit.DisposeAsync(); ct.ThrowIfCancellationRequested(); return; }
        _edit = edit; _model = edit.Snapshot(); _name.Text = _model.Name; _description.Text = _model.Description;
        _resources.ItemsSource = _model.Resources; Details.IsEnabled = _save.IsEnabled = _delete.IsEnabled = true;
    }

    protected override Task BeforeAcceptAsync(CancellationToken ct) => SaveFieldsAsync(ct);
    private async Task SaveFieldsAsync(CancellationToken ct)
    {
        if (_edit == null || _model == null) return;
        _resources.CommitEdit(DataGridEditingUnit.Cell, true); _resources.CommitEdit(DataGridEditingUnit.Row, true);
        var baseline = _edit.Snapshot();
        _model.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled Scope" : _name.Text.Trim();
        _model.Description = _description.Text.Trim();
        if (SameScope(baseline, _model) && !_edit.IsDirty) return;
        if (!SameScope(baseline, _model)) _edit.Replace(_model);
        await _edit.SaveAsync(ct); ChangedScopeKeys.Add(_edit.SubjectKey);
        if (!IsLogicallyClosed) ReplaceSelectedSummary(new(_edit.ExpectedToken, SelectedSummary?.SortOrdinal ?? 0, _model.ScopeId, _model.Name));
    }

    private static bool SameScope(Scope a, Scope b)
    {
        if (a.ScopeId != b.ScopeId || a.Name != b.Name || a.Description != b.Description || a.Resources.Count != b.Resources.Count || a.VirtualFolders.Count != b.VirtualFolders.Count) return false;
        for (int i = 0; i < a.Resources.Count; i++)
        {
            var x = a.Resources[i]; var y = b.Resources[i];
            if (x.ResourceId != y.ResourceId || x.Kind != y.Kind || x.Path != y.Path || x.DisplayNameOverride != y.DisplayNameOverride ||
                x.DetailsOverride != y.DetailsOverride || x.AddedAtUtc != y.AddedAtUtc || x.IncludeChildren != y.IncludeChildren) return false;
        }
        for (int i = 0; i < a.VirtualFolders.Count; i++)
        {
            var x = a.VirtualFolders[i]; var y = b.VirtualFolders[i];
            if (x.VirtualFolderId != y.VirtualFolderId || x.Name != y.Name || x.ParentNodeKey != y.ParentNodeKey || !x.ChildNodeKeys.SequenceEqual(y.ChildNodeKeys)) return false;
        }
        return true;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || !CanBeginPickerCommand) return;
        using var operation = BeginPickerOperation();
        SetPickerCommandBusy(true);
        _busy = true; Commands.IsEnabled = Details.IsEnabled = false;
        try { await action(); } catch (Exception e) { Report(e); }
        finally { _busy = false; SetPickerCommandBusy(false); if (!IsLogicallyClosed) { Commands.IsEnabled = true; Details.IsEnabled = _model != null; _editDatabase.IsEnabled = _resources.SelectedItem is ScopedResource { Kind: ResourceKind.DatabaseSnapshot }; } }
    }
    protected override bool CanClose()
    {
        if (_busy) return false;
        if (_model == null || _edit == null) return true;
        var copy = _edit.Snapshot(); copy.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled Scope" : _name.Text.Trim(); copy.Description = _description.Text.Trim();
        if (SameScope(copy, _model) && !_edit.IsDirty) return true;
        SetStatus("Save or close using the Close button before leaving this scope."); return false;
    }
    protected override async Task OnLogicalCloseAsync() { if (_edit != null) { await _edit.DisposeAsync(); _edit = null; } }
    private static T Require<T>(StateLoad<T> load) where T : class => load.Value ?? throw load.Error ?? new InvalidOperationException("The selected scope is missing or unavailable.");
}
