using System.IO;
using System.Windows;
using System.Windows.Controls;
using Surf2.Models;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

namespace Surf2;

public partial class MainWindow
{
    private readonly CancellationTokenSource _relationalStateLifetime = new();
    private Task _relationalImageMenuTask = Task.CompletedTask;
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

    private void QueueRelationalImageMenuRefresh()
    {
        _relationalSelectedImageDefinition = null; _selectedDiagramImageId = null;
        if (!_relationalImageMenuTask.IsCompleted || _relationalStateLifetime.IsCancellationRequested) return;
        _relationalImageMenuTask = ReadRelationalImageMenuAsync(null);
    }
    private async Task ReadRelationalImageMenuAsync(PreferenceCursor<ImageDefinitionSummary>? cursor)
    {
        try
        {
            using var operation = BeginRelationalStateOperation();
            var page = RequireRelationalLoad(await RelationalStateRuntime.Preferences.ListImagesAsync(100, cursor, _relationalStateLifetime.Token), "image catalogue");
            _relationalStateLifetime.Token.ThrowIfCancellationRequested();
            var menu = DiagramImageToolButton?.ContextMenu; if (menu == null) return;
            menu.Items.Clear();
            foreach (var summary in page.Items)
            {
                var item = new MenuItem { Header = summary.Name, Tag = summary, IsEnabled = summary.HasAsset };
                item.Click += DiagramImageToolMenuItem_Click; menu.Items.Add(item);
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
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "read image catalogue"); }
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
            var asset = RequireRelationalLoad(await RelationalStateRuntime.Preferences.ReadImageAssetAsync(summary.Key, edit.ExpectedToken, _relationalStateLifetime.Token), "chosen image bytes");
            var chosen = edit.Snapshot(); _ = DecodeRelationalImage(asset.CopyBytes());
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
            if (_relationalStateClosing || !ReferenceEquals(scope, _relationalExplorerScope) || documentGeneration != _relationalDocumentContextGeneration || _isDiagramLocked) return;
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
