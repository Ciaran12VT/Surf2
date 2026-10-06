using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;
using Surf2.Storage;
using Microsoft.Win32;

namespace Surf2;

public partial class SettingsWindow
{
    private RelationalRuntime? _relationalPreferences;
    private StateToken? _relationalPreferencesOwner;
    private readonly AccessCancellation _relationalPreferenceLifetime = new();
    private readonly HashSet<Task> _relationalPreferenceOperations = [];
    private CancellationTokenRegistration _relationalPreferenceOwnerCancellation;
    private Task? _relationalPreferenceCleanup;
    private Task? _relationalPreferenceOwnerClose;
    private bool _relationalPreferenceClosing, _relationalPreferenceOwnerClosing;
    private readonly ObservableCollection<object> _relationalExtensions = [];
    private readonly ObservableCollection<object> _relationalStyles = [];
    private readonly ObservableCollection<object> _relationalImages = [];
    private readonly Dictionary<object, RelationalPreferenceEdit> _relationalPreferenceEdits = new(ReferenceEqualityComparer.Instance);
    private readonly List<PreferenceImageMove> _relationalImageMoves = [];
    private PreferenceCursor<ExtensionAppearanceSummary>? _relationalExtensionNext;
    private PreferenceCursor<ReferenceStyleSummary>? _relationalStyleNext;
    private PreferenceCursor<ImageDefinitionSummary>? _relationalImageNext;
    private long _relationalExtensionGeneration, _relationalStyleGeneration, _relationalImageGeneration;
    private long _relationalPreferenceRetainedBytes;
    private bool _relationalPreferenceLoading, _relationalPreferenceSaving, _relationalPreferenceFailed;
    private sealed class RelationalPreferenceEdit(long? key, object model, string original)
    {
        public long? Key { get; } = key;
        public object Model { get; } = model;
        public string Original { get; } = original;
        public bool Deleted { get; set; }
    }

