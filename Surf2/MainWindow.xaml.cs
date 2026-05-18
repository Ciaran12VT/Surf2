using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage;

namespace Surf2;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const double VirtualCanvasWidth = 100000;
    private const double VirtualCanvasHeight = 100000;
    private const double VirtualOriginX = VirtualCanvasWidth / 2;
    private const double VirtualOriginY = VirtualCanvasHeight / 2;
    private const double OldCanvasWidth = 3000;
    private const double OldCanvasHeight = 2000;
    private const double MinimumCanvasZoom = 0.25;
    private const double MaximumCanvasZoom = 3;
    private const double CanvasZoomStep = 1.08;

    private readonly FileTreeService _fileTreeService = new();
    private readonly SyntaxHighlightingService _syntaxHighlightingService = new();
    private readonly IWorkspaceStore _workspaceStore = new JsonWorkspaceStore();
    private readonly IScopeStore _scopeStore = new JsonScopeStore();
    private readonly ISettingsStore _settingsStore = new JsonSettingsStore();
    private readonly Dictionary<string, FloatingCodeWindow> _openWindows = new(StringComparer.OrdinalIgnoreCase);

    private WorkspaceState _workspaceState = new();
    private ScopeLibrary _scopeLibrary = new();
    private AppSettings _appSettings = new();
    private Scope? _activeScope;
    private int _windowSequence;
    private int _zIndex;
    private double _canvasZoom = 1;
    private bool _isCanvasPanning;
    private Point _canvasPanStartPoint;
    private double _canvasPanStartHorizontalOffset;
    private double _canvasPanStartVerticalOffset;
    private string _currentFolderDisplay = "No scope selected";
    private string _statusText = "Ready.";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FileSystemNode> RootNodes { get; } = [];

    public ObservableCollection<OpenWindowItem> OpenTabs { get; } = [];

    public string CurrentFolderDisplay
    {
        get => _currentFolderDisplay;
        private set
        {
            _currentFolderDisplay = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _workspaceState = await _workspaceStore.LoadAsync();
            _scopeLibrary = await _scopeStore.LoadAsync();
            _appSettings = await _settingsStore.LoadAsync();
            if (_appSettings.EnsureDefaults())
            {
                await _settingsStore.SaveAsync(_appSettings);
            }

            await MigrateSavedFolderToDefaultScopeAsync();

            _canvasZoom = NormalizeCanvasZoom(_workspaceState.CanvasZoom);
            ApplyCanvasZoom();
            MigrateLegacyWindowCoordinates();
            LoadLastActiveScope();

            foreach (OpenDocumentState documentState in _workspaceState.OpenDocuments.ToList())
            {
                if (File.Exists(documentState.FilePath))
                {
                    await OpenFileAsync(documentState.FilePath, documentState);
                }
            }

            UpdateEmptyWorkspaceHint();
            _ = Dispatcher.BeginInvoke(RestoreViewport);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not restore workspace: {ex.Message}";
        }
    }

    private async void ScopesButton_Click(object sender, RoutedEventArgs e)
    {
        var scopeManagerWindow = new ScopeManagerWindow(_scopeLibrary)
        {
            Owner = this
        };

        bool? result = scopeManagerWindow.ShowDialog();

        if (scopeManagerWindow.WasChanged)
        {
            await _scopeStore.SaveAsync(_scopeLibrary);
        }

        if (result != true || string.IsNullOrWhiteSpace(scopeManagerWindow.SelectedScopeId))
        {
            return;
        }

        bool scopeChanged = _activeScope?.ScopeId != scopeManagerWindow.SelectedScopeId;
        _scopeLibrary.LastActiveScopeId = scopeManagerWindow.SelectedScopeId;
        await _scopeStore.SaveAsync(_scopeLibrary);

        if (scopeChanged)
        {
            CloseAllOpenWindows();
            await SaveWorkspaceStateAsync();
        }

        LoadLastActiveScope();
    }

    private void LoadScope(Scope? scope)
    {
        RootNodes.Clear();
        _activeScope = scope;

        if (scope == null)
        {
            CurrentFolderDisplay = "No scope selected";
            StatusText = "Create or open a scope to load resources.";
            return;
        }

        foreach (FileSystemNode node in _fileTreeService.CreateRoots(scope.Resources))
        {
            RootNodes.Add(node);
        }

        CurrentFolderDisplay = $"Scope: {scope.Name}";
        StatusText = $"Loaded scope '{scope.Name}' with {scope.Resources.Count} resource(s).";
    }

    private void LoadLastActiveScope()
    {
        Scope? activeScope = null;

        if (!string.IsNullOrWhiteSpace(_scopeLibrary.LastActiveScopeId))
        {
            activeScope = _scopeLibrary.Scopes.FirstOrDefault(scope => scope.ScopeId == _scopeLibrary.LastActiveScopeId);
        }

        if (activeScope == null && _scopeLibrary.Scopes.Count == 1)
        {
            activeScope = _scopeLibrary.Scopes[0];
            _scopeLibrary.LastActiveScopeId = activeScope.ScopeId;
        }

        LoadScope(activeScope);
    }

    private async Task MigrateSavedFolderToDefaultScopeAsync()
    {
        if (_scopeLibrary.Scopes.Count > 0 ||
            string.IsNullOrWhiteSpace(_workspaceState.LastFolderPath) ||
            !Directory.Exists(_workspaceState.LastFolderPath))
        {
            return;
        }

        var scope = new Scope
        {
            Name = "Default Scope",
            Description = "Created from the folder that was previously opened directly."
        };

        scope.Resources.Add(new ScopedResource
        {
            Kind = ResourceKind.Folder,
            Path = _workspaceState.LastFolderPath,
            IncludeChildren = true
        });

        _scopeLibrary.Scopes.Add(scope);
        _scopeLibrary.LastActiveScopeId = scope.ScopeId;
        await _scopeStore.SaveAsync(_scopeLibrary);
    }

    private async void ObjectExplorer_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ObjectExplorer.SelectedItem is not FileSystemNode node || node.IsDirectory)
        {
            return;
        }

        await OpenFileAsync(node.FullPath);
    }

    private async void ObjectExplorerItem_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TreeViewItem? item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FileSystemNode node || node.IsDirectory)
        {
            return;
        }

        e.Handled = true;
        item.IsSelected = true;
        await OpenFileAsync(node.FullPath);
    }

    private void ObjectExplorerItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: FileSystemNode node })
        {
            _fileTreeService.LoadChildren(node);
        }
    }

    private async Task OpenFileAsync(string filePath, OpenDocumentState? existingState = null)
    {
        if (_openWindows.TryGetValue(filePath, out FloatingCodeWindow? existingWindow))
        {
            BringToFront(existingWindow);
            RevealWindow(existingWindow);
            StatusText = $"Already open: {Path.GetFileName(filePath)}";
            return;
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not open {Path.GetFileName(filePath)}: {ex.Message}";
            return;
        }

        OpenDocumentState state = existingState ?? CreateNewDocumentState(filePath);
        if (existingState == null)
        {
            _workspaceState.OpenDocuments.Add(state);
        }

        var window = new FloatingCodeWindow(state, content, _syntaxHighlightingService.GetDefinition(filePath));
        window.ApplyCodeBackcolor(GetCodeWindowBackcolor(filePath));
        window.CloseRequested += FloatingWindow_CloseRequested;
        window.BoundsChanged += FloatingWindow_BoundsChanged;
        window.BringToFrontRequested += (_, _) => BringToFront(window);

        WorkspaceCanvas.Children.Add(window);
        Canvas.SetLeft(window, state.Left);
        Canvas.SetTop(window, state.Top);
        BringToFront(window);
        RevealWindow(window);

        _openWindows[filePath] = window;
        AddOpenTab(state);
        _windowSequence++;
        StatusText = $"Opened {filePath}";
        UpdateEmptyWorkspaceHint();
    }

    private OpenDocumentState CreateNewDocumentState(string filePath)
    {
        double offset = (_windowSequence % 8) * 28;
        Point viewportOrigin = GetCurrentViewportOrigin();

        return new OpenDocumentState
        {
            FilePath = filePath,
            Left = Math.Clamp(viewportOrigin.X + 60 + offset, 0, VirtualCanvasWidth - 800),
            Top = Math.Clamp(viewportOrigin.Y + 50 + offset, 0, VirtualCanvasHeight - 540),
            Width = 760,
            Height = 500
        };
    }

    private async void FloatingWindow_CloseRequested(object? sender, EventArgs e)
    {
        if (sender is not FloatingCodeWindow window)
        {
            return;
        }

        await CloseOpenWindowAsync(window);
    }

    private async Task CloseOpenWindowAsync(FloatingCodeWindow window)
    {
        WorkspaceCanvas.Children.Remove(window);
        _openWindows.Remove(window.State.FilePath);
        _workspaceState.OpenDocuments.Remove(window.State);
        RemoveOpenTab(window.State.FilePath);
        StatusText = $"Closed {Path.GetFileName(window.State.FilePath)}";
        UpdateEmptyWorkspaceHint();
        await SaveWorkspaceStateAsync();
    }

    private void FloatingWindow_BoundsChanged(object? sender, EventArgs e)
    {
        if (sender is FloatingCodeWindow window)
        {
            window.State.Left = Canvas.GetLeft(window);
            window.State.Top = Canvas.GetTop(window);
            window.State.Width = window.Width;
            window.State.Height = window.Height;
            window.State.FontSize = Math.Max(1, window.State.FontSize);
        }
    }

    private void WorkspaceCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != WorkspaceCanvas)
        {
            return;
        }

        _isCanvasPanning = true;
        _canvasPanStartPoint = e.GetPosition(WorkspaceScrollViewer);
        _canvasPanStartHorizontalOffset = WorkspaceScrollViewer.HorizontalOffset;
        _canvasPanStartVerticalOffset = WorkspaceScrollViewer.VerticalOffset;

        WorkspaceCanvas.CaptureMouse();
        WorkspaceCanvas.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void WorkspaceCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isCanvasPanning || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point currentPoint = e.GetPosition(WorkspaceScrollViewer);
        Vector delta = currentPoint - _canvasPanStartPoint;

        WorkspaceScrollViewer.ScrollToHorizontalOffset(_canvasPanStartHorizontalOffset - delta.X);
        WorkspaceScrollViewer.ScrollToVerticalOffset(_canvasPanStartVerticalOffset - delta.Y);
        CaptureViewportState();
        e.Handled = true;
    }

    private void WorkspaceCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isCanvasPanning)
        {
            return;
        }

        _isCanvasPanning = false;
        WorkspaceCanvas.ReleaseMouseCapture();
        WorkspaceCanvas.Cursor = null;
        e.Handled = true;
    }

    private void WorkspaceCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        Point cursorInViewport = e.GetPosition(WorkspaceScrollViewer);
        Point cursorInCanvas = e.GetPosition(WorkspaceCanvas);
        double multiplier = e.Delta > 0 ? CanvasZoomStep : 1 / CanvasZoomStep;
        _canvasZoom = NormalizeCanvasZoom(_canvasZoom * multiplier);
        _workspaceState.CanvasZoom = _canvasZoom;

        ApplyCanvasZoom();
        WorkspaceScrollViewer.UpdateLayout();

        WorkspaceScrollViewer.ScrollToHorizontalOffset((cursorInCanvas.X * _canvasZoom) - cursorInViewport.X);
        WorkspaceScrollViewer.ScrollToVerticalOffset((cursorInCanvas.Y * _canvasZoom) - cursorInViewport.Y);
        CaptureViewportState();

        StatusText = $"Canvas zoom: {_canvasZoom:P0}";
        e.Handled = true;
    }

    private async void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        CloseAllOpenWindows();
        StatusText = "Closed all windows.";
        await SaveWorkspaceStateAsync();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_appSettings)
        {
            Owner = this
        };

        if (settingsWindow.ShowDialog() != true)
        {
            return;
        }

        _appSettings = settingsWindow.Settings;
        _appSettings.EnsureDefaults();
        await _settingsStore.SaveAsync(_appSettings);
        ApplySettingsToOpenWindows();
        StatusText = "Settings saved.";
    }

    private void CloseAllOpenWindows()
    {
        WorkspaceCanvas.Children.Clear();
        _openWindows.Clear();
        _workspaceState.OpenDocuments.Clear();
        OpenTabs.Clear();
        UpdateEmptyWorkspaceHint();
    }

    private void OpenTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenTabsList.SelectedItem is OpenWindowItem item)
        {
            RevealOpenTab(item);
        }
    }

    private void OpenTabsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ListBoxItem? listBoxItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (listBoxItem?.DataContext is OpenWindowItem item)
        {
            RevealOpenTab(item);
        }
    }

    private void OpenTabsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) == null)
        {
            e.Handled = true;
        }
    }

    private void OpenTabsListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem listBoxItem)
        {
            return;
        }

        listBoxItem.IsSelected = true;

        if (listBoxItem.DataContext is OpenWindowItem item)
        {
            RevealOpenTab(item);
        }
    }

    private async void CloseOpenTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (OpenTabsList.SelectedItem is not OpenWindowItem item ||
            !_openWindows.TryGetValue(item.FilePath, out FloatingCodeWindow? window))
        {
            return;
        }

        await CloseOpenWindowAsync(window);
    }

    private void RevealOpenTab(OpenWindowItem item)
    {
        if (_openWindows.TryGetValue(item.FilePath, out FloatingCodeWindow? window))
        {
            BringToFront(window);
            RevealWindow(window);
            StatusText = $"Focused {item.FileName}";
        }
    }

    private void BringToFront(FloatingCodeWindow window)
    {
        Panel.SetZIndex(window, ++_zIndex);
    }

    private void RevealWindow(FloatingCodeWindow window)
    {
        double left = Canvas.GetLeft(window);
        double top = Canvas.GetTop(window);

        if (double.IsNaN(left) || double.IsNaN(top))
        {
            return;
        }

        double margin = 0;
        double targetHorizontalOffset = Math.Max(0, (left * _canvasZoom) - margin);
        double targetVerticalOffset = Math.Max(0, (top * _canvasZoom) - margin);

        WorkspaceScrollViewer.ScrollToHorizontalOffset(targetHorizontalOffset);
        WorkspaceScrollViewer.ScrollToVerticalOffset(targetVerticalOffset);
        CaptureViewportState();
    }

    private void AddOpenTab(OpenDocumentState state)
    {
        if (OpenTabs.Any(tab => string.Equals(tab.FilePath, state.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        OpenTabs.Add(new OpenWindowItem(state));
    }

    private void RemoveOpenTab(string filePath)
    {
        OpenWindowItem? item = OpenTabs.FirstOrDefault(tab =>
            string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (item != null)
        {
            OpenTabs.Remove(item);
        }
    }

    private void ApplyCanvasZoom()
    {
        WorkspaceScaleTransform.ScaleX = _canvasZoom;
        WorkspaceScaleTransform.ScaleY = _canvasZoom;
    }

    private void ApplySettingsToOpenWindows()
    {
        foreach (FloatingCodeWindow window in _openWindows.Values)
        {
            window.ApplyCodeBackcolor(GetCodeWindowBackcolor(window.State.FilePath));
        }
    }

    private Brush GetCodeWindowBackcolor(string filePath)
    {
        string backcolor = _appSettings.CodeWindows.GetBackcolorForFile(filePath);
        try
        {
            if (ColorConverter.ConvertFromString(backcolor) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }

        return Brushes.White;
    }

    private Point GetCurrentViewportOrigin()
    {
        return new Point(
            WorkspaceScrollViewer.HorizontalOffset / _canvasZoom,
            WorkspaceScrollViewer.VerticalOffset / _canvasZoom);
    }

    private void CaptureViewportState()
    {
        _workspaceState.ViewportHorizontalOffset = WorkspaceScrollViewer.HorizontalOffset;
        _workspaceState.ViewportVerticalOffset = WorkspaceScrollViewer.VerticalOffset;
    }

    private void RestoreViewport()
    {
        double horizontalOffset = _workspaceState.ViewportHorizontalOffset;
        double verticalOffset = _workspaceState.ViewportVerticalOffset;

        if (horizontalOffset <= 0 && verticalOffset <= 0)
        {
            horizontalOffset = Math.Max(0, (VirtualOriginX * _canvasZoom) - (WorkspaceScrollViewer.ViewportWidth / 2));
            verticalOffset = Math.Max(0, (VirtualOriginY * _canvasZoom) - (WorkspaceScrollViewer.ViewportHeight / 2));
        }

        WorkspaceScrollViewer.ScrollToHorizontalOffset(horizontalOffset);
        WorkspaceScrollViewer.ScrollToVerticalOffset(verticalOffset);
        CaptureViewportState();
    }

    private void MigrateLegacyWindowCoordinates()
    {
        if (_workspaceState.OpenDocuments.Count == 0)
        {
            return;
        }

        bool appearsToUseOldCanvas = _workspaceState.OpenDocuments.All(document =>
            document.Left >= 0 &&
            document.Top >= 0 &&
            document.Left <= OldCanvasWidth &&
            document.Top <= OldCanvasHeight);

        if (!appearsToUseOldCanvas)
        {
            return;
        }

        foreach (OpenDocumentState document in _workspaceState.OpenDocuments)
        {
            document.Left += VirtualOriginX;
            document.Top += VirtualOriginY;
        }

        _workspaceState.ViewportHorizontalOffset = Math.Max(0, (VirtualOriginX * _canvasZoom) - 200);
        _workspaceState.ViewportVerticalOffset = Math.Max(0, (VirtualOriginY * _canvasZoom) - 160);
    }

    private static double NormalizeCanvasZoom(double zoom)
    {
        if (double.IsNaN(zoom) || double.IsInfinity(zoom) || zoom <= 0)
        {
            return 1;
        }

        return Math.Clamp(zoom, MinimumCanvasZoom, MaximumCanvasZoom);
    }

    private void UpdateEmptyWorkspaceHint()
    {
        EmptyWorkspaceHint.Visibility = WorkspaceCanvas.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SaveWorkspaceStateAsync()
    {
        try
        {
            await _workspaceStore.SaveAsync(_workspaceState);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save workspace state: {ex.Message}";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            _scopeStore.SaveAsync(_scopeLibrary).GetAwaiter().GetResult();
            _settingsStore.SaveAsync(_appSettings).GetAwaiter().GetResult();
            _workspaceStore.SaveAsync(_workspaceState).GetAwaiter().GetResult();
        }
        catch
        {
            // Avoid blocking application shutdown if persistence fails.
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
