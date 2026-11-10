using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

internal sealed class RelationalScopePickerWindow : RelationalQueryPickerWindow<ScopeSummary>
{
    private readonly RelationalRuntime _runtime;
    private readonly TextBox _name = new(), _description = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _resources = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow };
    private readonly Button _save = CommandButton("Save", "SurfSaveButtonStyle"), _delete = CommandButton("Delete", "RelationalDangerButtonStyle");
    private readonly Button _addResource = CommandButton("Add");
    private readonly ContextMenu _addResourceMenu = new();
    private readonly CheckBox _includeChildren = new() { Content = "Include children", Margin = new(3, 6, 3, 0) };
    private readonly Grid _detailContent = new();
    private readonly bool _manage;
    private StateEditSession<Scope>? _edit;
    private Scope? _model;
    private bool _busy;
    internal Func<ScopedResource, bool>? ConfirmResourceRemoval { get; set; }
    public HashSet<long> ChangedScopeKeys { get; } = [];
    public HashSet<long> DeletedScopeKeys { get; } = [];

    public RelationalScopePickerWindow(RelationalRuntime runtime, bool manage = true) : base("Scopes", async (cursor, ct) =>
    {
        var page = await runtime.State.ListScopesAsync(100, cursor as StateCatalogueCursor<ScopeSummary>, ct);
        return new(page.Items, page.Next);
    }, s => s.Name)
    {
        _runtime = runtime; _manage = manage;
        if (!manage) return;
        AutomationProperties.SetName(_name, "Scope name");
        AutomationProperties.SetName(_description, "Scope description");
        _description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Width = 1300; MinWidth = 1000; UseFlexibleDetails(220);
        var scroll = new ScrollViewer { Content = _detailContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _detailContent.SetBinding(MinHeightProperty, new Binding(nameof(ScrollViewer.ViewportHeight)) { Source = scroll });
        Details.Children.Add(scroll);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(64), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _detailContent.RowDefinitions.Add(new() { Height = height });
        AddDetail(new TextBlock { Text = "Name", Margin = new(0, 0, 0, 4) }, 0); AddDetail(_name, 1);
        AddDetail(new TextBlock { Text = "Description", Margin = new(0, 8, 0, 4) }, 2); AddDetail(_description, 3);
        var resourceCommands = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 4) };
        _addResource.ToolTip = "Add a new resource or reuse an existing Surf resource";
        AutomationProperties.SetName(_addResource, "Add resource");
        ConfigureAddResourceMenu();
        resourceCommands.Children.Add(_addResource);
        _resources.SelectionChanged += (_, _) => UpdateResourceSelection();
        AddDetail(resourceCommands, 4);
        var aliasText = new FrameworkElementFactory(typeof(TextBlock));
        aliasText.SetBinding(TextBlock.TextProperty, new Binding(nameof(ScopedResource.DisplayName)));
        aliasText.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ScopedResource.DisplayName)));
        aliasText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var aliasEdit = new FrameworkElementFactory(typeof(TextBox));
        aliasEdit.SetBinding(TextBox.TextProperty, new Binding(nameof(ScopedResource.DisplayNameOverride))
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        _resources.Columns.Add(new DataGridTemplateColumn { Header = "Alias", Width = 130, SortMemberPath = nameof(ScopedResource.DisplayName),
            CellTemplate = new DataTemplate { VisualTree = aliasText }, CellEditingTemplate = new DataTemplate { VisualTree = aliasEdit } });
        _resources.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(ScopedResource.ResourceTypeDisplay)), IsReadOnly = true, Width = 90 });
        _resources.Columns.Add(new DataGridTextColumn { Header = "Path/Resource", Binding = new Binding(nameof(ScopedResource.Details)), IsReadOnly = true,
            Width = new(1, DataGridLengthUnitType.Star), MinWidth = 160 });
        var addedHeader = new TextBlock { Text = "Added at", ToolTip = "Added to scope (AddedAtUtc), shown in local time." };
        AutomationProperties.SetName(addedHeader, "Added to scope, local time");
        _resources.Columns.Add(new DataGridTextColumn { Header = addedHeader, Binding = new Binding(nameof(ScopedResource.AddedDisplay)), IsReadOnly = true,
            SortMemberPath = nameof(ScopedResource.AddedAtUtc), Width = 150 });
        AddResourceActionColumn();
        foreach (var column in _resources.Columns.OfType<DataGridTextColumn>())
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(((Binding)column.Binding).Path.Path)));
            column.ElementStyle = textStyle;
        }
        _resources.MinHeight = 140;
        _resources.SetResourceReference(Control.BackgroundProperty, "Theme.SurfaceBrush");
        _resources.SetResourceReference(Control.ForegroundProperty, "Theme.TextBrush");
        _resources.SetResourceReference(Control.BorderBrushProperty, "Theme.StrongBorderBrush");
        _resources.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Theme.BorderBrush");
        _resources.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "Theme.BorderBrush");
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Theme.SurfaceBrush")));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Theme.TextBrush")));
        var selectedCell = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selectedCell.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Theme.SelectionBrush")));
        selectedCell.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Theme.SelectionTextBrush")));
        cellStyle.Triggers.Add(selectedCell); _resources.CellStyle = cellStyle;
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Theme.SurfaceBrush")));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Theme.TextBrush")));
        _resources.RowStyle = rowStyle;
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Theme.PanelBrush")));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Theme.TextBrush")));
        _resources.ColumnHeaderStyle = headerStyle;
        AddDetail(_resources, 5);
        _includeChildren.ToolTip = "Include files and subfolders as link/portal targets for this folder.";
        AutomationProperties.SetName(_includeChildren, "Include children for selected folder");
        AutomationProperties.SetHelpText(_includeChildren, "Include files and subfolders as link or portal targets.");
        _includeChildren.SetBinding(CheckBox.IsCheckedProperty, new Binding("SelectedItem." + nameof(ScopedResource.IncludeChildren))
        { Source = _resources, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        AddDetail(_includeChildren, 6); UpdateResourceSelection();
        // The outer scroller provides short-window access, but must never give the DataGrid an unbounded viewport.
        var headers = _detailContent.Children.OfType<FrameworkElement>().Where(c => c != _resources).ToArray();
        var resourceHeight = new MultiBinding { Converter = new ResourceViewportHeight(headers.Sum(c => c.Margin.Top + c.Margin.Bottom)) };
        resourceHeight.Bindings.Add(new Binding(nameof(ScrollViewer.ViewportHeight)) { Source = scroll });
        foreach (var header in headers) resourceHeight.Bindings.Add(new Binding(nameof(ActualHeight)) { Source = header });
        _resources.SetBinding(HeightProperty, resourceHeight);
        Button create = CommandButton("New"), merge = CommandButton("Add Scope Resources");
        merge.ToolTip = "Add resources and virtual folders from another scope; keep the source scope.";
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

    private void ConfigureAddResourceMenu()
    {
        var create = new MenuItem { Header = "New" };
        var existing = new MenuItem { Header = "Existing" };
        _addResourceMenu.Items.Add(create); _addResourceMenu.Items.Add(existing);
        Add(create, "Folder", () => AddPhysicalResourceAsync(ResourceKind.Folder));
        Add(create, "File", () => AddPhysicalResourceAsync(ResourceKind.File));
        Add(create, "Database", () => EditDatabaseAsync(replaceSelected: false));
        Add(create, "Diagram", AddNewDiagramAsync);
        Add(existing, "Folder", () => AddExistingPhysicalResourceAsync(ResourceKind.Folder));
        Add(existing, "File", () => AddExistingPhysicalResourceAsync(ResourceKind.File));
        Add(existing, "Database", AddExistingSnapshotAsync);
        Add(existing, "Diagram", AddExistingDiagramAsync);
        _addResource.ContextMenu = _addResourceMenu;
        _addResource.Click += (_, _) =>
        {
            _addResourceMenu.PlacementTarget = _addResource;
            _addResourceMenu.Placement = PlacementMode.Bottom;
            _addResourceMenu.IsOpen = true;
        };
        void Add(MenuItem parent, string label, Func<Task> action)
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await RunAsync(async () => { if (CommitResourceEdits()) await action(); });
            parent.Items.Add(item);
        }
    }

    private void AddResourceActionColumn()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        Add("\uE70F", "Edit resource", Color.FromRgb(0x25, 0x63, 0xEB), true, async (_, e) =>
        {
            if (e.Source is Button { DataContext: ScopedResource resource })
                await RunAsync(() => EditResourceAsync(resource));
        });
        Add("×", "Remove resource", Color.FromRgb(0xDC, 0x26, 0x26), false, (_, e) =>
        {
            if (e.Source is Button { DataContext: ScopedResource resource }) RemoveResource(resource);
        });
        _resources.Columns.Add(new DataGridTemplateColumn
        {
            Header = "", Width = 76, MinWidth = 76, MaxWidth = 76, IsReadOnly = true,
            CanUserSort = false, CanUserReorder = false,
            CellTemplate = new DataTemplate { VisualTree = panel }
        });
        void Add(string glyph, string label, Color color, bool iconFont, RoutedEventHandler click)
        {
            var button = new FrameworkElementFactory(typeof(Button));
            var style = new Style(typeof(Button), (Style)FindResource("SurfPrimaryButtonStyle"));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(color)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Hidden));
            var selected = new DataTrigger
            {
                Binding = new Binding(nameof(DataGridRow.IsSelected)) { RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1) },
                Value = true
            };
            selected.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible)); style.Triggers.Add(selected);
            button.SetValue(FrameworkElement.StyleProperty, style);
            button.SetValue(ContentControl.ContentProperty, glyph);
            button.SetValue(FrameworkElement.ToolTipProperty, label);
            button.SetValue(AutomationProperties.NameProperty, label);
            button.SetValue(Control.FontFamilyProperty, new FontFamily(iconFont ? "Segoe MDL2 Assets" : "Segoe UI"));
            button.SetValue(Control.FontSizeProperty, iconFont ? 14.0 : 21.0);
            button.SetValue(FrameworkElement.WidthProperty, 28.0); button.SetValue(FrameworkElement.HeightProperty, 28.0);
            button.SetValue(FrameworkElement.MinWidthProperty, 28.0); button.SetValue(Control.PaddingProperty, new Thickness(0));
            button.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
            button.AddHandler(Button.ClickEvent, click); panel.AppendChild(button);
        }
    }

    private Task AddPhysicalResourceAsync(ResourceKind kind)
    {
        if (_model == null) return Task.CompletedTask;
        if (kind == ResourceKind.Folder)
        {
            var dialog = new OpenFolderDialog { Title = "Add a folder reference" };
            if (dialog.ShowDialog(this) == true) AddPhysical(new() { Kind = kind, Path = dialog.FolderName });
        }
        else
        {
            var dialog = new OpenFileDialog { Title = "Add file references", Multiselect = true, CheckFileExists = true };
            if (dialog.ShowDialog(this) == true)
                foreach (string path in dialog.FileNames) AddPhysical(new() { Kind = kind, Path = Path.GetFullPath(path) });
        }
        return Task.CompletedTask;
    }

    private void AddPhysical(ScopedResource resource)
    {
        if (_model == null) return;
        static string Identity(string path)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path.Trim(); }
        }
        if (_model.Resources.Any(r => r.Kind == resource.Kind && string.Equals(Identity(r.Path), Identity(resource.Path), StringComparison.OrdinalIgnoreCase)))
        { SetStatus("That resource is already in this scope."); return; }
        _model.Resources.Add(resource); _resources.SelectedItem = resource;
    }

    private async Task AddExistingPhysicalResourceAsync(ResourceKind kind)
    {
        if (_edit == null || _model == null) return;
        await SaveFieldsAsync(Lifetime);
        var picker = new RelationalQueryPickerWindow<PhysicalResourceChoice>($"Existing {kind} References", async (cursor, ct) =>
        {
            var page = await _runtime.StateStore.ListPhysicalResourceChoicesAsync(kind, _edit.SubjectKey, 100, cursor as StateCursor, ct);
            return new(page.Items, page.Next);
        }, r => $"{r.Resource.DisplayName} — {r.Resource.Path} ({r.ScopeName})") { Owner = this };
        picker.LinkOwnerLifetime(Lifetime);
        bool? accepted;
        try { accepted = picker.ShowDialog(); } finally { await picker.DrainQueriesAsync(); }
        Lifetime.ThrowIfCancellationRequested();
        if (accepted == true && picker.SelectedSummary is { } selected)
        {
            var resource = selected.Resource;
            AddPhysical(new() { Kind = kind, Path = resource.Path, DisplayNameOverride = resource.DisplayNameOverride,
                DetailsOverride = resource.DetailsOverride, IncludeChildren = resource.IncludeChildren });
        }
    }

    private async Task AddNewDiagramAsync()
    {
        if (_edit == null || _model == null) return;
        var prompt = new RenameResourceWindow("New Diagram") { Owner = this, Title = "New Diagram" };
        if (prompt.ShowDialog() != true) return;
        await SaveFieldsAsync(Lifetime);
        var diagram = new DiagramDocument { Name = prompt.ResourceName };
        var scope = _edit.Snapshot();
        scope.Resources.Add(new() { Kind = ResourceKind.Diagram, Path = diagram.DiagramId,
            DisplayNameOverride = diagram.Name, DetailsOverride = "Diagram" });
        var catalogue = await _runtime.StateStore.ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue.Diagrams, Lifetime);
        var saved = await _runtime.StateStore.CreateDiagramInScopeAsync(new(diagram, new Dictionary<int, byte[]>()), scope,
            _edit.ExpectedToken, catalogue, Guid.NewGuid(), Lifetime);
        ChangedScopeKeys.Add(saved.Scope.Key); await ReloadScopeOwnerAsync(saved.Scope, scope);
        SetStatus("Created an empty diagram. Open it from Object Explorer to edit its contents.");
    }

    private async Task EditResourceAsync(ScopedResource resource)
    {
        if (_model == null || !_model.Resources.Contains(resource) || !CommitResourceEdits()) return;
        _resources.SelectedItem = resource;
        if (resource.Kind == ResourceKind.DatabaseSnapshot) { await EditDatabaseAsync(replaceSelected: true); return; }
        var prompt = new RenameResourceWindow(resource.DisplayName) { Owner = this, Title = "Edit Resource Alias" };
        if (prompt.ShowDialog() != true) return;
        resource.DisplayNameOverride = prompt.ResourceName; _resources.Items.Refresh();
    }

    private void RemoveResource(ScopedResource resource)
    {
        if (_busy || !CanBeginPickerCommand || _model == null || !_model.Resources.Contains(resource) || !_resources.SelectedItems.Contains(resource) || !CommitResourceEdits()) return;
        bool confirmed = ConfirmResourceRemoval?.Invoke(resource) ?? MessageBox.Show(this,
            $"Remove '{resource.DisplayName}' from this scope?\n\nThe underlying resource will be retained.", "Remove Resource",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (confirmed) _model.Resources.Remove(resource);
    }

    private bool CommitResourceEdits() => _resources.CommitEdit(DataGridEditingUnit.Cell, true) &&
        _resources.CommitEdit(DataGridEditingUnit.Row, true);

    private async Task<SnapshotSummary?> PrepareDatabaseEditAsync(bool replaceSelected)
    {
        if (_edit == null || _model == null) throw new InvalidOperationException("Select a scope first.");
        var selected = _resources.SelectedItem as ScopedResource;
        if (replaceSelected && selected?.Kind != ResourceKind.DatabaseSnapshot)
            throw new InvalidOperationException("Select a database snapshot to edit.");
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
        return replacement;
    }

    private async Task EditDatabaseAsync(bool replaceSelected)
    {
        var workflow = new DatabaseResourceWorkflowWindow(replaceSelected ? (_resources.SelectedItem as ScopedResource)?.DisplayName : null) { Owner = this };
        if (workflow.ShowDialog() != true) return;
        if (workflow.SelectedWorkflow == DatabaseResourceWorkflow.DirectConnection)
        {
            await CaptureDatabaseAsync(replaceSelected); return;
        }
        var replacement = await PrepareDatabaseEditAsync(replaceSelected);
        var service = new RelationalSnapshotEditorService(_runtime);
        DatabaseMetadataSnapshot? model;
        if (workflow.SelectedWorkflow == DatabaseResourceWorkflow.ExportFolder)
        {
            var imported = await Task.Run(() => new DatabaseExportFolderImportService().ImportAsync(workflow.ExportFolderPath!, replacement?.DisplayName, Lifetime), Lifetime);
            Lifetime.ThrowIfCancellationRequested();
            if (imported.Warnings.Count > 0 && MessageBox.Show(this, string.Join("\n\n", imported.Warnings) + "\n\nContinue to review the imported database?",
                "Import Export Folder", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            model = imported.Snapshot;
            if (replacement != null)
            {
                model.SnapshotId = replacement.SnapshotId;
                if (string.IsNullOrWhiteSpace(model.DatabaseName)) model.DatabaseName = replacement.DatabaseName;
            }
        }
        else model = replacement == null ? null : await Task.Run(() => service.ReadAsync(replacement, Lifetime), Lifetime);
        Lifetime.ThrowIfCancellationRequested();
        var dialog = model == null ? new DatabaseResourceWindow() : new DatabaseResourceWindow(model);
        dialog.Title = replaceSelected ? "Edit Database" : "Add Database";
        dialog.Owner = this;
        using var cancellation = Lifetime.Register(() => _ = Dispatcher.InvokeAsync(dialog.Close));
        if (dialog.ShowDialog() != true || dialog.Snapshot is not { } edited) return;
        Lifetime.ThrowIfCancellationRequested();
        SnapshotRuntimeToken? expected = replacement == null ? null :
            new(_runtime.Session.Epoch, replacement.SnapshotKey, replacement.RowVersion);
        var scope = _edit!.Snapshot();
        var scopeToken = _edit.ExpectedToken;
        RelationalCaptureResult result;
        try
        {
            result = await Task.Run(() => service.SaveAsync(edited, scope, scopeToken, expected, Lifetime), Lifetime);
        }
        catch (SnapshotPublicationOutcomeUnknownException)
        {
            ChangedScopeKeys.Add(_edit.SubjectKey);
            await _edit.DisposeAsync(); _edit = null; _model = null;
            if (!IsLogicallyClosed) _resources.ItemsSource = null;
            throw;
        }
        Lifetime.ThrowIfCancellationRequested();
        ChangedScopeKeys.Add(result.UpdatedScopeToken.Key);
        await ReloadScopeOwnerAsync(result.UpdatedScopeToken, result.UpdatedScope);
    }

    private async Task CaptureDatabaseAsync(bool replaceSelected)
    {
        var replacement = await PrepareDatabaseEditAsync(replaceSelected);
        var capture = new RelationalDatabaseCaptureWindow(_runtime, _edit!.Snapshot(), _edit.ExpectedToken, replacement, allowTargetSelection: false) { Owner = this };
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

    private void AddDetail(UIElement control, int row) { Grid.SetRow(control, row); _detailContent.Children.Add(control); }
    private sealed class ResourceViewportHeight(double margins) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            double viewport = values[0] is double height && double.IsFinite(height) ? height : 0;
            double headers = values.Skip(1).OfType<double>().Where(double.IsFinite).Sum();
            return Math.Max(140, viewport - headers - margins);
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
    private void UpdateResourceSelection()
    {
        _includeChildren.IsEnabled = !_busy && _resources.SelectedItem is ScopedResource { Kind: ResourceKind.Folder };
    }
    protected override async Task OnSelectedAsync(ScopeSummary? selected, CancellationToken ct)
    {
        if (!_manage) return;
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
        bool wasEnabled = Details.IsEnabled;
        Details.IsEnabled = false;
        try
        {
            if (!CommitResourceEdits()) throw new InvalidOperationException("Finish editing the selected resource before saving.");
            var baseline = _edit.Snapshot();
            _model.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled Scope" : _name.Text.Trim();
            _model.Description = _description.Text.Trim();
            if (SameScope(baseline, _model) && !_edit.IsDirty) return;
            if (!SameScope(baseline, _model)) _edit.Replace(_model);
            await _edit.SaveAsync(ct); ChangedScopeKeys.Add(_edit.SubjectKey);
            if (!IsLogicallyClosed) ReplaceSelectedSummary(new(_edit.ExpectedToken, SelectedSummary?.SortOrdinal ?? 0, _model.ScopeId, _model.Name));
        }
        finally { if (!IsLogicallyClosed) Details.IsEnabled = wasEnabled && !_busy && _model != null; }
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
        finally { _busy = false; SetPickerCommandBusy(false); if (!IsLogicallyClosed) { Commands.IsEnabled = true; Details.IsEnabled = _model != null; UpdateResourceSelection(); } }
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
