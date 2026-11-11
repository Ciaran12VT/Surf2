using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

/// <summary>The master file picker, populated from bounded version identity metadata.</summary>
public partial class RelationalHistoryFilePickerWindow : Window
{
    private const int MaximumRows = 100_000;
    private const long MaximumMetadataBytes = 32L * 1024 * 1024;
    private readonly RelationalRuntime _runtime;
    private readonly RelationalSnapshotHistoryService _service;
    private readonly RelationalComparisonTarget _defaultTarget;
    private readonly ObservableCollection<FileChoice> _filteredItems = [];
    private IReadOnlyList<FileChoice> _files = [];
    private ExplorerCancellationLifetime? _request;
    private Task _loadTask = Task.CompletedTask;
    private Task? _initialization, _closeTask;
    private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _generation;
    private bool _initializing = true, _busy, _closing, _closed, _allowClose, _accepted;

    public RelationalHistoryFilePickerWindow(RelationalRuntime runtime, IReadOnlyList<SnapshotSummary> scopedSnapshots,
        RelationalComparisonTarget defaultTarget)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _service = new(runtime);
        _defaultTarget = defaultTarget ?? throw new ArgumentNullException(nameof(defaultTarget));
        if (defaultTarget.Epoch != runtime.Session.Epoch) throw new SnapshotConcurrencyException();
        ArgumentNullException.ThrowIfNull(scopedSnapshots);
        long bytes = 0;
        if (scopedSnapshots.Count > MaximumRows) throw new InvalidOperationException("Too many databases are in scope for this picker.");
        foreach (var snapshot in scopedSnapshots)
            AddBudget(ref bytes, 256L + 2L * (snapshot.DisplayName.Length + snapshot.DatabaseName.Length + snapshot.SnapshotId.Length));
        InitializeComponent();
        FilesListView.ItemsSource = _filteredItems;
        DatabaseComboBox.ItemsSource = scopedSnapshots.OrderBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        DatabaseComboBox.SelectedItem = scopedSnapshots.FirstOrDefault(snapshot => snapshot.SnapshotKey == defaultTarget.SnapshotKey)
            ?? DatabaseComboBox.Items.Cast<SnapshotSummary>().FirstOrDefault();
        _initializing = false;
        Loaded += async (_, _) => { await InitializeAsync(); if (!_closing) FilterTextBox.Focus(); };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            if (CleanupFailure is { } failure) _closeCompletion.TrySetException(failure); else _closeCompletion.TrySetResult();
        };
    }

    public RelationalComparisonTarget? SelectedTarget { get; private set; }
    public Exception? CleanupFailure { get; private set; }
    public Task Initialization => _initialization ?? Task.CompletedTask;
    public Task CurrentOperation => _loadTask;
    public Task CloseCompletion => _closeCompletion.Task;
    public Task InitializeAsync() => _initialization ??= UpdateDatabaseAsync();
    public Task CloseAndDrainAsync() { Dispatcher.VerifyAccess(); if (!_closed) Close(); return CloseCompletion; }

    private async void DatabaseComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_initializing && !_closing) await UpdateDatabaseAsync(); }
    private async void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_initializing && !_closing) await UpdateVersionAsync(); }
    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    { if (!_initializing && !_closing) ApplyFilter(selectDefault: false); }
    private void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PickButton.IsEnabled = !_busy && !_closing && FilesListView.SelectedItem is FileChoice;
    private async void FilesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await PickSelectedAsync();
    private async void PickButton_Click(object sender, RoutedEventArgs e) => await PickSelectedAsync();
    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private Task UpdateDatabaseAsync()
    {
        Dispatcher.VerifyAccess();
        var snapshot = DatabaseComboBox.SelectedItem as SnapshotSummary;
        _initializing = true;
        VersionComboBox.ItemsSource = null;
        _initializing = false;
        VersionComboBox.IsEnabled = false;
        return StartRequest(async (generation, ct) =>
        {
            if (snapshot == null) { StatusTextBlock.Text = "No database selected."; return; }
            var versions = await _service.GetDisplayedVersionsAsync(snapshot, ct);
            if (!Accepts(generation, ct)) return;
            var choices = versions.Select((version, index) => new VersionChoice(snapshot, version,
                version.VersionName + (index == 0 ? " (Current)" : ""))).ToArray();
            _initializing = true;
            VersionComboBox.ItemsSource = choices;
            VersionComboBox.SelectedItem = choices.FirstOrDefault();
            _initializing = false;
            VersionComboBox.IsEnabled = true;
            if (choices.Length == 0) { StatusTextBlock.Text = "No version selected."; return; }
            await LoadFilesAsync(choices[0], generation, ct);
        }, "Loading versions…");
    }

    private Task UpdateVersionAsync()
    {
        var choice = VersionComboBox.SelectedItem as VersionChoice;
        return StartRequest(async (generation, ct) =>
        {
            if (choice == null) { StatusTextBlock.Text = "No version selected."; return; }
            await LoadFilesAsync(choice, generation, ct);
        }, "Loading files…");
    }

    private async Task LoadFilesAsync(VersionChoice choice, long generation, CancellationToken ct)
    {
        var files = await Task.Run(() => ReadFilesAsync(choice, ct), ct);
        if (!Accepts(generation, ct)) return;
        _files = files;
        ApplyFilter(selectDefault: true);
        StatusTextBlock.Text = $"{_filteredItems.Count} file(s).";
    }

    private async Task<IReadOnlyList<FileChoice>> ReadFilesAsync(VersionChoice choice, CancellationToken ct)
    {
        await using var context = await _service.OpenVersionAsync(choice.Snapshot.SnapshotKey, choice.Version.VersionKey, ct).ConfigureAwait(false);
        if (!context.Header.RowVersion.AsSpan().SequenceEqual(choice.Snapshot.RowVersion)) throw new SnapshotConcurrencyException();
        var result = new List<FileChoice>(); long bytes = 0;
        foreach (var collection in new[] { HistoricalCollection.Objects, HistoricalCollection.Tables, HistoricalCollection.TableDataSets })
            await foreach (var entry in context.StreamAsync(collection, ct).ConfigureAwait(false))
            {
                DatabaseVersionedResourceKind? kind = collection switch
                {
                    HistoricalCollection.Tables => DatabaseVersionedResourceKind.TableMetadata,
                    HistoricalCollection.TableDataSets => DatabaseVersionedResourceKind.TableData,
                    _ => SnapshotIdentity.ResourceKind(entry.ObjectKind ?? SqlDatabaseObjectKind.Unknown)
                };
                if (!kind.HasValue || !entry.ResourceKey.HasValue) continue;
                if (result.Count == MaximumRows) throw new InvalidOperationException("This version exceeds the file picker resource limit.");
                string name = SqlName.FormatPlainMultipartName(entry.SchemaName, entry.Name);
                string path = FilePath(kind.Value, name);
                AddBudget(ref bytes, 512L + 2L * (entry.SchemaName.Length + entry.Name.Length + name.Length + path.Length));
                result.Add(new(choice, entry, kind.Value, name, path));
            }
        return result.OrderBy(file => DatabaseHistoryDisplay.GetResourceTypeSortOrder(file.Kind))
            .ThenBy(file => file.FileNameDisplay, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void ApplyFilter(bool selectDefault)
    {
        string filter = FilterTextBox.Text.Trim();
        var matches = _files.Where(file => filter.Length == 0 ||
            DatabaseHistoryDisplay.ContainsText(file.ResourceTypeDisplay, filter) ||
            DatabaseHistoryDisplay.ContainsText(file.FileNameDisplay, filter) ||
            DatabaseHistoryDisplay.ContainsText(file.DisplayName, filter) ||
            DatabaseHistoryDisplay.ContainsText(file.RelativePath, filter));
        _filteredItems.Clear();
        foreach (var file in matches.Take(500)) _filteredItems.Add(file);
        if (!_busy || _files.Count != 0)
            StatusTextBlock.Text = VersionComboBox.SelectedItem == null ? "No version selected." : $"{_filteredItems.Count} file(s).";
        FilesListView.SelectedItem = selectDefault ? _filteredItems.FirstOrDefault(file =>
            file.Version.Snapshot.SnapshotKey == _defaultTarget.SnapshotKey && file.Entry.ResourceKey == _defaultTarget.ResourceKey &&
            ResourceKind(file.Kind) == _defaultTarget.Resource.Kind) : null;
        if (FilesListView.SelectedItem is FileChoice selected) FilesListView.ScrollIntoView(selected);
        PickButton.IsEnabled = !_busy && !_closing && FilesListView.SelectedItem is FileChoice;
    }

    private Task PickSelectedAsync()
    {
        if (_busy || _closing || FilesListView.SelectedItem is not FileChoice selected) return Task.CompletedTask;
        return StartRequest(async (generation, ct) =>
        {
            // Only the chosen identity is resolved. Resolving every candidate
            // would repeatedly scan a version and turn metadata browsing quadratic.
            await using var context = await _service.OpenVersionAsync(selected.Version.Snapshot.SnapshotKey,
                selected.Version.Version.VersionKey, ct);
            if (!context.Header.RowVersion.AsSpan().SequenceEqual(selected.Version.Snapshot.RowVersion)) throw new SnapshotConcurrencyException();
            var target = await _service.ResourceTargetAsync(context, selected.Entry, ct);
            if (!Accepts(generation, ct)) return;
            SelectedTarget = target with { Resource = target.Resource with
            { DisplayName = selected.Version.Snapshot.DisplayName + " " + selected.Version.DisplayName + ": " + selected.FileNameDisplay } };
            _accepted = true; Close();
        }, "Selecting file…", clearFiles: false);
    }

    private Task StartRequest(Func<long, CancellationToken, Task> work, string status, bool clearFiles = true)
    {
        Dispatcher.VerifyAccess();
        if (_closing || _closed) return Task.CompletedTask;
        var previous = _loadTask;
        Task callbacks = _request?.Cancel() ?? Task.CompletedTask;
        var request = new ExplorerCancellationLifetime(); _request = request;
        long generation = ++_generation;
        _busy = true; PickButton.IsEnabled = false; SelectedTarget = null;
        if (clearFiles) { _files = []; _filteredItems.Clear(); }
        StatusTextBlock.Text = status;
        return _loadTask = RunAsync();

        async Task RunAsync()
        {
            var ct = request.Token;
            try
            {
                await callbacks; await previous; ct.ThrowIfCancellationRequested();
                await work(generation, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (Accepts(generation, ct)) StatusTextBlock.Text = error.Message;
            }
            finally
            {
                await request.RetireAsync();
                if (ReferenceEquals(_request, request)) _request = null;
                if (!_closing && generation == _generation)
                {
                    _busy = false; VersionComboBox.IsEnabled = true;
                    PickButton.IsEnabled = FilesListView.SelectedItem is FileChoice;
                }
            }
        }
    }

    private bool Accepts(long generation, CancellationToken ct) => !_closing && !_closed && generation == _generation && !ct.IsCancellationRequested;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; ++_generation;
        DatabaseComboBox.IsEnabled = VersionComboBox.IsEnabled = FilterTextBox.IsEnabled = FilesListView.IsEnabled = PickButton.IsEnabled = false;
        var callbacks = _request?.Cancel() ?? Task.CompletedTask;
        _closeTask = CloseCoreAsync(callbacks, _loadTask);
    }

    private async Task CloseCoreAsync(Task callbacks, Task load)
    {
        try { await callbacks; await load; }
        catch (Exception error) { CleanupFailure ??= error; }
        await Dispatcher.InvokeAsync(() =>
        {
            _allowClose = true;
            if (_accepted) DialogResult = true; else Close();
        });
    }

    private static void AddBudget(ref long bytes, long additional)
    {
        if (additional < 0 || additional > MaximumMetadataBytes - bytes)
            throw new InvalidOperationException("The file picker metadata exceeds its memory limit. Narrow the active scope.");
        bytes += additional;
    }
    private static ComparisonResourceKind ResourceKind(DatabaseVersionedResourceKind kind) => kind switch
    {
        DatabaseVersionedResourceKind.StoredProcedure => ComparisonResourceKind.StoredProcedure,
        DatabaseVersionedResourceKind.Function => ComparisonResourceKind.Function,
        DatabaseVersionedResourceKind.View => ComparisonResourceKind.View,
        DatabaseVersionedResourceKind.Trigger => ComparisonResourceKind.Trigger,
        DatabaseVersionedResourceKind.TableMetadata => ComparisonResourceKind.TableMetadata,
        DatabaseVersionedResourceKind.TableData => ComparisonResourceKind.TableData,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static string FilePath(DatabaseVersionedResourceKind kind, string name) => (kind switch
    {
        DatabaseVersionedResourceKind.StoredProcedure => "Stored Procedures/",
        DatabaseVersionedResourceKind.Function => "Functions/",
        DatabaseVersionedResourceKind.View => "Views/",
        DatabaseVersionedResourceKind.Trigger => "Triggers/",
        DatabaseVersionedResourceKind.TableMetadata => "Tables/",
        DatabaseVersionedResourceKind.TableData => "Table Data/",
        _ => ""
    }) + name + (kind == DatabaseVersionedResourceKind.TableData ? ".csv" : kind == DatabaseVersionedResourceKind.TableMetadata ? ".txt" : ".sql");
    public sealed record VersionChoice(SnapshotSummary Snapshot, SnapshotVersionSummary Version, string DisplayName);
    public sealed record FileChoice(VersionChoice Version, HistoricalSnapshotEntry Entry, DatabaseVersionedResourceKind Kind,
        string DisplayName, string RelativePath)
    {
        public string ResourceTypeDisplay => DatabaseHistoryDisplay.GetResourceTypeDisplay(Kind);
        public string FileNameDisplay => DatabaseHistoryDisplay.GetFileNameDisplay(DisplayName, RelativePath);
    }
}