    private sealed class RelationalPreferenceOperation : IDisposable
    {
        private readonly SettingsWindow _owner;
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AccessCancellation _cancellation = new();
        private readonly CancellationTokenRegistration _ownerCancellation, _callerCancellation;
        private bool _disposed;
        public RelationalPreferenceOperation(SettingsWindow owner, CancellationToken caller)
        {
            _owner = owner;
            _ownerCancellation = owner._relationalPreferenceLifetime.Token.Register(static state => _ = ((AccessCancellation)state!).Cancel(), _cancellation);
            _callerCancellation = caller.Register(static state => _ = ((AccessCancellation)state!).Cancel(), _cancellation);
        }
        public Task Completion => _finished.Task;
        public CancellationToken Token => _cancellation.Token;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _ownerCancellation.Dispose(); _callerCancellation.Dispose(); _ = FinishAsync();
        }
        private async Task FinishAsync()
        {
            try { await _cancellation.RetireAsync(); }
            catch (Exception error) { _finished.TrySetException(error); return; }
            _owner._relationalPreferenceOperations.Remove(Completion); _finished.TrySetResult();
        }
    }
    private RelationalPreferenceOperation BeginRelationalPreferenceOperation(CancellationToken ct = default)
    {
        if (_relationalPreferenceClosing) throw new OperationCanceledException();
        var operation = new RelationalPreferenceOperation(this, ct); _relationalPreferenceOperations.Add(operation.Completion); return operation;
    }
    internal Task DrainRelationalPreferenceQueriesAsync() => _relationalPreferenceCleanup ??= DrainRelationalPreferenceQueriesCoreAsync();
    private Task CloseRelationalPreferenceOwnerAsync() => _relationalPreferenceOwnerClose ??= CloseRelationalPreferenceOwnerCoreAsync();
    private async Task CloseRelationalPreferenceOwnerCoreAsync()
    {
        if (_relationalPreferenceClosing) return;
        using var operation = BeginRelationalPreferenceOperation();
        _relationalPreferenceOwnerClosing = true;
        // The package/connection owner vetoes Close until its actual operation retires.
        // Observe that owner rather than bypassing its close guard or disposing its source.
        while (_relationalPersistenceOperation is { } persistence)
        {
            await persistence.Cancel();
            if (_relationalPersistenceOperation != null) await Task.Delay(50);
        }
        if (!_relationalPreferenceClosing) Close();
    }
    private async Task DrainRelationalPreferenceQueriesCoreAsync()
    {
        _relationalPreferenceClosing = true;
        _relationalExtensionGeneration++; _relationalStyleGeneration++; _relationalImageGeneration++;
        // AccessCancellation retains the first CancelAsync callback batch, not a later already-cancelled task.
        var callbacks = _relationalPreferenceLifetime.Cancel();
        try { await Task.WhenAll(_relationalPreferenceOperations.ToArray().Append(callbacks)); }
        finally
        {
            _relationalPreferenceEdits.Clear(); _relationalPreferenceOwnerCancellation.Dispose();
            await _relationalPreferenceLifetime.RetireAsync();
        }
    }

    public async Task InitializeRelationalPreferencesAsync(RelationalRuntime runtime, StateToken expectedOwner,
        CancellationToken ct = default)
    {
        _relationalPreferences = runtime; _relationalPreferencesOwner = expectedOwner;
        var ownerLifetime = ct;
        using var operation = BeginRelationalPreferenceOperation(ct);
        ct = operation.Token;
        Closing += (_, e) =>
        {
            if (_relationalPreferenceSaving && !_relationalPreferenceOwnerClosing) { e.Cancel = true; return; }
            if (!e.Cancel) { _relationalPreferenceClosing = true; _ = _relationalPreferenceLifetime.Cancel(); }
        };
        Closed += (_, _) => { _relationalPreferenceClosing = true; _ = DrainRelationalPreferenceQueriesAsync(); };
        _relationalPreferenceOwnerCancellation = ownerLifetime.Register(() =>
        {
            _ = _relationalPreferenceLifetime.Cancel();
            _ = Dispatcher.InvokeAsync(() => _ = CloseRelationalPreferenceOwnerAsync());
        });
        _relationalPreferenceLoading = true;
        try
        {
            _backcolors.Clear(); _highlightStyles.Clear(); _diagramImages.Clear();
            _activeSetting = null; _activeHighlightStyle = null; _activeDiagramImage = null;
            Settings.CodeWindows.BackcolorsByExtension.Clear(); Settings.ReferenceHighlights.Styles.Clear(); Settings.DiagramImages.Images.Clear();
            BackcolorList.ItemsSource = _relationalExtensions; HighlightStyleList.ItemsSource = _relationalStyles; DiagramImageList.ItemsSource = _relationalImages;
            LoadSelectedBackcolor(null); LoadSelectedHighlightStyle(null); LoadSelectedDiagramImage(null);
            InstallRelationalNextButton(BackcolorList, () => ReadRelationalPreferencePageAsync(0));
            InstallRelationalNextButton(HighlightStyleList, () => ReadRelationalPreferencePageAsync(1));
            InstallRelationalNextButton(DiagramImageList, () => ReadRelationalPreferencePageAsync(2));
            await ReadRelationalPreferencePageAsync(0, ct); await ReadRelationalPreferencePageAsync(1, ct); await ReadRelationalPreferencePageAsync(2, ct);
        }
        catch { _relationalPreferenceFailed = true; throw; }
        finally { _relationalPreferenceLoading = false; }
    }

    private void InstallRelationalNextButton(ListBox list, Func<Task> next)
    {
        if (list.Parent is not Grid grid) return;
        var button = new Button { Content = "Next", ToolTip = "Next catalogue page", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 4), MinWidth = 52 };
        button.SetResourceReference(StyleProperty, "SurfCompactPrimaryButtonStyle");
        Grid.SetRow(button, Grid.GetRow(list));
        list.Margin = new Thickness(list.Margin.Left, 32, list.Margin.Right, list.Margin.Bottom);
        button.Click += async (_, _) => { if (_relationalPreferenceClosing) return; button.IsEnabled = false; try { await next(); } catch (Exception error) { RelationalPreferenceError(error); } finally { if (!_relationalPreferenceClosing) button.IsEnabled = true; } };
        grid.Children.Add(button);
    }

    private async Task ReadRelationalPreferencePageAsync(int kind, CancellationToken ct = default)
    {
        if (_relationalPreferences == null || _relationalPreferenceClosing) return;
        using var operation = BeginRelationalPreferenceOperation(ct);
        if (!_relationalPreferenceLoading && !CommitRelationalActivePreferences()) return;
        ct = operation.Token;
        var access = _relationalPreferences.Preferences;
        _relationalPreferenceLoading = true;
        try
        {
            if (kind == 0)
            {
                var page = RequirePreference(await access.ListExtensionAppearanceAsync(100, _relationalExtensionNext, ct));
                ct.ThrowIfCancellationRequested(); _relationalExtensionGeneration++;
                CheckPreferenceOwner(page.Owner); _relationalExtensions.Clear(); foreach (var row in page.Items) _relationalExtensions.Add(row); _relationalExtensionNext = page.Next;
                _activeSetting = null; LoadSelectedBackcolor(null);
            }
            else if (kind == 1)
            {
                var page = RequirePreference(await access.ListStylesAsync(100, _relationalStyleNext, ct));
                ct.ThrowIfCancellationRequested(); _relationalStyleGeneration++;
                CheckPreferenceOwner(page.Owner); _relationalStyles.Clear(); foreach (var row in page.Items) _relationalStyles.Add(row); _relationalStyleNext = page.Next;
                _activeHighlightStyle = null; LoadSelectedHighlightStyle(null);
            }
            else
            {
                var page = RequirePreference(await access.ListImagesAsync(100, _relationalImageNext, ct));
                ct.ThrowIfCancellationRequested(); _relationalImageGeneration++;
                CheckPreferenceOwner(page.Owner); _relationalImages.Clear(); foreach (var row in page.Items) _relationalImages.Add(row); _relationalImageNext = page.Next;
                _activeDiagramImage = null; LoadSelectedDiagramImage(null);
            }
        }
        finally { _relationalPreferenceLoading = false; }
    }

    private static T RequirePreference<T>(StateLoad<T> load) where T : class => load.Value ?? throw load.Error ??
        new InvalidDataException("Selected preference is missing; no replacement will be created.");
    private void CheckPreferenceOwner(StateToken token)
    {
        if (_relationalPreferencesOwner == null || !StateEditSession<StartupPreferences>.SameToken(token, _relationalPreferencesOwner))
            throw new StateConflictException("settings catalogue");
    }

    private async Task SelectRelationalPreferenceAsync(int kind)
    {
        if (_relationalPreferenceLoading || _relationalPreferences == null || _relationalPreferenceClosing) return;
        using var operation = BeginRelationalPreferenceOperation();
        long generation = kind == 0 ? ++_relationalExtensionGeneration : kind == 1 ? ++_relationalStyleGeneration : ++_relationalImageGeneration;
        object? selected = kind == 0 ? BackcolorList.SelectedItem : kind == 1 ? HighlightStyleList.SelectedItem : DiagramImageList.SelectedItem;
        try
        {
            if (!CommitRelationalActivePreferences()) return;
            if (kind == 0) { _activeSetting = null; LoadSelectedBackcolor(null); }
            else if (kind == 1) { _activeHighlightStyle = null; LoadSelectedHighlightStyle(null); }
            else { _activeDiagramImage = null; LoadSelectedDiagramImage(null); }
            object? model = selected;
            long? key = null;
            if (selected is ExtensionAppearanceSummary extension)
            {
                key = extension.Key; model = FindPreferenceEdit<ExtensionBackcolorSetting>(key.Value);
                if (model == null) { await using var edit = RequirePreference(await _relationalPreferences.Preferences.LoadExtensionAppearanceAsync(key.Value, _relationalPreferenceLifetime.Token)); CheckPreferenceOwner(edit.ExpectedToken); model = edit.Snapshot(); }
            }
            else if (selected is ReferenceStyleSummary style)
            {
                key = style.Key; model = FindPreferenceEdit<ReferenceHighlightStyleSetting>(key.Value);
                if (model == null) { await using var edit = RequirePreference(await _relationalPreferences.Preferences.LoadStyleAsync(key.Value, _relationalPreferenceLifetime.Token)); CheckPreferenceOwner(edit.ExpectedToken); model = edit.Snapshot(); }
            }
            else if (selected is ImageDefinitionSummary image)
            {
                key = image.Key; model = FindPreferenceEdit<DiagramImageDefinition>(key.Value);
                if (model == null) { await using var edit = RequirePreference(await _relationalPreferences.Preferences.LoadImageAsync(key.Value, _relationalPreferenceLifetime.Token)); CheckPreferenceOwner(edit.ExpectedToken); model = edit.Snapshot(); }
            }
            if (generation != (kind == 0 ? _relationalExtensionGeneration : kind == 1 ? _relationalStyleGeneration : _relationalImageGeneration) || _relationalPreferenceClosing || _relationalPreferenceLifetime.Token.IsCancellationRequested) return;
            if (model == null) return;
            TrackPreferenceEdit(key, model);
            var rows = kind == 0 ? _relationalExtensions : kind == 1 ? _relationalStyles : _relationalImages;
            int index = selected == null ? -1 : rows.IndexOf(selected);
            if (index >= 0 && !ReferenceEquals(selected, model))
            {
                _relationalPreferenceLoading = true;
                try { rows[index] = model; if (kind == 0) BackcolorList.SelectedItem = model; else if (kind == 1) HighlightStyleList.SelectedItem = model; else DiagramImageList.SelectedItem = model; }
                finally { _relationalPreferenceLoading = false; }
            }
            if (model is ExtensionBackcolorSetting e) { _activeSetting = e; LoadSelectedBackcolor(e); }
            else if (model is ReferenceHighlightStyleSetting s) { _activeHighlightStyle = s; LoadSelectedHighlightStyle(s); }
            else if (model is DiagramImageDefinition i) { _activeDiagramImage = i; LoadSelectedDiagramImage(i); MoveDiagramImageDownButton.IsEnabled = DiagramImageList.SelectedIndex < _relationalImages.Count - 1; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { RelationalPreferenceError(error); }
    }

    private T? FindPreferenceEdit<T>(long key) where T : class => _relationalPreferenceEdits.Values
        .FirstOrDefault(e => e.Key == key && e.Model is T && !e.Deleted)?.Model as T;
    private void TrackPreferenceEdit(long? key, object model)
    {
        if (_relationalPreferenceEdits.ContainsKey(model)) return;
        if (_relationalPreferenceEdits.Count >= 200) throw new InvalidDataException("Save and reopen settings before editing more than 200 selected children.");
        string original = JsonSerializer.Serialize(model, model.GetType());
        long length = checked((long)original.Length * 2);
        if (length > 64L * 1024 * 1024 - _relationalPreferenceRetainedBytes) throw new InvalidDataException("Selected preference edits exceed the 64 MiB retained edit budget. Save and reopen settings.");
        _relationalPreferenceRetainedBytes += length; _relationalPreferenceEdits.Add(model, new(key, model, original));
    }
    private bool CommitRelationalActivePreferences() =>
        (_activeSetting == null || CommitFieldsToSetting(_activeSetting, true)) &&
        (_activeHighlightStyle == null || CommitHighlightFieldsToSetting(_activeHighlightStyle, true)) &&
        (_activeDiagramImage == null || CommitDiagramImageFieldsToSetting(_activeDiagramImage, true));

    private void AddRelationalPreference(int kind)
    {
        if (!CommitRelationalActivePreferences()) return;
        object model = kind == 0 ? new ExtensionBackcolorSetting { Extension = ".txt", Backcolor = Settings.CodeWindows.DefaultBackcolor,
            Language = CodeWindowSettings.PlainTextLanguage } : new DiagramImageDefinition { Name = "New Image", SortOrder = int.MaxValue };
        try
        {
            TrackPreferenceEdit(null, model);
            if (kind == 0) { _relationalExtensions.Add(model); BackcolorList.SelectedItem = model; }
            else { _relationalImages.Add(model); DiagramImageList.SelectedItem = model; }
        }
        catch (Exception error) { RelationalPreferenceError(error); }
    }
    private void RemoveRelationalPreference(int kind)
    {
        object? selected = kind == 0 ? _activeSetting : _activeDiagramImage;
        if (selected == null || !_relationalPreferenceEdits.TryGetValue(selected, out var edit)) return;
        edit.Deleted = true;
        var list = kind == 0 ? _relationalExtensions : _relationalImages;
        object? row = list.FirstOrDefault(r => ReferenceEquals(r, selected) || r is ExtensionAppearanceSummary e && e.Key == edit.Key || r is ImageDefinitionSummary i && i.Key == edit.Key);
        if (row != null) list.Remove(row);
        if (kind == 0) { _activeSetting = null; LoadSelectedBackcolor(null); } else { _activeDiagramImage = null; LoadSelectedDiagramImage(null); }
    }
    private void MoveRelationalImage(int offset)
    {
        int index = DiagramImageList.SelectedIndex, adjacent = index + offset;
        if (index < 0 || adjacent < 0 || adjacent >= _relationalImages.Count || !CommitRelationalActivePreferences()) return;
        long? KeyOf(object row) => row is ImageDefinitionSummary summary ? summary.Key : _relationalPreferenceEdits.TryGetValue(row, out var edit) ? edit.Key : null;
        var key = KeyOf(_relationalImages[index]); var other = KeyOf(_relationalImages[adjacent]);
        if (!key.HasValue || !other.HasValue) { SetDiagramImageError("Save new images before reordering them."); return; }
        _relationalImageMoves.Add(new(key.Value, other.Value)); _relationalImages.Move(index, adjacent);
    }

    private async Task SaveRelationalPreferenceSelectionAsync()
    {
        if (_relationalPreferences == null || _relationalPreferencesOwner == null || _relationalPreferenceFailed || _relationalPreferenceSaving || _relationalPreferenceClosing) return;
        if (_relationalPreferenceOperations.Count != 0) { ValidationText.Text = "Wait for the selected settings operation to finish before saving."; return; }
        using var operation = BeginRelationalPreferenceOperation();
        if (!CommitRelationalActivePreferences()) return;
        _relationalPreferenceSaving = true;
        try
        {
            SaveWorkbenchSettings(); SaveKeyboardShortcutSettings(); SaveAppearanceSettings(); SaveDiagnosticsSettings();
            var changed = _relationalPreferenceEdits.Values.Where(e => e.Key == null || e.Deleted || e.Original != JsonSerializer.Serialize(e.Model, e.Model.GetType())).ToArray();
            PreferenceChildChange<T>[] Changes<T>() where T : class => changed.Where(e => e.Model is T && !(e.Key == null && e.Deleted))
                .Select(e => new PreferenceChildChange<T>(e.Key, e.Deleted ? null : (T)e.Model)).ToArray();
            _relationalPreferencesOwner = await _relationalPreferences.StateStore.SavePreferenceSelectionAsync(Settings, _relationalPreferencesOwner,
                Changes<ExtensionBackcolorSetting>(), Changes<ReferenceHighlightStyleSetting>(), Changes<DiagramImageDefinition>(), _relationalImageMoves,
                Guid.NewGuid(), _relationalPreferenceLifetime.Token);
            if (_relationalPreferenceClosing) return;
            _relationalPreferenceSaving = false; DialogResult = true; Close();
        }
        catch (Exception error) { _relationalPreferenceFailed = true; RelationalPreferenceError(error); }
        finally { _relationalPreferenceSaving = false; }
    }
    private void RelationalPreferenceError(Exception error)
    {
        if (_relationalPreferenceClosing || error is OperationCanceledException) return;
        _relationalPreferenceFailed = true;
        InternalLogService.Error(error, "Selected preference operation failed.");
        ValidationText.Text = HighlightValidationText.Text = DiagramImageValidationText.Text =
            "Selected settings could not be loaded or saved. Existing rows were retained. Reopen settings before retrying a failed save.";
    }

    private async Task ImportRelationalPreferenceImageAsync()
    {
        if (_relationalPreferenceClosing) return;
        using var operation = BeginRelationalPreferenceOperation();
        var selected = _activeDiagramImage; if (selected == null) return;
        var dialog = new OpenFileDialog { Title = "Import Diagram Image", Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await using var source = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            if (source.Length <= 0 || source.Length > 16 * 1024 * 1024) throw new InvalidDataException("Selected image exceeds 16 MiB.");
            var bytes = new byte[checked((int)source.Length)]; await source.ReadExactlyAsync(bytes, _relationalPreferenceLifetime.Token);
            StateImages.Validate(bytes, new(), cancellationToken: _relationalPreferenceLifetime.Token);
            if (!ReferenceEquals(selected, _activeDiagramImage) || _relationalPreferenceClosing || _relationalPreferenceLifetime.Token.IsCancellationRequested) return;
            selected.ImageDataBase64 = Convert.ToBase64String(bytes); selected.OriginalFileName = Path.GetFileName(dialog.FileName);
            if (string.IsNullOrWhiteSpace(DiagramImageNameTextBox.Text) || DiagramImageNameTextBox.Text == "New Image") DiagramImageNameTextBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            CommitDiagramImageFieldsToSetting(selected, true); LoadSelectedDiagramImage(selected);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { RelationalPreferenceError(error); }
    }

    private async Task ExportRelationalSelectedImagePackageAsync()
    {
        if (_relationalPreferenceClosing) return;
        using var operation = BeginRelationalPreferenceOperation();
        if (_activeDiagramImage == null || !CommitRelationalActivePreferences()) { SetDiagramImageError("Select an image to export."); return; }
        if (MessageBox.Show(this, "Export only the selected image definition? Use Database Export for the complete saved setup.",
            "Selected Image Package", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Title = "Export Selected Image Package", Filter = "Surf2 image package|*.surfimages.zip", DefaultExt = ".surfimages.zip", FileName = "selected-image.surfimages.zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var copy = _activeDiagramImage.Clone();
        try { await _diagramImagePackageService.ExportAsync(dialog.FileName, [copy], _relationalPreferenceLifetime.Token); if (!_relationalPreferenceClosing) SetDiagramImageStatus("Exported the selected image.", false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { RelationalPreferenceError(error); }
    }

    private async Task ImportRelationalImagePackageAsync()
    {
        if (_relationalPreferenceClosing) return;
        using var operation = BeginRelationalPreferenceOperation();
        if (!CommitRelationalActivePreferences()) return;
        var dialog = new OpenFileDialog { Title = "Import Image Package", Filter = "Surf2 image package|*.surfimages.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await using var source = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, true);
            if (archive.Entries.Count > 201) throw new InvalidDataException("Image package exceeds 200 selected images.");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal); long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Repeated image package entry.");
                if (entry.FullName != "manifest.json" && (!entry.FullName.StartsWith("images/", StringComparison.Ordinal) ||
                    Path.GetFileName(entry.FullName[7..]) != entry.FullName[7..] || entry.FullName.Contains('\\') || entry.FullName[7..].Length == 0))
                    throw new InvalidDataException("Invalid image package entry name.");
                long limit = entry.FullName == "manifest.json" ? 1024 * 1024 : 16 * 1024 * 1024;
                if (entry.Length <= 0 || entry.Length > limit || entry.Length > 64L * 1024 * 1024 - total) throw new InvalidDataException("Image package exceeds its byte budget.");
                total += entry.Length;
            }
            if (!entries.TryGetValue("manifest.json", out var manifestEntry)) throw new InvalidDataException("Missing image manifest.");
            await using var manifestStream = manifestEntry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<DiagramImagePackageManifest>(manifestStream, cancellationToken: _relationalPreferenceLifetime.Token) ?? throw new InvalidDataException("Null image manifest.");
            if (manifest.FormatVersion != 1 || manifest.Images.Count == 0 || manifest.Images.Count > 200 - _relationalPreferenceEdits.Count)
                throw new InvalidDataException("Unsupported or oversized selected image package.");
            var pending = new List<DiagramImageDefinition>(); var seen = new HashSet<string>(StringComparer.Ordinal); var budget = new StateBudget(new());
            foreach (var item in manifest.Images)
            {
                if (!seen.Add(item.FileName) || item.FileName == "manifest.json" || !entries.TryGetValue(item.FileName, out var entry)) throw new InvalidDataException("Missing or repeated referenced image entry.");
                if (string.IsNullOrWhiteSpace(item.Name)) throw new InvalidDataException("Imported image name is empty.");
                foreach (string pattern in new[] { item.NameRegex, item.ContentRegex, item.Regex }) if (!string.IsNullOrWhiteSpace(pattern)) _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                var bytes = new byte[checked((int)entry.Length)]; await using var stream = entry.Open();
                await stream.ReadExactlyAsync(bytes, _relationalPreferenceLifetime.Token);
                budget.Asset(bytes.LongLength); StateImages.Validate(bytes, new(), cancellationToken: _relationalPreferenceLifetime.Token);
                pending.Add(new() { Id = item.Id, Name = item.Name, Regex = item.Regex, MatchTarget = item.MatchTarget,
                    NameRegex = item.NameRegex, ContentRegex = item.ContentRegex, ResourceTypeFilter = item.ResourceTypeFilter,
                    SortOrder = item.SortOrder, OriginalFileName = item.OriginalFileName, ImageDataBase64 = Convert.ToBase64String(bytes) });
            }
            if (seen.Count != entries.Count - 1) throw new InvalidDataException("Image package contains unreferenced entries.");
            long retained = pending.Sum(i => checked((long)JsonSerializer.Serialize(i).Length * 2));
            if (retained > 64L * 1024 * 1024 - _relationalPreferenceRetainedBytes) throw new InvalidDataException("Image package exceeds the retained edit budget.");
            _relationalPreferenceLifetime.Token.ThrowIfCancellationRequested();
            if (_relationalPreferenceClosing) return;
            foreach (var image in pending) { TrackPreferenceEdit(null, image); _relationalImages.Add(image); }
            DiagramImageList.SelectedItem = pending[0]; SetDiagramImageStatus("Imported " + pending.Count + " selected images. Save to publish them.", false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { RelationalPreferenceError(error); }
    }
}
