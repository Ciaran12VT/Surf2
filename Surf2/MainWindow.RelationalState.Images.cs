using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

namespace Surf2;

public partial class MainWindow
{
    private ImageSource GetRelationalDiagramImageSource(DiagramObjectSnapshot snapshot, out string imageData)
    {
        var payload = FindRelationalImagePayload(snapshot)
            ?? throw new InvalidDataException("The image occurrence has no selected SQL asset.");
        imageData = snapshot.ImageDataBase64;
        return DecodeRelationalImage(payload.DisplayBytes);
    }

    private void ApplyRelationalDiagramImageSnapshot(DiagramImageControl image, DiagramObjectSnapshot snapshot)
    {
        var payload = FindRelationalImagePayload(snapshot)
            ?? throw new InvalidDataException("The image occurrence has no selected SQL asset.");
        image.ApplyImage(snapshot.ImageDefinitionId, snapshot.ImageName, DecodeRelationalImage(payload.DisplayBytes),
            snapshot.ImageDataBase64, snapshot.PastedImageFileName);
        _relationalControlImages.Remove(image);
        _relationalControlImages.Add(image, payload);
    }

    private async Task EditRelationalDiagramImageAsync(DiagramImageControl image)
    {
        try
        {
            using var operation = BeginRelationalStateOperation();
            long generation = _relationalSelectionGeneration;
            var choices = await RelationalStateRuntime.Preferences.ReadImageChoicesAsync(operation.Token);
            if (_relationalStateClosing || generation != _relationalSelectionGeneration || !DiagramCanvas.Children.Contains(image)) return;
            var dialog = new DiagramImageDetailsWindow(image.LabelText, image.LabelFontSize, choices,
                image.ImageSource, image.ImageName) { Owner = this };
            if (dialog.ShowDialog() != true || generation != _relationalSelectionGeneration ||
                _relationalStateClosing || _isDiagramLocked || !DiagramCanvas.Children.Contains(image)) return;
            var before = CreateDiagramObjectSnapshot(image);
            if (dialog.SelectedImage is DiagramImageDefinition replacement)
            {
                byte[] bytes = StateImages.Decode(replacement.ImageDataBase64, RelationalRuntimeStateLimits,
                    new(RelationalRuntimeStateLimits), operation.Token) ?? throw new InvalidDataException("The selected image is empty.");
                var payload = new RelationalImagePayload(string.Empty, bytes, null, null);
                image.ApplyImage(replacement.Id, replacement.Name, DecodeRelationalImage(bytes), replacement.ImageDataBase64);
                _relationalControlImages.Remove(image);
                _relationalControlImages.Add(image, payload);
            }
            image.ApplyDetails(dialog.LabelText, dialog.LabelFontSize);
            var after = CreateDiagramObjectSnapshot(image);
            if (before != null && after != null) PushDiagramUndo(DiagramUndoActionKind.Modified, before, after);
            StatusText = "Updated diagram image.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "change diagram image"); }
    }

    private readonly CancellationTokenSource _relationalStateLifetime = new();
    private Task _relationalImageMenuTask = Task.CompletedTask;
    private StateToken? _relationalImagePreviewOwner;
    private readonly Dictionary<long, ImageSource> _relationalImagePreviews = [];
    private Task _relationalImageSelectionTask = Task.CompletedTask;
    private DiagramImageDefinition? _relationalSelectedImageDefinition;
    private long _relationalImageSelectionGeneration;
    private bool _relationalStateClosing;
    private Task? _relationalStateDrainTask;
    private Task? _relationalStateFirstCancellation;
    private readonly HashSet<Task> _relationalStateOperations = [];
    private sealed class RelationalStateOperation : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AccessCancellation _cancellation = new();
        private readonly CancellationTokenRegistration _ownerCancellation, _callerCancellation;
        private bool _disposed;
        public RelationalStateOperation(MainWindow owner, CancellationToken caller)
        {
            _owner = owner;
            _ownerCancellation = owner._relationalStateLifetime.Token.Register(static state => _ = ((AccessCancellation)state!).Cancel(), _cancellation);
            _callerCancellation = caller.Register(static state => _ = ((AccessCancellation)state!).Cancel(), _cancellation);
        }
        public CancellationToken Token => _cancellation.Token;
        public Task Completion => _finished.Task;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _ownerCancellation.Dispose(); _callerCancellation.Dispose();
            _ = FinishAsync();
        }
        private async Task FinishAsync()
        {
            try { await _cancellation.RetireAsync(); }
            catch (Exception error) { _finished.TrySetException(error); return; }
            _owner._relationalStateOperations.Remove(Completion); _finished.TrySetResult();
        }
    }
    private RelationalStateOperation BeginRelationalStateOperation(CancellationToken ct = default)
    {
        Dispatcher.VerifyAccess();
        if (_relationalStateClosing) throw new OperationCanceledException("The window is closing.");
        var operation = new RelationalStateOperation(this, ct); _relationalStateOperations.Add(operation.Completion); return operation;
    }

    private void QueueRelationalImageMenuRefresh(bool includePreviews = false)
    {
        _relationalSelectedImageDefinition = null; _selectedDiagramImageId = null;
        if (!_relationalImageMenuTask.IsCompleted || _relationalStateLifetime.IsCancellationRequested) return;
        _relationalImageMenuTask = ReadRelationalImageMenuAsync(null, includePreviews);
    }
    private async Task ReadRelationalImageMenuAsync(PreferenceCursor<ImageDefinitionSummary>? cursor, bool includePreviews = true)
    {
        try
        {
            using var operation = BeginRelationalStateOperation();
            var page = RequireRelationalLoad(await RelationalStateRuntime.Preferences.ListImagesAsync(100, cursor, _relationalStateLifetime.Token), "image catalogue");
            _relationalStateLifetime.Token.ThrowIfCancellationRequested();
            var menu = DiagramImageToolButton?.ContextMenu; if (menu == null) return;
            if (_relationalImagePreviewOwner == null || !StateEditSession<StartupPreferences>.SameToken(page.Owner, _relationalImagePreviewOwner))
            {
                _relationalImagePreviews.Clear(); _relationalImagePreviewOwner = page.Owner;
            }
            // Keep only the displayed page's thumbnails; source image bytes are released after decoding.
            var pageKeys = page.Items.Select(item => item.Key).ToHashSet();
            foreach (long key in _relationalImagePreviews.Keys.Where(key => !pageKeys.Contains(key)).ToArray()) _relationalImagePreviews.Remove(key);
            menu.Items.Clear();
            var imageItems = new Dictionary<long, MenuItem>();
            foreach (var summary in page.Items)
            {
                _relationalImagePreviews.TryGetValue(summary.Key, out var preview);
                var item = new MenuItem { Header = CreateRelationalImageMenuHeader(summary.Name, preview), Tag = summary, IsEnabled = summary.HasAsset };
                item.Click += DiagramImageToolMenuItem_Click; menu.Items.Add(item);
                imageItems.Add(summary.Key, item);
            }
            if (page.Next != null)
            {
                var next = new MenuItem { Header = "Next page" };
                next.Click += async (_, _) => { if (_relationalStateClosing || !_relationalImageMenuTask.IsCompleted) return; _relationalImageMenuTask = ReadRelationalImageMenuAsync(page.Next); await _relationalImageMenuTask; if (!_relationalStateClosing) OpenDiagramImageToolMenu(); };
                menu.Items.Add(new Separator()); menu.Items.Add(next);
            }
            if (cursor != null)
            {
                var first = new MenuItem { Header = "First page" };
                first.Click += async (_, _) => { if (_relationalStateClosing || !_relationalImageMenuTask.IsCompleted) return; _relationalImageMenuTask = ReadRelationalImageMenuAsync(null); await _relationalImageMenuTask; if (!_relationalStateClosing) OpenDiagramImageToolMenu(); };
                menu.Items.Add(first);
            }
            if (page.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No images configured", IsEnabled = false });
            if (!includePreviews && !menu.IsOpen) return;
            var missing = page.Items.Where(item => item.HasAsset && !_relationalImagePreviews.ContainsKey(item.Key)).Select(item => item.Key).ToArray();
            var loading = new MenuItem { Header = "Loading image previews...", IsEnabled = false };
            if (missing.Length > 0) menu.Items.Insert(0, loading);
            // Small batches cap transient source-byte retention even when the catalogue contains large images.
            try
            {
                foreach (long[] keys in missing.Chunk(3))
                {
                    var assets = await RelationalStateRuntime.Preferences.ReadImageAssetsAsync(keys, page.Owner, operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    foreach (var (key, asset) in assets)
                    {
                        using var stream = new MemoryStream(asset.CopyBytes(), false);
                        var preview = new BitmapImage(); preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad;
                        preview.DecodePixelWidth = 48; preview.StreamSource = stream; preview.EndInit(); preview.Freeze();
                        _relationalImagePreviews[key] = preview;
                        var item = imageItems[key];
                        item.Header = CreateRelationalImageMenuHeader(((ImageDefinitionSummary)item.Tag).Name, preview);
                    }
                }
            }
            finally { menu.Items.Remove(loading); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "read image catalogue"); }
    }

    private static UIElement CreateRelationalImageMenuHeader(string name, ImageSource? preview)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 160 };
        panel.Children.Add(new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform, Source = preview });
        panel.Children.Add(new TextBlock { Text = name, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private Task SelectRelationalDiagramImageAsync(ImageDefinitionSummary summary)
    {
        _relationalImageSelectionTask = SelectRelationalDiagramImageCoreAsync(summary, ++_relationalImageSelectionGeneration);
        return _relationalImageSelectionTask;
    }
    private async Task SelectRelationalDiagramImageCoreAsync(ImageDefinitionSummary summary, long generation)
    {
        try
        {
            using var operation = BeginRelationalStateOperation();
            await using var edit = RequireRelationalLoad(await RelationalStateRuntime.Preferences.LoadImageAsync(summary.Key, _relationalStateLifetime.Token), "chosen image definition");
            var chosen = edit.Snapshot();
            if (string.IsNullOrWhiteSpace(chosen.ImageDataBase64)) throw new InvalidDataException("The selected image has no SQL asset.");
            if (generation != _relationalImageSelectionGeneration || _relationalStateLifetime.IsCancellationRequested) return;
            _relationalSelectedImageDefinition = chosen; _selectedDiagramImageId = chosen.Id;
            _isUpdatingDiagramToolToggles = true;
            try
            {
                DiagramSelectionToolButton.IsChecked = DiagramRectangleToolButton.IsChecked = DiagramEllipseToolButton.IsChecked = false;
                DiagramLineToolButton.IsChecked = DiagramLabelToolButton.IsChecked = DiagramPortalToolButton.IsChecked = DiagramInfoPointToolButton.IsChecked = false;
                DiagramImageToolButton.IsChecked = true;
            }
            finally { _isUpdatingDiagramToolToggles = false; }
            DiagramImageToolButton.ToolTip = "Image: " + chosen.Name; StatusText = "Image tool: " + chosen.Name + ".";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "load chosen image"); }
    }

    private Task DrainRelationalStateQueriesAsync() => _relationalStateDrainTask ??= DrainRelationalStateQueriesCoreAsync();

    // Signal promptly during startup; only Drain owns eventual source disposal.
    private void CancelRelationalStateQueries()
    {
        Dispatcher.VerifyAccess();
        if (_relationalStateClosing) return;
        _relationalStateClosing = true; _relationalImageSelectionGeneration++;
        RelationalScopeResourceAdditionHandler = null; RelationalScopeResourceRemovalHandler = null;
        _relationalSelectionGeneration++;
        _relationalStateFirstCancellation ??= _relationalStateLifetime.CancelAsync();
        if (_relationalSelectionLifetime != null) _ = _relationalSelectionLifetime.CancelAsync();
    }

    private async Task DrainRelationalStateQueriesCoreAsync()
    {
        Dispatcher.VerifyAccess();
        CancelRelationalStateQueries();
        if (_relationalSelectionLifetime != null) RetireRelationalSelection(_relationalSelectionLifetime);
        _relationalSelectionLifetime = null;
        // Keep the first callback batch, including failures, while every query lease drains.
        var firstCancellation = _relationalStateFirstCancellation!;
        try
        {
            await Task.WhenAll(_relationalStateOperations.ToArray().Concat(_relationalSelectionRetirements)
                .Append(firstCancellation).Append(_relationalImageMenuTask).Append(_relationalImageSelectionTask).Append(_relationalWorkbenchRefreshTask));
        }
        finally { _relationalSelectionRetirements.Clear(); _relationalStateLifetime.Dispose(); }
    }

    private async Task PlaceRelationalDiagramResourceAsync(string name, string link, ResourceKind kind, Point point)
    {
        try
        {
            using var operation = BeginRelationalStateOperation();
            if (TryBlockDiagramObjectEditWhenLocked("add resources to the diagram")) return;
            var owner = _relationalPreferenceEdit?.ExpectedToken ?? throw new InvalidOperationException("Preferences are not loaded.");
            var scope = _relationalExplorerScope; long documentGeneration = _relationalDocumentContextGeneration;
            var budget = new StateBudget(new StateLimits { MaximumAggregateBytes = 8L * 1024 * 1024 });
            RuntimeImageRuleCursor? cursor = null; RuntimeImageRule? matched = null;
            string? content = null; bool readContent = false;
            do
            {
                var page = await RelationalStateRuntime.StateStore.ListRuntimeImageRulesAsync(owner, cursor, ct: _relationalStateLifetime.Token);
                foreach (var rule in page.Items)
                {
                    budget.Row(); budget.Text(256);
                    foreach (var field in new[] { rule.Id, rule.Name, rule.NameRegex, rule.ContentRegex, rule.LegacyRegex, rule.MatchTarget, rule.ResourceTypeFilter }) budget.Text(checked((long)field.Length * 2));
                    if (!DiagramImageResourceTypeMatches(rule.ResourceTypeFilter, kind)) continue;
                    string namePattern = rule.NameRegex.Trim(), contentPattern = rule.ContentRegex.Trim();
                    if (namePattern.Length == 0 && contentPattern.Length == 0 && !string.IsNullOrWhiteSpace(rule.LegacyRegex))
                    {
                        if (DiagramImageDefinition.NormalizeMatchTarget(rule.MatchTarget) == DiagramImageDefinition.ContentMatchTarget) contentPattern = rule.LegacyRegex.Trim();
                        else namePattern = rule.LegacyRegex.Trim();
                    }
                    if (namePattern.Length == 0 && contentPattern.Length == 0) continue;
                    if (namePattern.Length > 0 && (!TryCreateDiagramImageRegex(namePattern, out var nameRegex) || nameRegex == null || !nameRegex.IsMatch(name))) continue;
                    if (contentPattern.Length > 0)
                    {
                        if (!TryCreateDiagramImageRegex(contentPattern, out var contentRegex) || contentRegex == null) continue;
                        if (!readContent)
                        {
                            readContent = true;
                            var documents = _relationalDocuments ?? throw new InvalidOperationException("Document access is closing.");
                            var address = await documents.ResolveAsync(link, scope, null, _relationalStateLifetime.Token);
                            var plan = await documents.DescribeTextAsync(address, _relationalStateLifetime.Token);
                            if (plan.EstimatedTextBytes > 8L * 1024 * 1024) throw new InvalidDataException("Automatic image matching requires selected content below 8 MiB. Choose an image tool explicitly for larger resources.");
                            content = (await documents.ReadTextAsync(plan, _relationalStateLifetime.Token)).Text;
                            await documents.ValidateTextPlanAsync(plan, scope, _relationalStateLifetime.Token);
                        }
                        if (string.IsNullOrEmpty(content) || !contentRegex.IsMatch(content)) continue;
                    }
                    matched = rule; break;
                }
                cursor = matched == null ? page.Next : null;
            } while (cursor != null);
            if (matched == null) { if (!_relationalStateClosing) StatusText = "No diagram image regex matched '" + name + "'."; return; }
            await using var edit = RequireRelationalLoad(await RelationalStateRuntime.Preferences.LoadImageAsync(matched.Key, _relationalStateLifetime.Token), "matched image definition");
            if (!SameRelationalOwner(edit.ExpectedToken, owner)) throw new StateConflictException("matched image definition");
            var asset = RequireRelationalLoad(await RelationalStateRuntime.Preferences.ReadImageAssetAsync(matched.Key, owner, _relationalStateLifetime.Token), "matched image asset");
            var definition = edit.Snapshot(); var imageSource = DecodeRelationalImage(asset.CopyBytes());
            link = await RelationalStateRuntime.StateStore.NormalizeDocumentLinkAsync(NormalizeRelationalMetadataLink(link), operation.Token);
            if (_relationalStateClosing || !Services.RelationalRuntime.SameExplorerSelection(scope, _relationalExplorerScope) || documentGeneration != _relationalDocumentContextGeneration || _isDiagramLocked) return;
            var image = new Surf2.Controls.DiagramImageControl(definition.Id, definition.Name, imageSource, definition.ImageDataBase64);
            AttachDiagramImageHandlers(image); image.SetCanvasBounds(point.X - 60, point.Y - 48, 120, 96);
            image.ApplyDetails(name); image.ApplyMetadata(new DiagramObjectMetadata { Link = link }); ApplyDefaultDiagramZIndex(image);
            DiagramCanvas.Children.Add(image); SelectDiagramObject(image); PushDiagramUndo(DiagramUndoActionKind.Added, null, CreateDiagramObjectSnapshot(image));
            StatusText = "Added '" + name + "' using diagram image '" + definition.Name + "'.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "match a resource image"); }
    }
}
