using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Win32;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage;
using VisualBasicSyntaxKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;
using VisualBasicSyntaxTree = Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxTree;

namespace Surf2;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private enum WorkspaceSplitOrientation
    {
        DiagramTop,
        DiagramRight,
        DiagramBottom,
        DiagramLeft
    }

    private enum WorkspaceViewKind
    {
        Code,
        Diagram
    }

    private enum CodeViewMode
    {
        Canvas,
        Tabs
    }

    private sealed record ObjectExplorerViewState(
        HashSet<string> ExpandedNodeKeys,
        double HorizontalOffset,
        double VerticalOffset);

    private enum DiagramUndoActionKind
    {
        Added,
        Removed,
        Modified
    }

    private enum SqlFindOperation
    {
        Any,
        Select,
        Delete,
        Update,
        Insert,
        InsertOrUpdate
    }

    private sealed record DiagramUndoAction(
        DiagramUndoActionKind Kind,
        DiagramObjectSnapshot? Before,
        DiagramObjectSnapshot? After);

    private sealed record MetadataLinkTarget(
        string Link,
        int? LineNumber);

    private sealed record PendingPortalPairPlacement(
        string SourceDiagramId,
        string SourcePortalObjectId);

    private sealed record ReferenceResourceContext(
        string Key,
        string DisplayName,
        ResourceKind Kind,
        string Path);

    private sealed record CodeReferenceOccurrence(
        string Token,
        int LineNumber,
        int ColumnNumber,
        ReferenceEntity Target,
        ReferenceResourceContext TargetResource,
        bool IsInternal);

    private sealed record SqlReferenceToken(
        string Token,
        int LineNumber,
        int ColumnNumber,
        int StartOffset);

    private sealed record SqlTableReferenceCandidate(
        string TableName,
        string SimpleTableName,
        string Alias,
        int StartOffset);

    private const double VirtualCanvasWidth = 100000;
    private const double VirtualCanvasHeight = 100000;
    private const double VirtualOriginX = VirtualCanvasWidth / 2;
    private const double VirtualOriginY = VirtualCanvasHeight / 2;
    private const double OldCanvasWidth = 3000;
    private const double OldCanvasHeight = 2000;
    private const double MinimumCanvasZoom = 0.25;
    private const double MaximumCanvasZoom = 3;
    private const double CanvasZoomStep = 1.08;
    private const double MinimumPreviewFontSize = 8;
    private const double MaximumPreviewFontSize = 36;
    private const double PreviewFontZoomStep = 1.1;
    private const string TransparentDiagramColorText = "Transparent";
    private const double DiagramPasteOffset = 28;
    private const string PastedDiagramImagesFolderName = "PastedDiagramImages";
    private const int MaximumDiagramUndoActions = 100;
    private const int DiagramExportTileSize = 2048;
    private const int MaximumDiagramExportDimension = 32767;
    private const long MaximumDiagramExportPixels = 100_000_000;
    private const double DiagramExportMargin = 40;
    private const double DiagramSidebarDefaultWidth = 420;
    private const double DiagramSidebarMinimumWidth = 280;
    private const double WorkflowMarkerSize = 36;
    private const int DiagramLayerStep = 10;
    private const double DiagramShiftPanDeadZone = 3;
    private const double DiagramShiftPanSpeedFactor = 0.12;
    private const double DiagramCtrlShiftZoomDeadZone = 3;
    private const double DiagramCtrlShiftZoomSpeedFactor = 0.00008;
    private const string ObjectExplorerDragDataFormat = "Surf2.ObjectExplorerNode";
    private const string DynamicReferencesContextMenuTag = "DynamicReferencesContextMenu";
    private static readonly ReferenceEntityKind[] SqlContextMenuReferenceKinds =
    [
        ReferenceEntityKind.StoredProcedure,
        ReferenceEntityKind.View,
        ReferenceEntityKind.Function,
        ReferenceEntityKind.Table
    ];

    private static readonly (SqlFindOperation Operation, string Header)[] SqlFindOperations =
    [
        (SqlFindOperation.Any, "Any"),
        (SqlFindOperation.Select, "SELECT"),
        (SqlFindOperation.Delete, "DELETE"),
        (SqlFindOperation.Update, "UPDATE"),
        (SqlFindOperation.Insert, "INSERT"),
        (SqlFindOperation.InsertOrUpdate, "INSERT OR UPDATE")
    ];

    private static readonly Regex SqlReferenceTokenPattern = new(
        @"(?<![@A-Za-z0-9_#$\]])(?:\[[^\]\r\n]+\]|[A-Za-z_#][A-Za-z0-9_#$]*)(?:\s*\.\s*(?:\[[^\]\r\n]+\]|[A-Za-z_#][A-Za-z0-9_#$]*)){0,3}(?![A-Za-z0-9_#$\[])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SqlDefinitionPrefixPattern = new(
        @"\b(?:create(?:\s+or\s+alter)?|alter)\s+(procedure|proc|function|view|table)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SqlTableReferencePattern = new(
        @"\b(?:from|join)\s+(?<table>(?:\[[^\]\r\n]+\]|[A-Za-z_#][A-Za-z0-9_#$]*)(?:\s*\.\s*(?:\[[^\]\r\n]+\]|[A-Za-z_#][A-Za-z0-9_#$]*)){0,2})(?:\s+(?:as\s+)?(?<alias>\[[^\]\r\n]+\]|[A-Za-z_#][A-Za-z0-9_#$]*))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MetadataLinkLineSuffixPattern = new(
        @"^(?<link>.+?)(?:(?:#L|#line=|:)(?<line>[1-9]\d*))$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Brush ActiveCodeCanvasBrush = CreateFrozenBrush(Color.FromRgb(0xEE, 0xF1, 0xF5));
    private static readonly Brush ActiveDiagramCanvasBrush = CreateFrozenBrush(Color.FromRgb(0xF4, 0xF6, 0xF8));
    private static readonly Brush InactiveCanvasBrush = CreateFrozenBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
    private static readonly TimeSpan ShutdownSaveTimeout = TimeSpan.FromSeconds(10);

    private readonly FileTreeService _fileTreeService = new();
    private readonly SyntaxHighlightingService _syntaxHighlightingService = new();
    private readonly ScopeReferenceIndexService _referenceIndexService = new();
    private readonly NavigationHistoryService _navigationHistoryService = new();
    private readonly SqlTraceService _sqlTraceService = new();
    private readonly LinkableResourceService _linkableResourceService = new();
    private readonly IWorkspaceStore _workspaceStore = new SqlServerWorkspaceStore();
    private readonly IScopeStore _scopeStore = new SqlServerScopeStore();
    private readonly ISettingsStore _settingsStore = new SqlServerSettingsStore();
    private readonly IDatabaseMetadataStore _databaseMetadataStore = new SqlServerDatabaseMetadataStore();
    private readonly IDiagramStore _diagramStore = new SqlServerDiagramStore();
    private readonly IWorkbenchStore _workbenchStore = new SqlServerWorkbenchStore();
    private readonly DatabaseDocumentService _databaseDocumentService = new();
    private readonly DatabaseMetadataImportService _databaseMetadataImportService = new();
    private readonly Dictionary<string, FloatingCodeWindow> _openWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FloatingSpreadsheetWindow> _openSpreadsheetWindows = new(StringComparer.OrdinalIgnoreCase);

    private WorkspaceState _workspaceState = new();
    private ScopeLibrary _scopeLibrary = new();
    private DatabaseSnapshotLibrary _databaseSnapshots = new();
    private DiagramLibrary _diagramLibrary = new();
    private WorkbenchLibrary _workbenchLibrary = new();
    private AppSettings _appSettings = new();
    private ScopeReferenceIndex _referenceIndex = ScopeReferenceIndex.Empty;
    private ReferenceHighlightColorizer? _previewReferenceHighlightColorizer;
    private Scope? _activeScope;
    private int _windowSequence;
    private int _zIndex;
    private int _previewRequestVersion;
    private WorkspaceSplitOrientation _workspaceSplitOrientation = WorkspaceSplitOrientation.DiagramBottom;
    private WorkspaceViewKind _activeWorkspaceView = WorkspaceViewKind.Code;
    private CodeViewMode _codeViewMode = CodeViewMode.Canvas;
    private double _canvasZoom = 1;
    private double _diagramCanvasZoom = 1;
    private bool _isCanvasPanning;
    private bool _isDiagramCanvasPanning;
    private bool _isControlPanningCodeCanvas;
    private bool _isControlPanningDiagramCanvas;
    private bool _isShiftPanningCodeCanvas;
    private bool _isShiftPanningDiagramCanvas;
    private bool _isDrawingDiagramShape;
    private bool _isDrawingDiagramImage;
    private bool _isDrawingDiagramLine;
    private bool _isDiagramExporting;
    private bool _isDiagramSidebarOpen;
    private bool _isUpdatingMetadataEditor;
    private bool _isUpdatingMetadataToolbar;
    private bool _isUpdatingWorkflowEditor;
    private bool _isCreatingNewWorkflow;
    private bool _isAddingWorkflowItems;
    private bool _isUpdatingViewToggles;
    private bool _isUpdatingCodeViewMode;
    private bool _isUpdatingDiagramToolToggles;
    private bool _isUpdatingOpenTabsSelection;
    private bool _isDiagramLocked = true;
    private bool _isUpdatingDiagramLockToggle;
    private bool _isUpdatingWorkbenchSelection;
    private bool _diagramViewportInitialized;
    private bool _shutdownRequested;
    private bool _shutdownSaveCompleted;
    private Point _canvasPanStartPoint;
    private Point _diagramPanStartPoint;
    private Point _controlCodeCanvasPanPoint;
    private Point _controlDiagramCanvasPanPoint;
    private Point _codeShiftPanOriginPoint;
    private Point _codeShiftPanCurrentPoint;
    private Point _codeCtrlShiftZoomOriginPoint;
    private Point _codeCtrlShiftZoomCurrentPoint;
    private Point _codeCtrlShiftZoomAnchorViewportPoint;
    private Point _codeCtrlShiftZoomAnchorCanvasPoint;
    private Point _diagramShiftPanOriginPoint;
    private Point _diagramShiftPanCurrentPoint;
    private Point _diagramCtrlShiftZoomOriginPoint;
    private Point _diagramCtrlShiftZoomCurrentPoint;
    private Point _diagramCtrlShiftZoomAnchorViewportPoint;
    private Point _diagramCtrlShiftZoomAnchorCanvasPoint;
    private Point _objectExplorerDragStartPoint;
    private Point _diagramShapeDrawStartPoint;
    private Point _diagramImageDrawStartPoint;
    private Point _diagramLineDrawStartPoint;
    private double _canvasPanStartHorizontalOffset;
    private double _canvasPanStartVerticalOffset;
    private double _diagramPanStartHorizontalOffset;
    private double _diagramPanStartVerticalOffset;
    private string _diagramOutlineColor = "#000000";
    private string _diagramBackColor = "#FFFFFF";
    private DiagramShapeControl? _activeDiagramDrawingShape;
    private DiagramImageControl? _activeDiagramDrawingImage;
    private DiagramLineControl? _activeDiagramDrawingLine;
    private FloatingCodeWindow? _activeCodeWindow;
    private FrameworkElement? _selectedDiagramObject;
    private DiagramObjectSnapshot? _diagramClipboardSnapshot;
    private DiagramObjectSnapshot? _pendingDiagramInteractionSnapshot;
    private readonly Stack<DiagramUndoAction> _diagramUndoStack = new();
    private readonly List<QueryItem> _metadataEditorQueries = [];
    private readonly Dictionary<string, bool> _expandedWorkflowItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RichTextBox> _workflowItemDocumentationEditors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _workflowItemContainers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBox> _workflowItemDescriptionEditors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedObjectExplorerNodeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _openingObjectExplorerNodeKeys = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedDiagramImageId;
    private string? _pendingPortalName;
    private PendingPortalPairPlacement? _pendingPortalPairPlacement;
    private bool _diagramLineHasEndArrow;
    private string? _activeDiagramId;
    private string _currentDiagramName = "Unsaved Diagram";
    private List<WorkflowDocument> _currentDiagramWorkflows = [];
    private FrameworkElement? _metadataEditorTarget;
    private WorkflowDocument? _workflowEditorTarget;
    private FileSystemNode? _pendingObjectExplorerDragNode;
    private double _diagramSidebarWidth = DiagramSidebarDefaultWidth;
    private string _currentFolderDisplay = "No scope selected";
    private string _statusText = "Ready.";
    private string? _previewFilePath;
    private int _objectExplorerLoadingDepth;
    private int _scopeLoadVersion;
    private bool _isRestoringObjectExplorerExpansion;
    private bool _isCtrlShiftZoomingCodeCanvas;
    private bool _isCtrlShiftZoomingDiagramCanvas;
    private readonly DispatcherTimer _codeShiftPanTimer;
    private readonly DispatcherTimer _codeCtrlShiftZoomTimer;
    private readonly DispatcherTimer _diagramShiftPanTimer;
    private readonly DispatcherTimer _diagramCtrlShiftZoomTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        ObjectExplorer.ContextMenu = new ContextMenu();
        _codeShiftPanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _codeShiftPanTimer.Tick += CodeShiftPanTimer_Tick;
        _codeCtrlShiftZoomTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _codeCtrlShiftZoomTimer.Tick += CodeCtrlShiftZoomTimer_Tick;
        _diagramShiftPanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _diagramShiftPanTimer.Tick += DiagramShiftPanTimer_Tick;
        _diagramCtrlShiftZoomTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _diagramCtrlShiftZoomTimer.Tick += DiagramCtrlShiftZoomTimer_Tick;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        InitializeMetadataEditorControls();
        ApplyWorkspaceViewLayout();
        ApplyDiagramSidebarLayout();
        SetDiagramLockState(_isDiagramLocked, updateToggle: true, showStatus: false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FileSystemNode> RootNodes { get; } = [];

    public ObservableCollection<OpenWindowItem> OpenTabs { get; } = [];

    public ObservableCollection<WorkbenchState> SavedWorkbenches { get; } = [];

    public IReadOnlyList<NavigationHistoryEntry> NavigationHistory => _navigationHistoryService.Entries;

    public bool CanNavigateBack => _navigationHistoryService.CanMoveBack;

    public bool CanNavigateForward => _navigationHistoryService.CanMoveForward;

    public bool CanSaveDiagramAs => !string.IsNullOrWhiteSpace(_activeDiagramId);

    public bool CanDeleteDiagram => !string.IsNullOrWhiteSpace(_activeDiagramId);

    public string CurrentDiagramName
    {
        get => _currentDiagramName;
        private set
        {
            _currentDiagramName = string.IsNullOrWhiteSpace(value) ? "Unsaved Diagram" : value;
            OnPropertyChanged();
        }
    }

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
            _databaseSnapshots = await _databaseMetadataStore.LoadAsync();
            _diagramLibrary = await _diagramStore.LoadAsync();
            _workbenchLibrary = await _workbenchStore.LoadAsync();
            bool workbenchLibraryChanged = NormalizeWorkbenchLibrary();
            _appSettings = await _settingsStore.LoadAsync();
            bool databaseSnapshotsChanged = NormalizeDatabaseSnapshots();
            if (_appSettings.EnsureDefaults())
            {
                await _settingsStore.SaveAsync(_appSettings);
            }

            if (workbenchLibraryChanged)
            {
                await _workbenchStore.SaveAsync(_workbenchLibrary);
            }

            if (databaseSnapshotsChanged)
            {
                await _databaseMetadataStore.SaveAsync(_databaseSnapshots);
            }

            RefreshDiagramImageToolMenu();
            ApplyInternalLoggingSetting("startup settings loaded");
            RefreshSavedWorkbenches();
            await MigrateSavedFolderToDefaultScopeAsync();

            WorkbenchState? startupWorkbench = _appSettings.LoadMostRecentWorkbenchOnStartup
                ? _workbenchLibrary.Workbenches
                    .OrderByDescending(GetWorkbenchUpdatedAtUtc)
                    .FirstOrDefault()
                : null;

            if (startupWorkbench != null)
            {
                await LoadWorkbenchAsync(startupWorkbench, updateSelector: true);
            }
            else
            {
                await InitializeDefaultStartupStateAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Could not restore workspace: {ex.Message}";
        }
    }

    private async void ScopesButton_Click(object sender, RoutedEventArgs e)
    {
        var scopeManagerWindow = new ScopeManagerWindow(_scopeLibrary, _databaseSnapshots)
        {
            Owner = this
        };

        bool? result = scopeManagerWindow.ShowDialog();

        if (scopeManagerWindow.WasChanged)
        {
            await _scopeStore.SaveAsync(_scopeLibrary);
            await _databaseMetadataStore.SaveAsync(_databaseSnapshots);
        }

        if (result != true || string.IsNullOrWhiteSpace(scopeManagerWindow.SelectedScopeId))
        {
            if (scopeManagerWindow.WasChanged && _activeScope != null)
            {
                await LoadLastActiveScopeAsync();
            }

            return;
        }

        bool scopeChanged = _activeScope?.ScopeId != scopeManagerWindow.SelectedScopeId;
        _scopeLibrary.LastActiveScopeId = scopeManagerWindow.SelectedScopeId;
        await _scopeStore.SaveAsync(_scopeLibrary);
        await _databaseMetadataStore.SaveAsync(_databaseSnapshots);

        if (scopeChanged)
        {
            CloseAllOpenWindows();
            await SaveWorkspaceStateAsync();
        }

        await LoadLastActiveScopeAsync();
    }

    private async Task LoadScopeAsync(Scope? scope)
    {
        int loadVersion = ++_scopeLoadVersion;
        RootNodes.Clear();
        _expandedObjectExplorerNodeKeys.Clear();
        _activeScope = scope;
        ObjectExplorerSearchTextBox.Clear();

        if (scope == null)
        {
            _referenceIndex = ScopeReferenceIndex.Empty;
            ClearReferencePreview("Create or open a scope, then single-click a reference to preview it.");
            CurrentFolderDisplay = "No scope selected";
            StatusText = "Create or open a scope to load resources.";
            return;
        }

        await ShowObjectExplorerLoadingAsync($"Loading {scope.Name}...");

        try
        {
            DatabaseSnapshotLibrary databaseSnapshots = _databaseSnapshots;
            DiagramLibrary diagramLibrary = _diagramLibrary;
            CodeWindowSettings codeWindowSettings = _appSettings.CodeWindows;
            HashSet<string> unloadedResourceIds = GetUnloadedResourceIds();

            List<FileSystemNode> roots = await Task.Run(() =>
                _fileTreeService
                    .CreateRoots(scope.Resources, databaseSnapshots, diagramLibrary, scope.VirtualFolders, unloadedResourceIds)
                    .ToList());
            ScopeReferenceIndex referenceIndex = await Task.Run(() =>
                _referenceIndexService.Build(scope, databaseSnapshots, codeWindowSettings, unloadedResourceIds));

            if (loadVersion != _scopeLoadVersion || !ReferenceEquals(_activeScope, scope))
            {
                return;
            }

            RootNodes.Clear();
            foreach (FileSystemNode node in roots)
            {
                RootNodes.Add(node);
            }

            _referenceIndex = referenceIndex;
            CurrentFolderDisplay = $"Scope: {scope.Name}";
            ApplyReferenceHighlightsToOpenWindows();
            int loadedResourceCount = scope.Resources.Count(resource => IsScopeResourceLoaded(resource, unloadedResourceIds));
            StatusText = $"Loaded scope '{scope.Name}' with {loadedResourceCount} of {scope.Resources.Count} resource(s) active. Indexed {_referenceIndex.EntityCount} definition(s) across {_referenceIndex.IndexedFileCount} file(s).";
        }
        finally
        {
            HideObjectExplorerLoading();
        }
    }

    private void LoadObjectExplorerRoots(Scope scope)
    {
        RootNodes.Clear();

        foreach (FileSystemNode node in _fileTreeService.CreateRoots(scope.Resources, _databaseSnapshots, _diagramLibrary, scope.VirtualFolders, GetUnloadedResourceIds()))
        {
            RootNodes.Add(node);
        }
    }

    private async Task ShowObjectExplorerLoadingAsync(string message)
    {
        _objectExplorerLoadingDepth++;
        ObjectExplorerLoadingText.Text = message;
        ObjectExplorerLoadingOverlay.Visibility = Visibility.Visible;
        ObjectExplorerLoadingOverlay.IsHitTestVisible = true;
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
    }

    private void HideObjectExplorerLoading()
    {
        if (_objectExplorerLoadingDepth > 0)
        {
            _objectExplorerLoadingDepth--;
        }

        if (_objectExplorerLoadingDepth == 0)
        {
            ObjectExplorerLoadingOverlay.Visibility = Visibility.Collapsed;
            ObjectExplorerLoadingOverlay.IsHitTestVisible = false;
        }
    }

    private async void ObjectExplorerSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ApplyObjectExplorerSearchAsync();
    }

    private async Task ApplyObjectExplorerSearchAsync()
    {
        if (_activeScope == null)
        {
            StatusText = "Create or open a scope before searching.";
            return;
        }

        string query = ObjectExplorerSearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            LoadObjectExplorerRoots(_activeScope);
            StatusText = "Object Explorer search cleared.";
            return;
        }

        ObjectExplorerSearchTarget searchTarget = GetObjectExplorerSearchTarget();
        bool useRegex = ObjectExplorerRegexToggle.IsChecked == true;
        List<ScopedResource> resources = _activeScope.Resources.ToList();
        HashSet<string> unloadedResourceIds = GetUnloadedResourceIds();

        try
        {
            StatusText = $"Searching Object Explorer {searchTarget.ToString().ToLowerInvariant()}...";
            await ShowObjectExplorerLoadingAsync("Searching resources...");
            ObjectExplorerSearchResult result = await Task.Run(() =>
                _fileTreeService.CreateFilteredRoots(resources, _databaseSnapshots, _diagramLibrary, query, searchTarget, useRegex, _activeScope.VirtualFolders, unloadedResourceIds));

            RootNodes.Clear();
            foreach (FileSystemNode node in result.Roots)
            {
                RootNodes.Add(node);
            }

            StatusText = $"Object Explorer search found {result.MatchCount} match(es) across {result.SearchedCount} object(s).";
        }
        catch (ArgumentException ex) when (useRegex)
        {
            StatusText = $"Invalid regular expression: {ex.Message}";
        }
        finally
        {
            HideObjectExplorerLoading();
        }
    }

    private ObjectExplorerSearchTarget GetObjectExplorerSearchTarget()
    {
        if (ObjectExplorerSearchTargetComboBox.SelectedItem is ComboBoxItem item &&
            string.Equals(item.Content?.ToString(), "Content", StringComparison.OrdinalIgnoreCase))
        {
            return ObjectExplorerSearchTarget.Content;
        }

        return ObjectExplorerSearchTarget.Name;
    }

    private async Task LoadLastActiveScopeAsync()
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

        await LoadScopeAsync(activeScope);
    }

    private async Task InitializeDefaultStartupStateAsync()
    {
        _workspaceState = new WorkspaceState
        {
            UnloadedResourceIds = GetUnloadedResourceIds().ToList()
        };
        _canvasZoom = 1;
        _diagramCanvasZoom = 1;
        ApplyCanvasZoom();
        ApplyDiagramCanvasZoom();
        SetCodeViewMode(CodeViewMode.Canvas);
        SetWorkspaceViewVisibility(showCode: true, showDiagram: false, WorkspaceViewKind.Code);
        await LoadScopeAsync(null);
        UpdateEmptyWorkspaceHint();
        _ = Dispatcher.BeginInvoke(RestoreViewport);
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
        TreeViewItem? item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FileSystemNode node || node.IsDirectory)
        {
            return;
        }

        e.Handled = true;
        item.IsSelected = true;
        InternalLogService.Info(
            "Object Explorer mouse double-click.",
            ("NodeName", node.Name),
            ("Path", node.FullPath),
            ("NodeKey", node.NodeKey),
            ("IsLoaded", node.IsScopeResourceLoaded),
            ("IsDiagram", DiagramDocumentService.IsDiagramDocumentPath(node.FullPath)),
            ("OriginalSource", e.OriginalSource?.GetType().FullName));

        if (!node.IsScopeResourceLoaded)
        {
            StatusText = $"Load resource '{node.Name}' before opening it.";
            return;
        }

        if (DiagramDocumentService.IsDiagramDocumentPath(node.FullPath))
        {
            await OpenObjectExplorerDiagramNodeAsync(node);
            return;
        }

        await OpenObjectExplorerNodeSafelyAsync(node);
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
        InternalLogService.Info(
            "Object Explorer item preview double-click.",
            ("NodeName", node.Name),
            ("Path", node.FullPath),
            ("NodeKey", node.NodeKey),
            ("IsLoaded", node.IsScopeResourceLoaded),
            ("IsDiagram", DiagramDocumentService.IsDiagramDocumentPath(node.FullPath)),
            ("OriginalSource", e.OriginalSource?.GetType().FullName));

        if (!node.IsScopeResourceLoaded)
        {
            StatusText = $"Load resource '{node.Name}' before opening it.";
            return;
        }

        if (DiagramDocumentService.IsDiagramDocumentPath(node.FullPath))
        {
            await OpenObjectExplorerDiagramNodeAsync(node);
            return;
        }

        await OpenObjectExplorerNodeSafelyAsync(node);
    }

    private async Task OpenObjectExplorerDiagramNodeAsync(FileSystemNode node)
    {
        InternalLogService.Info(
            "Opening Object Explorer diagram node.",
            ("NodeName", node.Name),
            ("Path", node.FullPath),
            ("NodeKey", node.NodeKey));

        try
        {
            await OpenDiagramAsync(node.FullPath);
            InternalLogService.Info(
                "Opened Object Explorer diagram node.",
                ("NodeName", node.Name),
                ("Path", node.FullPath),
                ("NodeKey", node.NodeKey));
        }
        catch (Exception ex)
        {
            InternalLogService.Error(
                ex,
                "Failed to open Object Explorer diagram node.",
                ("NodeName", node.Name),
                ("Path", node.FullPath),
                ("NodeKey", node.NodeKey));
            StatusText = $"Could not open {node.Name}: {ex.Message}";
        }
    }

    private async Task OpenObjectExplorerNodeSafelyAsync(FileSystemNode node)
    {
        string nodeKey = string.IsNullOrWhiteSpace(node.NodeKey) ? node.FullPath : node.NodeKey;
        if (!_openingObjectExplorerNodeKeys.Add(nodeKey))
        {
            InternalLogService.Warning(
                "Skipped duplicate Object Explorer node open.",
                ("NodeName", node.Name),
                ("Path", node.FullPath),
                ("NodeKey", nodeKey));
            return;
        }

        InternalLogService.Info(
            "Opening Object Explorer node.",
            ("NodeName", node.Name),
            ("Path", node.FullPath),
            ("NodeKey", nodeKey),
            ("OpenTabsCount", OpenTabs.Count),
            ("CodeViewMode", _codeViewMode));

        try
        {
            await OpenObjectExplorerNodeAsync(node);
            InternalLogService.Info(
                "Opened Object Explorer node.",
                ("NodeName", node.Name),
                ("Path", node.FullPath),
                ("NodeKey", nodeKey),
                ("OpenTabsCount", OpenTabs.Count),
                ("CodeViewMode", _codeViewMode));
        }
        catch (Exception ex)
        {
            InternalLogService.Error(
                ex,
                "Failed to open Object Explorer node.",
                ("NodeName", node.Name),
                ("Path", node.FullPath),
                ("NodeKey", nodeKey),
                ("OpenTabsCount", OpenTabs.Count),
                ("CodeViewMode", _codeViewMode));
            StatusText = $"Could not open {node.Name}: {ex.Message}";
        }
        finally
        {
            _openingObjectExplorerNodeKeys.Remove(nodeKey);
            InternalLogService.Info(
                "Finished Object Explorer node open attempt.",
                ("NodeName", node.Name),
                ("Path", node.FullPath),
                ("NodeKey", nodeKey),
                ("OpenTabsCount", OpenTabs.Count),
                ("CodeViewMode", _codeViewMode));
        }
    }

    private async Task OpenObjectExplorerNodeAsync(FileSystemNode node)
    {
        if (!node.IsScopeResourceLoaded)
        {
            StatusText = $"Load resource '{node.Name}' before opening it.";
            return;
        }

        await OpenFileAsync(node.FullPath);

        if (!_databaseDocumentService.TryGetTableDataDocumentPathForTableDocument(
                node.FullPath,
                _databaseSnapshots,
                out string tableDataDocumentPath))
        {
            return;
        }

        _openWindows.TryGetValue(node.FullPath, out FloatingCodeWindow? sourceWindow);
        await OpenFileAsync(tableDataDocumentPath, sourceWindow: sourceWindow, suppressHistory: true);
        ApplyObjectExplorerSpreadsheetFilters(tableDataDocumentPath, node);
    }

    private void ApplyObjectExplorerSpreadsheetFilters(string documentPath, FileSystemNode node)
    {
        if (node.SpreadsheetSearchFilters.Count == 0 ||
            !_openSpreadsheetWindows.TryGetValue(documentPath, out FloatingSpreadsheetWindow? spreadsheetWindow))
        {
            return;
        }

        spreadsheetWindow.ApplyColumnFilters(node.SpreadsheetSearchFilters);
        string columnText = node.SpreadsheetSearchFilters.Count == 1 ? "column" : "columns";
        StatusText = $"Opened {node.Name} and filtered full data ({node.SpreadsheetSearchFilters.Count} {columnText}).";
    }

    private void ObjectExplorerItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: FileSystemNode node })
        {
            node.IsExpanded = true;
            if (!_isRestoringObjectExplorerExpansion)
            {
                _expandedObjectExplorerNodeKeys.Add(node.NodeKey);
            }

            _fileTreeService.LoadChildren(node, _activeScope?.VirtualFolders);
        }
    }

    private void ObjectExplorerItem_Collapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: FileSystemNode node })
        {
            node.IsExpanded = false;
            if (!_isRestoringObjectExplorerExpansion)
            {
                _expandedObjectExplorerNodeKeys.Remove(node.NodeKey);
            }
        }
    }

    private void ObjectExplorerItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        TreeViewItem? item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FileSystemNode node)
        {
            return;
        }

        item.IsSelected = true;
        item.Focus();
        item.ContextMenu = CreateObjectExplorerContextMenu(node);
    }

    private ContextMenu? CreateObjectExplorerContextMenu(FileSystemNode node)
    {
        bool canCreateDiagram = string.Equals(node.FullPath, DiagramDocumentService.DiagramRootPath, StringComparison.OrdinalIgnoreCase);
        bool canOpenContainingFolder = CanOpenContainingFolder(node);
        bool canCreateVirtualFolder = _activeScope != null;
        bool canToggleResourceLoad = CanToggleScopeResourceLoad(node);
        if (!canCreateDiagram && !canOpenContainingFolder && !canCreateVirtualFolder && !node.IsVirtualFolder && !canToggleResourceLoad)
        {
            return null;
        }

        var contextMenu = new ContextMenu();
        if (canToggleResourceLoad)
        {
            var loadResourceItem = new MenuItem
            {
                Header = node.IsScopeResourceLoaded ? "Unload" : "Load"
            };
            loadResourceItem.Click += async (_, _) => await SetScopeResourceLoadedAsync(node.ScopeResourceId, !node.IsScopeResourceLoaded);
            contextMenu.Items.Add(loadResourceItem);
        }

        if (node.IsVirtualFolder)
        {
            if (contextMenu.Items.Count > 0)
            {
                contextMenu.Items.Add(new Separator());
            }

            var disbandVirtualFolderItem = new MenuItem
            {
                Header = "Disband"
            };
            disbandVirtualFolderItem.Click += async (_, _) => await DisbandVirtualFolderAsync(node.VirtualFolderId);
            contextMenu.Items.Add(disbandVirtualFolderItem);
        }

        if (canCreateVirtualFolder)
        {
            if (contextMenu.Items.Count > 0)
            {
                contextMenu.Items.Add(new Separator());
            }

            var newVirtualFolderHereItem = new MenuItem
            {
                Header = "New Virtual Folder Here"
            };
            newVirtualFolderHereItem.Click += async (_, _) => await CreateVirtualFolderAsync(node.NaturalParentKey);
            contextMenu.Items.Add(newVirtualFolderHereItem);

            if (node.IsDirectory && !node.IsVirtualFolder && node.IsScopeResourceLoaded)
            {
                var newVirtualFolderInsideItem = new MenuItem
                {
                    Header = "New Virtual Folder Inside"
                };
                newVirtualFolderInsideItem.Click += async (_, _) => await CreateVirtualFolderAsync(node.NodeKey);
                contextMenu.Items.Add(newVirtualFolderInsideItem);
            }
        }

        if (canCreateDiagram)
        {
            if (contextMenu.Items.Count > 0)
            {
                contextMenu.Items.Add(new Separator());
            }

            var newDiagramItem = new MenuItem
            {
                Header = "New Diagram"
            };
            newDiagramItem.Click += (_, _) => CreateNewBlankDiagram();
            contextMenu.Items.Add(newDiagramItem);
        }

        if (canOpenContainingFolder)
        {
            if (contextMenu.Items.Count > 0)
            {
                contextMenu.Items.Add(new Separator());
            }

            var openContainingFolderItem = new MenuItem
            {
                Header = "Open Containing Folder"
            };

            openContainingFolderItem.Click += (_, _) => OpenContainingFolder(node);
            contextMenu.Items.Add(openContainingFolderItem);
        }

        return contextMenu;
    }

    private bool CanToggleScopeResourceLoad(FileSystemNode node)
    {
        return _activeScope != null &&
               node.IsScopeResourceRoot &&
               !string.IsNullOrWhiteSpace(node.ScopeResourceId) &&
               _activeScope.Resources.Any(resource =>
                   string.Equals(resource.ResourceId, node.ScopeResourceId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SetScopeResourceLoadedAsync(string resourceId, bool isLoaded)
    {
        if (_activeScope == null)
        {
            return;
        }

        ScopedResource? resource = _activeScope.Resources.FirstOrDefault(candidate =>
            string.Equals(candidate.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase));
        if (resource == null)
        {
            StatusText = "Resource no longer exists in the active scope.";
            return;
        }

        NormalizeUnloadedResourceIds();
        List<string> unloadedResourceIds = _workspaceState.UnloadedResourceIds;
        unloadedResourceIds.RemoveAll(id => string.Equals(id, resource.ResourceId, StringComparison.OrdinalIgnoreCase));
        if (!isLoaded)
        {
            unloadedResourceIds.Add(resource.ResourceId);
        }

        NormalizeUnloadedResourceIds();
        RebuildReferenceIndexForActiveScope();
        ApplyReferenceHighlightsToOpenWindows();
        ApplyReferenceHighlightsToPreview();
        await SaveWorkspaceStateAsync();
        await RefreshObjectExplorerForVirtualFolderChangeAsync();

        string action = isLoaded ? "Loaded" : "Unloaded";
        StatusText = $"{action} resource '{GetReferenceResourceDisplayName(resource)}'.";
    }

    private HashSet<string> GetUnloadedResourceIds()
    {
        return (_workspaceState.UnloadedResourceIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool IsScopeResourceLoaded(ScopedResource resource)
    {
        return IsScopeResourceLoaded(resource, GetUnloadedResourceIds());
    }

    private static bool IsScopeResourceLoaded(ScopedResource resource, IReadOnlySet<string> unloadedResourceIds)
    {
        return string.IsNullOrWhiteSpace(resource.ResourceId) ||
               !unloadedResourceIds.Contains(resource.ResourceId);
    }

    private void NormalizeUnloadedResourceIds()
    {
        _workspaceState.UnloadedResourceIds = (_workspaceState.UnloadedResourceIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ObjectExplorer_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) != null)
        {
            return;
        }

        if (_activeScope == null)
        {
            e.Handled = true;
            return;
        }

        var contextMenu = new ContextMenu();
        var newVirtualFolderItem = new MenuItem
        {
            Header = "New Virtual Folder"
        };
        newVirtualFolderItem.Click += async (_, _) => await CreateVirtualFolderAsync(FileSystemNode.RootParentKey);
        contextMenu.Items.Add(newVirtualFolderItem);
        ObjectExplorer.ContextMenu = contextMenu;
    }

    private async Task CreateVirtualFolderAsync(string parentNodeKey)
    {
        if (_activeScope == null)
        {
            StatusText = "Open a scope before creating a virtual folder.";
            return;
        }

        string normalizedParentKey = NormalizeVirtualFolderParentKey(parentNodeKey);
        string? folderName = PromptForDiagramName("Virtual Folder", "Virtual folder name", "Virtual Folder");
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        if (_activeScope.VirtualFolders.Any(folder =>
                string.Equals(NormalizeVirtualFolderParentKey(folder.ParentNodeKey), normalizedParentKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(folder.Name, folderName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"A virtual folder named '{folderName}' already exists at that level.";
            return;
        }

        var virtualFolder = new VirtualFolder
        {
            Name = folderName,
            ParentNodeKey = normalizedParentKey
        };
        _activeScope.VirtualFolders.Add(virtualFolder);

        await SaveVirtualFolderChangesAsync(
            $"Created virtual folder '{folderName}'.",
            [FileSystemNode.CreateVirtualFolderNodeKey(virtualFolder.VirtualFolderId)]);
    }

    private async Task DisbandVirtualFolderAsync(string virtualFolderId)
    {
        if (_activeScope == null)
        {
            return;
        }

        VirtualFolder? virtualFolder = _activeScope.VirtualFolders.FirstOrDefault(folder =>
            string.Equals(folder.VirtualFolderId, virtualFolderId, StringComparison.OrdinalIgnoreCase));
        if (virtualFolder == null)
        {
            StatusText = "Virtual folder no longer exists.";
            return;
        }

        string folderName = virtualFolder.Name;
        _activeScope.VirtualFolders.Remove(virtualFolder);
        await SaveVirtualFolderChangesAsync($"Disbanded virtual folder '{folderName}'.");
    }

    private void ObjectExplorer_DragOver(object sender, DragEventArgs e)
    {
        FileSystemNode? draggedNode = e.Data.GetData(ObjectExplorerDragDataFormat) as FileSystemNode;
        FileSystemNode? targetNode = GetObjectExplorerDropTarget(e.OriginalSource as DependencyObject);
        e.Effects = CanDropObjectExplorerNodeOnVirtualFolder(draggedNode, targetNode)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ObjectExplorer_Drop(object sender, DragEventArgs e)
    {
        FileSystemNode? draggedNode = e.Data.GetData(ObjectExplorerDragDataFormat) as FileSystemNode;
        FileSystemNode? targetNode = GetObjectExplorerDropTarget(e.OriginalSource as DependencyObject);
        if (!CanDropObjectExplorerNodeOnVirtualFolder(draggedNode, targetNode) ||
            draggedNode == null ||
            targetNode == null ||
            _activeScope == null)
        {
            return;
        }

        VirtualFolder? targetFolder = _activeScope.VirtualFolders.FirstOrDefault(folder =>
            string.Equals(folder.VirtualFolderId, targetNode.VirtualFolderId, StringComparison.OrdinalIgnoreCase));
        if (targetFolder == null)
        {
            return;
        }

        foreach (VirtualFolder virtualFolder in _activeScope.VirtualFolders)
        {
            if (string.Equals(NormalizeVirtualFolderParentKey(virtualFolder.ParentNodeKey), targetFolder.ParentNodeKey, StringComparison.OrdinalIgnoreCase))
            {
                RemoveVirtualFolderChildKey(virtualFolder, draggedNode.NodeKey);
            }
        }

        if (!targetFolder.ChildNodeKeys.Any(key => string.Equals(key, draggedNode.NodeKey, StringComparison.OrdinalIgnoreCase)))
        {
            targetFolder.ChildNodeKeys.Add(draggedNode.NodeKey);
        }

        e.Handled = true;
        await SaveVirtualFolderChangesAsync(
            $"Moved '{draggedNode.Name}' into virtual folder '{targetFolder.Name}'.",
            [targetNode.NodeKey]);
    }

    private bool CanDropObjectExplorerNodeOnVirtualFolder(FileSystemNode? draggedNode, FileSystemNode? targetNode)
    {
        if (_activeScope == null ||
            draggedNode == null ||
            targetNode?.IsVirtualFolder != true ||
            draggedNode.IsVirtualFolder ||
            string.Equals(draggedNode.NodeKey, targetNode.NodeKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(
            NormalizeVirtualFolderParentKey(draggedNode.NaturalParentKey),
            NormalizeVirtualFolderParentKey(targetNode.NaturalParentKey),
            StringComparison.OrdinalIgnoreCase);
    }

    private static FileSystemNode? GetObjectExplorerDropTarget(DependencyObject? source)
    {
        return FindAncestor<TreeViewItem>(source)?.DataContext as FileSystemNode;
    }

    private async Task SaveVirtualFolderChangesAsync(
        string statusText,
        IEnumerable<string>? additionalExpandedNodeKeys = null)
    {
        try
        {
            await _scopeStore.SaveAsync(_scopeLibrary);
            await RefreshObjectExplorerForVirtualFolderChangeAsync(additionalExpandedNodeKeys);
            StatusText = statusText;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save virtual folder changes: {ex.Message}";
        }
    }

    private async Task RefreshObjectExplorerForVirtualFolderChangeAsync(IEnumerable<string>? additionalExpandedNodeKeys = null)
    {
        if (_activeScope == null)
        {
            return;
        }

        ObjectExplorerViewState viewState = CaptureObjectExplorerViewState();
        if (additionalExpandedNodeKeys != null)
        {
            foreach (string nodeKey in additionalExpandedNodeKeys)
            {
                if (!string.IsNullOrWhiteSpace(nodeKey))
                {
                    viewState.ExpandedNodeKeys.Add(nodeKey);
                }
            }
        }

        _isRestoringObjectExplorerExpansion = true;
        try
        {
            if (string.IsNullOrWhiteSpace(ObjectExplorerSearchTextBox.Text))
            {
                LoadObjectExplorerRoots(_activeScope);
                await RestoreObjectExplorerViewStateAsync(viewState);
                return;
            }

            await ApplyObjectExplorerSearchAsync();
            await RestoreObjectExplorerViewStateAsync(viewState);
        }
        finally
        {
            ReplaceExpandedObjectExplorerState(viewState.ExpandedNodeKeys);
            _isRestoringObjectExplorerExpansion = false;
        }
    }

    private ObjectExplorerViewState CaptureObjectExplorerViewState()
    {
        var expandedNodeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        expandedNodeKeys.UnionWith(_expandedObjectExplorerNodeKeys);
        CaptureExpandedObjectExplorerNodes(RootNodes, expandedNodeKeys);
        CaptureExpandedObjectExplorerContainers(ObjectExplorer, expandedNodeKeys);

        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(ObjectExplorer);
        return new ObjectExplorerViewState(
            expandedNodeKeys,
            scrollViewer?.HorizontalOffset ?? 0,
            scrollViewer?.VerticalOffset ?? 0);
    }

    private static void CaptureExpandedObjectExplorerNodes(
        IEnumerable<FileSystemNode> nodes,
        HashSet<string> expandedNodeKeys)
    {
        foreach (FileSystemNode node in nodes)
        {
            if (node.IsExpanded)
            {
                expandedNodeKeys.Add(node.NodeKey);
            }

            CaptureExpandedObjectExplorerNodes(node.Children, expandedNodeKeys);
        }
    }

    private static void CaptureExpandedObjectExplorerContainers(
        ItemsControl itemsControl,
        HashSet<string> expandedNodeKeys)
    {
        itemsControl.UpdateLayout();
        foreach (object item in itemsControl.Items)
        {
            if (item is not FileSystemNode node ||
                itemsControl.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem treeViewItem)
            {
                continue;
            }

            if (treeViewItem.IsExpanded)
            {
                expandedNodeKeys.Add(node.NodeKey);
                CaptureExpandedObjectExplorerContainers(treeViewItem, expandedNodeKeys);
            }
        }
    }

    private async Task RestoreObjectExplorerViewStateAsync(ObjectExplorerViewState viewState)
    {
        RestoreExpandedObjectExplorerNodes(RootNodes, viewState.ExpandedNodeKeys);

        for (int pass = 0; pass < 3; pass++)
        {
            ObjectExplorer.UpdateLayout();
            await Dispatcher.InvokeAsync(
                () =>
                {
                    RestoreObjectExplorerContainerExpansion(ObjectExplorer, viewState.ExpandedNodeKeys);
                    ObjectExplorer.UpdateLayout();
                },
                DispatcherPriority.Loaded);
        }

        await Dispatcher.InvokeAsync(
            () =>
            {
                ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(ObjectExplorer);
                scrollViewer?.ScrollToHorizontalOffset(viewState.HorizontalOffset);
                scrollViewer?.ScrollToVerticalOffset(viewState.VerticalOffset);
            },
            DispatcherPriority.ContextIdle);
    }

    private void RestoreExpandedObjectExplorerNodes(
        IEnumerable<FileSystemNode> nodes,
        HashSet<string> expandedNodeKeys)
    {
        foreach (FileSystemNode node in nodes)
        {
            if (!expandedNodeKeys.Contains(node.NodeKey))
            {
                continue;
            }

            if (node.IsDirectory && !node.IsLoaded)
            {
                _fileTreeService.LoadChildren(node, _activeScope?.VirtualFolders);
            }

            node.IsExpanded = true;
            RestoreExpandedObjectExplorerNodes(node.Children, expandedNodeKeys);
        }
    }

    private void RestoreObjectExplorerContainerExpansion(
        ItemsControl itemsControl,
        HashSet<string> expandedNodeKeys)
    {
        itemsControl.UpdateLayout();
        foreach (object item in itemsControl.Items)
        {
            if (item is not FileSystemNode node ||
                !expandedNodeKeys.Contains(node.NodeKey) ||
                itemsControl.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem treeViewItem)
            {
                continue;
            }

            if (node.IsDirectory && !node.IsLoaded)
            {
                _fileTreeService.LoadChildren(node, _activeScope?.VirtualFolders);
            }

            node.IsExpanded = true;
            treeViewItem.IsExpanded = true;
            treeViewItem.UpdateLayout();
            RestoreObjectExplorerContainerExpansion(treeViewItem, expandedNodeKeys);
        }
    }

    private void ReplaceExpandedObjectExplorerState(IEnumerable<string> expandedNodeKeys)
    {
        _expandedObjectExplorerNodeKeys.Clear();
        foreach (string nodeKey in expandedNodeKeys)
        {
            if (!string.IsNullOrWhiteSpace(nodeKey))
            {
                _expandedObjectExplorerNodeKeys.Add(nodeKey);
            }
        }
    }

    private static void RemoveVirtualFolderChildKey(VirtualFolder virtualFolder, string childNodeKey)
    {
        List<string> keysToRemove = virtualFolder.ChildNodeKeys
            .Where(key => string.Equals(key, childNodeKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (string key in keysToRemove)
        {
            virtualFolder.ChildNodeKeys.Remove(key);
        }
    }

    private static string NormalizeVirtualFolderParentKey(string? parentNodeKey)
    {
        return string.IsNullOrWhiteSpace(parentNodeKey)
            ? FileSystemNode.RootParentKey
            : parentNodeKey;
    }

    private static bool CanOpenContainingFolder(FileSystemNode node)
    {
        try
        {
            return !node.IsDirectory &&
                   !node.IsVirtualDocument &&
                   File.Exists(node.FullPath) &&
                   !string.IsNullOrWhiteSpace(Path.GetDirectoryName(node.FullPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void OpenContainingFolder(FileSystemNode node)
    {
        if (!CanOpenContainingFolder(node))
        {
            StatusText = "Containing folder is not available for this Object Explorer item.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{node.FullPath}\"",
                UseShellExecute = true
            });
            StatusText = $"Opened containing folder for {node.Name}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            StatusText = $"Could not open containing folder: {ex.Message}";
        }
    }

    private void ObjectExplorer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _objectExplorerDragStartPoint = e.GetPosition(ObjectExplorer);
        TreeViewItem? item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        _pendingObjectExplorerDragNode = item?.DataContext as FileSystemNode;
    }

    private void ObjectExplorer_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pendingObjectExplorerDragNode == null)
        {
            return;
        }

        Point currentPoint = e.GetPosition(ObjectExplorer);
        if (Math.Abs(currentPoint.X - _objectExplorerDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - _objectExplorerDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        FileSystemNode draggedNode = _pendingObjectExplorerDragNode;
        _pendingObjectExplorerDragNode = null;
        var dataObject = new DataObject(ObjectExplorerDragDataFormat, draggedNode);
        DragDrop.DoDragDrop(ObjectExplorer, dataObject, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void ViewToggle_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingViewToggles ||
            CodeViewToggle == null ||
            DiagramViewToggle == null ||
            CodeViewRow == null ||
            DiagramViewRow == null)
        {
            return;
        }

        bool showCode = CodeViewToggle.IsChecked == true;
        bool showDiagram = DiagramViewToggle.IsChecked == true;

        if (!showCode && !showDiagram)
        {
            _isUpdatingViewToggles = true;
            if (ReferenceEquals(sender, CodeViewToggle))
            {
                DiagramViewToggle.IsChecked = true;
            }
            else
            {
                CodeViewToggle.IsChecked = true;
            }

            _isUpdatingViewToggles = false;
        }

        ApplyWorkspaceViewLayout();
    }

    private void ViewToggle_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, CodeViewToggle))
        {
            ShowSingleWorkspaceView(WorkspaceViewKind.Code);
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(sender, DiagramViewToggle))
        {
            ShowSingleWorkspaceView(WorkspaceViewKind.Diagram);
            e.Handled = true;
        }
    }

    private void ShowSingleWorkspaceView(WorkspaceViewKind viewKind)
    {
        if (CodeViewToggle == null || DiagramViewToggle == null)
        {
            return;
        }

        _isUpdatingViewToggles = true;
        CodeViewToggle.IsChecked = viewKind == WorkspaceViewKind.Code;
        DiagramViewToggle.IsChecked = viewKind == WorkspaceViewKind.Diagram;
        _isUpdatingViewToggles = false;

        _activeWorkspaceView = viewKind;
        ApplyWorkspaceViewLayout();
    }

    private void SetWorkspaceViewVisibility(bool showCode, bool showDiagram, WorkspaceViewKind activeView)
    {
        if (CodeViewToggle == null || DiagramViewToggle == null)
        {
            return;
        }

        if (!showCode && !showDiagram)
        {
            showCode = true;
            activeView = WorkspaceViewKind.Code;
        }

        _isUpdatingViewToggles = true;
        try
        {
            CodeViewToggle.IsChecked = showCode;
            DiagramViewToggle.IsChecked = showDiagram;
        }
        finally
        {
            _isUpdatingViewToggles = false;
        }

        _activeWorkspaceView = showDiagram && activeView == WorkspaceViewKind.Diagram
            ? WorkspaceViewKind.Diagram
            : WorkspaceViewKind.Code;
        ApplyWorkspaceViewLayout();
    }

    private void DiagramToolButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingDiagramToolToggles ||
            DiagramRectangleToolButton == null ||
            DiagramEllipseToolButton == null ||
            DiagramImageToolButton == null ||
            DiagramLineToolButton == null ||
            DiagramLabelToolButton == null ||
            DiagramPortalToolButton == null)
        {
            return;
        }

        if (_isDiagramLocked)
        {
            ClearDiagramDrawingTools();
            StatusText = "Unlock the diagram to use drawing tools.";
            return;
        }

        _isUpdatingDiagramToolToggles = true;
        SetWorkflowAddItemsMode(false);

        if (ReferenceEquals(sender, DiagramRectangleToolButton))
        {
            DiagramEllipseToolButton.IsChecked = false;
            DiagramImageToolButton.IsChecked = false;
            DiagramLineToolButton.IsChecked = false;
            DiagramLabelToolButton.IsChecked = false;
            DiagramPortalToolButton.IsChecked = false;
            _selectedDiagramImageId = null;
            _pendingPortalName = null;
            _pendingPortalPairPlacement = null;
        }
        else if (ReferenceEquals(sender, DiagramEllipseToolButton))
        {
            DiagramRectangleToolButton.IsChecked = false;
            DiagramImageToolButton.IsChecked = false;
            DiagramLineToolButton.IsChecked = false;
            DiagramLabelToolButton.IsChecked = false;
            DiagramPortalToolButton.IsChecked = false;
            _selectedDiagramImageId = null;
            _pendingPortalName = null;
            _pendingPortalPairPlacement = null;
        }
        else if (ReferenceEquals(sender, DiagramImageToolButton))
        {
            DiagramRectangleToolButton.IsChecked = false;
            DiagramEllipseToolButton.IsChecked = false;
            DiagramLineToolButton.IsChecked = false;
            DiagramLabelToolButton.IsChecked = false;
            DiagramPortalToolButton.IsChecked = false;
            _pendingPortalName = null;
            _pendingPortalPairPlacement = null;
        }
        else if (ReferenceEquals(sender, DiagramLineToolButton))
        {
            DiagramRectangleToolButton.IsChecked = false;
            DiagramEllipseToolButton.IsChecked = false;
            DiagramImageToolButton.IsChecked = false;
            DiagramLabelToolButton.IsChecked = false;
            DiagramPortalToolButton.IsChecked = false;
            _selectedDiagramImageId = null;
            _pendingPortalName = null;
            _pendingPortalPairPlacement = null;
        }
        else if (ReferenceEquals(sender, DiagramLabelToolButton))
        {
            DiagramRectangleToolButton.IsChecked = false;
            DiagramEllipseToolButton.IsChecked = false;
            DiagramImageToolButton.IsChecked = false;
            DiagramLineToolButton.IsChecked = false;
            DiagramPortalToolButton.IsChecked = false;
            _selectedDiagramImageId = null;
            _pendingPortalName = null;
            _pendingPortalPairPlacement = null;
        }
        else if (ReferenceEquals(sender, DiagramPortalToolButton))
        {
            DiagramRectangleToolButton.IsChecked = false;
            DiagramEllipseToolButton.IsChecked = false;
            DiagramImageToolButton.IsChecked = false;
            DiagramLineToolButton.IsChecked = false;
            DiagramLabelToolButton.IsChecked = false;
            _selectedDiagramImageId = null;
        }

        _isUpdatingDiagramToolToggles = false;
    }

    private void DiagramLockToggle_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingDiagramLockToggle)
        {
            return;
        }

        bool isLocked = sender is ToggleButton toggle
            ? toggle.IsChecked == true
            : DiagramLockToggleButton?.IsChecked == true;
        SetDiagramLockState(isLocked, updateToggle: false, showStatus: true);
    }

    private void DiagramLineToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiagramLineToolButton.IsChecked != true)
        {
            StatusText = "Line tool cleared.";
            return;
        }

        DiagramLineEndArrowMenuItem.IsChecked = _diagramLineHasEndArrow;
        OpenDiagramLineToolMenu();
        StatusText = _diagramLineHasEndArrow
            ? "Line tool: arrow at end."
            : "Line tool.";
    }

    private void DiagramLineEndArrowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _diagramLineHasEndArrow = DiagramLineEndArrowMenuItem.IsChecked;
        if (DiagramLineToolButton.IsChecked != true)
        {
            DiagramLineToolButton.IsChecked = true;
        }

        StatusText = _diagramLineHasEndArrow
            ? "Line tool: arrow at end."
            : "Line tool: no arrow.";
    }

    private void DiagramImageToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiagramImageToolButton.IsChecked != true)
        {
            _selectedDiagramImageId = null;
            StatusText = "Image tool cleared.";
            return;
        }

        RefreshDiagramImageToolMenu();
        OpenDiagramImageToolMenu();
    }

    private void DiagramPortalToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiagramPortalToolButton.IsChecked != true)
        {
            _pendingPortalName = null;
            _pendingPortalPairPlacement = null;
            StatusText = "Portal tool cleared.";
            return;
        }

        if (_pendingPortalPairPlacement != null)
        {
            StatusText = "Click the diagram canvas to place the paired portal point.";
            return;
        }

        string? portalName = PromptForPortalName("Portal", "Portal name");
        if (string.IsNullOrWhiteSpace(portalName))
        {
            DiagramPortalToolButton.IsChecked = false;
            return;
        }

        _pendingPortalName = portalName;
        StatusText = $"Portal tool: click the diagram canvas to place '{portalName}'.";
    }

    private void DiagramImageToolMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (DiagramImageToolButton.IsChecked == true &&
            string.IsNullOrWhiteSpace(_selectedDiagramImageId))
        {
            DiagramImageToolButton.IsChecked = false;
        }
    }

    private void DiagramImageToolMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string imageId)
        {
            return;
        }

        DiagramImageDefinition? image = FindDiagramImageDefinition(imageId);
        if (image == null)
        {
            _selectedDiagramImageId = null;
            DiagramImageToolButton.IsChecked = false;
            StatusText = "Selected diagram image is no longer available.";
            return;
        }

        _selectedDiagramImageId = image.Id;
        _isUpdatingDiagramToolToggles = true;
        DiagramRectangleToolButton.IsChecked = false;
        DiagramEllipseToolButton.IsChecked = false;
        DiagramImageToolButton.IsChecked = true;
        _isUpdatingDiagramToolToggles = false;
        DiagramImageToolButton.ToolTip = $"Image: {image.Name}";
        StatusText = $"Image tool: {image.Name}.";
    }

    private void DiagramOutlineColorButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDiagramColorMenu(DiagramOutlineColorButton);
    }

    private void DiagramOutlineColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string colorText)
        {
            return;
        }

        SetDiagramOutlineColor(colorText);
    }

    private void DiagramCustomOutlineColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPickerWindow(GetColorPickerInitialColor(_diagramOutlineColor, "#000000"))
        {
            Owner = this
        };

        if (picker.ShowDialog() == true)
        {
            SetDiagramOutlineColor(picker.SelectedColor);
        }
    }

    private void DiagramBackColorButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDiagramColorMenu(DiagramBackColorButton);
    }

    private void DiagramBackColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string colorText)
        {
            return;
        }

        SetDiagramBackColor(colorText);
    }

    private void DiagramCustomBackColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPickerWindow(GetColorPickerInitialColor(_diagramBackColor, "#FFFFFF"))
        {
            Owner = this
        };

        if (picker.ShowDialog() == true)
        {
            SetDiagramBackColor(picker.SelectedColor);
        }
    }

    private async void ExportDiagramPngButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDiagramExporting)
        {
            return;
        }

        Keyboard.ClearFocus();
        Focus();
        DiagramCanvas.UpdateLayout();

        List<DiagramObjectSnapshot> snapshots = CaptureDiagramObjects();
        if (snapshots.Count == 0)
        {
            StatusText = "There is no diagram content to export.";
            return;
        }

        Rect exportBounds = CalculateDiagramExportBounds(snapshots);
        if (exportBounds.IsEmpty || exportBounds.Width <= 0 || exportBounds.Height <= 0)
        {
            StatusText = "There is no diagram content to export.";
            return;
        }

        int exportWidth = (int)Math.Ceiling(exportBounds.Width);
        int exportHeight = (int)Math.Ceiling(exportBounds.Height);
        long exportPixels = (long)exportWidth * exportHeight;
        if (exportWidth > MaximumDiagramExportDimension ||
            exportHeight > MaximumDiagramExportDimension ||
            exportPixels > MaximumDiagramExportPixels)
        {
            StatusText = $"Diagram export is too large ({exportWidth:N0} x {exportHeight:N0}). Reduce the diagram bounds or split it before exporting.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".png",
            FileName = $"{CreateSafeFileName(CurrentDiagramName)}.png",
            Filter = "PNG image (*.png)|*.png",
            OverwritePrompt = true,
            Title = "Export Diagram as PNG"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SetDiagramExportProgress(isExporting: true, progress: 0);
            StatusText = "Exporting diagram PNG...";
            await ExportDiagramToPngAsync(dialog.FileName, snapshots, exportBounds, exportWidth, exportHeight);
            StatusText = $"Exported diagram PNG to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            StatusText = $"Could not export diagram PNG: {ex.Message}";
        }
        finally
        {
            SetDiagramExportProgress(isExporting: false, progress: 0);
        }
    }

    private async void SaveDiagramButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveDiagramFromUiAsync();
    }

    private void NewDiagramButton_Click(object sender, RoutedEventArgs e)
    {
        CreateNewBlankDiagram();
    }

    private async void DiagramSidebarSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveDiagramFromUiAsync();
    }

    private async Task SaveDiagramFromUiAsync()
    {
        CommitMetadataEditorChanges();
        if (!CommitWorkflowEditorChanges(requireValidWorkflowName: true))
        {
            return;
        }

        if (_activeScope == null)
        {
            StatusText = "Open a scope before saving a diagram.";
            return;
        }

        string diagramId = _activeDiagramId ?? Guid.NewGuid().ToString("N");
        string diagramName = CurrentDiagramName;

        if (string.IsNullOrWhiteSpace(_activeDiagramId))
        {
            string? promptedName = PromptForDiagramName("Untitled Diagram", "Diagram name");
            if (string.IsNullOrWhiteSpace(promptedName))
            {
                return;
            }

            diagramName = promptedName;
        }

        await SaveCurrentDiagramAsync(diagramId, diagramName);
    }

    private async void SaveDiagramAsButton_Click(object sender, RoutedEventArgs e)
    {
        CommitMetadataEditorChanges();
        if (!CommitWorkflowEditorChanges(requireValidWorkflowName: true))
        {
            return;
        }

        if (_activeScope == null)
        {
            StatusText = "Open a scope before saving a diagram.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_activeDiagramId))
        {
            StatusText = "Save the diagram before using Save As.";
            return;
        }

        string? diagramName = PromptForDiagramName($"{CurrentDiagramName} Copy", "New diagram name");
        if (string.IsNullOrWhiteSpace(diagramName))
        {
            return;
        }

        await SaveCurrentDiagramAsync(Guid.NewGuid().ToString("N"), diagramName);
    }

    private async void DeleteDiagramButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_activeDiagramId))
        {
            StatusText = "Open a saved diagram before deleting it.";
            return;
        }

        string diagramId = _activeDiagramId;
        string diagramName = CurrentDiagramName;
        MessageBoxResult result = MessageBox.Show(
            this,
            $"Delete diagram '{diagramName}'? This will remove it from all scopes.",
            "Delete Diagram",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        DiagramDocument? diagram = _diagramLibrary.Find(diagramId);
        if (diagram != null)
        {
            _diagramLibrary.Diagrams.Remove(diagram);
        }

        int removedResources = 0;
        foreach (Scope scope in _scopeLibrary.Scopes)
        {
            List<ScopedResource> resourcesToRemove = scope.Resources
                .Where(resource =>
                    resource.Kind == ResourceKind.Diagram &&
                    string.Equals(resource.Path, diagramId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (ScopedResource resource in resourcesToRemove)
            {
                scope.Resources.Remove(resource);
                removedResources++;
            }
        }

        try
        {
            await _diagramStore.SaveAsync(_diagramLibrary);
            await _scopeStore.SaveAsync(_scopeLibrary);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not delete diagram: {ex.Message}";
            return;
        }

        ClearDiagramObjects();
        _currentDiagramWorkflows.Clear();
        ResetWorkflowEditor();
        _diagramUndoStack.Clear();
        SetCurrentDiagramIdentity(null, "Unsaved Diagram");
        await RefreshObjectExplorerAfterDiagramChangeAsync();
        StatusText = $"Deleted diagram '{diagramName}' and removed {removedResources} scope reference(s).";
    }

    private async Task SaveCurrentDiagramAsync(string diagramId, string diagramName)
    {
        DiagramDocument diagram = CreateCurrentDiagramDocument(diagramId, diagramName);

        _diagramLibrary.Upsert(diagram);
        EnsureActiveScopeContainsDiagram(diagram);

        try
        {
            await _diagramStore.SaveAsync(_diagramLibrary);
            await _scopeStore.SaveAsync(_scopeLibrary);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save diagram: {ex.Message}";
            return;
        }

        SetCurrentDiagramIdentity(diagram.DiagramId, diagram.Name);
        await RefreshObjectExplorerAfterDiagramChangeAsync();
        StatusText = $"Saved diagram '{diagram.Name}'.";
    }

    private DiagramDocument CreateCurrentDiagramDocument(string diagramId, string diagramName)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DiagramDocument? existingDiagram = _diagramLibrary.Find(diagramId);
        return new DiagramDocument
        {
            DiagramId = diagramId,
            Name = diagramName,
            CreatedAtUtc = existingDiagram?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
            CanvasZoom = _diagramCanvasZoom,
            ViewportHorizontalOffset = DiagramScrollViewer.HorizontalOffset,
            ViewportVerticalOffset = DiagramScrollViewer.VerticalOffset,
            Objects = CaptureDiagramObjects(),
            Workflows = _currentDiagramWorkflows
                .Select(workflow => workflow.Clone())
                .ToList()
        };
    }

    private async Task RefreshObjectExplorerAfterDiagramChangeAsync()
    {
        if (_activeScope == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ObjectExplorerSearchTextBox.Text))
        {
            LoadObjectExplorerRoots(_activeScope);
            return;
        }

        await ApplyObjectExplorerSearchAsync();
    }

    private void EnsureActiveScopeContainsDiagram(DiagramDocument diagram)
    {
        if (_activeScope == null)
        {
            return;
        }

        ScopedResource? resource = _activeScope.Resources.FirstOrDefault(candidate =>
            candidate.Kind == ResourceKind.Diagram &&
            string.Equals(candidate.Path, diagram.DiagramId, StringComparison.OrdinalIgnoreCase));

        string details = $"Updated {diagram.UpdatedAtUtc.LocalDateTime:g}";
        if (resource == null)
        {
            _activeScope.Resources.Add(new ScopedResource
            {
                Kind = ResourceKind.Diagram,
                Path = diagram.DiagramId,
                DisplayNameOverride = diagram.Name,
                DetailsOverride = details,
                IncludeChildren = false,
                AddedAtUtc = DateTimeOffset.UtcNow
            });
            return;
        }

        resource.DisplayNameOverride = diagram.Name;
        resource.DetailsOverride = details;
    }

    private string? PromptForDiagramName(string initialName, string prompt, string title = "Save Diagram")
    {
        var dialog = new DiagramNameWindow(initialName, prompt, title)
        {
            Owner = this
        };

        return dialog.ShowDialog() == true ? dialog.DiagramName : null;
    }

    private async Task ExportDiagramToPngAsync(
        string filePath,
        IReadOnlyList<DiagramObjectSnapshot> snapshots,
        Rect exportBounds,
        int exportWidth,
        int exportHeight)
    {
        var finalBitmap = new WriteableBitmap(
            exportWidth,
            exportHeight,
            96,
            96,
            PixelFormats.Pbgra32,
            null);

        int columns = (int)Math.Ceiling(exportWidth / (double)DiagramExportTileSize);
        int rows = (int)Math.Ceiling(exportHeight / (double)DiagramExportTileSize);
        int totalTiles = Math.Max(1, columns * rows);
        int completedTiles = 0;

        for (int row = 0; row < rows; row++)
        {
            int tileY = row * DiagramExportTileSize;
            int tileHeight = Math.Min(DiagramExportTileSize, exportHeight - tileY);

            for (int column = 0; column < columns; column++)
            {
                int tileX = column * DiagramExportTileSize;
                int tileWidth = Math.Min(DiagramExportTileSize, exportWidth - tileX);

                RenderTargetBitmap tileBitmap = RenderDiagramExportTile(snapshots, exportBounds, tileX, tileY, tileWidth, tileHeight);
                int stride = tileWidth * 4;
                byte[] pixels = new byte[stride * tileHeight];
                tileBitmap.CopyPixels(pixels, stride, 0);
                finalBitmap.WritePixels(new Int32Rect(tileX, tileY, tileWidth, tileHeight), pixels, stride, 0);

                completedTiles++;
                SetDiagramExportProgress(isExporting: true, completedTiles * 90d / totalTiles);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        SetDiagramExportProgress(isExporting: true, progress: 95);
        await using FileStream stream = File.Create(filePath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(finalBitmap));
        encoder.Save(stream);
        SetDiagramExportProgress(isExporting: true, progress: 100);
    }

    private RenderTargetBitmap RenderDiagramExportTile(
        IReadOnlyList<DiagramObjectSnapshot> snapshots,
        Rect exportBounds,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight)
    {
        var tileCanvas = new Canvas
        {
            Width = tileWidth,
            Height = tileHeight,
            Background = Brushes.White,
            ClipToBounds = true
        };

        double tileOriginX = exportBounds.Left + tileX;
        double tileOriginY = exportBounds.Top + tileY;
        var tileBounds = new Rect(tileOriginX, tileOriginY, tileWidth, tileHeight);

        foreach (DiagramObjectSnapshot snapshot in snapshots)
        {
            Rect objectBounds = GetDiagramObjectExportBounds(snapshot);
            objectBounds.Intersect(tileBounds);
            if (objectBounds.IsEmpty)
            {
                continue;
            }

            DiagramObjectSnapshot shiftedSnapshot = CreateShiftedDiagramSnapshot(snapshot, tileOriginX, tileOriginY);
            FrameworkElement? diagramObject = CreateDiagramObjectFromSnapshot(shiftedSnapshot);
            if (diagramObject != null)
            {
                tileCanvas.Children.Add(diagramObject);
            }
        }

        var tileSize = new Size(tileWidth, tileHeight);
        tileCanvas.Measure(tileSize);
        tileCanvas.Arrange(new Rect(tileSize));
        tileCanvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(tileWidth, tileHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(tileCanvas);
        return bitmap;
    }

    private static DiagramObjectSnapshot CreateShiftedDiagramSnapshot(
        DiagramObjectSnapshot snapshot,
        double offsetX,
        double offsetY)
    {
        DiagramObjectSnapshot shifted = snapshot.Clone();
        shifted.Left -= offsetX;
        shifted.Top -= offsetY;
        shifted.LineStartX -= offsetX;
        shifted.LineStartY -= offsetY;
        shifted.LineEndX -= offsetX;
        shifted.LineEndY -= offsetY;
        shifted.LabelAnchorX -= offsetX;
        shifted.LabelAnchorY -= offsetY;
        shifted.LabelBoxLeft -= offsetX;
        shifted.LabelBoxTop -= offsetY;
        return shifted;
    }

    private static Rect CalculateDiagramExportBounds(IEnumerable<DiagramObjectSnapshot> snapshots)
    {
        Rect bounds = Rect.Empty;

        foreach (DiagramObjectSnapshot snapshot in snapshots)
        {
            Rect objectBounds = GetDiagramObjectExportBounds(snapshot);
            if (objectBounds.IsEmpty)
            {
                continue;
            }

            if (bounds.IsEmpty)
            {
                bounds = objectBounds;
            }
            else
            {
                bounds.Union(objectBounds);
            }
        }

        if (bounds.IsEmpty)
        {
            return Rect.Empty;
        }

        bounds.Inflate(DiagramExportMargin, DiagramExportMargin);
        double left = Math.Max(0, Math.Floor(bounds.Left));
        double top = Math.Max(0, Math.Floor(bounds.Top));
        double right = Math.Min(VirtualCanvasWidth, Math.Ceiling(bounds.Right));
        double bottom = Math.Min(VirtualCanvasHeight, Math.Ceiling(bounds.Bottom));

        return right <= left || bottom <= top
            ? Rect.Empty
            : new Rect(left, top, right - left, bottom - top);
    }

    private static Rect GetDiagramObjectExportBounds(DiagramObjectSnapshot snapshot)
    {
        Rect bounds = snapshot.ObjectType switch
        {
            DiagramObjectType.Shape or DiagramObjectType.Image or DiagramObjectType.WorkflowMarker => new Rect(
                snapshot.Left,
                snapshot.Top,
                Math.Max(1, snapshot.Width),
                Math.Max(1, snapshot.Height)),

            DiagramObjectType.Line => CreateLineBounds(snapshot.LineStartX, snapshot.LineStartY, snapshot.LineEndX, snapshot.LineEndY),

            DiagramObjectType.Label => CreateLabelBounds(snapshot),

            _ => Rect.Empty
        };

        if (!bounds.IsEmpty)
        {
            bounds.Inflate(8, 8);
        }

        return bounds;
    }

    private static Rect CreateLineBounds(double startX, double startY, double endX, double endY)
    {
        double left = Math.Min(startX, endX);
        double top = Math.Min(startY, endY);
        double width = Math.Max(1, Math.Abs(endX - startX));
        double height = Math.Max(1, Math.Abs(endY - startY));
        return new Rect(left, top, width, height);
    }

    private static Rect CreateLabelBounds(DiagramObjectSnapshot snapshot)
    {
        var bounds = new Rect(
            snapshot.LabelBoxLeft,
            snapshot.LabelBoxTop,
            Math.Max(1, snapshot.LabelBoxWidth),
            Math.Max(1, snapshot.LabelBoxHeight));

        bounds.Union(new Point(snapshot.LabelAnchorX, snapshot.LabelAnchorY));
        return bounds;
    }

    private void SetDiagramExportProgress(bool isExporting, double progress)
    {
        _isDiagramExporting = isExporting;
        ExportDiagramPngButton.IsEnabled = !isExporting;
        DiagramExportProgressBar.Value = Math.Clamp(progress, 0, 100);
        DiagramExportProgressBar.Visibility = isExporting ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string CreateSafeFileName(string name)
    {
        string fileName = string.IsNullOrWhiteSpace(name) ? "Diagram" : name.Trim();
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(fileName) ? "Diagram" : fileName;
    }

    private static void OpenDiagramColorMenu(Button button)
    {
        if (button.ContextMenu == null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void RefreshDiagramImageToolMenu()
    {
        if (DiagramImageToolButton?.ContextMenu == null)
        {
            return;
        }

        ContextMenu menu = DiagramImageToolButton.ContextMenu;
        menu.Items.Clear();

        List<DiagramImageDefinition> images = _appSettings.DiagramImages.Images
            .Where(image => !string.IsNullOrWhiteSpace(image.Name) &&
                            !string.IsNullOrWhiteSpace(image.ImageDataBase64))
            .OrderBy(image => image.Name)
            .ToList();

        if (images.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "No images configured",
                IsEnabled = false
            });
            return;
        }

        foreach (DiagramImageDefinition image in images)
        {
            var menuItem = new MenuItem
            {
                Header = CreateDiagramImageMenuHeader(image),
                Tag = image.Id
            };
            menuItem.Click += DiagramImageToolMenuItem_Click;
            menu.Items.Add(menuItem);
        }
    }

    private UIElement CreateDiagramImageMenuHeader(DiagramImageDefinition image)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            MinWidth = 160
        };

        var preview = new Image
        {
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
            Source = CreateImageSource(image.ImageDataBase64)
        };

        panel.Children.Add(preview);
        panel.Children.Add(new TextBlock
        {
            Text = image.Name,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        return panel;
    }

    private void OpenDiagramImageToolMenu()
    {
        if (DiagramImageToolButton.ContextMenu == null)
        {
            return;
        }

        DiagramImageToolButton.ContextMenu.PlacementTarget = DiagramImageToolButton;
        DiagramImageToolButton.ContextMenu.IsOpen = true;
    }

    private void OpenDiagramLineToolMenu()
    {
        if (DiagramLineToolButton.ContextMenu == null)
        {
            return;
        }

        DiagramLineToolButton.ContextMenu.PlacementTarget = DiagramLineToolButton;
        DiagramLineToolButton.ContextMenu.IsOpen = true;
    }

    private DiagramImageDefinition? FindDiagramImageDefinition(string? imageId)
    {
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return null;
        }

        return _appSettings.DiagramImages.Images.FirstOrDefault(image =>
            string.Equals(image.Id, imageId, StringComparison.OrdinalIgnoreCase));
    }

    private void SplitOrientationButton_Click(object sender, RoutedEventArgs e)
    {
        if (CodeViewToggle.IsChecked != true || DiagramViewToggle.IsChecked != true)
        {
            return;
        }

        _workspaceSplitOrientation = _workspaceSplitOrientation switch
        {
            WorkspaceSplitOrientation.DiagramTop => WorkspaceSplitOrientation.DiagramRight,
            WorkspaceSplitOrientation.DiagramRight => WorkspaceSplitOrientation.DiagramBottom,
            WorkspaceSplitOrientation.DiagramBottom => WorkspaceSplitOrientation.DiagramLeft,
            WorkspaceSplitOrientation.DiagramLeft => WorkspaceSplitOrientation.DiagramTop,
            _ => WorkspaceSplitOrientation.DiagramBottom
        };

        ApplyWorkspaceViewLayout();
    }

    private void CodeViewModeToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingCodeViewMode)
        {
            return;
        }

        if (ReferenceEquals(sender, CodeCanvasModeToggle))
        {
            SetCodeViewMode(CodeViewMode.Canvas);
            return;
        }

        if (ReferenceEquals(sender, CodeTabModeToggle))
        {
            SetCodeViewMode(CodeViewMode.Tabs);
        }
    }

    private void CodeViewModeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingCodeViewMode)
        {
            return;
        }

        if (CodeCanvasModeToggle == null || CodeTabModeToggle == null)
        {
            return;
        }

        if (CodeCanvasModeToggle.IsChecked != true && CodeTabModeToggle.IsChecked != true)
        {
            SetCodeViewMode(_codeViewMode);
        }
    }

    private void CodeViewHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SetActiveWorkspaceView(WorkspaceViewKind.Code);
        StopCodeShiftPan();
        StopCodeCtrlShiftZoom();

        FrameworkElement? floatingWindow = GetFloatingCodeViewWindowFromEvent(e);
        if (_codeViewMode == CodeViewMode.Canvas &&
            e.ChangedButton == MouseButton.Left &&
            IsMouseEventInsideElement(e, WorkspaceScrollViewer) &&
            floatingWindow == null)
        {
            SetActiveCodeWindow(null);
        }

        if (floatingWindow != null)
        {
            return;
        }

        if (TryHandleControlCodeCanvasPan(e))
        {
            e.Handled = true;
        }
    }

    private void CodeViewHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (IsMouseEventOverActiveCodeWindow(e))
        {
            _isControlPanningCodeCanvas = false;
            StopCodeShiftPan();
            StopCodeCtrlShiftZoom();
            return;
        }

        if (TryHandleCodeCanvasCtrlShiftZoom(e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleCodeCanvasShiftPan(e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleControlCodeCanvasPan(e))
        {
            e.Handled = true;
        }
    }

    private void CodeViewHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetActiveWorkspaceView(WorkspaceViewKind.Code);
        StopCodeShiftPan();
        StopCodeCtrlShiftZoom();

        if (IsMouseEventOverActiveCodeWindow(e))
        {
            return;
        }
    }

    private FloatingCodeWindow? GetFloatingCodeWindowFromEvent(MouseEventArgs e)
    {
        return FindAncestor<FloatingCodeWindow>(e.OriginalSource as DependencyObject);
    }

    private FrameworkElement? GetFloatingCodeViewWindowFromEvent(MouseEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        return FindAncestor<FloatingCodeWindow>(source) ??
               (FrameworkElement?)FindAncestor<FloatingSpreadsheetWindow>(source);
    }

    private bool IsMouseEventOverActiveCodeWindow(MouseEventArgs e)
    {
        return _activeCodeWindow != null &&
               ReferenceEquals(GetFloatingCodeWindowFromEvent(e), _activeCodeWindow);
    }

    private bool IsMouseOverActiveCodeWindow()
    {
        return _activeCodeWindow?.IsMouseOver == true;
    }

    private void DiagramViewHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SetActiveWorkspaceView(WorkspaceViewKind.Diagram);
        StopDiagramShiftPan();
        StopDiagramCtrlShiftZoom();
        if (TryHandleControlDiagramCanvasPan(e))
        {
            e.Handled = true;
        }
    }

    private void DiagramViewHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (TryHandleDiagramCtrlShiftZoom(e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleDiagramShiftPan(e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleControlDiagramCanvasPan(e))
        {
            e.Handled = true;
        }
    }

    private void DiagramViewHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetActiveWorkspaceView(WorkspaceViewKind.Diagram);
        StopDiagramShiftPan();
        StopDiagramCtrlShiftZoom();
    }

    private bool TryHandleDiagramCtrlShiftZoom(MouseEventArgs e)
    {
        if (_appSettings.KeyboardShortcuts.EnableDiagramCtrlShiftMouseZooming != true ||
            DiagramScrollViewer == null ||
            DiagramScrollViewer.Visibility != Visibility.Visible ||
            _isDrawingDiagramShape ||
            _isDrawingDiagramImage ||
            _isDrawingDiagramLine ||
            _isDiagramCanvasPanning ||
            e.LeftButton != MouseButtonState.Released ||
            e.MiddleButton != MouseButtonState.Released ||
            e.RightButton != MouseButtonState.Released)
        {
            StopDiagramCtrlShiftZoom();
            return false;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != ModifierKeys.Control ||
            (modifiers & ModifierKeys.Shift) != ModifierKeys.Shift ||
            !IsMouseEventInsideElement(e, DiagramScrollViewer))
        {
            StopDiagramCtrlShiftZoom();
            return false;
        }

        Point currentPoint = e.GetPosition(DiagramScrollViewer);
        if (!_isCtrlShiftZoomingDiagramCanvas)
        {
            StopDiagramShiftPan();
            _diagramCtrlShiftZoomOriginPoint = currentPoint;
            _diagramCtrlShiftZoomCurrentPoint = currentPoint;
            _diagramCtrlShiftZoomAnchorViewportPoint = currentPoint;
            _diagramCtrlShiftZoomAnchorCanvasPoint = e.GetPosition(DiagramCanvas);
            _isCtrlShiftZoomingDiagramCanvas = true;
            DiagramViewHost.Cursor = Cursors.SizeNS;
            if (!_diagramCtrlShiftZoomTimer.IsEnabled)
            {
                _diagramCtrlShiftZoomTimer.Start();
            }
        }
        else
        {
            _diagramCtrlShiftZoomCurrentPoint = currentPoint;
        }

        SetActiveWorkspaceView(WorkspaceViewKind.Diagram);
        return true;
    }

    private bool TryHandleDiagramShiftPan(MouseEventArgs e)
    {
        if (_appSettings.KeyboardShortcuts.EnableDiagramShiftMousePanning != true ||
            DiagramScrollViewer == null ||
            DiagramScrollViewer.Visibility != Visibility.Visible ||
            _isDrawingDiagramShape ||
            _isDrawingDiagramImage ||
            _isDrawingDiagramLine ||
            _isDiagramCanvasPanning ||
            e.LeftButton != MouseButtonState.Released ||
            e.MiddleButton != MouseButtonState.Released ||
            e.RightButton != MouseButtonState.Released)
        {
            StopDiagramShiftPan();
            return false;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != ModifierKeys.Shift ||
            (modifiers & ModifierKeys.Control) == ModifierKeys.Control ||
            !IsMouseEventInsideElement(e, DiagramScrollViewer))
        {
            StopDiagramShiftPan();
            return false;
        }

        Point currentPoint = e.GetPosition(DiagramScrollViewer);
        if (!_isShiftPanningDiagramCanvas)
        {
            _diagramShiftPanOriginPoint = currentPoint;
            _diagramShiftPanCurrentPoint = currentPoint;
            _isShiftPanningDiagramCanvas = true;
            DiagramViewHost.Cursor = Cursors.ScrollAll;
            if (!_diagramShiftPanTimer.IsEnabled)
            {
                _diagramShiftPanTimer.Start();
            }
        }
        else
        {
            _diagramShiftPanCurrentPoint = currentPoint;
        }

        SetActiveWorkspaceView(WorkspaceViewKind.Diagram);
        return true;
    }

    private void DiagramShiftPanTimer_Tick(object? sender, EventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (!_isShiftPanningDiagramCanvas ||
            DiagramScrollViewer == null ||
            DiagramScrollViewer.Visibility != Visibility.Visible ||
            !DiagramScrollViewer.IsMouseOver ||
            (modifiers & ModifierKeys.Shift) != ModifierKeys.Shift ||
            (modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            StopDiagramShiftPan();
            return;
        }

        Vector delta = _diagramShiftPanCurrentPoint - _diagramShiftPanOriginPoint;
        double horizontalChange = Math.Abs(delta.X) <= DiagramShiftPanDeadZone
            ? 0
            : delta.X * DiagramShiftPanSpeedFactor;
        double verticalChange = Math.Abs(delta.Y) <= DiagramShiftPanDeadZone
            ? 0
            : delta.Y * DiagramShiftPanSpeedFactor;

        if (Math.Abs(horizontalChange) <= 0.01 && Math.Abs(verticalChange) <= 0.01)
        {
            return;
        }

        DiagramScrollViewer.ScrollToHorizontalOffset(DiagramScrollViewer.HorizontalOffset + horizontalChange);
        DiagramScrollViewer.ScrollToVerticalOffset(DiagramScrollViewer.VerticalOffset + verticalChange);
    }

    private void DiagramCtrlShiftZoomTimer_Tick(object? sender, EventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (!_isCtrlShiftZoomingDiagramCanvas ||
            DiagramScrollViewer == null ||
            DiagramScrollViewer.Visibility != Visibility.Visible ||
            !DiagramScrollViewer.IsMouseOver ||
            (modifiers & ModifierKeys.Control) != ModifierKeys.Control ||
            (modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
        {
            StopDiagramCtrlShiftZoom();
            return;
        }

        double verticalDistance = _diagramCtrlShiftZoomCurrentPoint.Y - _diagramCtrlShiftZoomOriginPoint.Y;
        if (Math.Abs(verticalDistance) <= DiagramCtrlShiftZoomDeadZone)
        {
            return;
        }

        double previousZoom = _diagramCanvasZoom;
        double multiplier = Math.Exp(-verticalDistance * DiagramCtrlShiftZoomSpeedFactor);
        _diagramCanvasZoom = NormalizeCanvasZoom(_diagramCanvasZoom * multiplier);
        if (Math.Abs(_diagramCanvasZoom - previousZoom) <= 0.0001)
        {
            return;
        }

        ApplyDiagramCanvasZoom();
        DiagramScrollViewer.UpdateLayout();

        DiagramScrollViewer.ScrollToHorizontalOffset(
            (_diagramCtrlShiftZoomAnchorCanvasPoint.X * _diagramCanvasZoom) -
            _diagramCtrlShiftZoomAnchorViewportPoint.X);
        DiagramScrollViewer.ScrollToVerticalOffset(
            (_diagramCtrlShiftZoomAnchorCanvasPoint.Y * _diagramCanvasZoom) -
            _diagramCtrlShiftZoomAnchorViewportPoint.Y);

        StatusText = $"Diagram zoom: {_diagramCanvasZoom:P0}";
    }

    private void StopDiagramShiftPan()
    {
        _isShiftPanningDiagramCanvas = false;
        if (_diagramShiftPanTimer.IsEnabled)
        {
            _diagramShiftPanTimer.Stop();
        }

        if (DiagramViewHost != null)
        {
            DiagramViewHost.Cursor = null;
        }
    }

    private void StopDiagramCtrlShiftZoom()
    {
        _isCtrlShiftZoomingDiagramCanvas = false;
        if (_diagramCtrlShiftZoomTimer.IsEnabled)
        {
            _diagramCtrlShiftZoomTimer.Stop();
        }

        if (DiagramViewHost != null)
        {
            DiagramViewHost.Cursor = null;
        }
    }

    private bool TryHandleCodeCanvasCtrlShiftZoom(MouseEventArgs e)
    {
        if (_appSettings.KeyboardShortcuts.EnableCodeCanvasCtrlShiftMouseZooming != true ||
            _codeViewMode != CodeViewMode.Canvas ||
            WorkspaceScrollViewer == null ||
            WorkspaceScrollViewer.Visibility != Visibility.Visible ||
            IsMouseEventOverActiveCodeWindow(e) ||
            _isCanvasPanning ||
            e.LeftButton != MouseButtonState.Released ||
            e.MiddleButton != MouseButtonState.Released ||
            e.RightButton != MouseButtonState.Released)
        {
            StopCodeCtrlShiftZoom();
            return false;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != ModifierKeys.Control ||
            (modifiers & ModifierKeys.Shift) != ModifierKeys.Shift ||
            !IsMouseEventInsideElement(e, WorkspaceScrollViewer))
        {
            StopCodeCtrlShiftZoom();
            return false;
        }

        Point currentPoint = e.GetPosition(WorkspaceScrollViewer);
        if (!_isCtrlShiftZoomingCodeCanvas)
        {
            StopCodeShiftPan();
            _codeCtrlShiftZoomOriginPoint = currentPoint;
            _codeCtrlShiftZoomCurrentPoint = currentPoint;
            _codeCtrlShiftZoomAnchorViewportPoint = currentPoint;
            _codeCtrlShiftZoomAnchorCanvasPoint = e.GetPosition(WorkspaceCanvas);
            _isCtrlShiftZoomingCodeCanvas = true;
            CodeViewHost.Cursor = Cursors.SizeNS;
            if (!_codeCtrlShiftZoomTimer.IsEnabled)
            {
                _codeCtrlShiftZoomTimer.Start();
            }
        }
        else
        {
            _codeCtrlShiftZoomCurrentPoint = currentPoint;
        }

        SetActiveWorkspaceView(WorkspaceViewKind.Code);
        return true;
    }

    private bool TryHandleCodeCanvasShiftPan(MouseEventArgs e)
    {
        if (_appSettings.KeyboardShortcuts.EnableCodeCanvasShiftMousePanning != true ||
            _codeViewMode != CodeViewMode.Canvas ||
            WorkspaceScrollViewer == null ||
            WorkspaceScrollViewer.Visibility != Visibility.Visible ||
            IsMouseEventOverActiveCodeWindow(e) ||
            _isCanvasPanning ||
            e.LeftButton != MouseButtonState.Released ||
            e.MiddleButton != MouseButtonState.Released ||
            e.RightButton != MouseButtonState.Released)
        {
            StopCodeShiftPan();
            return false;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != ModifierKeys.Shift ||
            (modifiers & ModifierKeys.Control) == ModifierKeys.Control ||
            !IsMouseEventInsideElement(e, WorkspaceScrollViewer))
        {
            StopCodeShiftPan();
            return false;
        }

        Point currentPoint = e.GetPosition(WorkspaceScrollViewer);
        if (!_isShiftPanningCodeCanvas)
        {
            _codeShiftPanOriginPoint = currentPoint;
            _codeShiftPanCurrentPoint = currentPoint;
            _isShiftPanningCodeCanvas = true;
            CodeViewHost.Cursor = Cursors.ScrollAll;
            if (!_codeShiftPanTimer.IsEnabled)
            {
                _codeShiftPanTimer.Start();
            }
        }
        else
        {
            _codeShiftPanCurrentPoint = currentPoint;
        }

        SetActiveWorkspaceView(WorkspaceViewKind.Code);
        return true;
    }

    private void CodeShiftPanTimer_Tick(object? sender, EventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (!_isShiftPanningCodeCanvas ||
            _codeViewMode != CodeViewMode.Canvas ||
            WorkspaceScrollViewer == null ||
            WorkspaceScrollViewer.Visibility != Visibility.Visible ||
            !WorkspaceScrollViewer.IsMouseOver ||
            IsMouseOverActiveCodeWindow() ||
            (modifiers & ModifierKeys.Shift) != ModifierKeys.Shift ||
            (modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            StopCodeShiftPan();
            return;
        }

        Vector delta = _codeShiftPanCurrentPoint - _codeShiftPanOriginPoint;
        double horizontalChange = Math.Abs(delta.X) <= DiagramShiftPanDeadZone
            ? 0
            : delta.X * DiagramShiftPanSpeedFactor;
        double verticalChange = Math.Abs(delta.Y) <= DiagramShiftPanDeadZone
            ? 0
            : delta.Y * DiagramShiftPanSpeedFactor;

        if (Math.Abs(horizontalChange) <= 0.01 && Math.Abs(verticalChange) <= 0.01)
        {
            return;
        }

        WorkspaceScrollViewer.ScrollToHorizontalOffset(WorkspaceScrollViewer.HorizontalOffset + horizontalChange);
        WorkspaceScrollViewer.ScrollToVerticalOffset(WorkspaceScrollViewer.VerticalOffset + verticalChange);
        CaptureViewportState();
    }

    private void CodeCtrlShiftZoomTimer_Tick(object? sender, EventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (!_isCtrlShiftZoomingCodeCanvas ||
            _codeViewMode != CodeViewMode.Canvas ||
            WorkspaceScrollViewer == null ||
            WorkspaceScrollViewer.Visibility != Visibility.Visible ||
            !WorkspaceScrollViewer.IsMouseOver ||
            IsMouseOverActiveCodeWindow() ||
            (modifiers & ModifierKeys.Control) != ModifierKeys.Control ||
            (modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
        {
            StopCodeCtrlShiftZoom();
            return;
        }

        double verticalDistance = _codeCtrlShiftZoomCurrentPoint.Y - _codeCtrlShiftZoomOriginPoint.Y;
        if (Math.Abs(verticalDistance) <= DiagramCtrlShiftZoomDeadZone)
        {
            return;
        }

        double previousZoom = _canvasZoom;
        double multiplier = Math.Exp(-verticalDistance * DiagramCtrlShiftZoomSpeedFactor);
        _canvasZoom = NormalizeCanvasZoom(_canvasZoom * multiplier);
        if (Math.Abs(_canvasZoom - previousZoom) <= 0.0001)
        {
            return;
        }

        _workspaceState.CanvasZoom = _canvasZoom;
        ApplyCanvasZoom();
        WorkspaceScrollViewer.UpdateLayout();

        WorkspaceScrollViewer.ScrollToHorizontalOffset(
            (_codeCtrlShiftZoomAnchorCanvasPoint.X * _canvasZoom) -
            _codeCtrlShiftZoomAnchorViewportPoint.X);
        WorkspaceScrollViewer.ScrollToVerticalOffset(
            (_codeCtrlShiftZoomAnchorCanvasPoint.Y * _canvasZoom) -
            _codeCtrlShiftZoomAnchorViewportPoint.Y);
        CaptureViewportState();

        StatusText = $"Canvas zoom: {_canvasZoom:P0}";
    }

    private void StopCodeShiftPan()
    {
        _isShiftPanningCodeCanvas = false;
        if (_codeShiftPanTimer.IsEnabled)
        {
            _codeShiftPanTimer.Stop();
        }

        if (CodeViewHost != null)
        {
            CodeViewHost.Cursor = null;
        }
    }

    private void StopCodeCtrlShiftZoom()
    {
        _isCtrlShiftZoomingCodeCanvas = false;
        if (_codeCtrlShiftZoomTimer.IsEnabled)
        {
            _codeCtrlShiftZoomTimer.Stop();
        }

        if (CodeViewHost != null)
        {
            CodeViewHost.Cursor = null;
        }
    }

    private bool TryHandleControlCodeCanvasPan(MouseEventArgs e)
    {
        if (_appSettings.KeyboardShortcuts.EnableCanvasCtrlMousePanning != true ||
            _codeViewMode != CodeViewMode.Canvas ||
            WorkspaceScrollViewer == null ||
            WorkspaceScrollViewer.Visibility != Visibility.Visible ||
            IsMouseEventOverActiveCodeWindow(e))
        {
            _isControlPanningCodeCanvas = false;
            return false;
        }

        if (TryHandleControlPan(
                e,
                WorkspaceScrollViewer,
                ref _isControlPanningCodeCanvas,
                ref _controlCodeCanvasPanPoint))
        {
            SetActiveWorkspaceView(WorkspaceViewKind.Code);
            CaptureViewportState();
            return true;
        }

        return false;
    }

    private bool TryHandleControlDiagramCanvasPan(MouseEventArgs e)
    {
        if (_appSettings.KeyboardShortcuts.EnableCanvasCtrlMousePanning != true ||
            DiagramScrollViewer == null ||
            DiagramScrollViewer.Visibility != Visibility.Visible)
        {
            _isControlPanningDiagramCanvas = false;
            return false;
        }

        if (TryHandleControlPan(
                e,
                DiagramScrollViewer,
                ref _isControlPanningDiagramCanvas,
                ref _controlDiagramCanvasPanPoint))
        {
            SetActiveWorkspaceView(WorkspaceViewKind.Diagram);
            return true;
        }

        return false;
    }

    private bool TryHandleControlPan(
        MouseEventArgs e,
        ScrollViewer scrollViewer,
        ref bool isPanning,
        ref Point previousPoint)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != ModifierKeys.Control ||
            (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ||
            !IsMouseEventInsideElement(e, scrollViewer))
        {
            isPanning = false;
            return false;
        }

        Point currentPoint = e.GetPosition(scrollViewer);
        if (!isPanning)
        {
            previousPoint = currentPoint;
            isPanning = true;
            return true;
        }

        Vector delta = currentPoint - previousPoint;
        if (Math.Abs(delta.X) > 0.01 || Math.Abs(delta.Y) > 0.01)
        {
            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - delta.X);
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - delta.Y);
            previousPoint = currentPoint;
        }

        return true;
    }

    private static bool IsMouseEventInsideElement(MouseEventArgs e, FrameworkElement element)
    {
        if (!element.IsVisible ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            return false;
        }

        if (e.OriginalSource is not DependencyObject originalSource ||
            !IsDescendantOf(originalSource, element))
        {
            return false;
        }

        Point position = e.GetPosition(element);
        return position.X >= 0 &&
               position.Y >= 0 &&
               position.X <= element.ActualWidth &&
               position.Y <= element.ActualHeight;
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? current = child; current != null; current = GetDependencyParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetDependencyParent(DependencyObject dependencyObject)
    {
        try
        {
            DependencyObject? visualParent = VisualTreeHelper.GetParent(dependencyObject);
            if (visualParent != null)
            {
                return visualParent;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return LogicalTreeHelper.GetParent(dependencyObject);
    }

    private void OpenDiagramSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        SetDiagramSidebarOpen(true);
    }

    private void CollapseDiagramSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        SetDiagramSidebarOpen(false);
    }

    private void SetDiagramSidebarOpen(bool isOpen)
    {
        if (!isOpen && DiagramSidebarColumn != null && DiagramSidebarColumn.ActualWidth > 0)
        {
            _diagramSidebarWidth = Math.Max(DiagramSidebarMinimumWidth, DiagramSidebarColumn.ActualWidth);
        }

        _isDiagramSidebarOpen = isOpen;
        ApplyDiagramSidebarLayout();
    }

    private void ApplyDiagramSidebarLayout()
    {
        if (DiagramSidebarColumn == null ||
            DiagramSidebarSplitterColumn == null ||
            DiagramSidebarHost == null ||
            DiagramSidebarSplitter == null ||
            OpenDiagramSidebarButton == null)
        {
            return;
        }

        if (_isDiagramSidebarOpen)
        {
            double sidebarWidth = Math.Max(DiagramSidebarMinimumWidth, _diagramSidebarWidth);
            DiagramSidebarColumn.MinWidth = DiagramSidebarMinimumWidth;
            DiagramSidebarColumn.Width = new GridLength(sidebarWidth);
            DiagramSidebarSplitterColumn.Width = new GridLength(5);
            DiagramSidebarHost.Visibility = Visibility.Visible;
            DiagramSidebarSplitter.Visibility = Visibility.Visible;
            OpenDiagramSidebarButton.Visibility = Visibility.Collapsed;
            return;
        }

        DiagramSidebarColumn.MinWidth = 0;
        DiagramSidebarColumn.Width = new GridLength(0);
        DiagramSidebarSplitterColumn.Width = new GridLength(0);
        DiagramSidebarHost.Visibility = Visibility.Collapsed;
        DiagramSidebarSplitter.Visibility = Visibility.Collapsed;
        OpenDiagramSidebarButton.Visibility = Visibility.Visible;
    }

    private void InitializeMetadataEditorControls()
    {
        List<FontFamily> fontFamilies = Fonts.SystemFontFamilies
            .OrderBy(fontFamily => fontFamily.Source)
            .ToList();
        MetadataFontFamilyComboBox.ItemsSource = fontFamilies;
        MetadataFontFamilyComboBox.SelectedItem = fontFamilies.FirstOrDefault(fontFamily =>
            string.Equals(fontFamily.Source, "Segoe UI", StringComparison.OrdinalIgnoreCase)) ?? fontFamilies.FirstOrDefault();
        LoadMetadataEditorForSelection();
    }

    private void LoadMetadataEditorForSelection()
    {
        _isUpdatingMetadataEditor = true;
        try
        {
            _metadataEditorTarget = _selectedDiagramObject;
            _metadataEditorQueries.Clear();
            MetadataQueriesPanel.Children.Clear();

            if (_metadataEditorTarget == null)
            {
                ShowMetadataPlaceholder("Select a diagram object to edit metadata.");
                return;
            }

            if (_metadataEditorTarget is DiagramWorkflowMarkerControl)
            {
                ShowMetadataPlaceholder("Workflow markers are edited from the Workflows tab.");
                return;
            }

            DiagramObjectMetadata? metadata = GetDiagramObjectMetadata(_metadataEditorTarget);
            if (metadata == null)
            {
                ShowMetadataPlaceholder("This diagram object does not support metadata.");
                return;
            }

            MetadataEditorPlaceholder.Visibility = Visibility.Collapsed;
            MetadataEditorPanel.Visibility = Visibility.Visible;
            MetadataLinkTextBox.Text = metadata.Link;
            LoadMetadataDocumentation(metadata.DocumentationXaml);
            _metadataEditorQueries.AddRange(metadata.Queries.Select(query => query.Clone()));
            RebuildMetadataQueriesEditor();
        }
        finally
        {
            _isUpdatingMetadataEditor = false;
        }

        UpdateMetadataToolbarFromSelection();
    }

    private void ShowMetadataPlaceholder(string message)
    {
        MetadataEditorPanel.Visibility = Visibility.Collapsed;
        MetadataEditorPlaceholder.Visibility = Visibility.Visible;
        MetadataEditorPlaceholderText.Text = message;
        MetadataLinkTextBox.Text = string.Empty;
        MetadataDocumentationRichTextBox.Document = new FlowDocument();
        MetadataQueriesPanel.Children.Clear();
    }

    private void CommitMetadataEditorChanges()
    {
        if (_isUpdatingMetadataEditor ||
            _metadataEditorTarget == null ||
            _metadataEditorTarget is DiagramWorkflowMarkerControl ||
            MetadataEditorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var metadata = new DiagramObjectMetadata
        {
            Link = MetadataLinkTextBox.Text.Trim(),
            DocumentationXaml = SerializeMetadataDocumentation(),
            Queries = _metadataEditorQueries
                .Select(query => query.Clone())
                .ToList()
        };

        ApplyDiagramObjectMetadata(_metadataEditorTarget, metadata);
        UpdateMetadataEditorTargetQueryIndicator();
    }

    private static DiagramObjectMetadata? GetDiagramObjectMetadata(FrameworkElement diagramObject)
    {
        return diagramObject switch
        {
            DiagramShapeControl shape => shape.Metadata.Clone(),
            DiagramImageControl image => image.Metadata.Clone(),
            DiagramLineControl line => line.Metadata.Clone(),
            DiagramLabelControl label => label.Metadata.Clone(),
            _ => null
        };
    }

    private static void ApplyDiagramObjectMetadata(FrameworkElement diagramObject, DiagramObjectMetadata metadata)
    {
        switch (diagramObject)
        {
            case DiagramShapeControl shape:
                shape.ApplyMetadata(metadata);
                break;

            case DiagramImageControl image:
                image.ApplyMetadata(metadata);
                break;

            case DiagramLineControl line:
                line.ApplyMetadata(metadata);
                break;

            case DiagramLabelControl label:
                label.ApplyMetadata(metadata);
                break;
        }
    }

    private void UpdateMetadataEditorTargetQueryIndicator()
    {
        if (_metadataEditorTarget == null || _metadataEditorTarget is DiagramWorkflowMarkerControl)
        {
            return;
        }

        SetDiagramObjectMetadataQueryIndicator(
            _metadataEditorTarget,
            DiagramQueryState.HasUnresolvedQueries(_metadataEditorQueries));
    }

    private static void SetDiagramObjectMetadataQueryIndicator(FrameworkElement diagramObject, bool hasUnresolvedQueries)
    {
        switch (diagramObject)
        {
            case DiagramShapeControl shape:
                shape.SetHasUnresolvedQueries(hasUnresolvedQueries);
                break;

            case DiagramImageControl image:
                image.SetHasUnresolvedQueries(hasUnresolvedQueries);
                break;

            case DiagramLineControl line:
                line.SetHasUnresolvedQueries(hasUnresolvedQueries);
                break;

            case DiagramLabelControl label:
                label.SetHasUnresolvedQueries(hasUnresolvedQueries);
                break;
        }
    }

    private void LoadMetadataDocumentation(string documentationXaml)
    {
        MetadataDocumentationRichTextBox.Document = new FlowDocument();

        if (string.IsNullOrWhiteSpace(documentationXaml))
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(documentationXaml));
            var range = new TextRange(
                MetadataDocumentationRichTextBox.Document.ContentStart,
                MetadataDocumentationRichTextBox.Document.ContentEnd);
            range.Load(stream, DataFormats.Xaml);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Windows.Markup.XamlParseException)
        {
            MetadataDocumentationRichTextBox.Document.Blocks.Clear();
            MetadataDocumentationRichTextBox.Document.Blocks.Add(new Paragraph(new Run(documentationXaml)));
        }
    }

    private string SerializeMetadataDocumentation()
    {
        var range = new TextRange(
            MetadataDocumentationRichTextBox.Document.ContentStart,
            MetadataDocumentationRichTextBox.Document.ContentEnd);

        if (string.IsNullOrWhiteSpace(range.Text))
        {
            return string.Empty;
        }

        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.Xaml);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void AddMetadataQueryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_metadataEditorTarget == null || _metadataEditorTarget is DiagramWorkflowMarkerControl)
        {
            return;
        }

        int nextQueryNumber = _metadataEditorQueries.Count == 0
            ? 1
            : _metadataEditorQueries.Max(query => query.QueryNumber) + 1;
        _metadataEditorQueries.Add(new QueryItem
        {
            QueryNumber = nextQueryNumber,
            CreatedDateUtc = DateTimeOffset.UtcNow,
            Status = QueryStatus.Active
        });

        RebuildMetadataQueriesEditor();
        UpdateMetadataEditorTargetQueryIndicator();
        StatusText = $"Added metadata query {nextQueryNumber}.";
    }

    private void RebuildMetadataQueriesEditor()
    {
        MetadataQueriesPanel.Children.Clear();

        if (_metadataEditorQueries.Count == 0)
        {
            MetadataQueriesPanel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = CreateFrozenBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                Text = "No queries yet.",
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (QueryItem query in _metadataEditorQueries.OrderBy(query => query.QueryNumber))
        {
            MetadataQueriesPanel.Children.Add(CreateMetadataQueryBlock(query));
        }
    }

    private UIElement CreateMetadataQueryBlock(QueryItem query)
    {
        Border? queryBlock = null;
        var header = new DockPanel
        {
            LastChildFill = true
        };

        var statusComboBox = new ComboBox
        {
            Width = 104,
            Height = 24,
            ItemsSource = Enum.GetValues<QueryStatus>(),
            SelectedItem = query.Status
        };
        statusComboBox.SelectionChanged += (_, _) =>
        {
            if (!_isUpdatingMetadataEditor && statusComboBox.SelectedItem is QueryStatus status)
            {
                query.Status = status;
                if (queryBlock != null)
                {
                    queryBlock.Background = GetQueryBlockBackgroundBrush(status);
                }

                UpdateMetadataEditorTargetQueryIndicator();
            }
        };
        DockPanel.SetDock(statusComboBox, Dock.Right);
        header.Children.Add(statusComboBox);

        var numberTextBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            Text = $"Query {query.QueryNumber}"
        };
        header.Children.Add(numberTextBlock);

        var descriptionTextBox = new TextBox
        {
            MinHeight = 58,
            Margin = new Thickness(0, 8, 0, 6),
            AcceptsReturn = true,
            Text = query.QueryDescription,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        descriptionTextBox.TextChanged += (_, _) =>
        {
            if (!_isUpdatingMetadataEditor)
            {
                query.QueryDescription = descriptionTextBox.Text;
            }
        };

        var createdTextBlock = new TextBlock
        {
            Foreground = CreateFrozenBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            FontSize = 11,
            Text = $"Created {query.CreatedDateUtc.LocalDateTime:g}"
        };

        var layout = new StackPanel();
        layout.Children.Add(header);
        layout.Children.Add(descriptionTextBox);
        layout.Children.Add(createdTextBlock);

        queryBlock = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8),
            BorderBrush = CreateFrozenBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderThickness = new Thickness(1),
            Background = GetQueryBlockBackgroundBrush(query.Status),
            Child = layout
        };

        return queryBlock;
    }

    private static Brush GetQueryBlockBackgroundBrush(QueryStatus status)
    {
        return status switch
        {
            QueryStatus.Resolved => CreateFrozenBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)),
            QueryStatus.Irrelevant => CreateFrozenBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            _ => Brushes.White
        };
    }

    private void MetadataBoldButton_Click(object sender, RoutedEventArgs e)
    {
        ExecuteMetadataEditingCommand(EditingCommands.ToggleBold);
    }

    private void MetadataItalicButton_Click(object sender, RoutedEventArgs e)
    {
        ExecuteMetadataEditingCommand(EditingCommands.ToggleItalic);
    }

    private void MetadataUnderlineButton_Click(object sender, RoutedEventArgs e)
    {
        ExecuteMetadataEditingCommand(EditingCommands.ToggleUnderline);
    }

    private void ExecuteMetadataEditingCommand(RoutedUICommand command)
    {
        if (_isUpdatingMetadataToolbar || MetadataEditorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        MetadataDocumentationRichTextBox.Focus();
        command.Execute(null, MetadataDocumentationRichTextBox);
        UpdateMetadataToolbarFromSelection();
    }

    private void MetadataStrikethroughButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMetadataToolbar || MetadataEditorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        SetMetadataSelectionDecoration(
            TextDecorationLocation.Strikethrough,
            MetadataStrikethroughButton.IsChecked == true);
    }

    private void MetadataFontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMetadataToolbar ||
            _isUpdatingMetadataEditor ||
            MetadataEditorPanel.Visibility != Visibility.Visible ||
            MetadataFontFamilyComboBox.SelectedItem is not FontFamily fontFamily)
        {
            return;
        }

        ApplyMetadataSelectionProperty(TextElement.FontFamilyProperty, fontFamily);
    }

    private void MetadataFontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMetadataToolbar ||
            _isUpdatingMetadataEditor ||
            MetadataEditorPanel.Visibility != Visibility.Visible ||
            MetadataFontSizeComboBox.SelectedItem is not ComboBoxItem selectedItem ||
            selectedItem.Content is not string fontSizeText ||
            !double.TryParse(fontSizeText, NumberStyles.Number, CultureInfo.InvariantCulture, out double fontSize))
        {
            return;
        }

        ApplyMetadataSelectionProperty(TextElement.FontSizeProperty, fontSize);
    }

    private void MetadataHeaderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMetadataToolbar ||
            _isUpdatingMetadataEditor ||
            MetadataEditorPanel.Visibility != Visibility.Visible ||
            MetadataHeaderComboBox.SelectedItem is not ComboBoxItem selectedItem)
        {
            return;
        }

        string headerText = selectedItem.Content?.ToString() ?? "Normal";
        double fontSize = headerText switch
        {
            "Header 1" => 24,
            "Header 2" => 20,
            "Header 3" => 16,
            _ => 12
        };
        FontWeight fontWeight = string.Equals(headerText, "Normal", StringComparison.Ordinal)
            ? FontWeights.Normal
            : FontWeights.Bold;

        ApplyMetadataSelectionProperty(TextElement.FontSizeProperty, fontSize);
        ApplyMetadataSelectionProperty(TextElement.FontWeightProperty, fontWeight);
    }

    private void MetadataDocumentationRichTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateMetadataToolbarFromSelection();
    }

    private void ApplyMetadataSelectionProperty(DependencyProperty property, object value)
    {
        MetadataDocumentationRichTextBox.Focus();
        MetadataDocumentationRichTextBox.Selection.ApplyPropertyValue(property, value);
        UpdateMetadataToolbarFromSelection();
    }

    private void SetMetadataSelectionDecoration(TextDecorationLocation location, bool isEnabled)
    {
        TextDecorationCollection decorations = GetMetadataSelectionDecorations();

        foreach (TextDecoration decoration in decorations.Where(decoration => decoration.Location == location).ToList())
        {
            decorations.Remove(decoration);
        }

        if (isEnabled)
        {
            TextDecorationCollection sourceDecorations = location == TextDecorationLocation.Strikethrough
                ? TextDecorations.Strikethrough
                : TextDecorations.Underline;
            foreach (TextDecoration decoration in sourceDecorations)
            {
                decorations.Add(decoration);
            }
        }

        ApplyMetadataSelectionProperty(Inline.TextDecorationsProperty, decorations);
    }

    private TextDecorationCollection GetMetadataSelectionDecorations()
    {
        object value = MetadataDocumentationRichTextBox.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        return value is TextDecorationCollection decorations
            ? new TextDecorationCollection(decorations)
            : [];
    }

    private void UpdateMetadataToolbarFromSelection()
    {
        if (_isUpdatingMetadataEditor || _isUpdatingMetadataToolbar)
        {
            return;
        }

        _isUpdatingMetadataToolbar = true;
        try
        {
            TextSelection selection = MetadataDocumentationRichTextBox.Selection;
            MetadataBoldButton.IsChecked = IsSelectionPropertyValue(selection, TextElement.FontWeightProperty, FontWeights.Bold);
            MetadataItalicButton.IsChecked = IsSelectionPropertyValue(selection, TextElement.FontStyleProperty, FontStyles.Italic);
            MetadataUnderlineButton.IsChecked = SelectionHasDecoration(selection, TextDecorationLocation.Underline);
            MetadataStrikethroughButton.IsChecked = SelectionHasDecoration(selection, TextDecorationLocation.Strikethrough);
            UpdateSelectedFontFamily(selection);
            UpdateSelectedFontSize(selection);
        }
        finally
        {
            _isUpdatingMetadataToolbar = false;
        }
    }

    private static bool IsSelectionPropertyValue(TextRange selection, DependencyProperty property, object expectedValue)
    {
        object value = selection.GetPropertyValue(property);
        return value != DependencyProperty.UnsetValue && Equals(value, expectedValue);
    }

    private static bool SelectionHasDecoration(TextRange selection, TextDecorationLocation location)
    {
        object value = selection.GetPropertyValue(Inline.TextDecorationsProperty);
        return value is TextDecorationCollection decorations &&
            decorations.Any(decoration => decoration.Location == location);
    }

    private void UpdateSelectedFontFamily(TextRange selection)
    {
        object value = selection.GetPropertyValue(TextElement.FontFamilyProperty);
        if (value is not FontFamily selectedFontFamily)
        {
            MetadataFontFamilyComboBox.SelectedIndex = -1;
            return;
        }

        foreach (object item in MetadataFontFamilyComboBox.Items)
        {
            if (item is FontFamily fontFamily &&
                string.Equals(fontFamily.Source, selectedFontFamily.Source, StringComparison.OrdinalIgnoreCase))
            {
                MetadataFontFamilyComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateSelectedFontSize(TextRange selection)
    {
        object value = selection.GetPropertyValue(TextElement.FontSizeProperty);
        if (value is not double selectedFontSize)
        {
            MetadataFontSizeComboBox.SelectedIndex = -1;
            return;
        }

        string selectedText = selectedFontSize.ToString("0", CultureInfo.InvariantCulture);
        foreach (object item in MetadataFontSizeComboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                string.Equals(comboBoxItem.Content?.ToString(), selectedText, StringComparison.Ordinal))
            {
                MetadataFontSizeComboBox.SelectedItem = comboBoxItem;
                return;
            }
        }
    }

    private async void MetadataOpenLinkButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenMetadataLinkAsync(MetadataLinkTextBox.Text.Trim());
    }

    private async Task<bool> OpenMetadataLinkAsync(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            StatusText = "Metadata link is empty.";
            return false;
        }

        try
        {
            link = link.Trim();
            if (Uri.TryCreate(link, UriKind.Absolute, out Uri? externalUri) &&
                (string.Equals(externalUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(externalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                Process.Start(new ProcessStartInfo(link)
                {
                    UseShellExecute = true
                });
                StatusText = $"Opened metadata link: {link}";
                return true;
            }

            MetadataLinkTarget target = ParseMetadataLinkTarget(link);
            string targetLink = target.Link;

            if (Uri.TryCreate(targetLink, UriKind.Absolute, out Uri? uri) &&
                (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                Process.Start(new ProcessStartInfo(targetLink)
                {
                    UseShellExecute = true
                });
                StatusText = $"Opened metadata link: {targetLink}";
                return true;
            }

            if (DiagramDocumentService.IsDiagramDocumentPath(targetLink))
            {
                await OpenDiagramAsync(targetLink);
                return true;
            }

            string filePath = uri?.IsFile == true ? uri.LocalPath : targetLink;
            if (TryFocusInternalContainerLink(filePath))
            {
                return true;
            }

            if (Directory.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo(filePath)
                {
                    UseShellExecute = true
                });
                StatusText = $"Opened metadata folder link: {filePath}";
                return true;
            }

            if (File.Exists(filePath) || DatabaseDocumentService.IsDatabaseDocumentPath(filePath))
            {
                EnsureCodeViewVisible();
                await OpenFileAsync(filePath, targetLineNumber: target.LineNumber);
                StatusText = target.LineNumber.HasValue
                    ? $"Opened metadata link: {filePath} line {target.LineNumber.Value}"
                    : $"Opened metadata link: {filePath}";
                return true;
            }

            StatusText = "Metadata link is not a recognised URL, file path, or Surf2 resource path.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"Could not open metadata link: {ex.Message}";
            return false;
        }
    }

    private static MetadataLinkTarget ParseMetadataLinkTarget(string link)
    {
        string trimmedLink = link.Trim();
        Match match = MetadataLinkLineSuffixPattern.Match(trimmedLink);
        if (!match.Success ||
            !int.TryParse(match.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int lineNumber))
        {
            return new MetadataLinkTarget(trimmedLink, null);
        }

        string targetLink = match.Groups["link"].Value.TrimEnd();
        return string.IsNullOrWhiteSpace(targetLink)
            ? new MetadataLinkTarget(trimmedLink, null)
            : new MetadataLinkTarget(targetLink, lineNumber);
    }

    private void MetadataPickResourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeScope == null)
        {
            StatusText = "Open a scope before picking an internal resource.";
            return;
        }

        IReadOnlyList<LinkableResource> linkableResources;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            linkableResources = _linkableResourceService.CreateLinkableResources(_activeScope, _databaseSnapshots, _diagramLibrary);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (linkableResources.Count == 0)
        {
            StatusText = "No linkable resources were found in the active scope.";
            return;
        }

        var dialog = new ResourcePickerWindow(linkableResources)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        MetadataLinkTextBox.Text = dialog.SelectedResourcePath;
        StatusText = $"Picked metadata resource '{dialog.SelectedResourceName}'.";
    }

    private bool TryFocusInternalContainerLink(string link)
    {
        if (_activeScope == null)
        {
            return false;
        }

        ScopedResource? databaseResource = _activeScope.Resources.FirstOrDefault(resource =>
            resource.Kind == ResourceKind.DatabaseSnapshot &&
            string.Equals(resource.Path, link, StringComparison.OrdinalIgnoreCase));
        if (databaseResource != null)
        {
            FocusDatabaseResourceInObjectExplorer(databaseResource);
            return true;
        }

        if (!Directory.Exists(link) || !IsDirectoryInActiveScope(link))
        {
            return false;
        }

        FocusFolderInObjectExplorer(link);
        return true;
    }

    private void FocusDatabaseResourceInObjectExplorer(ScopedResource databaseResource)
    {
        ObjectExplorerSearchTextBox.Clear();
        RootNodes.Clear();

        foreach (FileSystemNode node in _fileTreeService.CreateRoots([databaseResource], _databaseSnapshots, _diagramLibrary))
        {
            node.IsExpanded = true;
            CollapseChildren(node);
            RootNodes.Add(node);
        }

        StatusText = $"Focused database resource '{databaseResource.DisplayName}'.";
    }

    private void FocusFolderInObjectExplorer(string folderPath)
    {
        ObjectExplorerSearchTextBox.Clear();
        string displayName = GetFolderLinkDisplayName(folderPath);
        var node = new FileSystemNode(folderPath, isDirectory: true, displayName: displayName)
        {
            IsExpanded = true
        };

        _fileTreeService.LoadChildren(node);

        RootNodes.Clear();
        RootNodes.Add(node);
        StatusText = $"Focused folder resource '{displayName}'.";
    }

    private static void CollapseChildren(FileSystemNode node)
    {
        foreach (FileSystemNode child in node.Children)
        {
            child.IsExpanded = false;
        }
    }

    private string GetFolderLinkDisplayName(string folderPath)
    {
        ScopedResource? scopedResource = _activeScope?.Resources.FirstOrDefault(resource =>
            resource.Kind == ResourceKind.Folder &&
            string.Equals(NormalizePath(resource.Path), NormalizePath(folderPath), StringComparison.OrdinalIgnoreCase));
        if (scopedResource != null)
        {
            return scopedResource.DisplayName;
        }

        string name = Path.GetFileName(folderPath);
        return string.IsNullOrWhiteSpace(name) ? folderPath : name;
    }

    private bool IsDirectoryInActiveScope(string directoryPath)
    {
        if (_activeScope == null)
        {
            return false;
        }

        string normalizedDirectory = NormalizePath(directoryPath);
        return _activeScope.Resources.Any(resource =>
        {
            if (resource.Kind != ResourceKind.Folder || !Directory.Exists(resource.Path))
            {
                return false;
            }

            string normalizedScopePath = NormalizePath(resource.Path);
            return string.Equals(normalizedDirectory, normalizedScopePath, StringComparison.OrdinalIgnoreCase) ||
                normalizedDirectory.StartsWith(normalizedScopePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalizedDirectory.StartsWith(normalizedScopePath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private void NewWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        CommitWorkflowEditorChanges(requireValidWorkflowName: false);

        var workflow = new WorkflowDocument
        {
            WorkflowId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        ShowWorkflowDetails(workflow, isCreatingNewWorkflow: true, focusName: true);
        StatusText = "Creating new workflow.";
    }

    private void WorkflowBackButton_Click(object sender, RoutedEventArgs e)
    {
        CommitWorkflowEditorChanges(requireValidWorkflowName: false);
        ShowWorkflowList();
    }

    private void WorkflowAddItemsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("add workflow markers"))
        {
            SetWorkflowAddItemsMode(false);
            return;
        }

        bool requestedAddMode = WorkflowAddItemsToggleButton.IsChecked == true;
        if (requestedAddMode && !CommitWorkflowEditorChanges(requireValidWorkflowName: true))
        {
            SetWorkflowAddItemsMode(false);
            return;
        }

        SetWorkflowAddItemsMode(requestedAddMode);
        StatusText = _isAddingWorkflowItems
            ? "Workflow item placement is armed. Click the diagram canvas to add an item."
            : "Workflow item placement is off.";
    }

    private void SetWorkflowAddItemsMode(bool isAdding)
    {
        _isAddingWorkflowItems = isAdding;
        if (WorkflowAddItemsToggleButton != null)
        {
            WorkflowAddItemsToggleButton.IsChecked = isAdding;
            WorkflowAddItemsToggleButton.Content = isAdding ? "Add Items: ON" : "Add Items: OFF";
        }

        if (isAdding)
        {
            ClearDiagramDrawingTools();
        }
    }

    private void ClearDiagramDrawingTools()
    {
        if (DiagramRectangleToolButton == null ||
            DiagramEllipseToolButton == null ||
            DiagramImageToolButton == null ||
            DiagramLineToolButton == null ||
            DiagramLabelToolButton == null ||
            DiagramPortalToolButton == null)
        {
            return;
        }

        _isUpdatingDiagramToolToggles = true;
        DiagramRectangleToolButton.IsChecked = false;
        DiagramEllipseToolButton.IsChecked = false;
        DiagramImageToolButton.IsChecked = false;
        DiagramLineToolButton.IsChecked = false;
        DiagramLabelToolButton.IsChecked = false;
        DiagramPortalToolButton.IsChecked = false;
        _selectedDiagramImageId = null;
        _pendingPortalName = null;
        _pendingPortalPairPlacement = null;
        _isUpdatingDiagramToolToggles = false;
    }

    private void SetDiagramLockState(bool isLocked, bool updateToggle = true, bool showStatus = false)
    {
        _isDiagramLocked = isLocked;

        if (updateToggle && DiagramLockToggleButton != null)
        {
            _isUpdatingDiagramLockToggle = true;
            DiagramLockToggleButton.IsChecked = isLocked;
            _isUpdatingDiagramLockToggle = false;
        }

        if (isLocked)
        {
            ClearDiagramDrawingTools();
            SetWorkflowAddItemsMode(false);
            CommitDiagramObjectTextEdits();
        }

        UpdateDiagramLockUi();
        ApplyDiagramLockToObjects();

        if (showStatus)
        {
            StatusText = isLocked
                ? "Diagram editing locked."
                : "Diagram editing unlocked.";
        }
    }

    private void UpdateDiagramLockUi()
    {
        bool editingEnabled = !_isDiagramLocked;

        if (DiagramRectangleToolButton != null) DiagramRectangleToolButton.IsEnabled = editingEnabled;
        if (DiagramEllipseToolButton != null) DiagramEllipseToolButton.IsEnabled = editingEnabled;
        if (DiagramImageToolButton != null) DiagramImageToolButton.IsEnabled = editingEnabled;
        if (DiagramLineToolButton != null) DiagramLineToolButton.IsEnabled = editingEnabled;
        if (DiagramLabelToolButton != null) DiagramLabelToolButton.IsEnabled = editingEnabled;
        if (DiagramPortalToolButton != null) DiagramPortalToolButton.IsEnabled = editingEnabled;
        if (DiagramOutlineColorButton != null) DiagramOutlineColorButton.IsEnabled = editingEnabled;
        if (DiagramBackColorButton != null) DiagramBackColorButton.IsEnabled = editingEnabled;
        if (WorkflowAddItemsToggleButton != null) WorkflowAddItemsToggleButton.IsEnabled = editingEnabled;
    }

    private void ApplyDiagramLockToObjects()
    {
        if (DiagramCanvas == null)
        {
            return;
        }

        foreach (FrameworkElement diagramObject in DiagramCanvas.Children.OfType<FrameworkElement>())
        {
            ApplyDiagramLockToObject(diagramObject);
        }
    }

    private void ApplyDiagramLockToObject(FrameworkElement diagramObject)
    {
        switch (diagramObject)
        {
            case DiagramShapeControl shape:
                shape.IsLocked = _isDiagramLocked;
                break;

            case DiagramImageControl image:
                image.IsLocked = _isDiagramLocked;
                break;

            case DiagramLineControl line:
                line.IsLocked = _isDiagramLocked;
                break;

            case DiagramLabelControl label:
                label.IsLocked = _isDiagramLocked;
                break;

            case DiagramWorkflowMarkerControl marker:
                marker.IsLocked = _isDiagramLocked;
                break;

            case DiagramPortalControl portal:
                portal.IsLocked = _isDiagramLocked;
                break;
        }
    }

    private bool TryBlockDiagramObjectEditWhenLocked(string actionDescription = "edit diagram objects")
    {
        if (!_isDiagramLocked)
        {
            return false;
        }

        StatusText = $"Unlock the diagram to {actionDescription}.";
        return true;
    }

    private void ResetWorkflowEditor()
    {
        _workflowEditorTarget = null;
        _isCreatingNewWorkflow = false;
        SetWorkflowAddItemsMode(false);
        _expandedWorkflowItems.Clear();
        _workflowItemDocumentationEditors.Clear();
        _workflowItemContainers.Clear();
        _workflowItemDescriptionEditors.Clear();

        ShowWorkflowList();
    }

    private void ShowWorkflowList()
    {
        if (WorkflowListPanel == null || WorkflowDetailsPanel == null)
        {
            return;
        }

        _workflowEditorTarget = null;
        _isCreatingNewWorkflow = false;
        _workflowItemDocumentationEditors.Clear();
        _workflowItemContainers.Clear();
        _workflowItemDescriptionEditors.Clear();
        SetWorkflowAddItemsMode(false);
        WorkflowDetailsPanel.Visibility = Visibility.Collapsed;
        WorkflowListPanel.Visibility = Visibility.Visible;
        RefreshWorkflowList();
    }

    private void RefreshWorkflowList()
    {
        if (WorkflowListItemsPanel == null || WorkflowListPlaceholder == null)
        {
            return;
        }

        WorkflowListItemsPanel.Children.Clear();
        List<WorkflowDocument> workflows = _currentDiagramWorkflows
            .OrderBy(workflow => workflow.WorkflowName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        WorkflowListPlaceholder.Visibility = workflows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (WorkflowDocument workflow in workflows)
        {
            WorkflowListItemsPanel.Children.Add(CreateWorkflowListItem(workflow));
        }
    }

    private UIElement CreateWorkflowListItem(WorkflowDocument workflow)
    {
        bool hasUnresolvedQueries = DiagramQueryState.HasUnresolvedWorkflowQueries(workflow);
        var markerVisibilityCheckBox = new CheckBox
        {
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = workflow.AreMarkersVisible,
            ToolTip = "Show workflow markers on the diagram"
        };
        markerVisibilityCheckBox.Checked += (_, _) =>
        {
            workflow.AreMarkersVisible = true;
            workflow.UpdatedAtUtc = DateTimeOffset.UtcNow;
            ApplyWorkflowMarkerVisibility(workflow.WorkflowId);
            StatusText = $"Showing markers for workflow '{workflow.WorkflowName}'.";
        };
        markerVisibilityCheckBox.Unchecked += (_, _) =>
        {
            workflow.AreMarkersVisible = false;
            workflow.UpdatedAtUtc = DateTimeOffset.UtcNow;
            ApplyWorkflowMarkerVisibility(workflow.WorkflowId);
            StatusText = $"Hid markers for workflow '{workflow.WorkflowName}'.";
        };

        var nameTextBlock = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            Text = workflow.WorkflowName,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var nameLayout = new DockPanel
        {
            LastChildFill = true
        };
        DockPanel.SetDock(markerVisibilityCheckBox, Dock.Left);
        nameLayout.Children.Add(markerVisibilityCheckBox);

        if (hasUnresolvedQueries)
        {
            var warningTextBlock = new TextBlock
            {
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Firebrick,
                Text = "!"
            };
            DockPanel.SetDock(warningTextBlock, Dock.Right);
            nameLayout.Children.Add(warningTextBlock);
        }

        nameLayout.Children.Add(nameTextBlock);

        var detailsTextBlock = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = CreateFrozenBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            FontSize = 11,
            Text = $"{workflow.Items.Count} item(s)"
        };

        var layout = new StackPanel();
        layout.Children.Add(nameLayout);
        layout.Children.Add(detailsTextBlock);

        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(9),
            BorderBrush = CreateFrozenBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderThickness = new Thickness(1),
            Background = CreateFrozenBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            Cursor = Cursors.Hand,
            Child = layout
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source &&
                IsDescendantOf(source, markerVisibilityCheckBox))
            {
                return;
            }

            CommitWorkflowEditorChanges(requireValidWorkflowName: false);
            ShowWorkflowDetails(workflow, isCreatingNewWorkflow: false, focusName: false);
        };

        return border;
    }

    private void ApplyWorkflowMarkerVisibility(string workflowId)
    {
        WorkflowDocument? workflow = FindWorkflow(workflowId);
        bool isVisible = workflow?.AreMarkersVisible == true;

        foreach (DiagramWorkflowMarkerControl marker in DiagramCanvas.Children.OfType<DiagramWorkflowMarkerControl>()
                     .Where(marker => string.Equals(marker.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase)))
        {
            marker.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!isVisible &&
            _selectedDiagramObject is DiagramWorkflowMarkerControl selectedMarker &&
            string.Equals(selectedMarker.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
        {
            SelectDiagramObject(null);
        }
    }

    private void ShowWorkflowDetails(WorkflowDocument workflow, bool isCreatingNewWorkflow, bool focusName)
    {
        _isUpdatingWorkflowEditor = true;
        try
        {
            _workflowEditorTarget = workflow;
            _isCreatingNewWorkflow = isCreatingNewWorkflow;
            WorkflowListPanel.Visibility = Visibility.Collapsed;
            WorkflowDetailsPanel.Visibility = Visibility.Visible;
            WorkflowNameTextBox.Text = workflow.WorkflowName;
            WorkflowAddItemsToggleButton.IsChecked = _isAddingWorkflowItems;
            WorkflowAddItemsToggleButton.Content = _isAddingWorkflowItems ? "Add Items: ON" : "Add Items: OFF";
            RebuildWorkflowItemsEditor();
        }
        finally
        {
            _isUpdatingWorkflowEditor = false;
        }

        if (focusName)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                WorkflowNameTextBox.Focus();
                Keyboard.Focus(WorkflowNameTextBox);
                WorkflowNameTextBox.SelectAll();
            }), DispatcherPriority.ContextIdle);
        }
    }

    private bool CommitWorkflowEditorChanges(bool requireValidWorkflowName)
    {
        if (_isUpdatingWorkflowEditor || _workflowEditorTarget == null || WorkflowDetailsPanel.Visibility != Visibility.Visible)
        {
            return true;
        }

        CommitWorkflowItemDocumentationEditors();

        string workflowName = WorkflowNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(workflowName))
        {
            if (requireValidWorkflowName)
            {
                StatusText = "Workflow name is required.";
                WorkflowNameTextBox.Focus();
                return false;
            }

            return true;
        }

        if (IsWorkflowNameDuplicate(workflowName, _workflowEditorTarget.WorkflowId))
        {
            if (requireValidWorkflowName)
            {
                StatusText = $"A workflow named '{workflowName}' already exists.";
                WorkflowNameTextBox.Focus();
                WorkflowNameTextBox.SelectAll();
                return false;
            }

            return true;
        }

        _workflowEditorTarget.WorkflowName = workflowName;
        _workflowEditorTarget.UpdatedAtUtc = DateTimeOffset.UtcNow;

        if (_isCreatingNewWorkflow && !_currentDiagramWorkflows.Any(workflow =>
                string.Equals(workflow.WorkflowId, _workflowEditorTarget.WorkflowId, StringComparison.OrdinalIgnoreCase)))
        {
            _currentDiagramWorkflows.Add(_workflowEditorTarget);
            _isCreatingNewWorkflow = false;
        }

        RefreshWorkflowList();
        return true;
    }

    private bool IsWorkflowNameDuplicate(string workflowName, string workflowId)
    {
        return _currentDiagramWorkflows.Any(workflow =>
            !string.Equals(workflow.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(workflow.WorkflowName, workflowName, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildWorkflowItemsEditor()
    {
        _workflowItemDocumentationEditors.Clear();
        _workflowItemContainers.Clear();
        _workflowItemDescriptionEditors.Clear();
        WorkflowItemsPanel.Children.Clear();

        if (_workflowEditorTarget == null)
        {
            WorkflowItemsPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        List<WorkflowItem> items = _workflowEditorTarget.Items
            .OrderBy(item => item.ItemNumber)
            .ToList();
        WorkflowItemsPlaceholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (WorkflowItem item in items)
        {
            FrameworkElement block = CreateWorkflowItemBlock(item);
            _workflowItemContainers[item.WorkflowItemId] = block;
            WorkflowItemsPanel.Children.Add(block);
        }
    }

    private FrameworkElement CreateWorkflowItemBlock(WorkflowItem item)
    {
        bool isExpanded = _expandedWorkflowItems.TryGetValue(item.WorkflowItemId, out bool expanded) && expanded;
        var outerLayout = new StackPanel();

        var summary = new Grid();
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Border itemNumberBadge = CreateWorkflowBadge(
            item.ItemNumber.ToString(CultureInfo.InvariantCulture),
            CreateFrozenBrush(Color.FromRgb(0xDB, 0xE7, 0xFF)),
            CreateFrozenBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)));
        summary.Children.Add(itemNumberBadge);

        var descriptionTextBox = new TextBox
        {
            Height = 24,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = item.ItemDescription
        };
        descriptionTextBox.TextChanged += (_, _) =>
        {
            if (_isUpdatingWorkflowEditor)
            {
                return;
            }

            item.ItemDescription = descriptionTextBox.Text;
            UpdateWorkflowMarkerDetails(_workflowEditorTarget?.WorkflowId ?? string.Empty, item);
        };
        _workflowItemDescriptionEditors[item.WorkflowItemId] = descriptionTextBox;
        Grid.SetColumn(descriptionTextBox, 1);
        summary.Children.Add(descriptionTextBox);

        var moreButton = new Button
        {
            Width = 52,
            Height = 24,
            Margin = new Thickness(6, 0, 0, 0),
            Content = isExpanded ? "Less" : "More"
        };
        ApplyWorkflowSidebarButtonStyle(moreButton, CreateFrozenBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
        moreButton.Click += (_, _) =>
        {
            _expandedWorkflowItems[item.WorkflowItemId] = !isExpanded;
            RebuildWorkflowItemsEditor();
            FocusWorkflowItem(item.WorkflowItemId, focusDescription: false);
        };
        Grid.SetColumn(moreButton, 2);
        summary.Children.Add(moreButton);

        var deleteButton = new Button
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(4, 0, 0, 0),
            Content = "X",
            ToolTip = "Delete workflow item"
        };
        ApplyWorkflowSidebarButtonStyle(deleteButton, CreateFrozenBrush(Color.FromRgb(0xDC, 0x26, 0x26)));
        deleteButton.Click += (_, _) =>
        {
            if (_workflowEditorTarget == null)
            {
                return;
            }

            RemoveWorkflowItemAndMarker(_workflowEditorTarget.WorkflowId, item.WorkflowItemId);
            RebuildWorkflowItemsEditor();
            RefreshWorkflowList();
        };
        Grid.SetColumn(deleteButton, 3);
        summary.Children.Add(deleteButton);

        outerLayout.Children.Add(summary);
        if (isExpanded)
        {
            outerLayout.Children.Add(CreateWorkflowItemDetails(item));
        }

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8),
            BorderBrush = CreateFrozenBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderThickness = new Thickness(1),
            Background = isExpanded
                ? CreateFrozenBrush(Color.FromRgb(0xEF, 0xF6, 0xFF))
                : CreateFrozenBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            Child = outerLayout
        };
    }

    private void ApplyWorkflowSidebarButtonStyle(Button button, Brush background)
    {
        button.Background = background;
        button.Foreground = Brushes.White;
        button.BorderThickness = new Thickness(0);
        button.FontWeight = FontWeights.SemiBold;
        button.Cursor = Cursors.Hand;

        if (TryFindResource("RoundedToolbarButtonStyle") is Style roundedButtonStyle)
        {
            button.Style = roundedButtonStyle;
        }
    }

    private static Border CreateWorkflowBadge(string text, Brush background, Brush foreground, double minWidth = 28)
    {
        return new Border
        {
            MinWidth = minWidth,
            Height = 22,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = background,
            CornerRadius = new CornerRadius(11),
            Child = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = foreground,
                Text = text
            }
        };
    }

    private UIElement CreateWorkflowItemDetails(WorkflowItem item)
    {
        var layout = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0)
        };

        layout.Children.Add(new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            Text = "Documentation:"
        });

        var documentationBox = new RichTextBox
        {
            MinHeight = 96,
            Margin = new Thickness(0, 4, 0, 8),
            AcceptsTab = true,
            BorderBrush = CreateFrozenBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderThickness = new Thickness(1),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        LoadRichTextBoxDocument(documentationBox, item.ItemDocumentationXaml);
        documentationBox.LostKeyboardFocus += (_, _) =>
            item.ItemDocumentationXaml = SerializeRichTextBoxDocument(documentationBox);
        _workflowItemDocumentationEditors[item.WorkflowItemId] = documentationBox;
        layout.Children.Add(documentationBox);

        var queryHeader = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 6)
        };
        var addQueryButton = new Button
        {
            Width = 28,
            Height = 24,
            Content = "+"
        };
        ApplyWorkflowSidebarButtonStyle(addQueryButton, CreateFrozenBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
        addQueryButton.Click += (_, _) =>
        {
            int nextQueryNumber = item.Queries.Count == 0 ? 1 : item.Queries.Max(query => query.QueryNumber) + 1;
            item.Queries.Add(new QueryItem
            {
                QueryNumber = nextQueryNumber,
                CreatedDateUtc = DateTimeOffset.UtcNow,
                Status = QueryStatus.Active
            });
            _expandedWorkflowItems[item.WorkflowItemId] = true;
            RebuildWorkflowItemsEditor();
            UpdateWorkflowItemQueryIndicators(item);
            FocusWorkflowItem(item.WorkflowItemId, focusDescription: false);
        };
        DockPanel.SetDock(addQueryButton, Dock.Right);
        queryHeader.Children.Add(addQueryButton);
        queryHeader.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            Text = "Queries:"
        });
        layout.Children.Add(queryHeader);

        if (item.Queries.Count == 0)
        {
            layout.Children.Add(new TextBlock
            {
                Foreground = CreateFrozenBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                Text = "No queries yet."
            });
        }
        else
        {
            foreach (QueryItem query in item.Queries.OrderBy(query => query.QueryNumber))
            {
                layout.Children.Add(CreateWorkflowQueryBlock(item, query));
            }
        }

        var collapseButton = new Button
        {
            Height = 24,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "Collapse"
        };
        ApplyWorkflowSidebarButtonStyle(collapseButton, CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27)));
        collapseButton.Click += (_, _) =>
        {
            item.ItemDocumentationXaml = SerializeRichTextBoxDocument(documentationBox);
            _expandedWorkflowItems[item.WorkflowItemId] = false;
            RebuildWorkflowItemsEditor();
            FocusWorkflowItem(item.WorkflowItemId, focusDescription: false);
        };
        layout.Children.Add(collapseButton);

        return layout;
    }

    private UIElement CreateWorkflowQueryBlock(WorkflowItem item, QueryItem query)
    {
        Border? queryBlock = null;
        var header = new DockPanel
        {
            LastChildFill = true
        };

        var statusComboBox = new ComboBox
        {
            Width = 104,
            Height = 24,
            ItemsSource = Enum.GetValues<QueryStatus>(),
            SelectedItem = query.Status
        };
        statusComboBox.SelectionChanged += (_, _) =>
        {
            if (statusComboBox.SelectedItem is QueryStatus status)
            {
                query.Status = status;
                if (queryBlock != null)
                {
                    queryBlock.Background = GetQueryBlockBackgroundBrush(status);
                }

                UpdateWorkflowItemQueryIndicators(item);
            }
        };
        DockPanel.SetDock(statusComboBox, Dock.Right);
        header.Children.Add(statusComboBox);

        header.Children.Add(CreateWorkflowBadge(
            $"Query {query.QueryNumber}",
            CreateFrozenBrush(Color.FromRgb(0xEE, 0xF2, 0xFF)),
            CreateFrozenBrush(Color.FromRgb(0x37, 0x30, 0xA3)),
            minWidth: 62));

        var descriptionTextBox = new TextBox
        {
            MinHeight = 48,
            Margin = new Thickness(0, 6, 0, 4),
            AcceptsReturn = true,
            Text = query.QueryDescription,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        descriptionTextBox.TextChanged += (_, _) => query.QueryDescription = descriptionTextBox.Text;

        var createdTextBlock = new TextBlock
        {
            Foreground = CreateFrozenBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            FontSize = 11,
            Text = $"Created {query.CreatedDateUtc.LocalDateTime:g}"
        };

        var layout = new StackPanel();
        layout.Children.Add(header);
        layout.Children.Add(descriptionTextBox);
        layout.Children.Add(createdTextBlock);

        queryBlock = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8),
            BorderBrush = CreateFrozenBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderThickness = new Thickness(1),
            Background = GetQueryBlockBackgroundBrush(query.Status),
            Child = layout
        };

        return queryBlock;
    }

    private void CommitWorkflowItemDocumentationEditors()
    {
        if (_workflowEditorTarget == null)
        {
            return;
        }

        foreach (WorkflowItem item in _workflowEditorTarget.Items)
        {
            if (_workflowItemDocumentationEditors.TryGetValue(item.WorkflowItemId, out RichTextBox? documentationBox))
            {
                item.ItemDocumentationXaml = SerializeRichTextBoxDocument(documentationBox);
            }
        }
    }

    private static void LoadRichTextBoxDocument(RichTextBox richTextBox, string documentXaml)
    {
        richTextBox.Document = new FlowDocument();
        if (string.IsNullOrWhiteSpace(documentXaml))
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(documentXaml));
            var range = new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd);
            range.Load(stream, DataFormats.Xaml);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Windows.Markup.XamlParseException)
        {
            richTextBox.Document.Blocks.Clear();
            richTextBox.Document.Blocks.Add(new Paragraph(new Run(documentXaml)));
        }
    }

    private static string SerializeRichTextBoxDocument(RichTextBox richTextBox)
    {
        var range = new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd);
        if (string.IsNullOrWhiteSpace(range.Text))
        {
            return string.Empty;
        }

        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.Xaml);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void FocusWorkflowItem(string workflowItemId, bool focusDescription)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_workflowItemContainers.TryGetValue(workflowItemId, out FrameworkElement? container))
            {
                container.BringIntoView();
            }

            if (focusDescription && _workflowItemDescriptionEditors.TryGetValue(workflowItemId, out TextBox? descriptionTextBox))
            {
                descriptionTextBox.Focus();
                Keyboard.Focus(descriptionTextBox);
                descriptionTextBox.SelectAll();
            }
        }), DispatcherPriority.ContextIdle);
    }

    private void OpenWorkflowItemInSidebar(string workflowId, string workflowItemId)
    {
        WorkflowDocument? workflow = FindWorkflow(workflowId);
        WorkflowItem? item = FindWorkflowItem(workflowId, workflowItemId);
        if (workflow == null || item == null)
        {
            StatusText = "Workflow item could not be found for this marker.";
            return;
        }

        SetDiagramSidebarOpen(true);
        DiagramSidebarTabs.SelectedIndex = 1;
        _expandedWorkflowItems[workflowItemId] = true;
        ShowWorkflowDetails(workflow, isCreatingNewWorkflow: false, focusName: false);
        FocusWorkflowItem(workflowItemId, focusDescription: false);
        StatusText = $"Workflow item {item.ItemNumber}: {item.ItemDescription}";
    }

    private void UpdateWorkflowMarkerDetails(string workflowId, WorkflowItem item)
    {
        if (FindWorkflowMarkerByWorkflowItem(workflowId, item.WorkflowItemId) is DiagramWorkflowMarkerControl marker)
        {
            marker.ApplyDetails(workflowId, item.WorkflowItemId, item.ItemNumber, item.ItemDescription);
            marker.SetHasUnresolvedQueries(DiagramQueryState.HasUnresolvedWorkflowQueries(item));
        }
    }

    private void UpdateWorkflowItemQueryIndicators(WorkflowItem item)
    {
        if (_workflowEditorTarget != null)
        {
            UpdateWorkflowMarkerDetails(_workflowEditorTarget.WorkflowId, item);
        }

        RefreshWorkflowList();
    }

    private void DiagramCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_isDiagramLocked && e.Data.GetDataPresent(ObjectExplorerDragDataFormat)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void DiagramCanvas_Drop(object sender, DragEventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("drop resources onto the diagram"))
        {
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(ObjectExplorerDragDataFormat) is not FileSystemNode node)
        {
            return;
        }

        e.Handled = true;
        PlaceObjectExplorerResourceOnDiagram(node, e.GetPosition(DiagramCanvas));
    }

    private void PlaceObjectExplorerResourceOnDiagram(FileSystemNode node, Point canvasPoint)
    {
        if (TryBlockDiagramObjectEditWhenLocked("add resources to the diagram"))
        {
            return;
        }

        DiagramImageDefinition? imageDefinition = FindDiagramImageMatchForNode(node);
        if (imageDefinition == null)
        {
            StatusText = $"No diagram image regex matched '{node.Name}'.";
            return;
        }

        ImageSource? imageSource = CreateImageSource(imageDefinition.ImageDataBase64);
        if (imageSource == null)
        {
            StatusText = $"Diagram image '{imageDefinition.Name}' is not a valid image.";
            return;
        }

        const double defaultWidth = 120;
        const double defaultHeight = 96;
        var image = new DiagramImageControl(
            imageDefinition.Id,
            imageDefinition.Name,
            imageSource,
            imageDefinition.ImageDataBase64);
        AttachDiagramImageHandlers(image);
        image.SetCanvasBounds(
            canvasPoint.X - (defaultWidth / 2),
            canvasPoint.Y - (defaultHeight / 2),
            defaultWidth,
            defaultHeight);
        image.ApplyDetails(node.Name);
        image.ApplyMetadata(new DiagramObjectMetadata
        {
            Link = node.FullPath
        });

        ApplyDefaultDiagramZIndex(image);
        DiagramCanvas.Children.Add(image);
        SelectDiagramObject(image);
        PushDiagramUndo(DiagramUndoActionKind.Added, before: null, after: CreateDiagramObjectSnapshot(image));
        StatusText = $"Added '{node.Name}' using diagram image '{imageDefinition.Name}'.";
    }

    private DiagramImageDefinition? FindDiagramImageMatchForNode(FileSystemNode node)
    {
        foreach (DiagramImageDefinition image in _appSettings.DiagramImages.Images)
        {
            if (string.IsNullOrWhiteSpace(image.ImageDataBase64) ||
                string.IsNullOrWhiteSpace(image.Regex) ||
                !TryCreateDiagramImageRegex(image.Regex, out Regex? regex) ||
                regex == null)
            {
                continue;
            }

            string matchTarget = DiagramImageDefinition.NormalizeMatchTarget(image.MatchTarget);
            string? input = matchTarget == DiagramImageDefinition.ContentMatchTarget
                ? TryGetObjectExplorerNodeContent(node, out string content) ? content : null
                : node.Name;

            if (!string.IsNullOrEmpty(input) && regex.IsMatch(input))
            {
                return image;
            }
        }

        return null;
    }

    private static bool TryCreateDiagramImageRegex(string pattern, out Regex? regex)
    {
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }

    private bool TryGetObjectExplorerNodeContent(FileSystemNode node, out string content)
    {
        content = string.Empty;
        if (DiagramDocumentService.IsDiagramDocumentPath(node.FullPath))
        {
            string diagramId = DiagramDocumentService.GetDiagramId(node.FullPath);
            DiagramDocument? diagram = _diagramLibrary.Find(diagramId);
            if (diagram == null)
            {
                return false;
            }

            content = CreateDiagramMatchContent(diagram);
            return true;
        }

        if (node.IsDirectory)
        {
            return false;
        }

        return TryGetDocumentContent(
            node.FullPath,
            out content,
            out _,
            out _,
            out _);
    }

    private static string CreateDiagramMatchContent(DiagramDocument diagram)
    {
        var builder = new StringBuilder();
        builder.AppendLine(diagram.Name);

        foreach (DiagramObjectSnapshot diagramObject in diagram.Objects)
        {
            builder.AppendLine(diagramObject.LabelText);
            builder.AppendLine(diagramObject.ImageName);
            builder.AppendLine(diagramObject.Metadata.Link);
        }

        foreach (WorkflowDocument workflow in diagram.Workflows)
        {
            builder.AppendLine(workflow.WorkflowName);
            foreach (WorkflowItem item in workflow.Items)
            {
                builder.AppendLine(item.ItemDescription);
            }
        }

        return builder.ToString();
    }

    private bool TryPlaceWorkflowItemMarker(Point canvasPoint)
    {
        if (TryBlockDiagramObjectEditWhenLocked("add workflow markers"))
        {
            SetWorkflowAddItemsMode(false);
            return true;
        }

        if (!_isAddingWorkflowItems)
        {
            return false;
        }

        if (_workflowEditorTarget == null || WorkflowDetailsPanel.Visibility != Visibility.Visible)
        {
            SetWorkflowAddItemsMode(false);
            StatusText = "Open a workflow before adding items.";
            return true;
        }

        if (!CommitWorkflowEditorChanges(requireValidWorkflowName: true) || _workflowEditorTarget == null)
        {
            SetWorkflowAddItemsMode(false);
            return true;
        }

        WorkflowDocument workflow = _workflowEditorTarget;
        int itemNumber = workflow.Items.Count == 0
            ? 1
            : workflow.Items.Max(item => item.ItemNumber) + 1;
        string markerId = Guid.NewGuid().ToString("N");
        var workflowItem = new WorkflowItem
        {
            WorkflowItemId = Guid.NewGuid().ToString("N"),
            MarkerDiagramObjectId = markerId,
            ItemNumber = itemNumber,
            ItemDescription = string.Empty,
            ItemDocumentationXaml = string.Empty
        };

        workflow.Items.Add(workflowItem);
        workflow.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var marker = new DiagramWorkflowMarkerControl(
            workflow.WorkflowId,
            workflowItem.WorkflowItemId,
            workflowItem.ItemNumber,
            workflowItem.ItemDescription,
            markerId);
        AttachDiagramWorkflowMarkerHandlers(marker);
        marker.SetCanvasBounds(
            canvasPoint.X - (WorkflowMarkerSize / 2),
            canvasPoint.Y - (WorkflowMarkerSize / 2),
            WorkflowMarkerSize,
            WorkflowMarkerSize);

        ApplyDefaultDiagramZIndex(marker);
        DiagramCanvas.Children.Add(marker);
        ApplyWorkflowMarkerVisibility(workflow.WorkflowId);
        _expandedWorkflowItems[workflowItem.WorkflowItemId] = true;
        RebuildWorkflowItemsEditor();
        RefreshWorkflowList();
        SelectDiagramObject(workflow.AreMarkersVisible ? marker : null);
        PushDiagramUndo(DiagramUndoActionKind.Added, before: null, after: CreateDiagramObjectSnapshot(marker));
        FocusWorkflowItem(workflowItem.WorkflowItemId, focusDescription: true);
        StatusText = $"Added workflow item {workflowItem.ItemNumber}.";
        return true;
    }

    private Task OpenDiagramAsync(string diagramPath)
    {
        string diagramId = DiagramDocumentService.GetDiagramId(diagramPath);
        DiagramDocument? diagram = _diagramLibrary.Find(diagramId);
        if (diagram == null)
        {
            StatusText = "Could not open diagram. It may no longer exist in persistence.";
            return Task.CompletedTask;
        }

        EnsureDiagramViewVisible();
        LoadDiagram(diagram);
        SetActiveWorkspaceView(WorkspaceViewKind.Diagram);
        StatusText = $"Opened diagram '{diagram.Name}'.";
        return Task.CompletedTask;
    }

    private void LoadDiagram(DiagramDocument diagram)
    {
        ClearDiagramObjects();
        _currentDiagramWorkflows = diagram.Workflows
            .Select(workflow => workflow.Clone())
            .ToList();
        ResetWorkflowEditor();
        RefreshWorkflowList();

        bool hasPersistedLayering = diagram.Objects.Any(snapshot => snapshot.ZIndex != 0);
        foreach (DiagramObjectSnapshot snapshot in diagram.Objects)
        {
            FrameworkElement? diagramObject = CreateDiagramObjectFromSnapshot(snapshot);
            if (diagramObject != null)
            {
                DiagramCanvas.Children.Add(diagramObject);
            }
        }

        NormalizeDiagramZOrder(prioritizeTransparentObjects: !hasPersistedLayering);
        foreach (WorkflowDocument workflow in _currentDiagramWorkflows)
        {
            ApplyWorkflowMarkerVisibility(workflow.WorkflowId);
        }

        _diagramUndoStack.Clear();
        SelectDiagramObject(null);
        SetCurrentDiagramIdentity(diagram.DiagramId, diagram.Name);

        _diagramCanvasZoom = NormalizeCanvasZoom(diagram.CanvasZoom);
        ApplyDiagramCanvasZoom();
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            DiagramScrollViewer.UpdateLayout();
            if (diagram.ViewportHorizontalOffset > 0 || diagram.ViewportVerticalOffset > 0)
            {
                DiagramScrollViewer.ScrollToHorizontalOffset(diagram.ViewportHorizontalOffset);
                DiagramScrollViewer.ScrollToVerticalOffset(diagram.ViewportVerticalOffset);
            }
            else
            {
                RestoreDiagramViewport();
            }
        }), DispatcherPriority.ContextIdle);
    }

    private List<DiagramObjectSnapshot> CaptureDiagramObjects()
    {
        CommitDiagramObjectTextEdits();
        CommitMetadataEditorChanges();

        var snapshots = new List<DiagramObjectSnapshot>();
        foreach (FrameworkElement child in DiagramCanvas.Children
                     .OfType<FrameworkElement>()
                     .Where(IsDiagramObject)
                     .OrderBy(Panel.GetZIndex)
                     .ThenBy(child => DiagramCanvas.Children.IndexOf(child)))
        {
            DiagramObjectSnapshot? snapshot = CreateDiagramObjectSnapshot(child);
            if (snapshot != null)
            {
                snapshots.Add(snapshot.Clone());
            }
        }

        return snapshots;
    }

    private void ClearDiagramObjects()
    {
        foreach (FrameworkElement diagramObject in DiagramCanvas.Children.OfType<FrameworkElement>()
                     .Where(IsDiagramObject)
                     .ToList())
        {
            RemoveDiagramObject(diagramObject, pushUndo: false);
        }

        _activeDiagramDrawingShape = null;
        _activeDiagramDrawingImage = null;
        _activeDiagramDrawingLine = null;
        _isDrawingDiagramShape = false;
        _isDrawingDiagramImage = false;
        _isDrawingDiagramLine = false;
        _selectedDiagramObject = null;
        _pendingDiagramInteractionSnapshot = null;
        _pendingPortalName = null;
        _pendingPortalPairPlacement = null;
        _metadataEditorTarget = null;
        LoadMetadataEditorForSelection();
    }

    private async Task OpenFileAsync(
        string filePath,
        OpenDocumentState? existingState = null,
        int? targetLineNumber = null,
        int? targetColumnNumber = null,
        ReferenceEntity? targetReference = null,
        FloatingCodeWindow? sourceWindow = null,
        bool suppressHistory = false)
    {
        if (_openWindows.TryGetValue(filePath, out FloatingCodeWindow? existingWindow))
        {
            ActivateCodeWindow(existingWindow);
            RevealWindow(existingWindow);
            int? existingTargetLineNumber = targetReference?.LineNumber ?? targetLineNumber;
            if (existingTargetLineNumber.HasValue)
            {
                int existingTargetColumnNumber = targetReference?.ColumnNumber ?? targetColumnNumber ?? 1;
                ScrollWindowToPositionAndReveal(
                    existingWindow,
                    existingTargetLineNumber.Value,
                    existingTargetColumnNumber,
                    selectLine: targetReference != null,
                    suppressCursorPositionChanged: suppressHistory);
            }

            StatusText = $"Already open: {GetDocumentDisplayName(existingWindow.State)}";
            return;
        }

        if (_openSpreadsheetWindows.TryGetValue(filePath, out FloatingSpreadsheetWindow? existingSpreadsheetWindow))
        {
            SetActiveCodeWindow(null, syncOpenTabsSelection: false);
            BringToFront(existingSpreadsheetWindow);
            RevealWindow(existingSpreadsheetWindow);
            StatusText = $"Already open: {GetDocumentDisplayName(existingSpreadsheetWindow.State)}";
            return;
        }

        if (DatabaseDocumentService.IsDatabaseDocumentPath(filePath))
        {
            if (!_databaseDocumentService.TryGetDocument(filePath, _databaseSnapshots, out string virtualContent, out string syntaxPath, out string displayName))
            {
                if (_databaseDocumentService.TryGetSpreadsheetDocument(filePath, _databaseSnapshots, out string spreadsheetContent, out string spreadsheetDisplayName))
                {
                    await OpenSpreadsheetContentAsync(
                        filePath,
                        spreadsheetContent,
                        spreadsheetDisplayName,
                        existingState,
                        sourceWindow);
                    return;
                }

                StatusText = "Could not open database metadata document.";
                return;
            }

            await OpenDocumentContentAsync(
                filePath,
                virtualContent,
                syntaxPath,
                displayName,
                existingState,
                targetLineNumber,
                targetColumnNumber,
                targetReference,
                sourceWindow,
                suppressHistory);
            return;
        }

        if (IsSpreadsheetDocument(filePath))
        {
            string spreadsheetContent;
            try
            {
                spreadsheetContent = await File.ReadAllTextAsync(filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText = $"Could not open {Path.GetFileName(filePath)}: {ex.Message}";
                return;
            }

            await OpenSpreadsheetContentAsync(
                filePath,
                spreadsheetContent,
                Path.GetFileName(filePath),
                existingState);
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

        await OpenDocumentContentAsync(
            filePath,
            content,
            filePath,
            Path.GetFileName(filePath),
            existingState,
            targetLineNumber,
            targetColumnNumber,
            targetReference,
            sourceWindow,
            suppressHistory);
    }

    private Task OpenDocumentContentAsync(
        string documentPath,
        string content,
        string syntaxPath,
        string displayName,
        OpenDocumentState? existingState = null,
        int? targetLineNumber = null,
        int? targetColumnNumber = null,
        ReferenceEntity? targetReference = null,
        FloatingCodeWindow? sourceWindow = null,
        bool suppressHistory = false)
    {
        EnsureCodeViewVisible();
        OpenDocumentState state = existingState ?? CreateNewDocumentState(documentPath, sourceWindow);
        state.DisplayName = string.IsNullOrWhiteSpace(displayName) ? GetDisplayNameFromPath(documentPath) : displayName;
        if (existingState == null)
        {
            _workspaceState.OpenDocuments.Add(state);
        }

        var window = new FloatingCodeWindow(
            state,
            content,
            _syntaxHighlightingService.GetDefinition(syntaxPath, GetCodeLanguageForFile(syntaxPath)));
        window.ApplyCodeBackcolor(GetCodeWindowBackcolor(syntaxPath));
        window.ApplyKeyboardShortcutSettings(_appSettings.KeyboardShortcuts);
        window.ApplyReferenceHighlights(GetReferenceHighlightStylesForFile(syntaxPath));
        window.CloseRequested += FloatingWindow_CloseRequested;
        window.BoundsChanged += FloatingWindow_BoundsChanged;
        window.ActivationRequested += (_, _) => ActivateCodeWindow(window);
        window.BringToFrontRequested += (_, _) => ActivateCodeWindow(window);
        window.ReferenceNavigationRequested += FloatingWindow_ReferenceNavigationRequested;
        window.ReferencePreviewRequested += FloatingWindow_ReferencePreviewRequested;
        window.CursorPositionChanged += FloatingWindow_CursorPositionChanged;
        window.ContextMenuOpeningRequested += FloatingWindow_ContextMenuOpeningRequested;
        window.ScopeFindRequested += FloatingWindow_ScopeFindRequested;
        window.LineAddressCopied += FloatingWindow_LineAddressCopied;

        if (targetReference != null && existingState == null)
        {
            window.FitToLineRange(targetReference.LineNumber, targetReference.EndLineNumber);
        }

        if (sourceWindow != null && existingState == null)
        {
            Point placement = FindReferenceWindowPlacement(sourceWindow, window.Width, window.Height);
            state.Left = placement.X;
            state.Top = placement.Y;
        }

        _openWindows[documentPath] = window;
        AddOpenTab(state);
        AddWindowToCodeView(window, state, select: true);
        _windowSequence++;
        StatusText = $"Opened {state.DisplayName}";
        UpdateEmptyWorkspaceHint();

        int? newTargetLineNumber = targetReference?.LineNumber ?? targetLineNumber;
        if (newTargetLineNumber.HasValue)
        {
            int newTargetColumnNumber = targetReference?.ColumnNumber ?? targetColumnNumber ?? 1;
            ScrollWindowToPositionAndReveal(
                window,
                newTargetLineNumber.Value,
                newTargetColumnNumber,
                selectLine: targetReference != null,
                suppressCursorPositionChanged: suppressHistory);
        }
        else if (!suppressHistory)
        {
            TrackCursorPosition(documentPath, 1, 1);
        }

        return Task.CompletedTask;
    }

    private Task OpenSpreadsheetContentAsync(
        string documentPath,
        string content,
        string displayName,
        OpenDocumentState? existingState = null,
        FloatingCodeWindow? sourceWindow = null)
    {
        EnsureCodeViewVisible();
        OpenDocumentState state = existingState ?? CreateNewDocumentState(documentPath);
        state.DisplayName = string.IsNullOrWhiteSpace(displayName) ? GetDisplayNameFromPath(documentPath) : displayName;
        if (existingState == null)
        {
            _workspaceState.OpenDocuments.Add(state);
        }

        var window = new FloatingSpreadsheetWindow(state, content);
        window.ApplyGridBackcolor(GetCodeWindowBackcolor(documentPath));
        window.CloseRequested += SpreadsheetWindow_CloseRequested;
        window.BoundsChanged += FloatingWindow_BoundsChanged;
        window.BringToFrontRequested += (_, _) =>
        {
            SetActiveCodeWindow(null, syncOpenTabsSelection: false);
            BringToFront(window);
        };

        if (sourceWindow != null && existingState == null)
        {
            Point placement = FindReferenceWindowPlacement(sourceWindow, window.Width, window.Height);
            state.Left = placement.X;
            state.Top = placement.Y;
        }

        _openSpreadsheetWindows[documentPath] = window;
        AddOpenTab(state);
        AddWindowToCodeView(window, state, select: true);
        _windowSequence++;
        StatusText = $"Opened {state.DisplayName}";
        UpdateEmptyWorkspaceHint();

        return Task.CompletedTask;
    }

    private OpenDocumentState CreateNewDocumentState(string filePath, FloatingCodeWindow? sourceWindow = null)
    {
        const double defaultWidth = 760;
        const double defaultHeight = 500;

        double offset = (_windowSequence % 8) * 28;
        Point viewportOrigin = GetCurrentViewportOrigin();

        return new OpenDocumentState
        {
            FilePath = filePath,
            Left = Math.Clamp(viewportOrigin.X + 60 + offset, 0, VirtualCanvasWidth - defaultWidth),
            Top = Math.Clamp(viewportOrigin.Y + 50 + offset, 0, VirtualCanvasHeight - defaultHeight),
            Width = defaultWidth,
            Height = defaultHeight
        };
    }

    private static string GetDocumentDisplayName(OpenDocumentState state)
    {
        return string.IsNullOrWhiteSpace(state.DisplayName)
            ? GetDisplayNameFromPath(state.FilePath)
            : state.DisplayName;
    }

    private static string GetDisplayNameFromPath(string path)
    {
        string fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }

    private async void FloatingWindow_CloseRequested(object? sender, EventArgs e)
    {
        if (sender is not FloatingCodeWindow window)
        {
            return;
        }

        await CloseOpenWindowAsync(window);
    }

    private async void SpreadsheetWindow_CloseRequested(object? sender, EventArgs e)
    {
        if (sender is not FloatingSpreadsheetWindow window)
        {
            return;
        }

        await CloseSpreadsheetWindowAsync(window);
    }

    private async Task CloseOpenWindowAsync(FloatingCodeWindow window)
    {
        CloseOpenWindow(window);
        await SaveWorkspaceStateAsync();
    }

    private async Task CloseSpreadsheetWindowAsync(FloatingSpreadsheetWindow window)
    {
        CloseSpreadsheetWindow(window);
        await SaveWorkspaceStateAsync();
    }

    private void CloseOpenWindow(FloatingCodeWindow window)
    {
        if (ReferenceEquals(_activeCodeWindow, window))
        {
            SetActiveCodeWindow(null);
        }

        window.LineAddressCopied -= FloatingWindow_LineAddressCopied;
        RemoveWindowFromCodeView(window);
        _openWindows.Remove(window.State.FilePath);
        _workspaceState.OpenDocuments.Remove(window.State);
        RemoveOpenTab(window.State.FilePath);
        StatusText = $"Closed {GetDocumentDisplayName(window.State)}";
        UpdateEmptyWorkspaceHint();
    }

    private void CloseSpreadsheetWindow(FloatingSpreadsheetWindow window)
    {
        RemoveWindowFromCodeView(window);
        _openSpreadsheetWindows.Remove(window.State.FilePath);
        _workspaceState.OpenDocuments.Remove(window.State);
        RemoveOpenTab(window.State.FilePath);
        StatusText = $"Closed {GetDocumentDisplayName(window.State)}";
        UpdateEmptyWorkspaceHint();
    }

    private void AddWindowToCodeView(FrameworkElement window, OpenDocumentState state, bool select)
    {
        if (_codeViewMode == CodeViewMode.Tabs)
        {
            AddWindowToCodeTabs(window, state, select);
            return;
        }

        AddWindowToCodeCanvas(window, state, select);
    }

    private void AddWindowToCodeCanvas(FrameworkElement window, OpenDocumentState state, bool select)
    {
        DetachFromCurrentParent(window);
        SetWindowDockedMode(window, isDocked: false);
        WorkspaceCanvas.Children.Add(window);
        Canvas.SetLeft(window, state.Left);
        Canvas.SetTop(window, state.Top);

        if (select)
        {
            BringToFront(window);
            RevealWindow(window);
            if (window is FloatingCodeWindow codeWindow)
            {
                SetActiveCodeWindow(codeWindow);
            }
            else
            {
                SetActiveCodeWindow(null, syncOpenTabsSelection: false);
            }
        }
    }

    private void AddWindowToCodeTabs(FrameworkElement window, OpenDocumentState state, bool select)
    {
        DetachFromCurrentParent(window);
        SetWindowDockedMode(window, isDocked: true);

        TabItem tabItem = FindCodeDocumentTab(state.FilePath) ?? new TabItem
        {
            Tag = state.FilePath,
            ToolTip = state.FilePath
        };
        tabItem.Header = CreateCodeDocumentTabHeader(state);
        tabItem.Content = window;

        if (!CodeDocumentsTabControl.Items.Contains(tabItem))
        {
            CodeDocumentsTabControl.Items.Add(tabItem);
        }

        if (select)
        {
            CodeDocumentsTabControl.SelectedItem = tabItem;
            if (window is FloatingCodeWindow codeWindow)
            {
                SetActiveCodeWindow(codeWindow);
            }
            else
            {
                SetActiveCodeWindow(null, syncOpenTabsSelection: false);
            }
        }
    }

    private void RemoveWindowFromCodeView(FrameworkElement window)
    {
        WorkspaceCanvas.Children.Remove(window);

        string? filePath = GetWindowFilePath(window);
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            RemoveCodeDocumentTab(filePath);
        }
        else
        {
            DetachFromCurrentParent(window);
        }
    }

    private void DetachFromCurrentParent(FrameworkElement element)
    {
        if (element.Parent is Panel panel)
        {
            panel.Children.Remove(element);
            return;
        }

        if (LogicalTreeHelper.GetParent(element) is ContentControl contentControl &&
            ReferenceEquals(contentControl.Content, element))
        {
            contentControl.Content = null;
            return;
        }

        if (element.Parent is ContentControl parentContentControl &&
            ReferenceEquals(parentContentControl.Content, element))
        {
            parentContentControl.Content = null;
        }
    }

    private static void SetWindowDockedMode(FrameworkElement window, bool isDocked)
    {
        switch (window)
        {
            case FloatingCodeWindow codeWindow:
                codeWindow.SetDockedMode(isDocked);
                break;

            case FloatingSpreadsheetWindow spreadsheetWindow:
                spreadsheetWindow.SetDockedMode(isDocked);
                break;
        }
    }

    private static string? GetWindowFilePath(FrameworkElement window)
    {
        return window switch
        {
            FloatingCodeWindow codeWindow => codeWindow.State.FilePath,
            FloatingSpreadsheetWindow spreadsheetWindow => spreadsheetWindow.State.FilePath,
            _ => null
        };
    }

    private TabItem? FindCodeDocumentTab(string filePath)
    {
        return CodeDocumentsTabControl.Items
            .OfType<TabItem>()
            .FirstOrDefault(tab => string.Equals(tab.Tag as string, filePath, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveCodeDocumentTab(string filePath)
    {
        TabItem? tabItem = FindCodeDocumentTab(filePath);
        if (tabItem == null)
        {
            return;
        }

        tabItem.Content = null;
        CodeDocumentsTabControl.Items.Remove(tabItem);
    }

    private FrameworkElement CreateCodeDocumentTabHeader(OpenDocumentState state)
    {
        var panel = new DockPanel
        {
            LastChildFill = true
        };

        var closeButton = new Button
        {
            Content = "x",
            Tag = state.FilePath,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("TinyCloseButtonStyle"),
            ToolTip = "Close"
        };
        closeButton.Click += CodeDocumentTabCloseButton_Click;
        DockPanel.SetDock(closeButton, Dock.Right);

        var label = new TextBlock
        {
            Text = GetDocumentDisplayName(state),
            MaxWidth = 260,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        panel.Children.Add(closeButton);
        panel.Children.Add(label);
        return panel;
    }

    private void ClearCodeDocumentTabs()
    {
        foreach (TabItem tabItem in CodeDocumentsTabControl.Items.OfType<TabItem>().ToList())
        {
            tabItem.Content = null;
        }

        CodeDocumentsTabControl.Items.Clear();
    }

    private void FloatingWindow_ContextMenuOpeningRequested(object? sender, CodeWindowContextMenuOpeningEventArgs e)
    {
        RemoveDynamicReferencesMenuItems(e.ContextMenu);

        if (sender is not FloatingCodeWindow window)
        {
            return;
        }

        if (IsCSharpDocument(window.State.FilePath))
        {
            InsertDynamicReferencesMenu(e.ContextMenu, CreateCSharpReferencesContextMenu(window));
            return;
        }

        if (IsVisualBasicDocument(window.State.FilePath))
        {
            InsertDynamicReferencesMenu(e.ContextMenu, CreateVisualBasicReferencesContextMenu(window));
            return;
        }

        if (IsSqlDocument(window.State.FilePath))
        {
            InsertDynamicReferencesMenu(
                e.ContextMenu,
                CreateSqlFindContextMenu(window),
                CreateSqlTraceContextMenu(window),
                CreateSqlReferencesContextMenu(window));
        }
    }

    private void FloatingWindow_ScopeFindRequested(object? sender, EventArgs e)
    {
        ObjectExplorerSearchTextBox.Focus();
        Keyboard.Focus(ObjectExplorerSearchTextBox);
        ObjectExplorerSearchTextBox.SelectAll();
        StatusText = "Object Explorer search focused.";
    }

    private void FloatingWindow_LineAddressCopied(object? sender, LineAddressCopiedEventArgs e)
    {
        StatusText = e.Copied
            ? $"Copied line address: {e.LineAddress}"
            : $"Could not copy line address: {e.ErrorMessage}";
    }

    private MenuItem CreateCSharpReferencesContextMenu(FloatingCodeWindow window)
    {
        var referencesMenu = new MenuItem
        {
            Header = "References",
            Tag = DynamicReferencesContextMenuTag
        };

        if (_activeScope == null || _referenceIndex.EntityCount == 0)
        {
            AddDisabledMenuItem(referencesMenu, "No scope reference index loaded");
            return referencesMenu;
        }

        IReadOnlyList<CodeReferenceOccurrence> occurrences = BuildCSharpReferenceOccurrences(window);
        if (occurrences.Count == 0)
        {
            AddDisabledMenuItem(referencesMenu, "No references found in this file");
            return referencesMenu;
        }

        AddInternalReferencesMenu(referencesMenu, window, occurrences.Where(occurrence => occurrence.IsInternal));
        AddExternalReferencesMenu(referencesMenu, window, occurrences.Where(occurrence => !occurrence.IsInternal));
        return referencesMenu;
    }

    private MenuItem CreateVisualBasicReferencesContextMenu(FloatingCodeWindow window)
    {
        var referencesMenu = new MenuItem
        {
            Header = "References",
            Tag = DynamicReferencesContextMenuTag
        };

        if (_activeScope == null || _referenceIndex.EntityCount == 0)
        {
            AddDisabledMenuItem(referencesMenu, "No scope reference index loaded");
            return referencesMenu;
        }

        IReadOnlyList<CodeReferenceOccurrence> occurrences = BuildVisualBasicReferenceOccurrences(window);
        if (occurrences.Count == 0)
        {
            AddDisabledMenuItem(referencesMenu, "No references found in this file");
            return referencesMenu;
        }

        AddInternalReferencesMenu(
            referencesMenu,
            window,
            occurrences.Where(occurrence => occurrence.IsInternal),
            CodeWindowSettings.VisualBasicLanguage);
        AddExternalReferencesMenu(
            referencesMenu,
            window,
            occurrences.Where(occurrence => !occurrence.IsInternal),
            CodeWindowSettings.VisualBasicLanguage);
        return referencesMenu;
    }

    private MenuItem CreateSqlReferencesContextMenu(FloatingCodeWindow window)
    {
        var referencesMenu = new MenuItem
        {
            Header = "References",
            Tag = DynamicReferencesContextMenuTag
        };

        if (_activeScope == null || _referenceIndex.EntityCount == 0)
        {
            AddDisabledMenuItem(referencesMenu, "No scope reference index loaded");
            return referencesMenu;
        }

        IReadOnlyList<CodeReferenceOccurrence> occurrences = BuildSqlReferenceOccurrences(window);
        foreach (ReferenceEntityKind kind in SqlContextMenuReferenceKinds)
        {
            AddSqlReferenceKindMenu(referencesMenu, window, kind, occurrences.Where(occurrence => occurrence.Target.Kind == kind));
        }

        return referencesMenu;
    }

    private MenuItem CreateSqlFindContextMenu(FloatingCodeWindow window)
    {
        var findMenu = new MenuItem
        {
            Header = "Find",
            Tag = DynamicReferencesContextMenuTag
        };

        if (_activeScope == null)
        {
            AddDisabledMenuItem(findMenu, "No scope loaded");
            return findMenu;
        }

        foreach ((SqlFindOperation operation, string header) in SqlFindOperations)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, e) =>
            {
                e.Handled = true;
                await ApplySqlFindAsync(window, operation);
            };
            findMenu.Items.Add(item);
        }

        return findMenu;
    }

    private MenuItem CreateSqlTraceContextMenu(FloatingCodeWindow window)
    {
        var traceMenu = new MenuItem
        {
            Header = "Trace",
            Tag = DynamicReferencesContextMenuTag
        };

        traceMenu.Click += async (_, e) =>
        {
            e.Handled = true;
            await ApplySqlTraceAsync(window);
        };

        return traceMenu;
    }

    private async Task ApplySqlTraceAsync(FloatingCodeWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Text))
        {
            StatusText = "This SQL window has no content to trace.";
            return;
        }

        string documentName = GetDocumentDisplayName(window.State);
        SqlTraceResult trace = _sqlTraceService.BuildTrace(documentName, window.Text, _databaseSnapshots);
        if (string.IsNullOrWhiteSpace(trace.Text))
        {
            StatusText = $"No SQL trace could be generated for {documentName}.";
            return;
        }

        bool copiedToClipboard = TryCopyTextToClipboard(trace.Text);
        string tracePath = await SaveSqlTraceFileAsync(documentName, trace.Text);
        await OpenFileAsync(tracePath, sourceWindow: window, suppressHistory: true);

        string clipboardMessage = copiedToClipboard ? "copied to clipboard and opened" : "opened";
        StatusText = $"SQL trace {clipboardMessage}: {trace.QueryCount} quer{(trace.QueryCount == 1 ? "y" : "ies")}, {trace.StoredProcedureCallCount} stored procedure call{(trace.StoredProcedureCallCount == 1 ? string.Empty : "s")}.";
    }

    private static bool TryCopyTextToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    private static async Task<string> SaveSqlTraceFileAsync(string documentName, string traceText)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string traceDirectory = Path.Combine(appData, "Surf2", "Traces");
        Directory.CreateDirectory(traceDirectory);

        string baseFileName = CreateSafeFileName(Path.GetFileNameWithoutExtension(documentName));
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string tracePath = Path.Combine(traceDirectory, $"{baseFileName}-{timestamp}.sqltrace.txt");
        await File.WriteAllTextAsync(tracePath, traceText);
        return tracePath;
    }

    private async Task ApplySqlFindAsync(FloatingCodeWindow window, SqlFindOperation operation)
    {
        if (_activeScope == null)
        {
            StatusText = "Open a scope before using SQL Find.";
            return;
        }

        if (!window.TryGetSelectedTextOrReferenceToken(out string target, out int targetOffset) ||
            string.IsNullOrWhiteSpace(target))
        {
            StatusText = "Select a SQL table or field, or place the cursor on one, before using Find.";
            return;
        }

        string pattern = CreateSqlFindRegexPattern(window.Text, target, targetOffset, operation, out string targetDescription);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            StatusText = $"Could not build a SQL Find search for '{target}'.";
            return;
        }

        ObjectExplorerRegexToggle.IsChecked = true;
        SetObjectExplorerSearchTarget(ObjectExplorerSearchTarget.Content);
        ObjectExplorerSearchTextBox.Text = pattern;

        await ApplyObjectExplorerSearchAsync();
        StatusText = $"Filtered Object Explorer for {GetSqlFindOperationHeader(operation)} usage of {targetDescription}.";
    }

    private string CreateSqlFindRegexPattern(
        string sql,
        string rawTarget,
        int targetOffset,
        SqlFindOperation operation,
        out string targetDescription)
    {
        string normalizedTarget = NormalizeSqlReferenceToken(rawTarget);
        string simpleTarget = GetSqlSimpleName(normalizedTarget);
        targetDescription = $"'{simpleTarget}'";

        if (string.IsNullOrWhiteSpace(simpleTarget))
        {
            return string.Empty;
        }

        if (IsKnownSqlTableTarget(normalizedTarget))
        {
            return CreateSqlTableUsageRegex(simpleTarget, operation);
        }

        if (TryInferSqlFieldTable(sql, targetOffset, normalizedTarget, out string tableName))
        {
            string simpleTableName = GetSqlSimpleName(tableName);
            targetDescription = $"field '{simpleTarget}' on table '{simpleTableName}'";
            return CreateSqlFieldUsageRegex(simpleTarget, simpleTableName, operation);
        }

        return CreateSqlGenericUsageRegex(simpleTarget, operation);
    }

    private void SetObjectExplorerSearchTarget(ObjectExplorerSearchTarget searchTarget)
    {
        string targetName = searchTarget.ToString();
        for (int i = 0; i < ObjectExplorerSearchTargetComboBox.Items.Count; i++)
        {
            if (ObjectExplorerSearchTargetComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), targetName, StringComparison.OrdinalIgnoreCase))
            {
                ObjectExplorerSearchTargetComboBox.SelectedIndex = i;
                return;
            }
        }
    }

    private bool IsKnownSqlTableTarget(string normalizedTarget)
    {
        string simpleTarget = GetSqlSimpleName(normalizedTarget);
        return _referenceIndex.Resolve(normalizedTarget)
                   .Concat(_referenceIndex.Resolve(simpleTarget))
                   .Any(reference => reference.Kind == ReferenceEntityKind.Table);
    }

    private bool TryInferSqlFieldTable(string sql, int targetOffset, string normalizedTarget, out string tableName)
    {
        tableName = string.Empty;
        string[] parts = GetSqlNameParts(normalizedTarget);
        string qualifier = parts.Length > 1 ? parts[^2] : string.Empty;

        int searchStart = Math.Max(0, targetOffset - 5000);
        int searchEnd = Math.Min(sql.Length, targetOffset + 5000);
        string nearbySql = sql[searchStart..searchEnd];
        int relativeTargetOffset = Math.Clamp(targetOffset - searchStart, 0, nearbySql.Length);
        List<SqlTableReferenceCandidate> tableReferences = GetSqlTableReferenceCandidates(nearbySql);

        if (!string.IsNullOrWhiteSpace(qualifier))
        {
            SqlTableReferenceCandidate? qualifierMatch = tableReferences.FirstOrDefault(candidate =>
                string.Equals(candidate.Alias, qualifier, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.SimpleTableName, qualifier, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.TableName, qualifier, StringComparison.OrdinalIgnoreCase));
            if (qualifierMatch != null)
            {
                tableName = qualifierMatch.SimpleTableName;
                return true;
            }
        }

        SqlTableReferenceCandidate? followingReference = tableReferences
            .Where(candidate => candidate.StartOffset >= relativeTargetOffset)
            .OrderBy(candidate => candidate.StartOffset)
            .FirstOrDefault();
        if (followingReference != null)
        {
            tableName = followingReference.SimpleTableName;
            return true;
        }

        SqlTableReferenceCandidate? precedingReference = tableReferences
            .Where(candidate => candidate.StartOffset < relativeTargetOffset)
            .OrderByDescending(candidate => candidate.StartOffset)
            .FirstOrDefault();
        if (precedingReference != null)
        {
            tableName = precedingReference.SimpleTableName;
            return true;
        }

        ReferenceEntity? fieldReference = FindUniqueSqlFieldReference(normalizedTarget);
        if (!string.IsNullOrWhiteSpace(fieldReference?.ContainerName))
        {
            tableName = fieldReference.ContainerName;
            return true;
        }

        return false;
    }

    private ReferenceEntity? FindUniqueSqlFieldReference(string normalizedTarget)
    {
        string simpleTarget = GetSqlSimpleName(normalizedTarget);
        List<ReferenceEntity> fieldReferences = _referenceIndex.Resolve(normalizedTarget)
            .Concat(_referenceIndex.Resolve(simpleTarget))
            .Where(reference => reference.Kind == ReferenceEntityKind.Field)
            .DistinctBy(reference => $"{reference.FilePath}|{reference.QualifiedName}|{reference.ContainerName}")
            .ToList();

        return fieldReferences
            .Select(reference => reference.ContainerName)
            .Where(containerName => !string.IsNullOrWhiteSpace(containerName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Count() == 1
            ? fieldReferences.FirstOrDefault()
            : null;
    }

    private static List<SqlTableReferenceCandidate> GetSqlTableReferenceCandidates(string sql)
    {
        var candidates = new List<SqlTableReferenceCandidate>();
        foreach (Match match in SqlTableReferencePattern.Matches(sql))
        {
            string tableName = NormalizeSqlReferenceToken(match.Groups["table"].Value);
            string simpleTableName = GetSqlSimpleName(tableName);
            string alias = NormalizeSqlReferenceToken(match.Groups["alias"].Value);
            if (IsSqlTableAliasKeyword(alias))
            {
                alias = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(simpleTableName))
            {
                candidates.Add(new SqlTableReferenceCandidate(tableName, simpleTableName, alias, match.Index));
            }
        }

        return candidates;
    }

    private static string CreateSqlTableUsageRegex(string tableName, SqlFindOperation operation)
    {
        string tablePattern = CreateSqlObjectNameRegex(tableName);
        return operation switch
        {
            SqlFindOperation.Select => $@"(?is)\bselect\b[\s\S]*?\b(?:from|join)\b\s+{tablePattern}",
            SqlFindOperation.Delete => $@"(?is)\bdelete\b[\s\S]*?\bfrom\b\s+{tablePattern}",
            SqlFindOperation.Update => $@"(?is)(?:\bupdate\b\s+(?:top\s*\([^\)]*\)\s*)?{tablePattern}|\bupdate\b[\s\S]*?\bfrom\b\s+{tablePattern})",
            SqlFindOperation.Insert => $@"(?is)\binsert\b\s+(?:into\s+)?{tablePattern}",
            SqlFindOperation.InsertOrUpdate => $@"(?is)(?:\bmerge\b\s+(?:into\s+)?{tablePattern}|\binsert\b\s+(?:into\s+)?{tablePattern}|\bupdate\b\s+(?:top\s*\([^\)]*\)\s*)?{tablePattern}|\bupdate\b[\s\S]*?\bfrom\b\s+{tablePattern})",
            _ => $@"(?is){tablePattern}"
        };
    }

    private static string CreateSqlFieldUsageRegex(string fieldName, string tableName, SqlFindOperation operation)
    {
        string fieldPattern = CreateSqlIdentifierRegex(fieldName);
        string tablePattern = CreateSqlObjectNameRegex(tableName);
        string selectPattern = $@"\bselect\b[\s\S]*?{fieldPattern}[\s\S]*?\b(?:from|join)\b\s+{tablePattern}";
        string updatePattern = $@"(?:\bupdate\b\s+(?:top\s*\([^\)]*\)\s*)?{tablePattern}[\s\S]*?\bset\b[\s\S]*?{fieldPattern}|\bupdate\b[\s\S]*?\bset\b[\s\S]*?{fieldPattern}[\s\S]*?\bfrom\b\s+{tablePattern})";
        string insertPattern = $@"\binsert\b\s+(?:into\s+)?{tablePattern}[\s\S]*?(?:\([^\)]*{fieldPattern}[\s\S]*?\)|{fieldPattern})";

        return operation switch
        {
            SqlFindOperation.Select => $"(?is){selectPattern}",
            SqlFindOperation.Delete => CreateSqlTableUsageRegex(tableName, SqlFindOperation.Delete),
            SqlFindOperation.Update => $"(?is){updatePattern}",
            SqlFindOperation.Insert => $"(?is){insertPattern}",
            SqlFindOperation.InsertOrUpdate => $"(?is)(?:{insertPattern}|{updatePattern})",
            _ => $@"(?is)(?:{fieldPattern}[\s\S]*?\b(?:from|join)\b\s+{tablePattern}|{tablePattern}[\s\S]*?{fieldPattern})"
        };
    }

    private static string CreateSqlGenericUsageRegex(string targetName, SqlFindOperation operation)
    {
        string targetPattern = CreateSqlIdentifierRegex(targetName);
        return operation switch
        {
            SqlFindOperation.Select => $@"(?is)\bselect\b[\s\S]*?{targetPattern}",
            SqlFindOperation.Delete => $@"(?is)\bdelete\b[\s\S]*?{targetPattern}",
            SqlFindOperation.Update => $@"(?is)\bupdate\b[\s\S]*?{targetPattern}",
            SqlFindOperation.Insert => $@"(?is)\binsert\b[\s\S]*?{targetPattern}",
            SqlFindOperation.InsertOrUpdate => $@"(?is)(?:\binsert\b|\bupdate\b|\bmerge\b)[\s\S]*?{targetPattern}",
            _ => $@"(?is){targetPattern}"
        };
    }

    private static string CreateSqlObjectNameRegex(string objectName)
    {
        string simpleName = GetSqlSimpleName(objectName);
        return $@"(?:(?:\[[^\]\r\n]+\]|[A-Za-z_#][A-Za-z0-9_#$]*)\s*\.\s*){{0,3}}{CreateSqlIdentifierRegex(simpleName)}";
    }

    private static string CreateSqlIdentifierRegex(string identifier)
    {
        string escapedIdentifier = Regex.Escape(identifier);
        return $@"(?:\[{escapedIdentifier}\]|{escapedIdentifier})(?![A-Za-z0-9_#$])";
    }

    private static string GetSqlSimpleName(string name)
    {
        return GetSqlNameParts(name).LastOrDefault() ?? string.Empty;
    }

    private static string[] GetSqlNameParts(string name)
    {
        return NormalizeSqlReferenceToken(name)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsSqlTableAliasKeyword(string alias)
    {
        return string.IsNullOrWhiteSpace(alias) ||
               alias.Equals("where", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("on", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("join", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("inner", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("left", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("right", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("full", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("cross", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("group", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("order", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("having", StringComparison.OrdinalIgnoreCase) ||
               alias.Equals("union", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSqlFindOperationHeader(SqlFindOperation operation)
    {
        return SqlFindOperations.FirstOrDefault(item => item.Operation == operation).Header ?? operation.ToString();
    }

    private void AddSqlReferenceKindMenu(
        MenuItem referencesMenu,
        FloatingCodeWindow window,
        ReferenceEntityKind kind,
        IEnumerable<CodeReferenceOccurrence> occurrences)
    {
        var kindMenu = new MenuItem { Header = GetReferenceKindGroupHeader(kind) };
        List<CodeReferenceOccurrence> kindOccurrences = occurrences
            .OrderBy(occurrence => GetReferenceDisplayName(occurrence.Target))
            .ThenBy(occurrence => occurrence.LineNumber)
            .ThenBy(occurrence => occurrence.ColumnNumber)
            .ToList();

        if (kindOccurrences.Count == 0)
        {
            AddDisabledMenuItem(kindMenu, $"No {GetReferenceKindGroupHeader(kind).ToLowerInvariant()} references");
        }
        else
        {
            foreach (CodeReferenceOccurrence occurrence in kindOccurrences)
            {
                kindMenu.Items.Add(CreateSqlReferenceOccurrenceMenuItem(window, occurrence));
            }
        }

        referencesMenu.Items.Add(kindMenu);
    }

    private void AddInternalReferencesMenu(
        MenuItem referencesMenu,
        FloatingCodeWindow window,
        IEnumerable<CodeReferenceOccurrence> occurrences,
        string sourceLanguage = "")
    {
        List<CodeReferenceOccurrence> internalOccurrences = occurrences
            .OrderBy(occurrence => GetReferenceKindMenuRank(occurrence.Target.Kind))
            .ThenBy(occurrence => occurrence.Target.Name)
            .ThenBy(occurrence => occurrence.LineNumber)
            .ThenBy(occurrence => occurrence.ColumnNumber)
            .ToList();

        var internalMenu = new MenuItem { Header = "Internal" };
        if (internalOccurrences.Count == 0)
        {
            AddDisabledMenuItem(internalMenu, "No internal references");
        }
        else
        {
            foreach (IGrouping<ReferenceEntityKind, CodeReferenceOccurrence> kindGroup in internalOccurrences.GroupBy(occurrence => occurrence.Target.Kind))
            {
                var kindMenu = new MenuItem { Header = GetReferenceKindGroupHeader(kindGroup.Key, sourceLanguage) };
                foreach (CodeReferenceOccurrence occurrence in kindGroup)
                {
                    kindMenu.Items.Add(CreateReferenceOccurrenceMenuItem(window, occurrence));
                }

                internalMenu.Items.Add(kindMenu);
            }
        }

        referencesMenu.Items.Add(internalMenu);
    }

    private void AddExternalReferencesMenu(
        MenuItem referencesMenu,
        FloatingCodeWindow window,
        IEnumerable<CodeReferenceOccurrence> occurrences,
        string sourceLanguage = "")
    {
        List<CodeReferenceOccurrence> externalOccurrences = occurrences
            .OrderBy(occurrence => occurrence.TargetResource.DisplayName)
            .ThenBy(occurrence => GetReferenceKindMenuRank(occurrence.Target.Kind))
            .ThenBy(occurrence => occurrence.Target.FilePath)
            .ThenBy(occurrence => occurrence.LineNumber)
            .ThenBy(occurrence => occurrence.ColumnNumber)
            .ToList();

        var externalMenu = new MenuItem { Header = "External" };
        if (externalOccurrences.Count == 0)
        {
            AddDisabledMenuItem(externalMenu, "No external references");
        }
        else
        {
            foreach (IGrouping<string, CodeReferenceOccurrence> resourceGroup in externalOccurrences.GroupBy(occurrence => occurrence.TargetResource.Key))
            {
                ReferenceResourceContext resource = resourceGroup.First().TargetResource;
                var resourceMenu = new MenuItem
                {
                    Header = $"{resource.DisplayName} ({GetResourceKindDisplay(resource.Kind)})",
                    ToolTip = resource.Path
                };

                if (resource.Kind == ResourceKind.DatabaseSnapshot)
                {
                    AddExternalDatabaseReferenceMenus(resourceMenu, window, resourceGroup, sourceLanguage);
                }
                else
                {
                    AddExternalFileReferenceMenus(resourceMenu, window, resourceGroup);
                }

                externalMenu.Items.Add(resourceMenu);
            }
        }

        referencesMenu.Items.Add(externalMenu);
    }

    private void AddExternalDatabaseReferenceMenus(
        MenuItem resourceMenu,
        FloatingCodeWindow window,
        IEnumerable<CodeReferenceOccurrence> occurrences,
        string sourceLanguage = "")
    {
        foreach (IGrouping<ReferenceEntityKind, CodeReferenceOccurrence> kindGroup in occurrences
                     .OrderBy(occurrence => GetReferenceKindMenuRank(occurrence.Target.Kind))
                     .ThenBy(occurrence => occurrence.Target.Name)
                     .ThenBy(occurrence => occurrence.LineNumber)
                     .GroupBy(occurrence => occurrence.Target.Kind))
        {
            var kindMenu = new MenuItem { Header = GetReferenceKindGroupHeader(kindGroup.Key, sourceLanguage) };
            foreach (CodeReferenceOccurrence occurrence in kindGroup)
            {
                kindMenu.Items.Add(CreateReferenceOccurrenceMenuItem(window, occurrence));
            }

            resourceMenu.Items.Add(kindMenu);
        }
    }

    private void AddExternalFileReferenceMenus(
        MenuItem resourceMenu,
        FloatingCodeWindow window,
        IEnumerable<CodeReferenceOccurrence> occurrences)
    {
        foreach (IGrouping<string, CodeReferenceOccurrence> fileGroup in occurrences
                     .OrderBy(occurrence => occurrence.Target.FilePath)
                     .ThenBy(occurrence => occurrence.LineNumber)
                     .ThenBy(occurrence => occurrence.ColumnNumber)
                     .GroupBy(occurrence => occurrence.Target.FilePath))
        {
            var fileMenu = new MenuItem
            {
                Header = GetDisplayNameFromPath(fileGroup.Key),
                ToolTip = fileGroup.Key
            };

            foreach (CodeReferenceOccurrence occurrence in fileGroup)
            {
                fileMenu.Items.Add(CreateReferenceOccurrenceMenuItem(window, occurrence));
            }

            resourceMenu.Items.Add(fileMenu);
        }
    }

    private MenuItem CreateReferenceOccurrenceMenuItem(FloatingCodeWindow window, CodeReferenceOccurrence occurrence)
    {
        var item = new MenuItem
        {
            Header = FormatReferenceOccurrenceHeader(occurrence),
            ToolTip = $"{occurrence.Target.Kind}: {GetReferenceDisplayName(occurrence.Target)} in {GetDisplayNameFromPath(occurrence.Target.FilePath)}"
        };

        item.Click += (_, e) =>
        {
            e.Handled = true;
            NavigateToReferenceOccurrence(window, occurrence);
        };

        return item;
    }

    private MenuItem CreateSqlReferenceOccurrenceMenuItem(FloatingCodeWindow window, CodeReferenceOccurrence occurrence)
    {
        var item = new MenuItem
        {
            Header = $"{GetReferenceDisplayName(occurrence.Target)} - line {occurrence.LineNumber}",
            ToolTip = $"{occurrence.Target.Kind}: {GetReferenceDisplayName(occurrence.Target)} in {GetDisplayNameFromPath(occurrence.Target.FilePath)}"
        };

        item.Click += (_, e) =>
        {
            e.Handled = true;
            NavigateToReferenceOccurrence(window, occurrence);
        };

        return item;
    }

    private void NavigateToReferenceOccurrence(FloatingCodeWindow window, CodeReferenceOccurrence occurrence)
    {
        ActivateCodeWindow(window);
        ScrollWindowToPositionAndReveal(window, occurrence.LineNumber, occurrence.ColumnNumber, selectLine: true);
        StatusText = $"Scrolled to {occurrence.Target.Kind} reference '{occurrence.Token}' on line {occurrence.LineNumber}.";
    }

    private IReadOnlyList<CodeReferenceOccurrence> BuildCSharpReferenceOccurrences(FloatingCodeWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Text))
        {
            return [];
        }

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(window.Text).GetRoot();
        }
        catch (ArgumentException)
        {
            return [];
        }

        var contextCache = new Dictionary<string, IReadOnlyList<ReferenceResourceContext>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ReferenceResourceContext> sourceResources = GetCachedResourceContexts(window.State.FilePath);
        HashSet<string> sourceResourceKeys = sourceResources.Select(resource => resource.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenOccurrences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occurrences = new List<CodeReferenceOccurrence>();

        foreach (SyntaxToken token in root.DescendantTokens(descendIntoTrivia: false))
        {
            if (!IsCSharpReferenceIdentifierToken(token))
            {
                continue;
            }

            string tokenText = token.ValueText;
            if (string.IsNullOrWhiteSpace(tokenText))
            {
                continue;
            }

            int? argumentCount = TryGetCSharpInvocationArgumentCount(token);
            IReadOnlyList<ReferenceEntity> references = SortReferencesForSource(
                _referenceIndex.Resolve(tokenText, argumentCount),
                window.State.FilePath);
            if (references.Count == 0)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = token.GetLocation().GetLineSpan();
            int lineNumber = lineSpan.StartLinePosition.Line + 1;
            int columnNumber = lineSpan.StartLinePosition.Character + 1;

            foreach (ReferenceEntity reference in references)
            {
                IReadOnlyList<ReferenceResourceContext> targetResources = GetCachedResourceContexts(reference.FilePath);
                ReferenceResourceContext? sharedResource = targetResources.FirstOrDefault(resource => sourceResourceKeys.Contains(resource.Key));
                bool isInternal = sharedResource != null || IsSameFile(reference.FilePath, window.State.FilePath);
                ReferenceResourceContext targetResource = sharedResource
                    ?? targetResources.FirstOrDefault()
                    ?? CreateFallbackReferenceResourceContext(reference.FilePath);

                string occurrenceKey = string.Join(
                    "|",
                    token.SpanStart,
                    lineNumber,
                    columnNumber,
                    reference.Kind,
                    reference.FilePath,
                    reference.LineNumber,
                    reference.ColumnNumber,
                    targetResource.Key);
                if (!seenOccurrences.Add(occurrenceKey))
                {
                    continue;
                }

                occurrences.Add(new CodeReferenceOccurrence(
                    tokenText,
                    lineNumber,
                    columnNumber,
                    reference,
                    targetResource,
                    isInternal));
            }
        }

        return occurrences
            .OrderBy(occurrence => occurrence.IsInternal ? 0 : 1)
            .ThenBy(occurrence => occurrence.TargetResource.DisplayName)
            .ThenBy(occurrence => GetReferenceKindMenuRank(occurrence.Target.Kind))
            .ThenBy(occurrence => occurrence.LineNumber)
            .ThenBy(occurrence => occurrence.ColumnNumber)
            .ToList();

        IReadOnlyList<ReferenceResourceContext> GetCachedResourceContexts(string documentPath)
        {
            if (!contextCache.TryGetValue(documentPath, out IReadOnlyList<ReferenceResourceContext>? contexts))
            {
                contexts = FindReferenceResourceContextsForDocument(documentPath);
                contextCache[documentPath] = contexts;
            }

            return contexts;
        }
    }

    private IReadOnlyList<CodeReferenceOccurrence> BuildVisualBasicReferenceOccurrences(FloatingCodeWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Text))
        {
            return [];
        }

        SyntaxNode root;
        try
        {
            root = VisualBasicSyntaxTree.ParseText(window.Text).GetRoot();
        }
        catch (ArgumentException)
        {
            return [];
        }

        var contextCache = new Dictionary<string, IReadOnlyList<ReferenceResourceContext>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ReferenceResourceContext> sourceResources = GetCachedResourceContexts(window.State.FilePath);
        HashSet<string> sourceResourceKeys = sourceResources.Select(resource => resource.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenOccurrences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occurrences = new List<CodeReferenceOccurrence>();

        foreach (SyntaxToken token in root.DescendantTokens(descendIntoTrivia: false))
        {
            if (!IsVisualBasicReferenceIdentifierToken(token))
            {
                continue;
            }

            string tokenText = token.ValueText;
            if (string.IsNullOrWhiteSpace(tokenText))
            {
                continue;
            }

            int? argumentCount = TryGetVisualBasicInvocationArgumentCount(token);
            IReadOnlyList<ReferenceEntity> references = SortReferencesForSource(
                _referenceIndex.Resolve(tokenText, argumentCount),
                window.State.FilePath);
            if (references.Count == 0)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = token.GetLocation().GetLineSpan();
            int lineNumber = lineSpan.StartLinePosition.Line + 1;
            int columnNumber = lineSpan.StartLinePosition.Character + 1;

            foreach (ReferenceEntity reference in references)
            {
                IReadOnlyList<ReferenceResourceContext> targetResources = GetCachedResourceContexts(reference.FilePath);
                ReferenceResourceContext? sharedResource = targetResources.FirstOrDefault(resource => sourceResourceKeys.Contains(resource.Key));
                bool isInternal = sharedResource != null || IsSameFile(reference.FilePath, window.State.FilePath);
                ReferenceResourceContext targetResource = sharedResource
                    ?? targetResources.FirstOrDefault()
                    ?? CreateFallbackReferenceResourceContext(reference.FilePath);

                string occurrenceKey = string.Join(
                    "|",
                    token.SpanStart,
                    lineNumber,
                    columnNumber,
                    reference.Kind,
                    reference.FilePath,
                    reference.LineNumber,
                    reference.ColumnNumber,
                    targetResource.Key);
                if (!seenOccurrences.Add(occurrenceKey))
                {
                    continue;
                }

                occurrences.Add(new CodeReferenceOccurrence(
                    tokenText,
                    lineNumber,
                    columnNumber,
                    reference,
                    targetResource,
                    isInternal));
            }
        }

        return occurrences
            .OrderBy(occurrence => occurrence.IsInternal ? 0 : 1)
            .ThenBy(occurrence => occurrence.TargetResource.DisplayName)
            .ThenBy(occurrence => GetReferenceKindMenuRank(occurrence.Target.Kind))
            .ThenBy(occurrence => occurrence.LineNumber)
            .ThenBy(occurrence => occurrence.ColumnNumber)
            .ToList();

        IReadOnlyList<ReferenceResourceContext> GetCachedResourceContexts(string documentPath)
        {
            if (!contextCache.TryGetValue(documentPath, out IReadOnlyList<ReferenceResourceContext>? contexts))
            {
                contexts = FindReferenceResourceContextsForDocument(documentPath);
                contextCache[documentPath] = contexts;
            }

            return contexts;
        }
    }

    private IReadOnlyList<CodeReferenceOccurrence> BuildSqlReferenceOccurrences(FloatingCodeWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Text))
        {
            return [];
        }

        var contextCache = new Dictionary<string, IReadOnlyList<ReferenceResourceContext>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ReferenceResourceContext> sourceResources = GetCachedResourceContexts(window.State.FilePath);
        HashSet<string> sourceResourceKeys = sourceResources.Select(resource => resource.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenOccurrences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occurrences = new List<CodeReferenceOccurrence>();

        foreach (SqlReferenceToken token in EnumerateSqlReferenceTokens(window.Text))
        {
            IReadOnlyList<ReferenceEntity> references = SortReferencesForSource(
                    _referenceIndex.Resolve(token.Token),
                    window.State.FilePath)
                .Where(reference => SqlContextMenuReferenceKinds.Contains(reference.Kind))
                .ToList();

            if (references.Count == 0)
            {
                continue;
            }

            foreach (ReferenceEntity reference in references)
            {
                if (IsSqlDefinitionToken(window.Text, token, reference.Kind))
                {
                    continue;
                }

                IReadOnlyList<ReferenceResourceContext> targetResources = GetCachedResourceContexts(reference.FilePath);
                ReferenceResourceContext? sharedResource = targetResources.FirstOrDefault(resource => sourceResourceKeys.Contains(resource.Key));
                bool isInternal = sharedResource != null || IsSameFile(reference.FilePath, window.State.FilePath);
                ReferenceResourceContext targetResource = sharedResource
                    ?? targetResources.FirstOrDefault()
                    ?? CreateFallbackReferenceResourceContext(reference.FilePath);

                string occurrenceKey = string.Join(
                    "|",
                    token.StartOffset,
                    token.LineNumber,
                    token.ColumnNumber,
                    reference.Kind,
                    reference.FilePath,
                    reference.LineNumber,
                    reference.ColumnNumber,
                    targetResource.Key);
                if (!seenOccurrences.Add(occurrenceKey))
                {
                    continue;
                }

                occurrences.Add(new CodeReferenceOccurrence(
                    token.Token,
                    token.LineNumber,
                    token.ColumnNumber,
                    reference,
                    targetResource,
                    isInternal));
            }
        }

        return occurrences
            .OrderBy(occurrence => GetReferenceKindMenuRank(occurrence.Target.Kind))
            .ThenBy(occurrence => GetReferenceDisplayName(occurrence.Target))
            .ThenBy(occurrence => occurrence.LineNumber)
            .ThenBy(occurrence => occurrence.ColumnNumber)
            .ToList();

        IReadOnlyList<ReferenceResourceContext> GetCachedResourceContexts(string documentPath)
        {
            if (!contextCache.TryGetValue(documentPath, out IReadOnlyList<ReferenceResourceContext>? contexts))
            {
                contexts = FindReferenceResourceContextsForDocument(documentPath);
                contextCache[documentPath] = contexts;
            }

            return contexts;
        }
    }

    private IReadOnlyList<ReferenceResourceContext> FindReferenceResourceContextsForDocument(string documentPath)
    {
        if (_activeScope == null || string.IsNullOrWhiteSpace(documentPath))
        {
            return [];
        }

        return _activeScope.Resources
            .Where(resource => resource.Kind != ResourceKind.Diagram &&
                               IsScopeResourceLoaded(resource) &&
                               IsDocumentInScopedResource(resource, documentPath))
            .OrderByDescending(GetReferenceResourceSpecificity)
            .ThenBy(resource => resource.DisplayName)
            .Select(CreateReferenceResourceContext)
            .ToList();
    }

    private bool IsDocumentInScopedResource(ScopedResource resource, string documentPath)
    {
        if (resource.Kind == ResourceKind.DatabaseSnapshot)
        {
            return TryGetDatabaseSnapshotId(documentPath, out string snapshotId) &&
                   string.Equals(snapshotId, resource.Path, StringComparison.OrdinalIgnoreCase);
        }

        if (DatabaseDocumentService.IsDatabaseDocumentPath(documentPath))
        {
            return false;
        }

        return resource.Kind switch
        {
            ResourceKind.File => IsSameFileSystemPath(resource.Path, documentPath),
            ResourceKind.Project => IsProjectResourceDocument(resource, documentPath),
            ResourceKind.Folder => IsPathInDirectory(documentPath, resource.Path),
            _ => false
        };
    }

    private static bool IsProjectResourceDocument(ScopedResource resource, string documentPath)
    {
        if (IsSameFileSystemPath(resource.Path, documentPath))
        {
            return true;
        }

        string? projectDirectory = Path.GetDirectoryName(resource.Path);
        return !string.IsNullOrWhiteSpace(projectDirectory) && IsPathInDirectory(documentPath, projectDirectory);
    }

    private ReferenceResourceContext CreateReferenceResourceContext(ScopedResource resource)
    {
        string key = !string.IsNullOrWhiteSpace(resource.ResourceId)
            ? resource.ResourceId
            : $"{resource.Kind}:{resource.Path}";

        return new ReferenceResourceContext(
            key,
            GetReferenceResourceDisplayName(resource),
            resource.Kind,
            resource.Path);
    }

    private string GetReferenceResourceDisplayName(ScopedResource resource)
    {
        if (resource.Kind != ResourceKind.DatabaseSnapshot)
        {
            return resource.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(resource.DisplayNameOverride))
        {
            return resource.DisplayNameOverride;
        }

        DatabaseMetadataSnapshot? snapshot = _databaseSnapshots.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, resource.Path, StringComparison.OrdinalIgnoreCase));
        return snapshot?.DisplayName ?? resource.DisplayName;
    }

    private static ReferenceResourceContext CreateFallbackReferenceResourceContext(string documentPath)
    {
        if (TryGetDatabaseSnapshotId(documentPath, out string snapshotId))
        {
            return new ReferenceResourceContext(
                $"database:{snapshotId}",
                "Database",
                ResourceKind.DatabaseSnapshot,
                snapshotId);
        }

        return new ReferenceResourceContext(
            $"file:{documentPath}",
            GetDisplayNameFromPath(documentPath),
            ResourceKind.File,
            documentPath);
    }

    private bool IsCSharpDocument(string documentPath)
    {
        return !DatabaseDocumentService.IsDatabaseDocumentPath(documentPath) &&
               string.Equals(
                   GetCodeLanguageForFile(documentPath),
                   CodeWindowSettings.CSharpLanguage,
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool IsVisualBasicDocument(string documentPath)
    {
        return !DatabaseDocumentService.IsDatabaseDocumentPath(documentPath) &&
               string.Equals(
                   GetCodeLanguageForFile(documentPath),
                   CodeWindowSettings.VisualBasicLanguage,
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSqlDocument(string documentPath)
    {
        return DatabaseDocumentService.IsDatabaseDocumentPath(documentPath) ||
               string.Equals(
                   GetCodeLanguageForFile(documentPath),
                   CodeWindowSettings.SqlServerLanguage,
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSpreadsheetDocument(string documentPath)
    {
        return !DatabaseDocumentService.IsDatabaseDocumentPath(documentPath) &&
               string.Equals(
                   GetCodeLanguageForFile(documentPath),
                   CodeWindowSettings.XlsLanguage,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<SqlReferenceToken> EnumerateSqlReferenceTokens(string text)
    {
        List<(int Start, int End)> ignoredSpans = GetSqlIgnoredSpans(text);
        var document = new TextDocument(text);

        foreach (Match match in SqlReferenceTokenPattern.Matches(text))
        {
            if (!match.Success || IsOffsetInIgnoredSpan(match.Index, ignoredSpans))
            {
                continue;
            }

            string token = NormalizeSqlReferenceToken(match.Value);
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            TextLocation location = document.GetLocation(match.Index);
            yield return new SqlReferenceToken(token, location.Line, location.Column, match.Index);
        }
    }

    private static List<(int Start, int End)> GetSqlIgnoredSpans(string text)
    {
        var spans = new List<(int Start, int End)>();

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                int start = i;
                i += 2;
                while (i < text.Length && text[i] is not '\r' and not '\n')
                {
                    i++;
                }

                spans.Add((start, i));
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < text.Length && (text[i] != '*' || text[i + 1] != '/'))
                {
                    i++;
                }

                i = Math.Min(text.Length, i + 2);
                spans.Add((start, i));
                i--;
                continue;
            }

            if (text[i] == '\'')
            {
                int start = i;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == '\'')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                spans.Add((start, i));
                i--;
            }
        }

        return spans;
    }

    private static bool IsOffsetInIgnoredSpan(int offset, IReadOnlyList<(int Start, int End)> ignoredSpans)
    {
        foreach ((int start, int end) in ignoredSpans)
        {
            if (offset < start)
            {
                return false;
            }

            if (offset >= start && offset < end)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeSqlReferenceToken(string token)
    {
        return Regex.Replace(token.Trim(), @"\s*\.\s*", ".")
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Trim('.', '`', '"', '\'');
    }

    private static bool IsSqlDefinitionToken(string text, SqlReferenceToken token, ReferenceEntityKind kind)
    {
        int prefixStart = Math.Max(0, token.StartOffset - 200);
        string prefix = text[prefixStart..token.StartOffset];
        Match match = SqlDefinitionPrefixPattern.Match(prefix);
        if (!match.Success)
        {
            return false;
        }

        return kind == GetSqlDefinitionKind(match.Groups[1].Value);
    }

    private static ReferenceEntityKind GetSqlDefinitionKind(string sqlObjectKeyword)
    {
        return sqlObjectKeyword.ToLowerInvariant() switch
        {
            "procedure" or "proc" => ReferenceEntityKind.StoredProcedure,
            "function" => ReferenceEntityKind.Function,
            "view" => ReferenceEntityKind.View,
            "table" => ReferenceEntityKind.Table,
            _ => ReferenceEntityKind.File
        };
    }

    private static bool IsCSharpReferenceIdentifierToken(SyntaxToken token)
    {
        if (!token.IsKind(SyntaxKind.IdentifierToken))
        {
            return false;
        }

        return token.Parent switch
        {
            BaseTypeDeclarationSyntax typeDeclaration when typeDeclaration.Identifier == token => false,
            DelegateDeclarationSyntax delegateDeclaration when delegateDeclaration.Identifier == token => false,
            MethodDeclarationSyntax methodDeclaration when methodDeclaration.Identifier == token => false,
            ConstructorDeclarationSyntax constructorDeclaration when constructorDeclaration.Identifier == token => false,
            DestructorDeclarationSyntax destructorDeclaration when destructorDeclaration.Identifier == token => false,
            LocalFunctionStatementSyntax localFunction when localFunction.Identifier == token => false,
            VariableDeclaratorSyntax variableDeclarator when variableDeclarator.Identifier == token => false,
            ParameterSyntax parameter when parameter.Identifier == token => false,
            PropertyDeclarationSyntax propertyDeclaration when propertyDeclaration.Identifier == token => false,
            EventDeclarationSyntax eventDeclaration when eventDeclaration.Identifier == token => false,
            EnumMemberDeclarationSyntax enumMember when enumMember.Identifier == token => false,
            TypeParameterSyntax typeParameter when typeParameter.Identifier == token => false,
            _ => true
        };
    }

    private static int? TryGetCSharpInvocationArgumentCount(SyntaxToken token)
    {
        if (token.Parent is not SimpleNameSyntax simpleName)
        {
            return null;
        }

        if (simpleName.Parent is InvocationExpressionSyntax directInvocation &&
            directInvocation.Expression == simpleName)
        {
            return directInvocation.ArgumentList.Arguments.Count;
        }

        if (simpleName.Parent is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name == simpleName &&
            memberAccess.Parent is InvocationExpressionSyntax memberInvocation &&
            memberInvocation.Expression == memberAccess)
        {
            return memberInvocation.ArgumentList.Arguments.Count;
        }

        if (simpleName.Parent is MemberBindingExpressionSyntax memberBinding &&
            memberBinding.Name == simpleName &&
            memberBinding.Parent is InvocationExpressionSyntax conditionalInvocation &&
            conditionalInvocation.Expression == memberBinding)
        {
            return conditionalInvocation.ArgumentList.Arguments.Count;
        }

        return null;
    }

    private static bool IsVisualBasicReferenceIdentifierToken(SyntaxToken token)
    {
        if (!token.IsKind(VisualBasicSyntaxKind.IdentifierToken))
        {
            return false;
        }

        return token.Parent switch
        {
            Microsoft.CodeAnalysis.VisualBasic.Syntax.ClassStatementSyntax statement when statement.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.InterfaceStatementSyntax statement when statement.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.StructureStatementSyntax statement when statement.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.EnumStatementSyntax statement when statement.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.ModuleStatementSyntax statement when statement.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodStatementSyntax statement when statement.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.PropertyStatementSyntax statement when statement.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.EventStatementSyntax statement when statement.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.EnumMemberDeclarationSyntax statement when statement.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.ModifiedIdentifierSyntax modifiedIdentifier when modifiedIdentifier.Identifier == token => false,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.TypeParameterSyntax typeParameter when typeParameter.Identifier == token => false,
            _ => true
        };
    }

    private static int? TryGetVisualBasicInvocationArgumentCount(SyntaxToken token)
    {
        if (token.Parent is not Microsoft.CodeAnalysis.VisualBasic.Syntax.SimpleNameSyntax simpleName)
        {
            return null;
        }

        if (simpleName.Parent is Microsoft.CodeAnalysis.VisualBasic.Syntax.InvocationExpressionSyntax directInvocation &&
            directInvocation.Expression == simpleName)
        {
            return directInvocation.ArgumentList?.Arguments.Count;
        }

        if (simpleName.Parent is Microsoft.CodeAnalysis.VisualBasic.Syntax.MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name == simpleName &&
            memberAccess.Parent is Microsoft.CodeAnalysis.VisualBasic.Syntax.InvocationExpressionSyntax memberInvocation &&
            memberInvocation.Expression == memberAccess)
        {
            return memberInvocation.ArgumentList?.Arguments.Count;
        }

        return null;
    }

    private static string FormatReferenceOccurrenceHeader(CodeReferenceOccurrence occurrence)
    {
        string parameterLabel = occurrence.Target.ParameterCount.HasValue
            ? $" ({occurrence.Target.ParameterCount.Value} params)"
            : string.Empty;
        return $"{occurrence.Token}{parameterLabel} - line {occurrence.LineNumber}";
    }

    private static string GetReferenceKindGroupHeader(ReferenceEntityKind kind, string sourceLanguage)
    {
        return string.Equals(sourceLanguage, CodeWindowSettings.VisualBasicLanguage, StringComparison.OrdinalIgnoreCase) &&
               kind == ReferenceEntityKind.Method
            ? "Subs and Functions"
            : GetReferenceKindGroupHeader(kind);
    }

    private static string GetReferenceKindGroupHeader(ReferenceEntityKind kind)
    {
        return kind switch
        {
            ReferenceEntityKind.File => "Files",
            ReferenceEntityKind.Class => "Classes",
            ReferenceEntityKind.Interface => "Interfaces",
            ReferenceEntityKind.Struct => "Structs",
            ReferenceEntityKind.Enum => "Enums",
            ReferenceEntityKind.Delegate => "Delegates",
            ReferenceEntityKind.Method => "Methods",
            ReferenceEntityKind.Function => "Functions",
            ReferenceEntityKind.StoredProcedure => "Stored Procedures",
            ReferenceEntityKind.View => "Views",
            ReferenceEntityKind.Trigger => "Triggers",
            ReferenceEntityKind.Table => "Tables",
            ReferenceEntityKind.Field => "Fields",
            _ => kind.ToString()
        };
    }

    private static int GetReferenceKindMenuRank(ReferenceEntityKind kind)
    {
        return kind switch
        {
            ReferenceEntityKind.Class => 0,
            ReferenceEntityKind.Interface => 1,
            ReferenceEntityKind.Struct => 2,
            ReferenceEntityKind.Enum => 3,
            ReferenceEntityKind.Delegate => 4,
            ReferenceEntityKind.Method => 5,
            ReferenceEntityKind.Function => 6,
            ReferenceEntityKind.StoredProcedure => 7,
            ReferenceEntityKind.View => 8,
            ReferenceEntityKind.Table => 9,
            ReferenceEntityKind.Field => 10,
            ReferenceEntityKind.Trigger => 11,
            ReferenceEntityKind.File => 12,
            _ => 99
        };
    }

    private static string GetResourceKindDisplay(ResourceKind kind)
    {
        return kind switch
        {
            ResourceKind.DatabaseSnapshot => "Database",
            _ => kind.ToString()
        };
    }

    private static int GetReferenceResourceSpecificity(ScopedResource resource)
    {
        int pathLength = resource.Path.Length;
        return resource.Kind switch
        {
            ResourceKind.File => 300_000 + pathLength,
            ResourceKind.DatabaseSnapshot => 300_000 + pathLength,
            ResourceKind.Project => 200_000 + pathLength,
            ResourceKind.Folder => 100_000 + pathLength,
            _ => pathLength
        };
    }

    private static bool IsSameFileSystemPath(string path, string otherPath)
    {
        try
        {
            return string.Equals(
                NormalizeFileSystemPath(path),
                NormalizeFileSystemPath(otherPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(path, otherPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsPathInDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            string normalizedPath = NormalizeFileSystemPath(path);
            string normalizedDirectory = NormalizeFileSystemPath(directory);
            return string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(
                       normalizedDirectory + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeFileSystemPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryGetDatabaseSnapshotId(string documentPath, out string snapshotId)
    {
        const string databaseDocumentPrefix = "db://";
        snapshotId = string.Empty;

        if (!documentPath.StartsWith(databaseDocumentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = documentPath[databaseDocumentPrefix.Length..];
        int slashIndex = rest.IndexOf('/', StringComparison.Ordinal);
        snapshotId = slashIndex >= 0 ? rest[..slashIndex] : rest;
        return !string.IsNullOrWhiteSpace(snapshotId);
    }

    private static void InsertDynamicReferencesMenu(ContextMenu contextMenu, params MenuItem[] menuItems)
    {
        if (menuItems.Length == 0)
        {
            return;
        }

        var separator = new Separator { Tag = DynamicReferencesContextMenuTag };
        for (int i = 0; i < menuItems.Length; i++)
        {
            menuItems[i].Tag = DynamicReferencesContextMenuTag;
            contextMenu.Items.Insert(i, menuItems[i]);
        }

        contextMenu.Items.Insert(menuItems.Length, separator);
    }

    private static void RemoveDynamicReferencesMenuItems(ContextMenu contextMenu)
    {
        List<object> itemsToRemove = contextMenu.Items
            .Cast<object>()
            .Where(item => item is FrameworkElement frameworkElement &&
                           string.Equals(frameworkElement.Tag as string, DynamicReferencesContextMenuTag, StringComparison.Ordinal))
            .ToList();

        foreach (object item in itemsToRemove)
        {
            contextMenu.Items.Remove(item);
        }
    }

    private static void AddDisabledMenuItem(ItemsControl parent, string header)
    {
        parent.Items.Add(new MenuItem
        {
            Header = header,
            IsEnabled = false
        });
    }

    private async void FloatingWindow_ReferenceNavigationRequested(object? sender, ReferenceNavigationRequestedEventArgs e)
    {
        if (sender is not FloatingCodeWindow sourceWindow)
        {
            return;
        }

        IReadOnlyList<ReferenceEntity> references = SortReferencesForSource(
            _referenceIndex.Resolve(e.Token, e.ArgumentCount),
            sourceWindow.State.FilePath);
        if (references.Count == 0)
        {
            StatusText = $"No indexed definition found for '{e.Token}'.";
            return;
        }

        ReferenceEntity? reference = references.Count == 1
            ? references[0]
            : PickReference(e.Token, references, sourceWindow.State.FilePath);

        if (reference == null)
        {
            return;
        }

        if (IsSameFile(reference.FilePath, sourceWindow.State.FilePath))
        {
            BringToFront(sourceWindow);
            ScrollWindowToPositionAndReveal(sourceWindow, reference.LineNumber, reference.ColumnNumber, selectLine: true);
            StatusText = $"Navigated to {reference.Kind} '{GetReferenceDisplayName(reference)}' in the current file.";
            return;
        }

        await OpenFileAsync(reference.FilePath, targetReference: reference, sourceWindow: sourceWindow);
        StatusText = $"Navigated to {reference.Kind} '{GetReferenceDisplayName(reference)}' in {GetDisplayNameFromPath(reference.FilePath)}.";
    }

    private async void FloatingWindow_ReferencePreviewRequested(object? sender, ReferenceNavigationRequestedEventArgs e)
    {
        string sourceFilePath = sender is FloatingCodeWindow sourceWindow
            ? sourceWindow.State.FilePath
            : string.Empty;
        await PreviewReferenceAsync(e, sourceFilePath);
    }

    private async Task PreviewReferenceAsync(ReferenceNavigationRequestedEventArgs request, string sourceFilePath)
    {
        IReadOnlyList<ReferenceEntity> references = SortReferencesForSource(
            _referenceIndex.Resolve(request.Token, request.ArgumentCount),
            sourceFilePath);
        if (references.Count == 0)
        {
            return;
        }

        ReferenceEntity reference = references[0];
        int requestVersion = ++_previewRequestVersion;

        if (!TryGetDocumentContent(reference.FilePath, out string content, out string syntaxPath, out string displayName, out string errorMessage))
        {
            if (requestVersion == _previewRequestVersion)
            {
                ClearReferencePreview($"Could not preview {GetDisplayNameFromPath(reference.FilePath)}.");
                StatusText = errorMessage;
            }

            return;
        }

        if (requestVersion != _previewRequestVersion)
        {
            return;
        }

        ShowReferencePreview(reference, content, references.Count, syntaxPath, displayName);
    }

    private void ShowReferencePreview(ReferenceEntity reference, string content, int matchCount, string syntaxPath, string displayName)
    {
        _previewFilePath = reference.FilePath;
        PreviewEditor.Text = content;
        PreviewEditor.SyntaxHighlighting = _syntaxHighlightingService.GetDefinition(
            syntaxPath,
            GetCodeLanguageForFile(syntaxPath));
        ApplySettingsToPreview();

        PreviewHeaderText.Text = $"{reference.Kind}: {GetReferenceDisplayName(reference)}  ({displayName}:{reference.LineNumber})";
        PreviewEditor.Visibility = Visibility.Visible;
        PreviewEmptyText.Visibility = Visibility.Collapsed;
        ExplorerDetailTabs.SelectedItem = PreviewTabItem;

        ScrollPreviewToReference(reference);

        StatusText = matchCount == 1
            ? $"Previewing {reference.Kind} '{GetReferenceDisplayName(reference)}'."
            : $"Previewing first of {matchCount} matches for '{reference.Name}'.";
    }

    private bool TryGetDocumentContent(
        string documentPath,
        out string content,
        out string syntaxPath,
        out string displayName,
        out string errorMessage)
    {
        content = string.Empty;
        syntaxPath = documentPath;
        displayName = GetDisplayNameFromPath(documentPath);
        errorMessage = string.Empty;

        if (DatabaseDocumentService.IsDatabaseDocumentPath(documentPath))
        {
            bool found = _databaseDocumentService.TryGetDocument(
                documentPath,
                _databaseSnapshots,
                out content,
                out syntaxPath,
                out displayName);
            errorMessage = found ? string.Empty : "Could not load database metadata document.";
            return found;
        }

        try
        {
            content = File.ReadAllText(documentPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errorMessage = $"Could not read {displayName}: {ex.Message}";
            return false;
        }
    }

    private void ClearReferencePreview(string message)
    {
        _previewRequestVersion++;
        _previewFilePath = null;
        RemovePreviewReferenceHighlightColorizer();
        PreviewEditor.Text = string.Empty;
        PreviewEditor.Visibility = Visibility.Collapsed;
        PreviewEmptyText.Text = message;
        PreviewEmptyText.Visibility = Visibility.Visible;
        PreviewHeaderText.Text = "No reference selected";
    }

    private void ScrollPreviewToReference(ReferenceEntity reference)
    {
        _ = Dispatcher.BeginInvoke(new Action(() => ApplyPreviewScrollToReference(reference)), DispatcherPriority.ContextIdle);
    }

    private void ApplyPreviewScrollToReference(ReferenceEntity reference)
    {
        if (PreviewEditor.Document == null)
        {
            return;
        }

        TextDocument document = PreviewEditor.Document;
        int startLineNumber = Math.Clamp(reference.LineNumber, 1, Math.Max(1, document.LineCount));
        DocumentLine startLine = document.GetLineByNumber(startLineNumber);
        int targetColumn = Math.Clamp(reference.ColumnNumber, 1, startLine.Length + 1);
        int targetOffset = startLine.Offset + targetColumn - 1;

        PreviewEditor.UpdateLayout();
        PreviewEditor.TextArea.Caret.Offset = targetOffset;
        PreviewEditor.TextArea.TextView.EnsureVisualLines();
        PreviewEditor.ScrollTo(startLineNumber, targetColumn, VisualYPosition.LineTop, 6, 0);
        PreviewEditor.Select(targetOffset, 0);
    }

    private void PreviewEditor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        double multiplier = e.Delta > 0 ? PreviewFontZoomStep : 1 / PreviewFontZoomStep;
        PreviewEditor.FontSize = Math.Clamp(
            PreviewEditor.FontSize * multiplier,
            MinimumPreviewFontSize,
            MaximumPreviewFontSize);
        e.Handled = true;
    }

    private static string GetReferenceDisplayName(ReferenceEntity reference)
    {
        return string.IsNullOrWhiteSpace(reference.QualifiedName)
            ? reference.Name
            : reference.QualifiedName;
    }

    private void FloatingWindow_CursorPositionChanged(object? sender, CursorPositionChangedEventArgs e)
    {
        TrackCursorPosition(e.FilePath, e.LineNumber, e.ColumnNumber);
    }

    private void TrackCursorPosition(string filePath, int lineNumber, int columnNumber)
    {
        _navigationHistoryService.Record(filePath, lineNumber, columnNumber);
        NotifyNavigationHistoryStateChanged();
    }

    private ReferenceEntity? PickReference(string token, IReadOnlyList<ReferenceEntity> references, string sourceFilePath)
    {
        var picker = new ReferencePickerWindow(token, references, sourceFilePath)
        {
            Owner = this
        };

        return picker.ShowDialog() == true ? picker.SelectedReference : null;
    }

    private static IReadOnlyList<ReferenceEntity> SortReferencesForSource(
        IReadOnlyList<ReferenceEntity> references,
        string sourceFilePath)
    {
        if (references.Count <= 1 || string.IsNullOrWhiteSpace(sourceFilePath))
        {
            return references;
        }

        return references
            .OrderByDescending(reference => IsSameFile(reference.FilePath, sourceFilePath))
            .ToList();
    }

    private static bool IsSameFile(string filePath, string otherFilePath)
    {
        return !string.IsNullOrWhiteSpace(filePath) &&
               !string.IsNullOrWhiteSpace(otherFilePath) &&
               string.Equals(filePath, otherFilePath, StringComparison.OrdinalIgnoreCase);
    }

    private void FloatingWindow_BoundsChanged(object? sender, EventArgs e)
    {
        if (sender is FloatingCodeWindow window)
        {
            double left = Canvas.GetLeft(window);
            double top = Canvas.GetTop(window);
            if (!double.IsNaN(left))
            {
                window.State.Left = left;
            }

            if (!double.IsNaN(top))
            {
                window.State.Top = top;
            }

            if (!double.IsNaN(window.Width) && window.Width > 0)
            {
                window.State.Width = window.Width;
            }

            if (!double.IsNaN(window.Height) && window.Height > 0)
            {
                window.State.Height = window.Height;
            }

            window.State.FontSize = Math.Max(1, window.State.FontSize);
        }
        else if (sender is FloatingSpreadsheetWindow spreadsheetWindow)
        {
            double left = Canvas.GetLeft(spreadsheetWindow);
            double top = Canvas.GetTop(spreadsheetWindow);
            if (!double.IsNaN(left))
            {
                spreadsheetWindow.State.Left = left;
            }

            if (!double.IsNaN(top))
            {
                spreadsheetWindow.State.Top = top;
            }

            if (!double.IsNaN(spreadsheetWindow.Width) && spreadsheetWindow.Width > 0)
            {
                spreadsheetWindow.State.Width = spreadsheetWindow.Width;
            }

            if (!double.IsNaN(spreadsheetWindow.Height) && spreadsheetWindow.Height > 0)
            {
                spreadsheetWindow.State.Height = spreadsheetWindow.Height;
            }

            spreadsheetWindow.State.FontSize = Math.Max(1, spreadsheetWindow.State.FontSize);
        }
    }

    private void WorkspaceCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != WorkspaceCanvas)
        {
            return;
        }

        SetActiveCodeWindow(null);
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

        if (IsMouseEventOverActiveCodeWindow(e))
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

    private void DiagramCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != DiagramCanvas)
        {
            return;
        }

        if (_isDiagramLocked)
        {
            SelectDiagramObject(null);
            _isDiagramCanvasPanning = true;
            _diagramPanStartPoint = e.GetPosition(DiagramScrollViewer);
            _diagramPanStartHorizontalOffset = DiagramScrollViewer.HorizontalOffset;
            _diagramPanStartVerticalOffset = DiagramScrollViewer.VerticalOffset;

            DiagramCanvas.CaptureMouse();
            DiagramCanvas.Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        if (TryPlaceWorkflowItemMarker(e.GetPosition(DiagramCanvas)))
        {
            e.Handled = true;
            return;
        }

        if (TryPlaceDiagramPortal(e.GetPosition(DiagramCanvas)))
        {
            e.Handled = true;
            return;
        }

        DiagramImageDefinition? selectedImage = GetSelectedDiagramImageDefinition();
        if (selectedImage != null)
        {
            StartDiagramImageDraw(selectedImage, e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        if (IsDiagramLineToolSelected())
        {
            StartDiagramLineDraw(e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        if (IsDiagramLabelToolSelected())
        {
            PlaceDiagramLabel(e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        DiagramShapeKind? selectedShapeKind = GetSelectedDiagramShapeKind();
        if (selectedShapeKind != null)
        {
            StartDiagramShapeDraw(selectedShapeKind.Value, e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        SelectDiagramObject(null);
        _isDiagramCanvasPanning = true;
        _diagramPanStartPoint = e.GetPosition(DiagramScrollViewer);
        _diagramPanStartHorizontalOffset = DiagramScrollViewer.HorizontalOffset;
        _diagramPanStartVerticalOffset = DiagramScrollViewer.VerticalOffset;

        DiagramCanvas.CaptureMouse();
        DiagramCanvas.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void DiagramCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDrawingDiagramLine && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateDiagramLineDraw(e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        if (_isDrawingDiagramImage && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateDiagramImageDraw(e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        if (_isDrawingDiagramShape && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateDiagramShapeDraw(e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        if (!_isDiagramCanvasPanning || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point currentPoint = e.GetPosition(DiagramScrollViewer);
        Vector delta = currentPoint - _diagramPanStartPoint;

        DiagramScrollViewer.ScrollToHorizontalOffset(_diagramPanStartHorizontalOffset - delta.X);
        DiagramScrollViewer.ScrollToVerticalOffset(_diagramPanStartVerticalOffset - delta.Y);
        e.Handled = true;
    }

    private void DiagramCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDrawingDiagramLine)
        {
            FinishDiagramLineDraw(e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        if (_isDrawingDiagramImage)
        {
            FinishDiagramImageDraw(e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        if (_isDrawingDiagramShape)
        {
            FinishDiagramShapeDraw(e.GetPosition(DiagramCanvas));
            e.Handled = true;
            return;
        }

        if (!_isDiagramCanvasPanning)
        {
            return;
        }

        _isDiagramCanvasPanning = false;
        DiagramCanvas.ReleaseMouseCapture();
        DiagramCanvas.Cursor = null;
        e.Handled = true;
    }

    private void DiagramCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != DiagramCanvas)
        {
            return;
        }

        Point canvasPoint = e.GetPosition(DiagramCanvas);
        bool lockedInPlacementOrEdit = LockInActiveDiagramPlacementOrEdits(canvasPoint);
        bool clearedTools = HasActiveDiagramDrawingTool();
        if (clearedTools)
        {
            ClearDiagramDrawingTools();
            StatusText = lockedInPlacementOrEdit
                ? "Diagram placement locked in and drawing tools cleared."
                : "Diagram drawing tools cleared.";
        }
        else if (lockedInPlacementOrEdit)
        {
            StatusText = "Diagram object edit locked in.";
        }

        DiagramLineControl? fixedLine = FindFixedLineAt(canvasPoint);
        if (fixedLine?.ContextMenu == null)
        {
            e.Handled = clearedTools || lockedInPlacementOrEdit;
            return;
        }

        fixedLine.ContextMenu.PlacementTarget = DiagramCanvas;
        fixedLine.ContextMenu.Placement = PlacementMode.MousePoint;
        fixedLine.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void DiagramCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        Point cursorInViewport = e.GetPosition(DiagramScrollViewer);
        Point cursorInCanvas = e.GetPosition(DiagramCanvas);
        double multiplier = e.Delta > 0 ? CanvasZoomStep : 1 / CanvasZoomStep;
        _diagramCanvasZoom = NormalizeCanvasZoom(_diagramCanvasZoom * multiplier);

        ApplyDiagramCanvasZoom();
        DiagramScrollViewer.UpdateLayout();

        DiagramScrollViewer.ScrollToHorizontalOffset((cursorInCanvas.X * _diagramCanvasZoom) - cursorInViewport.X);
        DiagramScrollViewer.ScrollToVerticalOffset((cursorInCanvas.Y * _diagramCanvasZoom) - cursorInViewport.Y);

        StatusText = $"Diagram zoom: {_diagramCanvasZoom:P0}";
        e.Handled = true;
    }

    private DiagramLineControl? FindFixedLineAt(Point canvasPoint)
    {
        return DiagramCanvas.Children
            .OfType<DiagramLineControl>()
            .Where(line => !line.IsLoose && line.ContainsCanvasPoint(canvasPoint))
            .OrderByDescending(Panel.GetZIndex)
            .FirstOrDefault();
    }

    private bool LockInActiveDiagramPlacementOrEdits(Point canvasPoint)
    {
        bool lockedIn = false;

        if (_isDrawingDiagramLine)
        {
            FinishDiagramLineDraw(canvasPoint);
            lockedIn = true;
        }
        else if (_isDrawingDiagramImage)
        {
            FinishDiagramImageDraw(canvasPoint, beginEdit: false);
            lockedIn = true;
        }
        else if (_isDrawingDiagramShape)
        {
            FinishDiagramShapeDraw(canvasPoint, beginEdit: false);
            lockedIn = true;
        }

        if (CommitDiagramObjectTextEdits())
        {
            lockedIn = true;
        }

        Dispatcher.BeginInvoke(
            () => CommitDiagramObjectTextEdits(),
            DispatcherPriority.Background);

        return lockedIn;
    }

    private bool CommitDiagramObjectTextEdits()
    {
        if (DiagramCanvas == null)
        {
            return false;
        }

        bool committed = false;

        foreach (FrameworkElement diagramObject in DiagramCanvas.Children.OfType<FrameworkElement>())
        {
            switch (diagramObject)
            {
                case DiagramShapeControl { IsLabelEditing: true } shape:
                    shape.CommitLabelEdit();
                    committed = true;
                    break;

                case DiagramImageControl { IsLabelEditing: true } image:
                    image.CommitLabelEdit();
                    committed = true;
                    break;

                case DiagramLabelControl { IsLabelEditing: true } label:
                    label.CommitLabelEdit();
                    committed = true;
                    break;
            }
        }

        if (committed)
        {
            Keyboard.ClearFocus();
            Focus();
            SelectDiagramObject(null);
        }

        return committed;
    }

    private bool HasActiveDiagramDrawingTool()
    {
        return DiagramRectangleToolButton?.IsChecked == true ||
            DiagramEllipseToolButton?.IsChecked == true ||
            DiagramImageToolButton?.IsChecked == true ||
            DiagramLineToolButton?.IsChecked == true ||
            DiagramLabelToolButton?.IsChecked == true ||
            DiagramPortalToolButton?.IsChecked == true;
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (await TryHandleGlobalKeyboardShortcutAsync(e))
        {
            return;
        }

        if (!IsDiagramViewCommandTarget())
        {
            return;
        }

        bool isTextInput = IsTextInputElement(e.OriginalSource as DependencyObject);

        switch (e.Key)
        {
            case Key.C:
                if (isTextInput)
                {
                    return;
                }

                CopySelectedDiagramObject();
                e.Handled = true;
                break;

            case Key.X:
                if (isTextInput)
                {
                    return;
                }

                CutSelectedDiagramObject();
                e.Handled = true;
                break;

            case Key.V:
                if (isTextInput)
                {
                    return;
                }

                PasteDiagramClipboardContent();
                e.Handled = true;
                break;

            case Key.Z:
                UndoDiagramAction();
                e.Handled = true;
                break;
        }
    }

    private async Task<bool> TryHandleGlobalKeyboardShortcutAsync(KeyEventArgs e)
    {
        _appSettings.KeyboardShortcuts ??= new KeyboardShortcutSettings();
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        DependencyObject? originalSource = e.OriginalSource as DependencyObject;
        bool isTextInput = IsTextInputElement(originalSource);

        if (!isTextInput &&
            _appSettings.KeyboardShortcuts.EnableCodeTabCtrlASNavigation &&
            TryHandleCodeTabNavigationShortcut(key, originalSource))
        {
            e.Handled = true;
            return true;
        }

        if (!isTextInput &&
            _appSettings.KeyboardShortcuts.EnableDiagramCtrlQSidebarToggle &&
            key == Key.Q &&
            IsDiagramViewCommandTarget())
        {
            e.Handled = true;
            ToggleDiagramSidebar();
            return true;
        }

        if (!isTextInput &&
            _appSettings.KeyboardShortcuts.EnableDiagramCtrlWWorkflowSidebar &&
            key == Key.W &&
            IsDiagramViewCommandTarget())
        {
            e.Handled = true;
            OpenDiagramSidebarOnWorkflowTab();
            return true;
        }

        if (_appSettings.KeyboardShortcuts.EnableCodeViewCtrlPlusMinusNavigation)
        {
            if (IsBackShortcutKey(key))
            {
                e.Handled = true;
                await NavigateHistoryAsync(_navigationHistoryService.MoveBack(), "Back");
                return true;
            }

            if (IsForwardShortcutKey(key))
            {
                e.Handled = true;
                await NavigateHistoryAsync(_navigationHistoryService.MoveForward(), "Forward");
                return true;
            }
        }

        if (_appSettings.KeyboardShortcuts.EnableCtrlNumberViewSwitching)
        {
            if (key is Key.D1 or Key.NumPad1)
            {
                e.Handled = true;
                ShowSingleWorkspaceView(WorkspaceViewKind.Code);
                StatusText = "Code View selected.";
                return true;
            }

            if (key is Key.D2 or Key.NumPad2)
            {
                e.Handled = true;
                ShowSingleWorkspaceView(WorkspaceViewKind.Diagram);
                StatusText = "Diagram View selected.";
                return true;
            }
        }

        return false;
    }

    private bool TryHandleCodeTabNavigationShortcut(Key key, DependencyObject? originalSource)
    {
        if (key == Key.A)
        {
            return TryNavigateCodeDocumentTab(-1, originalSource);
        }

        if (key == Key.S)
        {
            return TryNavigateCodeDocumentTab(1, originalSource);
        }

        return false;
    }

    private bool TryNavigateCodeDocumentTab(int direction, DependencyObject? originalSource)
    {
        if (!IsCodeTabModeCommandTarget(originalSource) ||
            CodeDocumentsTabControl.Items.Count == 0)
        {
            return false;
        }

        int currentIndex = CodeDocumentsTabControl.SelectedIndex;
        if (currentIndex < 0)
        {
            currentIndex = direction >= 0 ? -1 : 0;
        }

        int nextIndex = (currentIndex + direction + CodeDocumentsTabControl.Items.Count) % CodeDocumentsTabControl.Items.Count;
        CodeDocumentsTabControl.SelectedIndex = nextIndex;

        if (CodeDocumentsTabControl.SelectedItem is TabItem { Tag: string filePath })
        {
            StatusText = $"Selected tab {Path.GetFileName(filePath)}.";
        }

        return true;
    }

    private bool IsCodeTabModeCommandTarget(DependencyObject? originalSource)
    {
        if (_codeViewMode != CodeViewMode.Tabs ||
            CodeViewHost.Visibility != Visibility.Visible ||
            CodeDocumentsTabControl.Visibility != Visibility.Visible)
        {
            return false;
        }

        return _activeWorkspaceView == WorkspaceViewKind.Code ||
               originalSource != null && IsDescendantOf(originalSource, CodeViewHost);
    }

    private void ToggleDiagramSidebar()
    {
        SetDiagramSidebarOpen(!_isDiagramSidebarOpen);
        StatusText = _isDiagramSidebarOpen
            ? "Diagram sidebar opened."
            : "Diagram sidebar closed.";
    }

    private void OpenDiagramSidebarOnWorkflowTab()
    {
        SetDiagramSidebarOpen(true);
        DiagramSidebarTabs.SelectedIndex = 1;
        StatusText = "Diagram sidebar opened on Workflows.";
    }

    private static bool IsBackShortcutKey(Key key)
    {
        return key is Key.OemMinus or Key.Subtract;
    }

    private static bool IsForwardShortcutKey(Key key)
    {
        return key is Key.OemPlus or Key.Add;
    }

    private bool IsDiagramViewCommandTarget()
    {
        return DiagramViewHost.Visibility == Visibility.Visible &&
               _activeWorkspaceView == WorkspaceViewKind.Diagram;
    }

    private static bool IsTextInputElement(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is TextBoxBase or PasswordBox)
            {
                return true;
            }

            try
            {
                source = VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
            }
            catch (InvalidOperationException)
            {
                source = LogicalTreeHelper.GetParent(source);
            }
        }

        return false;
    }

    private DiagramShapeKind? GetSelectedDiagramShapeKind()
    {
        if (DiagramRectangleToolButton?.IsChecked == true)
        {
            return DiagramShapeKind.Rectangle;
        }

        if (DiagramEllipseToolButton?.IsChecked == true)
        {
            return DiagramShapeKind.Ellipse;
        }

        return null;
    }

    private DiagramImageDefinition? GetSelectedDiagramImageDefinition()
    {
        if (DiagramImageToolButton?.IsChecked != true)
        {
            return null;
        }

        return FindDiagramImageDefinition(_selectedDiagramImageId);
    }

    private bool IsDiagramLineToolSelected()
    {
        return DiagramLineToolButton?.IsChecked == true;
    }

    private bool IsDiagramLabelToolSelected()
    {
        return DiagramLabelToolButton?.IsChecked == true;
    }

    private bool TryPlaceDiagramPortal(Point canvasPoint)
    {
        if (TryBlockDiagramObjectEditWhenLocked("place portals"))
        {
            ClearPortalToolSelection();
            return true;
        }

        if (DiagramPortalToolButton?.IsChecked != true &&
            _pendingPortalPairPlacement == null)
        {
            return false;
        }

        string? portalName = _pendingPortalName;
        if (string.IsNullOrWhiteSpace(portalName))
        {
            portalName = PromptForPortalName("Portal", "Portal name");
            if (string.IsNullOrWhiteSpace(portalName))
            {
                ClearPortalToolSelection();
                return true;
            }
        }

        PendingPortalPairPlacement? pendingPair = _pendingPortalPairPlacement;
        var portal = new DiagramPortalControl(portalName);
        AttachDiagramPortalHandlers(portal);
        portal.SetCanvasBounds(canvasPoint.X - 15, canvasPoint.Y - 15, 30, 30);

        ApplyDefaultDiagramZIndex(portal);
        DiagramCanvas.Children.Add(portal);
        SelectDiagramObject(portal);
        PushDiagramUndo(DiagramUndoActionKind.Added, before: null, after: CreateDiagramObjectSnapshot(portal));

        ClearPortalToolSelection();

        if (pendingPair != null)
        {
            _ = CompletePendingPortalPairAsync(pendingPair, portal);
        }
        else
        {
            StatusText = $"Added unpaired portal '{portalName}'.";
        }

        return true;
    }

    private void ClearPortalToolSelection()
    {
        _pendingPortalName = null;
        _pendingPortalPairPlacement = null;
        if (DiagramPortalToolButton == null)
        {
            return;
        }

        _isUpdatingDiagramToolToggles = true;
        DiagramPortalToolButton.IsChecked = false;
        _isUpdatingDiagramToolToggles = false;
    }

    private void ArmPortalPlacement(string portalName, PendingPortalPairPlacement? pendingPair)
    {
        if (TryBlockDiagramObjectEditWhenLocked("place paired portals"))
        {
            return;
        }

        if (DiagramRectangleToolButton == null ||
            DiagramEllipseToolButton == null ||
            DiagramImageToolButton == null ||
            DiagramLineToolButton == null ||
            DiagramLabelToolButton == null ||
            DiagramPortalToolButton == null)
        {
            return;
        }

        _pendingPortalName = portalName;
        _pendingPortalPairPlacement = pendingPair;
        _selectedDiagramImageId = null;

        _isUpdatingDiagramToolToggles = true;
        DiagramRectangleToolButton.IsChecked = false;
        DiagramEllipseToolButton.IsChecked = false;
        DiagramImageToolButton.IsChecked = false;
        DiagramLineToolButton.IsChecked = false;
        DiagramLabelToolButton.IsChecked = false;
        DiagramPortalToolButton.IsChecked = true;
        _isUpdatingDiagramToolToggles = false;
        SetWorkflowAddItemsMode(false);
    }

    private string? PromptForPortalName(string initialName, string prompt, string? diagramId = null, string? excludePortalId = null)
    {
        string suggestedName = initialName;
        while (true)
        {
            string? name = PromptForDiagramName(suggestedName, prompt, "Portal");
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (!PortalNameExists(name, diagramId ?? _activeDiagramId, excludePortalId))
            {
                return name;
            }

            MessageBox.Show(
                this,
                $"A portal named '{name}' already exists in this diagram. Portal addresses use the portal name and diagram name, so names need to be unique per diagram.",
                "Portal Name",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            suggestedName = name;
        }
    }

    private bool PortalNameExists(string portalName, string? diagramId, string? excludePortalId = null)
    {
        if (string.IsNullOrWhiteSpace(portalName))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(diagramId) ||
            string.Equals(diagramId, _activeDiagramId, StringComparison.OrdinalIgnoreCase))
        {
            return DiagramCanvas.Children
                .OfType<DiagramPortalControl>()
                .Any(portal =>
                    !string.Equals(portal.DiagramObjectId, excludePortalId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(portal.PortalName, portalName, StringComparison.OrdinalIgnoreCase));
        }

        return _diagramLibrary.Find(diagramId)?.Objects
            .Where(snapshot => snapshot.ObjectType == DiagramObjectType.Portal)
            .Any(snapshot =>
                !string.Equals(snapshot.Id, excludePortalId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.PortalName, portalName, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private async Task CompletePendingPortalPairAsync(PendingPortalPairPlacement pendingPair, DiagramPortalControl targetPortal)
    {
        if (string.IsNullOrWhiteSpace(_activeDiagramId))
        {
            StatusText = "Save the target diagram before pairing portals.";
            return;
        }

        if (await PairPortalsAsync(
                pendingPair.SourceDiagramId,
                pendingPair.SourcePortalObjectId,
                _activeDiagramId,
                targetPortal.DiagramObjectId))
        {
            ScrollDiagramObjectIntoView(targetPortal.DiagramObjectId);
            StatusText = $"Paired portal '{targetPortal.PortalName}'.";
        }
    }

    private async void DiagramPortal_OpenRequested(object? sender, EventArgs e)
    {
        if (sender is not DiagramPortalControl portal)
        {
            return;
        }

        SelectDiagramObject(portal);
        if (portal.IsPaired)
        {
            await NavigateToPairedPortalAsync(portal);
            return;
        }

        if (TryBlockDiagramObjectEditWhenLocked("pair portals"))
        {
            return;
        }

        await BeginPortalPairingAsync(portal);
    }

    private async Task BeginPortalPairingAsync(DiagramPortalControl sourcePortal)
    {
        if (TryBlockDiagramObjectEditWhenLocked("pair portals"))
        {
            return;
        }

        if (_activeScope == null)
        {
            StatusText = "Open a scope before pairing portals.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_activeDiagramId))
        {
            MessageBox.Show(
                this,
                "Save the current diagram before pairing portals. Portal links need a saved diagram identity.",
                "Pair Portal",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DiagramDocument? targetDiagram = PromptForPortalTargetDiagram();
        if (targetDiagram == null)
        {
            return;
        }

        string sourceDiagramId = _activeDiagramId;
        string sourcePortalId = sourcePortal.DiagramObjectId;

        if (!await PersistActiveDiagramSilentlyAsync())
        {
            return;
        }

        bool targetIsCurrentDiagram = string.Equals(targetDiagram.DiagramId, sourceDiagramId, StringComparison.OrdinalIgnoreCase);
        if (!targetIsCurrentDiagram)
        {
            LoadDiagram(targetDiagram);
            SetActiveWorkspaceView(WorkspaceViewKind.Diagram);
        }

        List<PortalPickerItem> unpairedPortals = GetCurrentUnpairedPortalPickerItems(
            targetIsCurrentDiagram ? sourcePortalId : null);

        var choiceDialog = new PortalTargetChoiceWindow(CurrentDiagramName, unpairedPortals.Count > 0)
        {
            Owner = this
        };

        if (choiceDialog.ShowDialog() != true)
        {
            return;
        }

        if (choiceDialog.Choice == PortalTargetChoice.SelectExisting)
        {
            var picker = new PortalPickerWindow(unpairedPortals, CurrentDiagramName)
            {
                Owner = this
            };

            if (picker.ShowDialog() == true &&
                !string.IsNullOrWhiteSpace(picker.SelectedPortalObjectId) &&
                await PairPortalsAsync(sourceDiagramId, sourcePortalId, _activeDiagramId!, picker.SelectedPortalObjectId))
            {
                ScrollDiagramObjectIntoView(picker.SelectedPortalObjectId);
                StatusText = "Paired portal points.";
            }

            return;
        }

        if (choiceDialog.Choice == PortalTargetChoice.PlaceNew)
        {
            string? pairedPortalName = PromptForPortalName(
                sourcePortal.PortalName,
                "New portal name",
                _activeDiagramId);
            if (string.IsNullOrWhiteSpace(pairedPortalName))
            {
                return;
            }

            ArmPortalPlacement(pairedPortalName, new PendingPortalPairPlacement(sourceDiagramId, sourcePortalId));
            StatusText = $"Click the diagram canvas to place the portal paired with '{sourcePortal.PortalName}'.";
        }
    }

    private DiagramDocument? PromptForPortalTargetDiagram()
    {
        List<LinkableResource> diagrams = GetScopedDiagramDocuments()
            .Select(diagram => new LinkableResource(
                diagram.Name,
                "Diagram",
                DiagramDocumentService.CreateDiagramDocumentPath(diagram.DiagramId),
                LinkableResourceKind.Diagram))
            .ToList();

        if (diagrams.Count == 0)
        {
            StatusText = "The current scope does not contain any saved diagrams.";
            return null;
        }

        var dialog = new ResourcePickerWindow(diagrams)
        {
            Owner = this,
            Title = "Select Portal Target Diagram"
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        string diagramId = DiagramDocumentService.GetDiagramId(dialog.SelectedResourcePath);
        return _diagramLibrary.Find(diagramId);
    }

    private List<DiagramDocument> GetScopedDiagramDocuments()
    {
        if (_activeScope == null)
        {
            return [];
        }

        return _activeScope.Resources
            .Where(resource => resource.Kind == ResourceKind.Diagram)
            .Select(resource => _diagramLibrary.Find(resource.Path))
            .OfType<DiagramDocument>()
            .OrderBy(diagram => diagram.Name)
            .ToList();
    }

    private List<PortalPickerItem> GetCurrentUnpairedPortalPickerItems(string? excludePortalId)
    {
        return DiagramCanvas.Children
            .OfType<DiagramPortalControl>()
            .Where(portal =>
                !portal.IsPaired &&
                !string.Equals(portal.DiagramObjectId, excludePortalId, StringComparison.OrdinalIgnoreCase))
            .Select(portal => new PortalPickerItem(
                portal.DiagramObjectId,
                portal.PortalName,
                GetCanvasLeft(portal),
                GetCanvasTop(portal)))
            .ToList();
    }

    private async Task NavigateToPairedPortalAsync(DiagramPortalControl portal)
    {
        if (string.IsNullOrWhiteSpace(portal.PairedPortalDiagramId) ||
            string.IsNullOrWhiteSpace(portal.PairedPortalObjectId))
        {
            StatusText = "This portal is not paired.";
            return;
        }

        if (string.Equals(portal.PairedPortalDiagramId, _activeDiagramId, StringComparison.OrdinalIgnoreCase))
        {
            if (ScrollDiagramObjectIntoView(portal.PairedPortalObjectId))
            {
                StatusText = $"Moved to portal {ResolvePortalAddress(portal.PairedPortalDiagramId, portal.PairedPortalObjectId)}.";
            }
            else
            {
                StatusText = "The paired portal could not be found in this diagram.";
            }

            return;
        }

        if (!IsDiagramInActiveScope(portal.PairedPortalDiagramId))
        {
            StatusText = "The paired portal diagram is not in the current scope.";
            return;
        }

        if (!await PersistActiveDiagramSilentlyAsync())
        {
            return;
        }

        DiagramDocument? targetDiagram = _diagramLibrary.Find(portal.PairedPortalDiagramId);
        if (targetDiagram == null)
        {
            StatusText = "The paired portal diagram could not be found in this scope.";
            return;
        }

        EnsureDiagramViewVisible();
        LoadDiagram(targetDiagram);
        SetActiveWorkspaceView(WorkspaceViewKind.Diagram);
        ScrollDiagramObjectIntoViewAfterLayout(portal.PairedPortalObjectId);
        StatusText = $"Opened portal target {ResolvePortalAddress(portal.PairedPortalDiagramId, portal.PairedPortalObjectId)}.";
    }

    private async Task<bool> PairPortalsAsync(
        string firstDiagramId,
        string firstPortalObjectId,
        string secondDiagramId,
        string secondPortalObjectId)
    {
        if (string.Equals(firstDiagramId, secondDiagramId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(firstPortalObjectId, secondPortalObjectId, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "A portal cannot be paired with itself.";
            return false;
        }

        string firstAddress = ResolvePortalAddress(firstDiagramId, firstPortalObjectId);
        string secondAddress = ResolvePortalAddress(secondDiagramId, secondPortalObjectId);

        ApplyPortalPairingToActiveDiagram(firstDiagramId, firstPortalObjectId, secondDiagramId, secondPortalObjectId, secondAddress);
        ApplyPortalPairingToActiveDiagram(secondDiagramId, secondPortalObjectId, firstDiagramId, firstPortalObjectId, firstAddress);
        ApplyPortalPairingToStoredDiagram(firstDiagramId, firstPortalObjectId, secondDiagramId, secondPortalObjectId);
        ApplyPortalPairingToStoredDiagram(secondDiagramId, secondPortalObjectId, firstDiagramId, firstPortalObjectId);

        if (!string.IsNullOrWhiteSpace(_activeDiagramId))
        {
            _diagramLibrary.Upsert(CreateCurrentDiagramDocument(_activeDiagramId, CurrentDiagramName));
        }

        try
        {
            await _diagramStore.SaveAsync(_diagramLibrary);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save portal pairing: {ex.Message}";
            return false;
        }
    }

    private void ApplyPortalPairingToActiveDiagram(
        string diagramId,
        string portalObjectId,
        string pairedDiagramId,
        string pairedPortalObjectId,
        string pairedAddress)
    {
        if (!string.Equals(diagramId, _activeDiagramId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (FindDiagramObjectById(portalObjectId) is DiagramPortalControl portal)
        {
            portal.ApplyPairing(pairedDiagramId, pairedPortalObjectId, pairedAddress);
        }
    }

    private void ApplyPortalPairingToStoredDiagram(
        string diagramId,
        string portalObjectId,
        string pairedDiagramId,
        string pairedPortalObjectId)
    {
        if (string.Equals(diagramId, _activeDiagramId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DiagramDocument? diagram = _diagramLibrary.Find(diagramId);
        DiagramObjectSnapshot? snapshot = diagram?.Objects.FirstOrDefault(item =>
            item.ObjectType == DiagramObjectType.Portal &&
            string.Equals(item.Id, portalObjectId, StringComparison.OrdinalIgnoreCase));
        if (snapshot == null)
        {
            return;
        }

        snapshot.PairedPortalDiagramId = pairedDiagramId;
        snapshot.PairedPortalObjectId = pairedPortalObjectId;
        diagram!.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private async Task ClearPairedPortalReferenceAsync(DiagramPortalControl portal)
    {
        if (!portal.IsPaired)
        {
            return;
        }

        ClearPortalPairingInActiveDiagram(portal.PairedPortalDiagramId, portal.PairedPortalObjectId);
        ClearPortalPairingInStoredDiagram(portal.PairedPortalDiagramId, portal.PairedPortalObjectId);

        try
        {
            await _diagramStore.SaveAsync(_diagramLibrary);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not update paired portal: {ex.Message}";
        }
    }

    private void ClearPortalPairingInActiveDiagram(string diagramId, string portalObjectId)
    {
        if (!string.Equals(diagramId, _activeDiagramId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (FindDiagramObjectById(portalObjectId) is DiagramPortalControl portal)
        {
            portal.ClearPairing();
        }
    }

    private void ClearPortalPairingInStoredDiagram(string diagramId, string portalObjectId)
    {
        if (string.Equals(diagramId, _activeDiagramId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DiagramDocument? diagram = _diagramLibrary.Find(diagramId);
        DiagramObjectSnapshot? snapshot = diagram?.Objects.FirstOrDefault(item =>
            item.ObjectType == DiagramObjectType.Portal &&
            string.Equals(item.Id, portalObjectId, StringComparison.OrdinalIgnoreCase));
        if (snapshot == null)
        {
            return;
        }

        snapshot.PairedPortalDiagramId = string.Empty;
        snapshot.PairedPortalObjectId = string.Empty;
        diagram!.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private bool IsDiagramInActiveScope(string diagramId)
    {
        return _activeScope?.Resources.Any(resource =>
            resource.Kind == ResourceKind.Diagram &&
            string.Equals(resource.Path, diagramId, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private string ResolvePortalAddress(string diagramId, string portalObjectId)
    {
        string diagramName = _diagramLibrary.Find(diagramId)?.Name ?? "Missing Diagram";
        string portalName = string.Empty;

        if (string.Equals(diagramId, _activeDiagramId, StringComparison.OrdinalIgnoreCase) &&
            FindDiagramObjectById(portalObjectId) is DiagramPortalControl activePortal)
        {
            portalName = activePortal.PortalName;
        }

        if (string.IsNullOrWhiteSpace(portalName))
        {
            portalName = _diagramLibrary.Find(diagramId)?.Objects
                .FirstOrDefault(snapshot =>
                    snapshot.ObjectType == DiagramObjectType.Portal &&
                    string.Equals(snapshot.Id, portalObjectId, StringComparison.OrdinalIgnoreCase))
                ?.PortalName ?? "Missing Portal";
        }

        return $"{portalName} @ {diagramName}";
    }

    private bool ScrollDiagramObjectIntoView(string diagramObjectId)
    {
        FrameworkElement? diagramObject = FindDiagramObjectById(diagramObjectId);
        if (diagramObject == null)
        {
            return false;
        }

        DiagramScrollViewer.UpdateLayout();
        double objectWidth = diagramObject.ActualWidth > 0 ? diagramObject.ActualWidth : diagramObject.Width;
        double objectHeight = diagramObject.ActualHeight > 0 ? diagramObject.ActualHeight : diagramObject.Height;
        double centerX = (GetCanvasLeft(diagramObject) + (objectWidth / 2)) * _diagramCanvasZoom;
        double centerY = (GetCanvasTop(diagramObject) + (objectHeight / 2)) * _diagramCanvasZoom;
        double viewportWidth = DiagramScrollViewer.ViewportWidth > 0 ? DiagramScrollViewer.ViewportWidth : DiagramScrollViewer.ActualWidth;
        double viewportHeight = DiagramScrollViewer.ViewportHeight > 0 ? DiagramScrollViewer.ViewportHeight : DiagramScrollViewer.ActualHeight;

        DiagramScrollViewer.ScrollToHorizontalOffset(Math.Max(0, centerX - (viewportWidth / 2)));
        DiagramScrollViewer.ScrollToVerticalOffset(Math.Max(0, centerY - (viewportHeight / 2)));
        SelectDiagramObject(diagramObject);
        return true;
    }

    private void ScrollDiagramObjectIntoViewAfterLayout(string diagramObjectId)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            DiagramScrollViewer.UpdateLayout();
            ScrollDiagramObjectIntoView(diagramObjectId);
        }), DispatcherPriority.ApplicationIdle);
    }

    private async Task<bool> PersistActiveDiagramSilentlyAsync()
    {
        if (string.IsNullOrWhiteSpace(_activeDiagramId))
        {
            return false;
        }

        try
        {
            _diagramLibrary.Upsert(CreateCurrentDiagramDocument(_activeDiagramId, CurrentDiagramName));
            await _diagramStore.SaveAsync(_diagramLibrary);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save current diagram state: {ex.Message}";
            return false;
        }
    }

    private void CopySelectedDiagramObject()
    {
        if (_selectedDiagramObject is DiagramWorkflowMarkerControl)
        {
            StatusText = "Workflow markers are tied to workflow items and cannot be copied.";
            return;
        }

        if (_selectedDiagramObject is DiagramPortalControl)
        {
            StatusText = "Portal links are unique and cannot be copied.";
            return;
        }

        DiagramObjectSnapshot? snapshot = CreateDiagramObjectSnapshot(_selectedDiagramObject);
        if (snapshot == null)
        {
            StatusText = "Select a diagram object to copy.";
            return;
        }

        _diagramClipboardSnapshot = snapshot.Clone();
        StatusText = "Copied diagram object.";
    }

    private void CutSelectedDiagramObject()
    {
        if (TryBlockDiagramObjectEditWhenLocked("cut diagram objects"))
        {
            return;
        }

        if (_selectedDiagramObject is DiagramWorkflowMarkerControl)
        {
            StatusText = "Delete the workflow item to remove its marker.";
            return;
        }

        if (_selectedDiagramObject is DiagramPortalControl)
        {
            StatusText = "Use the portal context menu to delete portal links.";
            return;
        }

        DiagramObjectSnapshot? snapshot = CreateDiagramObjectSnapshot(_selectedDiagramObject);
        if (snapshot == null)
        {
            StatusText = "Select a diagram object to cut.";
            return;
        }

        _diagramClipboardSnapshot = snapshot.Clone();
        RemoveDiagramObject(_selectedDiagramObject, pushUndo: true);
        StatusText = "Cut diagram object.";
    }

    private void PasteDiagramClipboardContent()
    {
        if (TryBlockDiagramObjectEditWhenLocked("paste diagram objects"))
        {
            return;
        }

        BitmapSource? clipboardImage = TryGetClipboardImage();
        if (clipboardImage != null)
        {
            PasteClipboardImage(clipboardImage);
            return;
        }

        PasteDiagramObject();
    }

    private static BitmapSource? TryGetClipboardImage()
    {
        try
        {
            return Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    private void PasteClipboardImage(BitmapSource clipboardImage)
    {
        if (TryBlockDiagramObjectEditWhenLocked("paste images onto the diagram"))
        {
            return;
        }

        try
        {
            string pastedImageFileName = SavePastedDiagramImage(clipboardImage);
            BitmapImage? imageSource = CreateImageSourceFromFile(GetPastedDiagramImagePath(pastedImageFileName));
            if (imageSource == null)
            {
                StatusText = "Could not load pasted image.";
                return;
            }

            Size imageSize = GetPastedImageDisplaySize(imageSource);
            Point centerPoint = GetDiagramPasteCenterPoint();
            var image = new DiagramImageControl(
                imageDefinitionId: string.Empty,
                imageName: "Pasted Image",
                imageSource: imageSource,
                imageDataBase64: string.Empty,
                pastedImageFileName: pastedImageFileName);
            AttachDiagramImageHandlers(image);
            image.SetCanvasBounds(
                centerPoint.X - (imageSize.Width / 2),
                centerPoint.Y - (imageSize.Height / 2),
                imageSize.Width,
                imageSize.Height);

            ApplyDefaultDiagramZIndex(image);
            DiagramCanvas.Children.Add(image);
            SelectDiagramObject(image);
            PushDiagramUndo(DiagramUndoActionKind.Added, before: null, after: CreateDiagramObjectSnapshot(image));
            StatusText = "Pasted image onto diagram.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StatusText = $"Could not paste image: {ex.Message}";
        }
    }

    private static Size GetPastedImageDisplaySize(BitmapSource imageSource)
    {
        double width = Math.Max(1, imageSource.Width);
        double height = Math.Max(1, imageSource.Height);
        const double maximumSize = 360;
        const double minimumSize = 48;

        double scale = Math.Min(1, maximumSize / Math.Max(width, height));
        width = Math.Max(minimumSize, width * scale);
        height = Math.Max(minimumSize, height * scale);
        return new Size(width, height);
    }

    private Point GetDiagramPasteCenterPoint()
    {
        DiagramScrollViewer.UpdateLayout();
        double viewportWidth = DiagramScrollViewer.ViewportWidth > 0 ? DiagramScrollViewer.ViewportWidth : DiagramScrollViewer.ActualWidth;
        double viewportHeight = DiagramScrollViewer.ViewportHeight > 0 ? DiagramScrollViewer.ViewportHeight : DiagramScrollViewer.ActualHeight;

        return new Point(
            (DiagramScrollViewer.HorizontalOffset + (viewportWidth / 2)) / _diagramCanvasZoom,
            (DiagramScrollViewer.VerticalOffset + (viewportHeight / 2)) / _diagramCanvasZoom);
    }

    private static string SavePastedDiagramImage(BitmapSource imageSource)
    {
        string fileName = $"pasted-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png";
        string path = GetPastedDiagramImagePath(fileName);
        Directory.CreateDirectory(GetPastedDiagramImagesDirectory());

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(imageSource));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return fileName;
    }

    private static string GetPastedDiagramImagesDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Surf2", PastedDiagramImagesFolderName);
    }

    private static string GetPastedDiagramImagePath(string fileName)
    {
        return Path.Combine(GetPastedDiagramImagesDirectory(), Path.GetFileName(fileName));
    }

    private void PasteDiagramObject()
    {
        if (TryBlockDiagramObjectEditWhenLocked("paste diagram objects"))
        {
            return;
        }

        if (_diagramClipboardSnapshot == null)
        {
            StatusText = "Copy or cut a diagram object before pasting.";
            return;
        }

        DiagramObjectSnapshot pastedSnapshot = _diagramClipboardSnapshot.Clone();
        if (pastedSnapshot.ObjectType == DiagramObjectType.WorkflowMarker)
        {
            StatusText = "Workflow markers are tied to workflow items and cannot be pasted.";
            return;
        }

        pastedSnapshot.Id = Guid.NewGuid().ToString("N");
        pastedSnapshot.Left += DiagramPasteOffset;
        pastedSnapshot.Top += DiagramPasteOffset;
        if (pastedSnapshot.ObjectType == DiagramObjectType.Line)
        {
            pastedSnapshot.LineStartX += DiagramPasteOffset;
            pastedSnapshot.LineStartY += DiagramPasteOffset;
            pastedSnapshot.LineEndX += DiagramPasteOffset;
            pastedSnapshot.LineEndY += DiagramPasteOffset;
        }
        else if (pastedSnapshot.ObjectType == DiagramObjectType.Label)
        {
            pastedSnapshot.LabelAnchorX += DiagramPasteOffset;
            pastedSnapshot.LabelAnchorY += DiagramPasteOffset;
            pastedSnapshot.LabelBoxLeft += DiagramPasteOffset;
            pastedSnapshot.LabelBoxTop += DiagramPasteOffset;
        }

        FrameworkElement? pastedObject = CreateDiagramObjectFromSnapshot(pastedSnapshot);
        if (pastedObject == null)
        {
            StatusText = "Could not paste diagram object.";
            return;
        }

        ApplyDefaultDiagramZIndex(pastedObject);
        pastedSnapshot.ZIndex = Panel.GetZIndex(pastedObject);
        DiagramCanvas.Children.Add(pastedObject);
        SelectDiagramObject(pastedObject);
        PushDiagramUndo(DiagramUndoActionKind.Added, before: null, after: pastedSnapshot);
        StatusText = "Pasted diagram object.";
    }

    private void UndoDiagramAction()
    {
        if (TryBlockDiagramObjectEditWhenLocked("undo diagram object changes"))
        {
            return;
        }

        if (_diagramUndoStack.Count == 0)
        {
            StatusText = "Nothing to undo.";
            return;
        }

        DiagramUndoAction action = _diagramUndoStack.Pop();

        switch (action.Kind)
        {
            case DiagramUndoActionKind.Added:
                if (action.After?.ObjectType == DiagramObjectType.WorkflowMarker)
                {
                    RemoveWorkflowItemAndMarker(action.After.WorkflowId, action.After.WorkflowItemId);
                }
                else
                {
                    RemoveDiagramObject(FindDiagramObjectById(action.After?.Id), pushUndo: false);
                }

                StatusText = "Undid diagram object creation.";
                break;

            case DiagramUndoActionKind.Removed:
                if (action.Before != null)
                {
                    FrameworkElement? restoredObject = CreateDiagramObjectFromSnapshot(action.Before);
                    if (restoredObject != null)
                    {
                        DiagramCanvas.Children.Add(restoredObject);
                        SelectDiagramObject(restoredObject);
                    }
                }

                StatusText = "Undid diagram object deletion.";
                break;

            case DiagramUndoActionKind.Modified:
                if (action.Before != null)
                {
                    FrameworkElement? target = FindDiagramObjectById(action.Before.Id);
                    if (target == null)
                    {
                        target = CreateDiagramObjectFromSnapshot(action.Before);
                        if (target != null)
                        {
                            DiagramCanvas.Children.Add(target);
                        }
                    }
                    else
                    {
                        ApplyDiagramObjectSnapshot(target, action.Before);
                    }

                    SelectDiagramObject(target);
                }

                StatusText = "Undid diagram object change.";
                break;
        }
    }

    private void StartDiagramShapeDraw(DiagramShapeKind shapeKind, Point startPoint)
    {
        if (TryBlockDiagramObjectEditWhenLocked("draw shapes"))
        {
            return;
        }

        _isDrawingDiagramShape = true;
        _diagramShapeDrawStartPoint = startPoint;

        var shape = new DiagramShapeControl(shapeKind, _diagramOutlineColor, _diagramBackColor);
        AttachDiagramShapeHandlers(shape);
        shape.SetCanvasBounds(startPoint.X, startPoint.Y, 1, 1);

        _activeDiagramDrawingShape = shape;
        ApplyDefaultDiagramZIndex(shape);
        ApplyDiagramLockToObject(shape);
        DiagramCanvas.Children.Add(shape);
        DiagramCanvas.CaptureMouse();
        DiagramCanvas.Cursor = Cursors.Cross;
    }

    private void StartDiagramImageDraw(DiagramImageDefinition imageDefinition, Point startPoint)
    {
        if (TryBlockDiagramObjectEditWhenLocked("draw images"))
        {
            return;
        }

        ImageSource? imageSource = CreateImageSource(imageDefinition.ImageDataBase64);
        if (imageSource == null)
        {
            StatusText = $"Could not load diagram image '{imageDefinition.Name}'.";
            return;
        }

        _isDrawingDiagramImage = true;
        _diagramImageDrawStartPoint = startPoint;

        var image = new DiagramImageControl(
            imageDefinition.Id,
            imageDefinition.Name,
            imageSource,
            imageDefinition.ImageDataBase64);
        AttachDiagramImageHandlers(image);
        image.SetCanvasBounds(startPoint.X, startPoint.Y, 1, 1);

        _activeDiagramDrawingImage = image;
        ApplyDefaultDiagramZIndex(image);
        ApplyDiagramLockToObject(image);
        DiagramCanvas.Children.Add(image);
        DiagramCanvas.CaptureMouse();
        DiagramCanvas.Cursor = Cursors.Cross;
    }

    private void StartDiagramLineDraw(Point startPoint)
    {
        if (TryBlockDiagramObjectEditWhenLocked("draw lines"))
        {
            return;
        }

        _isDrawingDiagramLine = true;
        _diagramLineDrawStartPoint = startPoint;

        var line = new DiagramLineControl(_diagramOutlineColor, _diagramLineHasEndArrow, isLoose: false);
        AttachDiagramLineHandlers(line);
        line.SetAbsoluteEndpoints(startPoint, startPoint);

        _activeDiagramDrawingLine = line;
        ApplyDefaultDiagramZIndex(line);
        ApplyDiagramLockToObject(line);
        DiagramCanvas.Children.Add(line);
        DiagramCanvas.CaptureMouse();
        DiagramCanvas.Cursor = Cursors.Cross;
    }

    private void PlaceDiagramLabel(Point anchorPoint)
    {
        if (TryBlockDiagramObjectEditWhenLocked("add labels"))
        {
            return;
        }

        var label = new DiagramLabelControl(_diagramOutlineColor, _diagramBackColor);
        AttachDiagramLabelHandlers(label);
        label.PlaceAt(anchorPoint);

        ApplyDefaultDiagramZIndex(label);
        ApplyDiagramLockToObject(label);
        DiagramCanvas.Children.Add(label);
        SelectDiagramObject(label);
        PushDiagramUndo(DiagramUndoActionKind.Added, before: null, after: CreateDiagramObjectSnapshot(label));
        StatusText = "Added label.";

        Dispatcher.BeginInvoke(
            () => label.BeginEditLabel(),
            DispatcherPriority.Input);
    }

    private void UpdateDiagramShapeDraw(Point currentPoint)
    {
        if (_activeDiagramDrawingShape == null)
        {
            return;
        }

        double left = Math.Min(_diagramShapeDrawStartPoint.X, currentPoint.X);
        double top = Math.Min(_diagramShapeDrawStartPoint.Y, currentPoint.Y);
        double width = Math.Abs(currentPoint.X - _diagramShapeDrawStartPoint.X);
        double height = Math.Abs(currentPoint.Y - _diagramShapeDrawStartPoint.Y);

        _activeDiagramDrawingShape.SetCanvasBounds(left, top, width, height);
    }

    private void UpdateDiagramImageDraw(Point currentPoint)
    {
        if (_activeDiagramDrawingImage == null)
        {
            return;
        }

        double left = Math.Min(_diagramImageDrawStartPoint.X, currentPoint.X);
        double top = Math.Min(_diagramImageDrawStartPoint.Y, currentPoint.Y);
        double width = Math.Abs(currentPoint.X - _diagramImageDrawStartPoint.X);
        double height = Math.Abs(currentPoint.Y - _diagramImageDrawStartPoint.Y);

        _activeDiagramDrawingImage.SetCanvasBounds(left, top, width, height);
    }

    private void UpdateDiagramLineDraw(Point currentPoint)
    {
        _activeDiagramDrawingLine?.SetAbsoluteEndpoints(_diagramLineDrawStartPoint, currentPoint);
    }

    private void FinishDiagramShapeDraw(Point endPoint, bool beginEdit = true)
    {
        DiagramShapeControl? completedShape = _activeDiagramDrawingShape;
        _activeDiagramDrawingShape = null;
        _isDrawingDiagramShape = false;

        if (DiagramCanvas.IsMouseCaptured)
        {
            DiagramCanvas.ReleaseMouseCapture();
        }

        DiagramCanvas.Cursor = null;

        if (completedShape == null)
        {
            return;
        }

        double rawWidth = Math.Abs(endPoint.X - _diagramShapeDrawStartPoint.X);
        double rawHeight = Math.Abs(endPoint.Y - _diagramShapeDrawStartPoint.Y);

        if (rawWidth < 6 && rawHeight < 6)
        {
            DetachDiagramShapeHandlers(completedShape);
            DiagramCanvas.Children.Remove(completedShape);
            return;
        }

        SelectDiagramObject(completedShape);
        PushDiagramUndo(DiagramUndoActionKind.Added, before: null, after: CreateDiagramObjectSnapshot(completedShape));
        StatusText = $"Added {completedShape.ShapeKind.ToString().ToLowerInvariant()} shape.";
        if (beginEdit)
        {
            Dispatcher.BeginInvoke(
                () => completedShape.BeginEditLabel(),
                DispatcherPriority.Input);
        }
    }

    private void FinishDiagramImageDraw(Point endPoint, bool beginEdit = true)
    {
        DiagramImageControl? completedImage = _activeDiagramDrawingImage;
        _activeDiagramDrawingImage = null;
        _isDrawingDiagramImage = false;

        if (DiagramCanvas.IsMouseCaptured)
        {
            DiagramCanvas.ReleaseMouseCapture();
        }

        DiagramCanvas.Cursor = null;

        if (completedImage == null)
        {
            return;
        }

        double rawWidth = Math.Abs(endPoint.X - _diagramImageDrawStartPoint.X);
        double rawHeight = Math.Abs(endPoint.Y - _diagramImageDrawStartPoint.Y);

        if (rawWidth < 6 && rawHeight < 6)
        {
            DetachDiagramImageHandlers(completedImage);
            DiagramCanvas.Children.Remove(completedImage);
            return;
        }

        SelectDiagramObject(completedImage);
        PushDiagramUndo(DiagramUndoActionKind.Added, before: null, after: CreateDiagramObjectSnapshot(completedImage));
        StatusText = $"Added image '{completedImage.ImageName}'.";
        if (beginEdit)
        {
            Dispatcher.BeginInvoke(
                () => completedImage.BeginEditLabel(),
                DispatcherPriority.Input);
        }
    }

    private void FinishDiagramLineDraw(Point endPoint)
    {
        DiagramLineControl? completedLine = _activeDiagramDrawingLine;
        _activeDiagramDrawingLine = null;
        _isDrawingDiagramLine = false;

        if (DiagramCanvas.IsMouseCaptured)
        {
            DiagramCanvas.ReleaseMouseCapture();
        }

        DiagramCanvas.Cursor = null;

        if (completedLine == null)
        {
            return;
        }

        if ((endPoint - _diagramLineDrawStartPoint).Length < 6)
        {
            DetachDiagramLineHandlers(completedLine);
            DiagramCanvas.Children.Remove(completedLine);
            return;
        }

        SelectDiagramObject(null);
        PushDiagramUndo(DiagramUndoActionKind.Added, before: null, after: CreateDiagramObjectSnapshot(completedLine));
        StatusText = completedLine.HasEndArrow
            ? "Added line with arrow."
            : "Added fixed line.";
    }

    private void AttachDiagramShapeHandlers(DiagramShapeControl shape)
    {
        ApplyDiagramLockToObject(shape);
        shape.PreviewMouseLeftButtonDown += DiagramObject_PreviewMouseLeftButtonDown;
        shape.Selected += DiagramObject_Selected;
        shape.InteractionStarted += DiagramObject_InteractionStarted;
        shape.InteractionCompleted += DiagramObject_InteractionCompleted;
        shape.LabelChanged += DiagramObject_LabelChanged;
        shape.EditRequested += DiagramShape_EditRequested;
        shape.DeleteRequested += DiagramShape_DeleteRequested;
        shape.LayerChangeRequested += DiagramObject_LayerChangeRequested;
    }

    private void AttachDiagramImageHandlers(DiagramImageControl image)
    {
        ApplyDiagramLockToObject(image);
        image.PreviewMouseLeftButtonDown += DiagramObject_PreviewMouseLeftButtonDown;
        image.Selected += DiagramObject_Selected;
        image.InteractionStarted += DiagramObject_InteractionStarted;
        image.InteractionCompleted += DiagramObject_InteractionCompleted;
        image.LabelChanged += DiagramObject_LabelChanged;
        image.EditRequested += DiagramImage_EditRequested;
        image.DeleteRequested += DiagramImage_DeleteRequested;
        image.LayerChangeRequested += DiagramObject_LayerChangeRequested;
    }

    private void AttachDiagramLineHandlers(DiagramLineControl line)
    {
        ApplyDiagramLockToObject(line);
        line.PreviewMouseLeftButtonDown += DiagramObject_PreviewMouseLeftButtonDown;
        line.Selected += DiagramObject_Selected;
        line.InteractionStarted += DiagramObject_InteractionStarted;
        line.InteractionCompleted += DiagramObject_InteractionCompleted;
        line.LooseStateToggleRequested += DiagramLine_LooseStateToggleRequested;
        line.EditRequested += DiagramLine_EditRequested;
        line.DeleteRequested += DiagramLine_DeleteRequested;
        line.LayerChangeRequested += DiagramObject_LayerChangeRequested;
    }

    private void AttachDiagramLabelHandlers(DiagramLabelControl label)
    {
        ApplyDiagramLockToObject(label);
        label.PreviewMouseLeftButtonDown += DiagramObject_PreviewMouseLeftButtonDown;
        label.Selected += DiagramObject_Selected;
        label.InteractionStarted += DiagramObject_InteractionStarted;
        label.InteractionCompleted += DiagramObject_InteractionCompleted;
        label.LabelChanged += DiagramObject_LabelChanged;
        label.EditRequested += DiagramLabel_EditRequested;
        label.DeleteRequested += DiagramLabel_DeleteRequested;
        label.TetherChangedRequested += DiagramLabel_TetherChangedRequested;
        label.LayerChangeRequested += DiagramObject_LayerChangeRequested;
    }

    private void AttachDiagramWorkflowMarkerHandlers(DiagramWorkflowMarkerControl marker)
    {
        ApplyDiagramLockToObject(marker);
        marker.Selected += DiagramObject_Selected;
        marker.InteractionStarted += DiagramObject_InteractionStarted;
        marker.InteractionCompleted += DiagramObject_InteractionCompleted;
        marker.OpenRequested += DiagramWorkflowMarker_OpenRequested;
        marker.DeleteRequested += DiagramWorkflowMarker_DeleteRequested;
        marker.LayerChangeRequested += DiagramObject_LayerChangeRequested;
    }

    private void AttachDiagramPortalHandlers(DiagramPortalControl portal)
    {
        ApplyDiagramLockToObject(portal);
        portal.Selected += DiagramObject_Selected;
        portal.InteractionStarted += DiagramObject_InteractionStarted;
        portal.InteractionCompleted += DiagramObject_InteractionCompleted;
        portal.OpenRequested += DiagramPortal_OpenRequested;
        portal.DeleteRequested += DiagramPortal_DeleteRequested;
        portal.LayerChangeRequested += DiagramObject_LayerChangeRequested;
    }

    private void DetachDiagramShapeHandlers(DiagramShapeControl shape)
    {
        shape.PreviewMouseLeftButtonDown -= DiagramObject_PreviewMouseLeftButtonDown;
        shape.Selected -= DiagramObject_Selected;
        shape.InteractionStarted -= DiagramObject_InteractionStarted;
        shape.InteractionCompleted -= DiagramObject_InteractionCompleted;
        shape.LabelChanged -= DiagramObject_LabelChanged;
        shape.EditRequested -= DiagramShape_EditRequested;
        shape.DeleteRequested -= DiagramShape_DeleteRequested;
        shape.LayerChangeRequested -= DiagramObject_LayerChangeRequested;
    }

    private void DetachDiagramImageHandlers(DiagramImageControl image)
    {
        image.PreviewMouseLeftButtonDown -= DiagramObject_PreviewMouseLeftButtonDown;
        image.Selected -= DiagramObject_Selected;
        image.InteractionStarted -= DiagramObject_InteractionStarted;
        image.InteractionCompleted -= DiagramObject_InteractionCompleted;
        image.LabelChanged -= DiagramObject_LabelChanged;
        image.EditRequested -= DiagramImage_EditRequested;
        image.DeleteRequested -= DiagramImage_DeleteRequested;
        image.LayerChangeRequested -= DiagramObject_LayerChangeRequested;
    }

    private void DetachDiagramLineHandlers(DiagramLineControl line)
    {
        line.PreviewMouseLeftButtonDown -= DiagramObject_PreviewMouseLeftButtonDown;
        line.Selected -= DiagramObject_Selected;
        line.InteractionStarted -= DiagramObject_InteractionStarted;
        line.InteractionCompleted -= DiagramObject_InteractionCompleted;
        line.LooseStateToggleRequested -= DiagramLine_LooseStateToggleRequested;
        line.EditRequested -= DiagramLine_EditRequested;
        line.DeleteRequested -= DiagramLine_DeleteRequested;
        line.LayerChangeRequested -= DiagramObject_LayerChangeRequested;
    }

    private void DetachDiagramLabelHandlers(DiagramLabelControl label)
    {
        label.PreviewMouseLeftButtonDown -= DiagramObject_PreviewMouseLeftButtonDown;
        label.Selected -= DiagramObject_Selected;
        label.InteractionStarted -= DiagramObject_InteractionStarted;
        label.InteractionCompleted -= DiagramObject_InteractionCompleted;
        label.LabelChanged -= DiagramObject_LabelChanged;
        label.EditRequested -= DiagramLabel_EditRequested;
        label.DeleteRequested -= DiagramLabel_DeleteRequested;
        label.TetherChangedRequested -= DiagramLabel_TetherChangedRequested;
        label.LayerChangeRequested -= DiagramObject_LayerChangeRequested;
    }

    private void DetachDiagramWorkflowMarkerHandlers(DiagramWorkflowMarkerControl marker)
    {
        marker.Selected -= DiagramObject_Selected;
        marker.InteractionStarted -= DiagramObject_InteractionStarted;
        marker.InteractionCompleted -= DiagramObject_InteractionCompleted;
        marker.OpenRequested -= DiagramWorkflowMarker_OpenRequested;
        marker.DeleteRequested -= DiagramWorkflowMarker_DeleteRequested;
        marker.LayerChangeRequested -= DiagramObject_LayerChangeRequested;
    }

    private void DetachDiagramPortalHandlers(DiagramPortalControl portal)
    {
        portal.Selected -= DiagramObject_Selected;
        portal.InteractionStarted -= DiagramObject_InteractionStarted;
        portal.InteractionCompleted -= DiagramObject_InteractionCompleted;
        portal.OpenRequested -= DiagramPortal_OpenRequested;
        portal.DeleteRequested -= DiagramPortal_DeleteRequested;
        portal.LayerChangeRequested -= DiagramObject_LayerChangeRequested;
    }

    private async void DiagramObject_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 ||
            sender is not FrameworkElement diagramObject ||
            IsTextInputElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        CommitMetadataEditorChanges();
        SelectDiagramObject(diagramObject);
        OpenDiagramObjectMetadataSidebar(diagramObject);

        DiagramObjectMetadata? metadata = GetDiagramObjectMetadata(diagramObject);
        if (!string.IsNullOrWhiteSpace(metadata?.Link))
        {
            await OpenMetadataLinkAsync(metadata.Link.Trim());
            return;
        }
    }

    private void OpenDiagramObjectMetadataSidebar(FrameworkElement diagramObject)
    {
        SelectDiagramObject(diagramObject);
        SetDiagramSidebarOpen(true);
        DiagramSidebarTabs.SelectedIndex = 0;
        LoadMetadataEditorForSelection();
        StatusText = "Opened diagram object metadata.";
    }

    private void DiagramObject_Selected(object? sender, EventArgs e)
    {
        SelectDiagramObject(sender as FrameworkElement);
    }

    private void DiagramObject_LayerChangeRequested(object? sender, DiagramLayerChangeRequestedEventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("change diagram object layering"))
        {
            return;
        }

        if (sender is not FrameworkElement diagramObject)
        {
            return;
        }

        SelectDiagramObject(diagramObject);

        DiagramObjectSnapshot? before = CreateDiagramObjectSnapshot(diagramObject);
        bool changed = e.Action switch
        {
            DiagramLayerChangeAction.BringForward => BringDiagramObjectForward(diagramObject),
            DiagramLayerChangeAction.SendBackward => SendDiagramObjectBackward(diagramObject),
            DiagramLayerChangeAction.SendToBack => SendDiagramObjectToBack(diagramObject),
            _ => false
        };

        if (!changed)
        {
            StatusText = "Diagram object is already at that layer.";
            return;
        }

        DiagramObjectSnapshot? after = CreateDiagramObjectSnapshot(diagramObject);
        if (before != null && after != null)
        {
            PushDiagramUndo(DiagramUndoActionKind.Modified, before, after);
        }

        StatusText = e.Action switch
        {
            DiagramLayerChangeAction.BringForward => "Brought diagram object forward.",
            DiagramLayerChangeAction.SendBackward => "Sent diagram object backward.",
            DiagramLayerChangeAction.SendToBack => "Sent diagram object to back.",
            _ => "Updated diagram object layer."
        };
    }

    private void DiagramObject_InteractionStarted(object? sender, EventArgs e)
    {
        _pendingDiagramInteractionSnapshot = CreateDiagramObjectSnapshot(sender as FrameworkElement);
    }

    private void DiagramObject_InteractionCompleted(object? sender, EventArgs e)
    {
        DiagramObjectSnapshot? after = CreateDiagramObjectSnapshot(sender as FrameworkElement);
        if (_pendingDiagramInteractionSnapshot != null && after != null)
        {
            PushDiagramUndo(DiagramUndoActionKind.Modified, _pendingDiagramInteractionSnapshot, after);
        }

        _pendingDiagramInteractionSnapshot = null;
    }

    private void DiagramObject_LabelChanged(object? sender, DiagramObjectLabelChangedEventArgs e)
    {
        DiagramObjectSnapshot? after = CreateDiagramObjectSnapshot(sender as FrameworkElement);
        if (after == null)
        {
            return;
        }

        DiagramObjectSnapshot before = after.Clone();
        before.LabelText = e.OldText;
        PushDiagramUndo(DiagramUndoActionKind.Modified, before, after);
    }

    private void SelectDiagramObject(FrameworkElement? diagramObject)
    {
        if (ReferenceEquals(_selectedDiagramObject, diagramObject))
        {
            return;
        }

        CommitMetadataEditorChanges();
        RetetherIfNeeded(_selectedDiagramObject);
        SetDiagramObjectSelected(_selectedDiagramObject, false);
        _selectedDiagramObject = diagramObject;
        SetDiagramObjectSelected(_selectedDiagramObject, true);
        LoadMetadataEditorForSelection();
    }

    private void RetetherIfNeeded(FrameworkElement? diagramObject)
    {
        if (diagramObject is not DiagramLabelControl { IsTethered: false } label)
        {
            return;
        }

        DiagramObjectSnapshot? before = CreateDiagramObjectSnapshot(label);
        label.RetetherAtCurrentAnchor();
        DiagramObjectSnapshot? after = CreateDiagramObjectSnapshot(label);
        if (before != null && after != null)
        {
            PushDiagramUndo(DiagramUndoActionKind.Modified, before, after);
        }
    }

    private static void SetDiagramObjectSelected(FrameworkElement? diagramObject, bool isSelected)
    {
        switch (diagramObject)
        {
            case DiagramShapeControl shape:
                shape.IsSelected = isSelected;
                break;

            case DiagramImageControl image:
                image.IsSelected = isSelected;
                break;

            case DiagramLineControl line:
                line.IsSelected = isSelected;
                break;

            case DiagramLabelControl label:
                label.IsSelected = isSelected;
                break;

            case DiagramWorkflowMarkerControl marker:
                marker.IsSelected = isSelected;
                break;

            case DiagramPortalControl portal:
                portal.IsSelected = isSelected;
                break;
        }
    }

    private DiagramObjectSnapshot? CreateDiagramObjectSnapshot(FrameworkElement? diagramObject)
    {
        return diagramObject switch
        {
            DiagramShapeControl shape => new DiagramObjectSnapshot
            {
                Id = shape.DiagramObjectId,
                ObjectType = DiagramObjectType.Shape,
                Metadata = shape.Metadata.Clone(),
                ZIndex = Panel.GetZIndex(shape),
                ShapeKind = shape.ShapeKind,
                LabelText = shape.LabelText,
                OutlineColorText = shape.OutlineColorText,
                BackColorText = shape.BackColorText,
                Left = GetCanvasLeft(shape),
                Top = GetCanvasTop(shape),
                Width = shape.ActualWidth > 0 ? shape.ActualWidth : shape.Width,
                Height = shape.ActualHeight > 0 ? shape.ActualHeight : shape.Height
            },
            DiagramImageControl image => new DiagramObjectSnapshot
            {
                Id = image.DiagramObjectId,
                ObjectType = DiagramObjectType.Image,
                Metadata = image.Metadata.Clone(),
                ZIndex = Panel.GetZIndex(image),
                ImageDefinitionId = image.ImageDefinitionId,
                ImageName = image.ImageName,
                ImageDataBase64 = image.ImageDataBase64,
                PastedImageFileName = image.PastedImageFileName,
                LabelText = image.LabelText,
                Left = GetCanvasLeft(image),
                Top = GetCanvasTop(image),
                Width = image.ActualWidth > 0 ? image.ActualWidth : image.Width,
                Height = image.ActualHeight > 0 ? image.ActualHeight : image.Height
            },
            DiagramLineControl line => new DiagramObjectSnapshot
            {
                Id = line.DiagramObjectId,
                ObjectType = DiagramObjectType.Line,
                Metadata = line.Metadata.Clone(),
                ZIndex = Panel.GetZIndex(line),
                OutlineColorText = line.ColorText,
                HasEndArrow = line.HasEndArrow,
                IsLineLoose = line.IsLoose,
                LineStartX = line.StartPoint.X,
                LineStartY = line.StartPoint.Y,
                LineEndX = line.EndPoint.X,
                LineEndY = line.EndPoint.Y,
                Left = GetCanvasLeft(line),
                Top = GetCanvasTop(line),
                Width = line.ActualWidth > 0 ? line.ActualWidth : line.Width,
                Height = line.ActualHeight > 0 ? line.ActualHeight : line.Height
            },
            DiagramLabelControl label => new DiagramObjectSnapshot
            {
                Id = label.DiagramObjectId,
                ObjectType = DiagramObjectType.Label,
                Metadata = label.Metadata.Clone(),
                ZIndex = Panel.GetZIndex(label),
                LabelText = label.LabelText,
                OutlineColorText = label.OutlineColorText,
                BackColorText = label.BackColorText,
                IsTethered = label.IsTethered,
                LabelAnchorX = label.AnchorPoint.X,
                LabelAnchorY = label.AnchorPoint.Y,
                LabelBoxLeft = label.BoxRect.Left,
                LabelBoxTop = label.BoxRect.Top,
                LabelBoxWidth = label.BoxRect.Width,
                LabelBoxHeight = label.BoxRect.Height,
                Left = GetCanvasLeft(label),
                Top = GetCanvasTop(label),
                Width = label.ActualWidth > 0 ? label.ActualWidth : label.Width,
                Height = label.ActualHeight > 0 ? label.ActualHeight : label.Height
            },
            DiagramWorkflowMarkerControl marker => new DiagramObjectSnapshot
            {
                Id = marker.DiagramObjectId,
                ObjectType = DiagramObjectType.WorkflowMarker,
                ZIndex = Panel.GetZIndex(marker),
                WorkflowId = marker.WorkflowId,
                WorkflowItemId = marker.WorkflowItemId,
                Left = GetCanvasLeft(marker),
                Top = GetCanvasTop(marker),
                Width = marker.ActualWidth > 0 ? marker.ActualWidth : marker.Width,
                Height = marker.ActualHeight > 0 ? marker.ActualHeight : marker.Height
            },
            DiagramPortalControl portal => new DiagramObjectSnapshot
            {
                Id = portal.DiagramObjectId,
                ObjectType = DiagramObjectType.Portal,
                ZIndex = Panel.GetZIndex(portal),
                PortalName = portal.PortalName,
                PairedPortalDiagramId = portal.PairedPortalDiagramId,
                PairedPortalObjectId = portal.PairedPortalObjectId,
                Left = GetCanvasLeft(portal),
                Top = GetCanvasTop(portal),
                Width = portal.ActualWidth > 0 ? portal.ActualWidth : portal.Width,
                Height = portal.ActualHeight > 0 ? portal.ActualHeight : portal.Height
            },
            _ => null
        };
    }

    private FrameworkElement? CreateDiagramObjectFromSnapshot(DiagramObjectSnapshot snapshot)
    {
        FrameworkElement? diagramObject = snapshot.ObjectType switch
        {
            DiagramObjectType.Shape => CreateDiagramShapeFromSnapshot(snapshot),
            DiagramObjectType.Image => CreateDiagramImageFromSnapshot(snapshot),
            DiagramObjectType.Line => CreateDiagramLineFromSnapshot(snapshot),
            DiagramObjectType.Label => CreateDiagramLabelFromSnapshot(snapshot),
            DiagramObjectType.WorkflowMarker => CreateDiagramWorkflowMarkerFromSnapshot(snapshot),
            DiagramObjectType.Portal => CreateDiagramPortalFromSnapshot(snapshot),
            _ => null
        };

        if (diagramObject != null)
        {
            ApplyDiagramObjectSnapshot(diagramObject, snapshot);
        }

        return diagramObject;
    }

    private DiagramShapeControl CreateDiagramShapeFromSnapshot(DiagramObjectSnapshot snapshot)
    {
        var shape = new DiagramShapeControl(
            snapshot.ShapeKind,
            snapshot.OutlineColorText,
            snapshot.BackColorText,
            snapshot.Id);
        AttachDiagramShapeHandlers(shape);
        return shape;
    }

    private DiagramImageControl? CreateDiagramImageFromSnapshot(DiagramObjectSnapshot snapshot)
    {
        string imageData = snapshot.ImageDataBase64;
        ImageSource? imageSource = null;

        if (!string.IsNullOrWhiteSpace(snapshot.PastedImageFileName))
        {
            imageSource = CreateImageSourceFromFile(GetPastedDiagramImagePath(snapshot.PastedImageFileName));
        }

        if (imageSource == null && string.IsNullOrWhiteSpace(imageData))
        {
            imageData = FindDiagramImageDefinition(snapshot.ImageDefinitionId)?.ImageDataBase64 ?? string.Empty;
        }

        imageSource ??= CreateImageSource(imageData);
        if (imageSource == null)
        {
            return null;
        }

        var image = new DiagramImageControl(
            imageDefinitionId: snapshot.ImageDefinitionId,
            imageName: snapshot.ImageName,
            imageSource: imageSource,
            imageDataBase64: imageData,
            pastedImageFileName: snapshot.PastedImageFileName,
            diagramObjectId: snapshot.Id);
        AttachDiagramImageHandlers(image);
        return image;
    }

    private DiagramLineControl CreateDiagramLineFromSnapshot(DiagramObjectSnapshot snapshot)
    {
        var line = new DiagramLineControl(
            snapshot.OutlineColorText,
            snapshot.HasEndArrow,
            snapshot.IsLineLoose,
            snapshot.Id);
        AttachDiagramLineHandlers(line);
        return line;
    }

    private DiagramLabelControl CreateDiagramLabelFromSnapshot(DiagramObjectSnapshot snapshot)
    {
        var label = new DiagramLabelControl(
            snapshot.OutlineColorText,
            snapshot.BackColorText,
            snapshot.Id);
        AttachDiagramLabelHandlers(label);
        return label;
    }

    private DiagramWorkflowMarkerControl CreateDiagramWorkflowMarkerFromSnapshot(DiagramObjectSnapshot snapshot)
    {
        WorkflowItem? workflowItem = FindWorkflowItem(snapshot.WorkflowId, snapshot.WorkflowItemId);
        var marker = new DiagramWorkflowMarkerControl(
            snapshot.WorkflowId,
            snapshot.WorkflowItemId,
            workflowItem?.ItemNumber ?? 0,
            workflowItem?.ItemDescription ?? string.Empty,
            snapshot.Id);
        marker.SetHasUnresolvedQueries(DiagramQueryState.HasUnresolvedWorkflowQueries(workflowItem));
        marker.Visibility = FindWorkflow(snapshot.WorkflowId)?.AreMarkersVisible == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        AttachDiagramWorkflowMarkerHandlers(marker);
        return marker;
    }

    private DiagramPortalControl CreateDiagramPortalFromSnapshot(DiagramObjectSnapshot snapshot)
    {
        var portal = new DiagramPortalControl(
            snapshot.PortalName,
            snapshot.PairedPortalDiagramId,
            snapshot.PairedPortalObjectId,
            ResolvePortalAddress(snapshot.PairedPortalDiagramId, snapshot.PairedPortalObjectId),
            snapshot.Id);
        AttachDiagramPortalHandlers(portal);
        return portal;
    }

    private void ApplyDiagramObjectSnapshot(FrameworkElement diagramObject, DiagramObjectSnapshot snapshot)
    {
        Panel.SetZIndex(diagramObject, snapshot.ZIndex);

        switch (diagramObject)
        {
            case DiagramShapeControl shape:
                shape.SetCanvasBounds(snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height);
                shape.ApplyDetails(snapshot.LabelText, snapshot.OutlineColorText, snapshot.BackColorText);
                shape.ApplyMetadata(snapshot.Metadata);
                break;

            case DiagramImageControl image:
                image.SetCanvasBounds(snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height);
                image.ApplyDetails(snapshot.LabelText);
                image.ApplyMetadata(snapshot.Metadata);
                break;

            case DiagramLineControl line:
                line.SetAbsoluteEndpoints(
                    new Point(snapshot.LineStartX, snapshot.LineStartY),
                    new Point(snapshot.LineEndX, snapshot.LineEndY));
                line.SetLooseState(snapshot.IsLineLoose);
                line.ApplyDetails(snapshot.OutlineColorText, snapshot.HasEndArrow);
                line.ApplyMetadata(snapshot.Metadata);
                break;

            case DiagramLabelControl label:
                label.SetGeometry(
                    new Point(snapshot.LabelAnchorX, snapshot.LabelAnchorY),
                    new Rect(snapshot.LabelBoxLeft, snapshot.LabelBoxTop, snapshot.LabelBoxWidth, snapshot.LabelBoxHeight),
                    snapshot.IsTethered);
                label.ApplyDetails(
                    snapshot.LabelText,
                    snapshot.OutlineColorText,
                    snapshot.BackColorText,
                    resizeToText: false);
                label.ApplyMetadata(snapshot.Metadata);
                break;

            case DiagramWorkflowMarkerControl marker:
                marker.SetCanvasBounds(snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height);
                break;

            case DiagramPortalControl portal:
                portal.SetCanvasBounds(snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height);
                portal.ApplyDetails(snapshot.PortalName);
                portal.ApplyPairing(
                    snapshot.PairedPortalDiagramId,
                    snapshot.PairedPortalObjectId,
                    ResolvePortalAddress(snapshot.PairedPortalDiagramId, snapshot.PairedPortalObjectId));
                break;
        }
    }

    private FrameworkElement? FindDiagramObjectById(string? diagramObjectId)
    {
        if (string.IsNullOrWhiteSpace(diagramObjectId))
        {
            return null;
        }

        foreach (UIElement child in DiagramCanvas.Children)
        {
            if (child is DiagramShapeControl shape &&
                string.Equals(shape.DiagramObjectId, diagramObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return shape;
            }

            if (child is DiagramImageControl image &&
                string.Equals(image.DiagramObjectId, diagramObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return image;
            }

            if (child is DiagramLineControl line &&
                string.Equals(line.DiagramObjectId, diagramObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }

            if (child is DiagramLabelControl label &&
                string.Equals(label.DiagramObjectId, diagramObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }

            if (child is DiagramWorkflowMarkerControl marker &&
                string.Equals(marker.DiagramObjectId, diagramObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return marker;
            }

            if (child is DiagramPortalControl portal &&
                string.Equals(portal.DiagramObjectId, diagramObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return portal;
            }
        }

        return null;
    }

    private WorkflowDocument? FindWorkflow(string workflowId)
    {
        return _currentDiagramWorkflows.FirstOrDefault(workflow =>
            string.Equals(workflow.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase));
    }

    private WorkflowItem? FindWorkflowItem(string workflowId, string workflowItemId)
    {
        return FindWorkflow(workflowId)?.Items.FirstOrDefault(item =>
            string.Equals(item.WorkflowItemId, workflowItemId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDiagramObject(FrameworkElement element)
    {
        return element is DiagramShapeControl or
            DiagramImageControl or
            DiagramLineControl or
            DiagramLabelControl or
            DiagramWorkflowMarkerControl or
            DiagramPortalControl;
    }

    private List<FrameworkElement> GetDiagramObjectsInLayerOrder()
    {
        return DiagramCanvas.Children
            .OfType<FrameworkElement>()
            .Where(IsDiagramObject)
            .Select(child => new
            {
                Element = child,
                ChildIndex = DiagramCanvas.Children.IndexOf(child)
            })
            .OrderBy(item => Panel.GetZIndex(item.Element))
            .ThenBy(item => item.ChildIndex)
            .Select(item => item.Element)
            .ToList();
    }

    private void NormalizeDiagramZOrder(bool prioritizeTransparentObjects = false)
    {
        var orderedObjects = DiagramCanvas.Children
            .OfType<FrameworkElement>()
            .Where(IsDiagramObject)
            .Select(child => new
            {
                Element = child,
                ChildIndex = DiagramCanvas.Children.IndexOf(child)
            })
            .OrderBy(item => prioritizeTransparentObjects && HasTransparentDiagramBackground(item.Element) ? 0 : 1)
            .ThenBy(item => Panel.GetZIndex(item.Element))
            .ThenBy(item => item.ChildIndex)
            .Select(item => item.Element)
            .ToList();

        for (int i = 0; i < orderedObjects.Count; i++)
        {
            Panel.SetZIndex(orderedObjects[i], i * DiagramLayerStep);
        }
    }

    private void ApplyDefaultDiagramZIndex(FrameworkElement diagramObject)
    {
        List<FrameworkElement> existingObjects = DiagramCanvas.Children
            .OfType<FrameworkElement>()
            .Where(IsDiagramObject)
            .Where(child => !ReferenceEquals(child, diagramObject))
            .ToList();

        if (existingObjects.Count == 0)
        {
            Panel.SetZIndex(diagramObject, 0);
            return;
        }

        int zIndex = HasTransparentDiagramBackground(diagramObject)
            ? existingObjects.Min(Panel.GetZIndex) - DiagramLayerStep
            : existingObjects.Max(Panel.GetZIndex) + DiagramLayerStep;
        Panel.SetZIndex(diagramObject, zIndex);
    }

    private void ApplyTransparentDiagramObjectLayerDefault(FrameworkElement diagramObject)
    {
        if (HasTransparentDiagramBackground(diagramObject))
        {
            ApplyDefaultDiagramZIndex(diagramObject);
        }
    }

    private static bool HasTransparentDiagramBackground(FrameworkElement diagramObject)
    {
        return diagramObject switch
        {
            DiagramShapeControl shape => IsTransparentDiagramColor(shape.BackColorText),
            DiagramLabelControl label => IsTransparentDiagramColor(label.BackColorText),
            _ => false
        };
    }

    private bool BringDiagramObjectForward(FrameworkElement diagramObject)
    {
        List<FrameworkElement> orderedObjects = GetDiagramObjectsInLayerOrder();
        int objectIndex = orderedObjects.FindIndex(child => ReferenceEquals(child, diagramObject));
        if (objectIndex < 0 || objectIndex >= orderedObjects.Count - 1)
        {
            return false;
        }

        Panel.SetZIndex(diagramObject, Panel.GetZIndex(orderedObjects[objectIndex + 1]) + 1);
        return true;
    }

    private bool SendDiagramObjectBackward(FrameworkElement diagramObject)
    {
        List<FrameworkElement> orderedObjects = GetDiagramObjectsInLayerOrder();
        int objectIndex = orderedObjects.FindIndex(child => ReferenceEquals(child, diagramObject));
        if (objectIndex <= 0)
        {
            return false;
        }

        Panel.SetZIndex(diagramObject, Panel.GetZIndex(orderedObjects[objectIndex - 1]) - 1);
        return true;
    }

    private bool SendDiagramObjectToBack(FrameworkElement diagramObject)
    {
        List<FrameworkElement> orderedObjects = GetDiagramObjectsInLayerOrder();
        int objectIndex = orderedObjects.FindIndex(child => ReferenceEquals(child, diagramObject));
        if (objectIndex <= 0)
        {
            return false;
        }

        int backMostZIndex = orderedObjects
            .Where(child => !ReferenceEquals(child, diagramObject))
            .Select(Panel.GetZIndex)
            .DefaultIfEmpty(0)
            .Min();
        Panel.SetZIndex(diagramObject, backMostZIndex - DiagramLayerStep);
        return true;
    }

    private void RemoveDiagramObject(FrameworkElement? diagramObject, bool pushUndo)
    {
        if (diagramObject == null)
        {
            return;
        }

        if (ReferenceEquals(_metadataEditorTarget, diagramObject))
        {
            CommitMetadataEditorChanges();
        }

        DiagramObjectSnapshot? snapshot = CreateDiagramObjectSnapshot(diagramObject);

        switch (diagramObject)
        {
            case DiagramShapeControl shape:
                DetachDiagramShapeHandlers(shape);
                break;

            case DiagramImageControl image:
                DetachDiagramImageHandlers(image);
                break;

            case DiagramLineControl line:
                DetachDiagramLineHandlers(line);
                break;

            case DiagramLabelControl label:
                DetachDiagramLabelHandlers(label);
                break;

            case DiagramWorkflowMarkerControl marker:
                DetachDiagramWorkflowMarkerHandlers(marker);
                break;

            case DiagramPortalControl portal:
                DetachDiagramPortalHandlers(portal);
                break;
        }

        DiagramCanvas.Children.Remove(diagramObject);

        if (ReferenceEquals(_selectedDiagramObject, diagramObject))
        {
            _selectedDiagramObject = null;
            LoadMetadataEditorForSelection();
        }

        if (pushUndo && snapshot != null)
        {
            PushDiagramUndo(DiagramUndoActionKind.Removed, before: snapshot, after: null);
        }
    }

    private void PushDiagramUndo(
        DiagramUndoActionKind kind,
        DiagramObjectSnapshot? before,
        DiagramObjectSnapshot? after)
    {
        _diagramUndoStack.Push(new DiagramUndoAction(kind, before?.Clone(), after?.Clone()));

        if (_diagramUndoStack.Count > MaximumDiagramUndoActions)
        {
            List<DiagramUndoAction> actionsToKeep = _diagramUndoStack
                .Take(MaximumDiagramUndoActions)
                .Reverse()
                .ToList();
            _diagramUndoStack.Clear();
            foreach (DiagramUndoAction action in actionsToKeep)
            {
                _diagramUndoStack.Push(action);
            }
        }
    }

    private static double GetCanvasLeft(FrameworkElement element)
    {
        double left = Canvas.GetLeft(element);
        return double.IsNaN(left) ? 0 : left;
    }

    private static double GetCanvasTop(FrameworkElement element)
    {
        double top = Canvas.GetTop(element);
        return double.IsNaN(top) ? 0 : top;
    }

    private void DiagramShape_EditRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("edit diagram shapes"))
        {
            return;
        }

        if (sender is not DiagramShapeControl shape)
        {
            return;
        }

        var dialog = new DiagramShapeDetailsWindow(
            shape.LabelText,
            shape.OutlineColorText,
            shape.BackColorText)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            DiagramObjectSnapshot? before = CreateDiagramObjectSnapshot(shape);
            shape.ApplyDetails(
                dialog.LabelText,
                dialog.OutlineColorText,
                dialog.BackColorText);
            ApplyTransparentDiagramObjectLayerDefault(shape);
            DiagramObjectSnapshot? after = CreateDiagramObjectSnapshot(shape);
            if (before != null && after != null)
            {
                PushDiagramUndo(DiagramUndoActionKind.Modified, before, after);
            }

            StatusText = "Updated diagram shape.";
        }
    }

    private void DiagramShape_DeleteRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("delete diagram shapes"))
        {
            return;
        }

        if (sender is not DiagramShapeControl shape)
        {
            return;
        }

        RemoveDiagramObject(shape, pushUndo: true);
        StatusText = "Deleted diagram shape.";
    }

    private void DiagramImage_EditRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("edit diagram images"))
        {
            return;
        }

        if (sender is not DiagramImageControl image)
        {
            return;
        }

        var dialog = new DiagramImageDetailsWindow(image.LabelText)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            DiagramObjectSnapshot? before = CreateDiagramObjectSnapshot(image);
            image.ApplyDetails(dialog.LabelText);
            DiagramObjectSnapshot? after = CreateDiagramObjectSnapshot(image);
            if (before != null && after != null)
            {
                PushDiagramUndo(DiagramUndoActionKind.Modified, before, after);
            }

            StatusText = "Updated diagram image.";
        }
    }

    private void DiagramImage_DeleteRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("delete diagram images"))
        {
            return;
        }

        if (sender is not DiagramImageControl image)
        {
            return;
        }

        RemoveDiagramObject(image, pushUndo: true);
        StatusText = "Deleted diagram image.";
    }

    private void DiagramLine_EditRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("edit diagram lines"))
        {
            return;
        }

        if (sender is not DiagramLineControl line)
        {
            return;
        }

        var dialog = new DiagramLineDetailsWindow(line.ColorText, line.HasEndArrow)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            DiagramObjectSnapshot? before = CreateDiagramObjectSnapshot(line);
            line.ApplyDetails(dialog.LineColorText, dialog.HasEndArrow);
            DiagramObjectSnapshot? after = CreateDiagramObjectSnapshot(line);
            if (before != null && after != null)
            {
                PushDiagramUndo(DiagramUndoActionKind.Modified, before, after);
            }

            StatusText = "Updated diagram line.";
        }
    }

    private void DiagramLine_DeleteRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("delete diagram lines"))
        {
            return;
        }

        if (sender is not DiagramLineControl line)
        {
            return;
        }

        RemoveDiagramObject(line, pushUndo: true);
        StatusText = "Deleted diagram line.";
    }

    private void DiagramLine_LooseStateToggleRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("change line edit state"))
        {
            return;
        }

        if (sender is not DiagramLineControl line)
        {
            return;
        }

        DiagramObjectSnapshot? before = CreateDiagramObjectSnapshot(line);
        line.SetLooseState(!line.IsLoose);
        DiagramObjectSnapshot? after = CreateDiagramObjectSnapshot(line);
        if (before != null && after != null)
        {
            PushDiagramUndo(DiagramUndoActionKind.Modified, before, after);
        }

        SelectDiagramObject(line.IsLoose ? line : null);
        StatusText = line.IsLoose
            ? "Diagram line is loose and can be moved or resized."
            : "Diagram line is fixed.";
    }

    private void DiagramLabel_EditRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("edit diagram labels"))
        {
            return;
        }

        if (sender is not DiagramLabelControl label)
        {
            return;
        }

        SelectDiagramObject(label);
        label.BeginEditLabel();
        StatusText = "Editing diagram label.";
    }

    private void DiagramLabel_DeleteRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("delete diagram labels"))
        {
            return;
        }

        if (sender is not DiagramLabelControl label)
        {
            return;
        }

        RemoveDiagramObject(label, pushUndo: true);
        StatusText = "Deleted diagram label.";
    }

    private void DiagramLabel_TetherChangedRequested(object? sender, DiagramLabelTetherChangedEventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("change label tethering"))
        {
            return;
        }

        if (sender is not DiagramLabelControl label)
        {
            return;
        }

        DiagramObjectSnapshot? before = CreateDiagramObjectSnapshot(label);
        label.SetTethered(e.IsTethered);
        DiagramObjectSnapshot? after = CreateDiagramObjectSnapshot(label);
        if (before != null && after != null)
        {
            PushDiagramUndo(DiagramUndoActionKind.Modified, before, after);
        }

        StatusText = e.IsTethered
            ? "Label tethered."
            : "Label untethered.";
    }

    private void DiagramWorkflowMarker_OpenRequested(object? sender, EventArgs e)
    {
        if (sender is not DiagramWorkflowMarkerControl marker)
        {
            return;
        }

        SelectDiagramObject(marker);
        OpenWorkflowItemInSidebar(marker.WorkflowId, marker.WorkflowItemId);
    }

    private void DiagramWorkflowMarker_DeleteRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("delete workflow markers"))
        {
            return;
        }

        if (sender is not DiagramWorkflowMarkerControl marker)
        {
            return;
        }

        RemoveWorkflowItemAndMarker(marker.WorkflowId, marker.WorkflowItemId);
    }

    private async void DiagramPortal_DeleteRequested(object? sender, EventArgs e)
    {
        if (TryBlockDiagramObjectEditWhenLocked("delete portals"))
        {
            return;
        }

        if (sender is not DiagramPortalControl portal)
        {
            return;
        }

        await ClearPairedPortalReferenceAsync(portal);
        RemoveDiagramObject(portal, pushUndo: true);
        StatusText = "Deleted diagram portal.";
    }

    private void RemoveWorkflowItemAndMarker(string workflowId, string workflowItemId)
    {
        if (TryBlockDiagramObjectEditWhenLocked("delete workflow markers"))
        {
            return;
        }

        WorkflowDocument? workflow = FindWorkflow(workflowId);
        WorkflowItem? workflowItem = workflow?.Items.FirstOrDefault(item =>
            string.Equals(item.WorkflowItemId, workflowItemId, StringComparison.OrdinalIgnoreCase));

        if (workflow == null || workflowItem == null)
        {
            FrameworkElement? orphanMarker = FindWorkflowMarkerByWorkflowItem(workflowId, workflowItemId);
            RemoveDiagramObject(orphanMarker, pushUndo: false);
            _expandedWorkflowItems.Remove(workflowItemId);
            _workflowItemDocumentationEditors.Remove(workflowItemId);
            _workflowItemContainers.Remove(workflowItemId);
            StatusText = "Removed orphaned workflow marker.";
            return;
        }

        FrameworkElement? marker = FindDiagramObjectById(workflowItem.MarkerDiagramObjectId) ??
            FindWorkflowMarkerByWorkflowItem(workflowId, workflowItemId);
        workflow.Items.Remove(workflowItem);
        _expandedWorkflowItems.Remove(workflowItemId);
        _workflowItemDocumentationEditors.Remove(workflowItemId);
        _workflowItemContainers.Remove(workflowItemId);
        RemoveDiagramObject(marker, pushUndo: false);
        RebuildWorkflowItemsEditor();
        RefreshWorkflowList();
        StatusText = $"Deleted workflow item {workflowItem.ItemNumber}.";
    }

    private FrameworkElement? FindWorkflowMarkerByWorkflowItem(string workflowId, string workflowItemId)
    {
        return DiagramCanvas.Children
            .OfType<DiagramWorkflowMarkerControl>()
            .FirstOrDefault(marker =>
                string.Equals(marker.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(marker.WorkflowItemId, workflowItemId, StringComparison.OrdinalIgnoreCase));
    }

    private async void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        CloseAllOpenWindows();
        ClearLoadedDiagram();
        StatusText = "Closed all windows and unloaded the diagram.";
        await SaveWorkspaceStateAsync();
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateHistoryAsync(_navigationHistoryService.MoveBack(), "Back");
    }

    private async void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateHistoryAsync(_navigationHistoryService.MoveForward(), "Forward");
    }

    private async Task NavigateHistoryAsync(NavigationHistoryEntry? entry, string direction)
    {
        NotifyNavigationHistoryStateChanged();

        if (entry == null)
        {
            return;
        }

        if (!File.Exists(entry.FilePath) && !DatabaseDocumentService.IsDatabaseDocumentPath(entry.FilePath))
        {
            StatusText = $"Cannot navigate {direction.ToLowerInvariant()}: file no longer exists.";
            return;
        }

        await OpenFileAsync(
            entry.FilePath,
            targetLineNumber: entry.LineNumber,
            targetColumnNumber: entry.ColumnNumber,
            suppressHistory: true);

        StatusText = $"{direction}: {Path.GetFileName(entry.FilePath)} line {entry.LineNumber}.";
        NotifyNavigationHistoryStateChanged();
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

        if (settingsWindow.PersistenceDatabaseImported)
        {
            StatusText = "Imported database export. Restart Surf2 to load the imported setup.";
            return;
        }

        bool connectionSettingsChanged = settingsWindow.ConnectionSettingsWereChanged;
        _appSettings = settingsWindow.Settings;
        _appSettings.EnsureDefaults();
        ApplyInternalLoggingSetting("settings saved");
        await _settingsStore.SaveAsync(_appSettings);
        RebuildReferenceIndexForActiveScope();
        ApplySettingsToOpenWindows();
        ApplyReferenceHighlightsToOpenWindows();
        ApplySettingsToPreview();
        RefreshDiagramImageToolMenu();
        if (_selectedDiagramImageId != null && FindDiagramImageDefinition(_selectedDiagramImageId) == null)
        {
            _selectedDiagramImageId = null;
            DiagramImageToolButton.IsChecked = false;
        }

        StatusText = connectionSettingsChanged
            ? "Settings saved. Persistence connection changes apply next time Surf2 starts."
            : "Settings saved.";
    }

    private void ApplyInternalLoggingSetting(string source)
    {
        _appSettings.Diagnostics ??= new DiagnosticsSettings();
        InternalLogService.Configure(_appSettings.Diagnostics.EnableInternalLogging);
        InternalLogService.Info(
            "Applied internal logging setting.",
            ("Source", source),
            ("Enabled", _appSettings.Diagnostics.EnableInternalLogging),
            ("LogFile", InternalLogService.IsEnabled ? InternalLogService.LogFilePath : "<disabled>"),
            ("LogDirectory", InternalLogService.IsEnabled ? InternalLogService.LogDirectory : "<disabled>"));
    }

    private async void SaveWorkbenchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WorkbenchState workbench = CaptureWorkbenchState();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string defaultName = GetDefaultWorkbenchName(workbench);
            WorkbenchState? existingDefault = _workbenchLibrary.Workbenches.FirstOrDefault(candidate =>
                candidate.IsDefaultForScope &&
                string.Equals(candidate.ScopeId, workbench.ScopeId, StringComparison.OrdinalIgnoreCase));

            if (existingDefault != null)
            {
                workbench.WorkbenchId = existingDefault.WorkbenchId;
                workbench.CreatedAtUtc = existingDefault.CreatedAtUtc == default
                    ? now
                    : existingDefault.CreatedAtUtc;
                _workbenchLibrary.Workbenches.Remove(existingDefault);
            }
            else
            {
                workbench.CreatedAtUtc = now;
            }

            workbench.Name = defaultName;
            workbench.IsDefaultForScope = true;
            workbench.UpdatedAtUtc = now;
            workbench.SavedAtUtc = now;
            _workbenchLibrary.Workbenches.Add(workbench);
            await _workbenchStore.SaveAsync(_workbenchLibrary);
            RefreshSavedWorkbenches(workbench.WorkbenchId);
            StatusText = $"Saved default Workbench '{workbench.Name}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save Workbench: {ex.Message}";
        }
    }

    private async void SaveWorkbenchAsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string initialName = GetSuggestedWorkbenchName();
            string? name = PromptForWorkbenchName(initialName);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (_workbenchLibrary.Workbenches.Any(workbench =>
                    string.Equals(workbench.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    this,
                    "A Workbench with that name already exists. Choose a different name.",
                    "Save Workbench As",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            WorkbenchState workbench = CaptureWorkbenchState();
            workbench.Name = name;
            workbench.IsDefaultForScope = false;
            workbench.CreatedAtUtc = now;
            workbench.UpdatedAtUtc = now;
            workbench.SavedAtUtc = now;

            _workbenchLibrary.Workbenches.Add(workbench);
            await _workbenchStore.SaveAsync(_workbenchLibrary);
            RefreshSavedWorkbenches(workbench.WorkbenchId);
            StatusText = $"Saved Workbench '{workbench.Name}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save Workbench: {ex.Message}";
        }
    }

    private async void DeleteWorkbenchButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorkbenchSelector.SelectedItem is not WorkbenchState workbench)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"Delete saved Workbench '{workbench.Name}'?",
            "Delete Workbench",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _workbenchLibrary.Workbenches.RemoveAll(candidate =>
                string.Equals(candidate.WorkbenchId, workbench.WorkbenchId, StringComparison.OrdinalIgnoreCase));
            await _workbenchStore.SaveAsync(_workbenchLibrary);
            RefreshSavedWorkbenches();
            StatusText = $"Deleted Workbench '{workbench.Name}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not delete Workbench: {ex.Message}";
        }
    }

    private async void WorkbenchSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingWorkbenchSelection ||
            WorkbenchSelector.SelectedItem is not WorkbenchState workbench)
        {
            UpdateWorkbenchCommandState();
            return;
        }

        UpdateWorkbenchCommandState();
        await LoadWorkbenchAsync(workbench, updateSelector: false);
    }

    private WorkbenchState CaptureWorkbenchState()
    {
        CaptureViewportState();
        SyncOpenDocumentStatesFromWindows();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new WorkbenchState
        {
            Name = _activeScope?.Name ?? "No Scope",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SavedAtUtc = now,
            ScopeId = _activeScope?.ScopeId ?? string.Empty,
            ScopeName = _activeScope?.Name ?? "No scope",
            IsCodeViewVisible = CodeViewToggle.IsChecked == true,
            IsDiagramViewVisible = DiagramViewToggle.IsChecked == true,
            ActiveWorkspaceView = _activeWorkspaceView.ToString(),
            WorkspaceSplitOrientation = _workspaceSplitOrientation.ToString(),
            CodeViewMode = _codeViewMode.ToString(),
            CodeCanvasZoom = _canvasZoom,
            CodeViewportHorizontalOffset = WorkspaceScrollViewer.HorizontalOffset,
            CodeViewportVerticalOffset = WorkspaceScrollViewer.VerticalOffset,
            UnloadedResourceIds = GetUnloadedResourceIds().ToList(),
            OpenDocuments = OpenTabs
                .Select(tab => CloneOpenDocumentState(tab.State))
                .ToList(),
            ActiveDocumentPath = GetActiveDocumentPath() ?? string.Empty,
            ActiveDiagramId = _activeDiagramId ?? string.Empty,
            ActiveDiagramName = CurrentDiagramName,
            ActiveDiagramSnapshot = CaptureActiveDiagramSnapshot(),
            IsDiagramLocked = _isDiagramLocked,
            DiagramCanvasZoom = _diagramCanvasZoom,
            DiagramViewportHorizontalOffset = DiagramScrollViewer.HorizontalOffset,
            DiagramViewportVerticalOffset = DiagramScrollViewer.VerticalOffset
        };
    }

    private string GetDefaultWorkbenchName(WorkbenchState workbench)
    {
        if (!string.IsNullOrWhiteSpace(workbench.ScopeName) &&
            !string.Equals(workbench.ScopeName, "No scope", StringComparison.OrdinalIgnoreCase))
        {
            return workbench.ScopeName;
        }

        return "No Scope";
    }

    private string GetSuggestedWorkbenchName()
    {
        string baseName = _activeScope?.Name ?? "Workbench";
        string candidate = $"{baseName} Setup";
        int index = 2;

        while (_workbenchLibrary.Workbenches.Any(workbench =>
                   string.Equals(workbench.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} Setup {index}";
            index++;
        }

        return candidate;
    }

    private string? PromptForWorkbenchName(string initialName)
    {
        var dialog = new DiagramNameWindow(initialName, "Workbench name", "Save Workbench As")
        {
            Owner = this
        };

        return dialog.ShowDialog() == true ? dialog.DiagramName : null;
    }

    private async Task LoadWorkbenchAsync(WorkbenchState workbench, bool updateSelector)
    {
        try
        {
            CloseAllOpenWindows();
            ClearLoadedDiagram();

            _workspaceState = CreateWorkspaceState(workbench);
            _canvasZoom = NormalizeCanvasZoom(workbench.CodeCanvasZoom);
            ApplyCanvasZoom();

            _workspaceSplitOrientation = ParseWorkspaceSplitOrientation(workbench.WorkspaceSplitOrientation);
            SetCodeViewMode(ParseCodeViewMode(workbench.CodeViewMode));
            SetDiagramLockState(workbench.IsDiagramLocked, updateToggle: true);

            Scope? scope = _scopeLibrary.Scopes.FirstOrDefault(candidate =>
                string.Equals(candidate.ScopeId, workbench.ScopeId, StringComparison.OrdinalIgnoreCase));
            if (scope != null)
            {
                _scopeLibrary.LastActiveScopeId = scope.ScopeId;
                await LoadScopeAsync(scope);
            }
            else
            {
                await LoadScopeAsync(null);
            }

            LoadWorkbenchDiagram(workbench);
            SetWorkspaceViewVisibility(
                workbench.IsCodeViewVisible,
                workbench.IsDiagramViewVisible,
                ParseWorkspaceViewKind(workbench.ActiveWorkspaceView));

            foreach (OpenDocumentState documentState in _workspaceState.OpenDocuments.ToList())
            {
                if (File.Exists(documentState.FilePath) || DatabaseDocumentService.IsDatabaseDocumentPath(documentState.FilePath))
                {
                    await OpenFileAsync(documentState.FilePath, documentState);
                }
            }

            if (!string.IsNullOrWhiteSpace(workbench.ActiveDocumentPath))
            {
                SelectOpenDocument(workbench.ActiveDocumentPath);
            }

            UpdateEmptyWorkspaceHint();
            _ = Dispatcher.BeginInvoke(new Action(RestoreViewport), DispatcherPriority.ContextIdle);

            if (updateSelector)
            {
                RefreshSavedWorkbenches(workbench.WorkbenchId);
            }

            StatusText = $"Loaded Workbench '{workbench.DisplayName}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load Workbench: {ex.Message}";
        }
    }

    private void LoadWorkbenchDiagram(WorkbenchState workbench)
    {
        DiagramDocument? diagram = ResolveWorkbenchDiagram(workbench);
        if (diagram == null)
        {
            ClearLoadedDiagram();
            _diagramCanvasZoom = NormalizeCanvasZoom(workbench.DiagramCanvasZoom);
            ApplyDiagramCanvasZoom();
            return;
        }

        LoadDiagram(diagram);
        _diagramCanvasZoom = NormalizeCanvasZoom(workbench.DiagramCanvasZoom);
        ApplyDiagramCanvasZoom();

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            DiagramScrollViewer.UpdateLayout();
            DiagramScrollViewer.ScrollToHorizontalOffset(Math.Max(0, workbench.DiagramViewportHorizontalOffset));
            DiagramScrollViewer.ScrollToVerticalOffset(Math.Max(0, workbench.DiagramViewportVerticalOffset));
        }), DispatcherPriority.ApplicationIdle);
    }

    private DiagramDocument? ResolveWorkbenchDiagram(WorkbenchState workbench)
    {
        DiagramDocument? persistedDiagram = _diagramLibrary.Find(workbench.ActiveDiagramId);
        DiagramDocument? snapshot = workbench.ActiveDiagramSnapshot;

        if (persistedDiagram == null)
        {
            return snapshot;
        }

        if (snapshot == null ||
            string.IsNullOrWhiteSpace(snapshot.DiagramId) ||
            !string.Equals(snapshot.DiagramId, persistedDiagram.DiagramId, StringComparison.OrdinalIgnoreCase))
        {
            return persistedDiagram;
        }

        return persistedDiagram.UpdatedAtUtc >= snapshot.UpdatedAtUtc
            ? persistedDiagram
            : snapshot;
    }

    private DiagramDocument? CaptureActiveDiagramSnapshot()
    {
        bool hasDiagramState =
            !string.IsNullOrWhiteSpace(_activeDiagramId) ||
            DiagramCanvas.Children.OfType<FrameworkElement>().Any(IsDiagramObject) ||
            _currentDiagramWorkflows.Count > 0;

        if (!hasDiagramState)
        {
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DiagramDocument? existingDiagram = _diagramLibrary.Find(_activeDiagramId);
        return new DiagramDocument
        {
            DiagramId = _activeDiagramId ?? string.Empty,
            Name = CurrentDiagramName,
            CreatedAtUtc = existingDiagram?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
            CanvasZoom = _diagramCanvasZoom,
            ViewportHorizontalOffset = DiagramScrollViewer.HorizontalOffset,
            ViewportVerticalOffset = DiagramScrollViewer.VerticalOffset,
            Objects = CaptureDiagramObjects(),
            Workflows = _currentDiagramWorkflows
                .Select(workflow => workflow.Clone())
                .ToList()
        };
    }

    private void ClearLoadedDiagram()
    {
        ClearDiagramObjects();
        _currentDiagramWorkflows = [];
        ResetWorkflowEditor();
        RefreshWorkflowList();
        _diagramUndoStack.Clear();
        SetCurrentDiagramIdentity(null, "Unsaved Diagram");
        _diagramCanvasZoom = 1;
        ApplyDiagramCanvasZoom();
        _diagramViewportInitialized = false;
        _ = Dispatcher.BeginInvoke(new Action(RestoreDiagramViewport), DispatcherPriority.ContextIdle);
    }

    private void CreateNewBlankDiagram()
    {
        ClearLoadedDiagram();
        EnsureDiagramViewVisible();
        StatusText = "Created a new blank diagram. Use Save to name and persist it.";
    }

    private void RefreshSavedWorkbenches(string? selectedWorkbenchId = null)
    {
        string? targetSelection = selectedWorkbenchId;
        if (string.IsNullOrWhiteSpace(targetSelection) &&
            WorkbenchSelector?.SelectedItem is WorkbenchState selectedWorkbench)
        {
            targetSelection = selectedWorkbench.WorkbenchId;
        }

        _isUpdatingWorkbenchSelection = true;
        try
        {
            SavedWorkbenches.Clear();
            foreach (WorkbenchState workbench in _workbenchLibrary.Workbenches
                         .OrderByDescending(GetWorkbenchUpdatedAtUtc))
            {
                SavedWorkbenches.Add(workbench);
            }

            if (WorkbenchSelector != null)
            {
                WorkbenchSelector.SelectedItem = SavedWorkbenches.FirstOrDefault(workbench =>
                    string.Equals(workbench.WorkbenchId, targetSelection, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _isUpdatingWorkbenchSelection = false;
            UpdateWorkbenchCommandState();
        }
    }

    private void UpdateWorkbenchCommandState()
    {
        if (DeleteWorkbenchButton != null)
        {
            DeleteWorkbenchButton.IsEnabled = WorkbenchSelector?.SelectedItem is WorkbenchState;
        }
    }

    private bool NormalizeWorkbenchLibrary()
    {
        bool changed = false;
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (WorkbenchState workbench in _workbenchLibrary.Workbenches)
        {
            if (string.IsNullOrWhiteSpace(workbench.WorkbenchId) ||
                !seenIds.Add(workbench.WorkbenchId))
            {
                workbench.WorkbenchId = Guid.NewGuid().ToString("N");
                seenIds.Add(workbench.WorkbenchId);
                changed = true;
            }

            DateTimeOffset savedAt = workbench.SavedAtUtc == default ? now : workbench.SavedAtUtc;
            if (workbench.CreatedAtUtc == default)
            {
                workbench.CreatedAtUtc = savedAt;
                changed = true;
            }

            if (workbench.UpdatedAtUtc == default)
            {
                workbench.UpdatedAtUtc = savedAt;
                changed = true;
            }

            if (workbench.SavedAtUtc == default ||
                workbench.SavedAtUtc != workbench.UpdatedAtUtc)
            {
                workbench.SavedAtUtc = workbench.UpdatedAtUtc;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(workbench.Name))
            {
                workbench.Name = !string.IsNullOrWhiteSpace(workbench.ScopeName)
                    ? workbench.ScopeName
                    : "Workbench";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(workbench.ScopeName))
            {
                workbench.ScopeName = "No scope";
                changed = true;
            }
        }

        return changed;
    }

    private bool NormalizeDatabaseSnapshots()
    {
        bool changed = false;
        foreach (DatabaseMetadataSnapshot snapshot in _databaseSnapshots.Snapshots)
        {
            changed |= _databaseMetadataImportService.NormalizeSnapshot(snapshot);
        }

        return changed;
    }

    private static DateTimeOffset GetWorkbenchUpdatedAtUtc(WorkbenchState workbench)
    {
        if (workbench.UpdatedAtUtc != default)
        {
            return workbench.UpdatedAtUtc;
        }

        if (workbench.SavedAtUtc != default)
        {
            return workbench.SavedAtUtc;
        }

        return workbench.CreatedAtUtc;
    }

    private void SyncOpenDocumentStatesFromWindows()
    {
        foreach (FloatingCodeWindow window in _openWindows.Values)
        {
            SyncOpenDocumentStateFromWindow(window.State, window);
        }

        foreach (FloatingSpreadsheetWindow window in _openSpreadsheetWindows.Values)
        {
            SyncOpenDocumentStateFromWindow(window.State, window);
        }
    }

    private void SyncOpenDocumentStateFromWindow(OpenDocumentState state, FrameworkElement window)
    {
        double left = Canvas.GetLeft(window);
        double top = Canvas.GetTop(window);
        if (!double.IsNaN(left))
        {
            state.Left = left;
        }

        if (!double.IsNaN(top))
        {
            state.Top = top;
        }

        if (!double.IsNaN(window.Width) && window.Width > 0)
        {
            state.Width = window.Width;
        }

        if (!double.IsNaN(window.Height) && window.Height > 0)
        {
            state.Height = window.Height;
        }
    }

    private string? GetActiveDocumentPath()
    {
        if (_codeViewMode == CodeViewMode.Tabs &&
            CodeDocumentsTabControl.SelectedItem is TabItem { Tag: string selectedPath })
        {
            return selectedPath;
        }

        return _openWindows.Values
            .Cast<FrameworkElement>()
            .Concat(_openSpreadsheetWindows.Values)
            .OrderByDescending(Panel.GetZIndex)
            .Select(GetWindowFilePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    }

    private void SelectOpenDocument(string filePath)
    {
        if (_openWindows.TryGetValue(filePath, out FloatingCodeWindow? window))
        {
            ActivateCodeWindow(window);
            RevealWindow(window);
            return;
        }

        if (_openSpreadsheetWindows.TryGetValue(filePath, out FloatingSpreadsheetWindow? spreadsheetWindow))
        {
            SetActiveCodeWindow(null, syncOpenTabsSelection: false);
            BringToFront(spreadsheetWindow);
            RevealWindow(spreadsheetWindow);
        }
    }

    private static WorkspaceState CreateWorkspaceState(WorkbenchState workbench)
    {
        return new WorkspaceState
        {
            CanvasZoom = workbench.CodeCanvasZoom,
            ViewportHorizontalOffset = workbench.CodeViewportHorizontalOffset,
            ViewportVerticalOffset = workbench.CodeViewportVerticalOffset,
            UnloadedResourceIds = (workbench.UnloadedResourceIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OpenDocuments = new ObservableCollection<OpenDocumentState>(
                workbench.OpenDocuments.Select(CloneOpenDocumentState))
        };
    }

    private static OpenDocumentState CloneOpenDocumentState(OpenDocumentState state)
    {
        return new OpenDocumentState
        {
            FilePath = state.FilePath,
            DisplayName = state.DisplayName,
            Left = state.Left,
            Top = state.Top,
            Width = state.Width,
            Height = state.Height,
            FontSize = state.FontSize
        };
    }

    private static CodeViewMode ParseCodeViewMode(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out CodeViewMode mode)
            ? mode
            : CodeViewMode.Canvas;
    }

    private static WorkspaceSplitOrientation ParseWorkspaceSplitOrientation(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out WorkspaceSplitOrientation orientation)
            ? orientation
            : WorkspaceSplitOrientation.DiagramBottom;
    }

    private static WorkspaceViewKind ParseWorkspaceViewKind(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out WorkspaceViewKind viewKind)
            ? viewKind
            : WorkspaceViewKind.Code;
    }

    private void CloseAllOpenWindows()
    {
        SetActiveCodeWindow(null);
        WorkspaceCanvas.Children.Clear();
        ClearCodeDocumentTabs();
        _openWindows.Clear();
        _openSpreadsheetWindows.Clear();
        _workspaceState.OpenDocuments.Clear();
        OpenTabs.Clear();
        _navigationHistoryService.Clear();
        NotifyNavigationHistoryStateChanged();
        ClearReferencePreview("Single-click a reference to preview it.");
        UpdateEmptyWorkspaceHint();
    }

    private void OpenTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingOpenTabsSelection)
        {
            return;
        }

        if (OpenTabsList.SelectedItem is OpenWindowItem item)
        {
            InternalLogService.Info(
                "Open tabs list selection changed.",
                ("FileName", item.FileName),
                ("Path", item.FilePath),
                ("SelectedIndex", OpenTabsList.SelectedIndex),
                ("OpenTabsCount", OpenTabs.Count),
                ("CodeViewMode", _codeViewMode),
                ("ActiveCodeWindow", _activeCodeWindow?.State.FilePath));
            RevealOpenTabSafely(item);
        }
    }

    private void OpenTabsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
        {
            return;
        }

        ListBoxItem? listBoxItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (listBoxItem?.DataContext is OpenWindowItem item)
        {
            InternalLogService.Info(
                "Open tabs list mouse button up.",
                ("FileName", item.FileName),
                ("Path", item.FilePath),
                ("SelectedIndex", OpenTabsList.SelectedIndex),
                ("OpenTabsCount", OpenTabs.Count),
                ("CodeViewMode", _codeViewMode),
                ("OriginalSource", e.OriginalSource?.GetType().FullName));
            RevealOpenTabSafely(item);
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
            InternalLogService.Info(
                "Open tabs list right button down.",
                ("FileName", item.FileName),
                ("Path", item.FilePath),
                ("SelectedIndex", OpenTabsList.SelectedIndex),
                ("OpenTabsCount", OpenTabs.Count),
                ("CodeViewMode", _codeViewMode),
                ("OriginalSource", e.OriginalSource?.GetType().FullName));
            RevealOpenTabSafely(item);
        }
    }

    private async void CloseOpenTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (OpenTabsList.SelectedItem is not OpenWindowItem item)
        {
            return;
        }

        await CloseOpenTabAsync(item.FilePath);
    }

    private async void OpenTabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenWindowItem item)
        {
            await CloseOpenTabAsync(item.FilePath);
        }

        e.Handled = true;
    }

    private async void CodeDocumentTabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        string? filePath = (sender as FrameworkElement)?.Tag as string;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            await CloseOpenTabAsync(filePath);
        }

        e.Handled = true;
    }

    private async Task CloseOpenTabAsync(string filePath)
    {
        if (_openWindows.TryGetValue(filePath, out FloatingCodeWindow? window))
        {
            await CloseOpenWindowAsync(window);
            return;
        }

        if (_openSpreadsheetWindows.TryGetValue(filePath, out FloatingSpreadsheetWindow? spreadsheetWindow))
        {
            await CloseSpreadsheetWindowAsync(spreadsheetWindow);
        }
    }

    private async void CloseAllButThisOpenTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (OpenTabsList.SelectedItem is not OpenWindowItem item)
        {
            return;
        }

        List<FloatingCodeWindow> codeWindowsToClose = _openWindows.Values
            .Where(window => !string.Equals(window.State.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<FloatingSpreadsheetWindow> spreadsheetWindowsToClose = _openSpreadsheetWindows.Values
            .Where(window => !string.Equals(window.State.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (FloatingCodeWindow window in codeWindowsToClose)
        {
            CloseOpenWindow(window);
        }

        foreach (FloatingSpreadsheetWindow window in spreadsheetWindowsToClose)
        {
            CloseSpreadsheetWindow(window);
        }

        if (_openWindows.TryGetValue(item.FilePath, out FloatingCodeWindow? remainingWindow))
        {
            ActivateCodeWindow(remainingWindow);
            RevealWindow(remainingWindow);
        }
        else if (_openSpreadsheetWindows.TryGetValue(item.FilePath, out FloatingSpreadsheetWindow? remainingSpreadsheetWindow))
        {
            SetActiveCodeWindow(null, syncOpenTabsSelection: false);
            BringToFront(remainingSpreadsheetWindow);
            RevealWindow(remainingSpreadsheetWindow);
        }

        StatusText = $"Closed all but {item.FileName}.";
        await SaveWorkspaceStateAsync();
    }

    private async void CloseAllOpenTabsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseAllOpenWindows();
        StatusText = "Closed all windows.";
        await SaveWorkspaceStateAsync();
    }

    private void RevealOpenTab(OpenWindowItem item)
    {
        if (_openWindows.TryGetValue(item.FilePath, out FloatingCodeWindow? window))
        {
            ActivateCodeWindow(window);
            RevealWindow(window);
            StatusText = $"Focused {item.FileName}";
            return;
        }

        if (_openSpreadsheetWindows.TryGetValue(item.FilePath, out FloatingSpreadsheetWindow? spreadsheetWindow))
        {
            SetActiveCodeWindow(null, syncOpenTabsSelection: false);
            BringToFront(spreadsheetWindow);
            RevealWindow(spreadsheetWindow);
            StatusText = $"Focused {item.FileName}";
        }
    }

    private void RevealOpenTabSafely(OpenWindowItem item)
    {
        InternalLogService.Info(
            "Revealing open tab.",
            ("FileName", item.FileName),
            ("Path", item.FilePath),
            ("OpenTabsCount", OpenTabs.Count),
            ("CodeWindowExists", _openWindows.ContainsKey(item.FilePath)),
            ("SpreadsheetWindowExists", _openSpreadsheetWindows.ContainsKey(item.FilePath)),
            ("CodeViewMode", _codeViewMode),
            ("ActiveCodeWindow", _activeCodeWindow?.State.FilePath));

        try
        {
            RevealOpenTab(item);
            InternalLogService.Info(
                "Revealed open tab.",
                ("FileName", item.FileName),
                ("Path", item.FilePath),
                ("OpenTabsCount", OpenTabs.Count),
                ("CodeViewMode", _codeViewMode),
                ("ActiveCodeWindow", _activeCodeWindow?.State.FilePath));
        }
        catch (Exception ex)
        {
            InternalLogService.Error(
                ex,
                "Failed to reveal open tab.",
                ("FileName", item.FileName),
                ("Path", item.FilePath),
                ("OpenTabsCount", OpenTabs.Count),
                ("CodeWindowExists", _openWindows.ContainsKey(item.FilePath)),
                ("SpreadsheetWindowExists", _openSpreadsheetWindows.ContainsKey(item.FilePath)),
                ("CodeViewMode", _codeViewMode),
                ("ActiveCodeWindow", _activeCodeWindow?.State.FilePath));
            StatusText = $"Could not focus {item.FileName}: {ex.Message}";
        }
    }

    private void ActivateCodeWindow(FloatingCodeWindow window)
    {
        SetActiveCodeWindow(window);
        BringToFront(window);
        StatusText = $"Focused {GetDocumentDisplayName(window.State)}";
    }

    private void SetActiveCodeWindow(FloatingCodeWindow? window, bool syncOpenTabsSelection = true)
    {
        if (!ReferenceEquals(_activeCodeWindow, window))
        {
            if (_activeCodeWindow != null)
            {
                _activeCodeWindow.IsActive = false;
            }

            _activeCodeWindow = window;

            if (_activeCodeWindow != null)
            {
                _activeCodeWindow.IsActive = true;
            }
        }

        if (syncOpenTabsSelection)
        {
            SelectOpenTabForActiveCodeWindow();
        }
    }

    private void SelectOpenTabForActiveCodeWindow()
    {
        OpenWindowItem? activeItem = _activeCodeWindow == null
            ? null
            : OpenTabs.FirstOrDefault(tab =>
                string.Equals(tab.FilePath, _activeCodeWindow.State.FilePath, StringComparison.OrdinalIgnoreCase));

        SelectOpenTabItem(activeItem);
    }

    private void SelectOpenTabItem(OpenWindowItem? item)
    {
        if (ReferenceEquals(OpenTabsList.SelectedItem, item))
        {
            return;
        }

        InternalLogService.Info(
            "Selecting open tab item.",
            ("FileName", item?.FileName),
            ("Path", item?.FilePath),
            ("OpenTabsCount", OpenTabs.Count),
            ("CodeViewMode", _codeViewMode),
            ("ActiveCodeWindow", _activeCodeWindow?.State.FilePath));

        _isUpdatingOpenTabsSelection = true;
        try
        {
            OpenTabsList.SelectedItem = item;
        }
        finally
        {
            _isUpdatingOpenTabsSelection = false;
        }
    }

    private void BringToFront(FrameworkElement window)
    {
        if (_codeViewMode == CodeViewMode.Tabs && SelectCodeDocumentTab(window))
        {
            return;
        }

        Panel.SetZIndex(window, ++_zIndex);
    }

    private void RevealWindow(FrameworkElement window)
    {
        if (_codeViewMode == CodeViewMode.Tabs && SelectCodeDocumentTab(window))
        {
            return;
        }

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

    private bool SelectCodeDocumentTab(FrameworkElement window)
    {
        string? filePath = GetWindowFilePath(window);
        return !string.IsNullOrWhiteSpace(filePath) && SelectCodeDocumentTab(filePath);
    }

    private bool SelectCodeDocumentTab(string filePath)
    {
        TabItem? tabItem = FindCodeDocumentTab(filePath);
        if (tabItem == null)
        {
            InternalLogService.Warning(
                "Could not find code document tab to select.",
                ("Path", filePath),
                ("TabCount", CodeDocumentsTabControl.Items.Count),
                ("OpenTabsCount", OpenTabs.Count),
                ("CodeViewMode", _codeViewMode));
            return false;
        }

        if (!ReferenceEquals(CodeDocumentsTabControl.SelectedItem, tabItem))
        {
            InternalLogService.Info(
                "Selecting code document tab.",
                ("Path", filePath),
                ("TabCount", CodeDocumentsTabControl.Items.Count),
                ("OpenTabsCount", OpenTabs.Count),
                ("SelectedIndex", CodeDocumentsTabControl.SelectedIndex),
                ("CodeViewMode", _codeViewMode),
                ("ActiveCodeWindow", _activeCodeWindow?.State.FilePath));
            CodeDocumentsTabControl.SelectedItem = tabItem;
        }

        return true;
    }

    private void ScrollWindowToPositionAndReveal(
        FloatingCodeWindow window,
        int lineNumber,
        int columnNumber,
        bool selectLine = false,
        bool suppressCursorPositionChanged = false)
    {
        window.ScrollToPosition(lineNumber, columnNumber, selectLine, suppressCursorPositionChanged);
        RevealDocumentPositionInWindowAfterScroll(window, lineNumber, columnNumber);
    }

    private void RevealDocumentPositionInWindowAfterScroll(
        FloatingCodeWindow window,
        int lineNumber,
        int columnNumber)
    {
        if (!window.IsLoaded)
        {
            RoutedEventHandler? loadedHandler = null;
            loadedHandler = (_, _) =>
            {
                window.Loaded -= loadedHandler;
                RevealDocumentPositionInWindowAfterScroll(window, lineNumber, columnNumber);
            };
            window.Loaded += loadedHandler;
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                RevealDocumentPositionInWindow(window, lineNumber, columnNumber);
            }), DispatcherPriority.ApplicationIdle);
        }), DispatcherPriority.ContextIdle);
    }

    private void RevealDocumentPositionInWindow(
        FloatingCodeWindow window,
        int lineNumber,
        int columnNumber)
    {
        if (_codeViewMode == CodeViewMode.Tabs && SelectCodeDocumentTab(window))
        {
            return;
        }

        double left = Canvas.GetLeft(window);
        double top = Canvas.GetTop(window);

        if (double.IsNaN(left) || double.IsNaN(top))
        {
            return;
        }

        Point positionInWindow = window.GetDocumentPositionInWindow(lineNumber, columnNumber);
        const double horizontalMargin = 64;
        const double verticalMargin = 80;

        double targetHorizontalOffset = Math.Max(0, ((left + positionInWindow.X) * _canvasZoom) - horizontalMargin);
        double targetVerticalOffset = Math.Max(0, ((top + positionInWindow.Y) * _canvasZoom) - verticalMargin);

        WorkspaceScrollViewer.ScrollToHorizontalOffset(targetHorizontalOffset);
        WorkspaceScrollViewer.ScrollToVerticalOffset(targetVerticalOffset);
        CaptureViewportState();
    }

    private Point FindReferenceWindowPlacement(FloatingCodeWindow sourceWindow, double width, double height)
    {
        double sourceLeft = Canvas.GetLeft(sourceWindow);
        double sourceTop = Canvas.GetTop(sourceWindow);

        if (double.IsNaN(sourceLeft))
        {
            sourceLeft = 0;
        }

        if (double.IsNaN(sourceTop))
        {
            sourceTop = 0;
        }

        double left = Math.Clamp(sourceLeft + sourceWindow.Width + 28, 0, VirtualCanvasWidth - width);
        double top = Math.Clamp(sourceTop, 0, VirtualCanvasHeight - height);
        const double verticalStep = 42;

        for (int attempt = 0; attempt < 200; attempt++)
        {
            double candidateTop = Math.Clamp(top + (attempt * verticalStep), 0, VirtualCanvasHeight - height);
            var candidate = new Rect(left, candidateTop, width, height);

            if (!IntersectsOpenWindow(candidate))
            {
                return new Point(left, candidateTop);
            }

            if (candidateTop >= VirtualCanvasHeight - height)
            {
                break;
            }
        }

        return new Point(
            Math.Clamp(sourceLeft + 48, 0, VirtualCanvasWidth - width),
            Math.Clamp(sourceTop + 48, 0, VirtualCanvasHeight - height));
    }

    private bool IntersectsOpenWindow(Rect candidate)
    {
        foreach (FloatingCodeWindow window in _openWindows.Values)
        {
            double left = Canvas.GetLeft(window);
            double top = Canvas.GetTop(window);

            if (double.IsNaN(left) || double.IsNaN(top))
            {
                continue;
            }

            var existing = new Rect(left, top, window.Width, window.Height);
            existing.Intersect(candidate);

            if (!existing.IsEmpty && existing.Width * existing.Height > 400)
            {
                return true;
            }
        }

        foreach (FloatingSpreadsheetWindow window in _openSpreadsheetWindows.Values)
        {
            double left = Canvas.GetLeft(window);
            double top = Canvas.GetTop(window);

            if (double.IsNaN(left) || double.IsNaN(top))
            {
                continue;
            }

            var existing = new Rect(left, top, window.Width, window.Height);
            existing.Intersect(candidate);

            if (!existing.IsEmpty && existing.Width * existing.Height > 400)
            {
                return true;
            }
        }

        return false;
    }

    private void AddOpenTab(OpenDocumentState state)
    {
        if (OpenTabs.Any(tab => string.Equals(tab.FilePath, state.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        OpenTabs.Add(new OpenWindowItem(
            state,
            GetCodeTypeLabel(state.FilePath),
            GetOpenTabTypeBackBrush(state.FilePath)));
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

    private void SetCodeViewMode(CodeViewMode mode)
    {
        bool modeChanged = _codeViewMode != mode;
        _codeViewMode = mode;

        if (CodeCanvasModeToggle == null || CodeTabModeToggle == null)
        {
            return;
        }

        _isUpdatingCodeViewMode = true;
        try
        {
            CodeCanvasModeToggle.IsChecked = mode == CodeViewMode.Canvas;
            CodeTabModeToggle.IsChecked = mode == CodeViewMode.Tabs;
        }
        finally
        {
            _isUpdatingCodeViewMode = false;
        }

        if (modeChanged)
        {
            ApplyCodeViewMode();
            StatusText = mode == CodeViewMode.Canvas
                ? "Code View switched to canvas mode."
                : "Code View switched to tab mode.";
        }
        else
        {
            UpdateEmptyWorkspaceHint();
        }
    }

    private void ApplyCodeViewMode()
    {
        if (WorkspaceScrollViewer == null ||
            CodeDocumentsTabControl == null)
        {
            return;
        }

        if (_codeViewMode == CodeViewMode.Tabs)
        {
            StopCodeShiftPan();
            StopCodeCtrlShiftZoom();
            MoveOpenWindowsToCodeTabs();
            WorkspaceScrollViewer.Visibility = Visibility.Collapsed;
            CodeDocumentsTabControl.Visibility = Visibility.Visible;
            UpdateEmptyWorkspaceHint();
            return;
        }

        MoveOpenWindowsToCodeCanvas();
        CodeDocumentsTabControl.Visibility = Visibility.Collapsed;
        WorkspaceScrollViewer.Visibility = Visibility.Visible;
        UpdateEmptyWorkspaceHint();
    }

    private void MoveOpenWindowsToCodeTabs()
    {
        ClearCodeDocumentTabs();

        foreach (OpenWindowItem item in OpenTabs.ToList())
        {
            FrameworkElement? window = GetOpenWindowElement(item.FilePath);
            if (window != null)
            {
                AddWindowToCodeTabs(window, item.State, select: false);
            }
        }

        if (CodeDocumentsTabControl.Items.Count > 0 && CodeDocumentsTabControl.SelectedItem == null)
        {
            CodeDocumentsTabControl.SelectedIndex = 0;
        }
    }

    private void MoveOpenWindowsToCodeCanvas()
    {
        foreach (OpenWindowItem item in OpenTabs.ToList())
        {
            FrameworkElement? window = GetOpenWindowElement(item.FilePath);
            if (window != null)
            {
                AddWindowToCodeCanvas(window, item.State, select: false);
            }
        }

        ClearCodeDocumentTabs();
    }

    private FrameworkElement? GetOpenWindowElement(string filePath)
    {
        if (_openWindows.TryGetValue(filePath, out FloatingCodeWindow? codeWindow))
        {
            return codeWindow;
        }

        if (_openSpreadsheetWindows.TryGetValue(filePath, out FloatingSpreadsheetWindow? spreadsheetWindow))
        {
            return spreadsheetWindow;
        }

        return null;
    }

    private void CodeDocumentsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CodeDocumentsTabControl) ||
            CodeDocumentsTabControl.SelectedItem is not TabItem { Tag: string filePath })
        {
            return;
        }

        try
        {
            InternalLogService.Info(
                "Code documents tab selection changed.",
                ("Path", filePath),
                ("SelectedIndex", CodeDocumentsTabControl.SelectedIndex),
                ("TabCount", CodeDocumentsTabControl.Items.Count),
                ("OpenTabsCount", OpenTabs.Count),
                ("OpenCodeWindows", _openWindows.Count),
                ("OpenSpreadsheetWindows", _openSpreadsheetWindows.Count),
                ("CodeViewMode", _codeViewMode),
                ("ActiveCodeWindow", _activeCodeWindow?.State.FilePath));

            OpenWindowItem? openTab = OpenTabs.FirstOrDefault(tab =>
                string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            if (_openWindows.TryGetValue(filePath, out FloatingCodeWindow? window))
            {
                SetActiveCodeWindow(window);
                return;
            }

            SelectOpenTabItem(openTab);
            if (_openSpreadsheetWindows.ContainsKey(filePath))
            {
                SetActiveCodeWindow(null, syncOpenTabsSelection: false);
            }
        }
        catch (Exception ex)
        {
            InternalLogService.Error(
                ex,
                "Failed to switch code document tab.",
                ("Path", filePath),
                ("SelectedIndex", CodeDocumentsTabControl.SelectedIndex),
                ("TabCount", CodeDocumentsTabControl.Items.Count),
                ("OpenTabsCount", OpenTabs.Count),
                ("OpenCodeWindows", _openWindows.Count),
                ("OpenSpreadsheetWindows", _openSpreadsheetWindows.Count),
                ("CodeViewMode", _codeViewMode),
                ("ActiveCodeWindow", _activeCodeWindow?.State.FilePath));
            StatusText = $"Could not switch tab: {ex.Message}";
        }
    }

    private void ApplyDiagramCanvasZoom()
    {
        DiagramScaleTransform.ScaleX = _diagramCanvasZoom;
        DiagramScaleTransform.ScaleY = _diagramCanvasZoom;
    }

    private void ApplyWorkspaceViewLayout()
    {
        if (CodeViewRow == null ||
            WorkspaceViewSplitterRow == null ||
            DiagramViewRow == null ||
            CodeViewColumn == null ||
            WorkspaceViewSplitterColumn == null ||
            DiagramViewColumn == null ||
            CodeViewHost == null ||
            DiagramViewHost == null ||
            WorkspaceViewSplitter == null ||
            SplitOrientationButton == null ||
            SplitOrientationIcon == null ||
            SplitOrientationIconRotation == null)
        {
            return;
        }

        bool showCode = CodeViewToggle?.IsChecked == true;
        bool showDiagram = DiagramViewToggle?.IsChecked == true;

        if (!showCode && !showDiagram)
        {
            showCode = true;
        }

        CodeViewHost.Visibility = showCode ? Visibility.Visible : Visibility.Collapsed;
        DiagramViewHost.Visibility = showDiagram ? Visibility.Visible : Visibility.Collapsed;
        UpdateSplitOrientationButton(showCode && showDiagram);
        UpdateWorkspaceCanvasBackgrounds(showCode, showDiagram);

        if (showCode && showDiagram)
        {
            ApplySplitWorkspaceLayout();
            EnsureDiagramViewportInitialized();
            return;
        }

        WorkspaceViewSplitter.Visibility = Visibility.Collapsed;
        SetSingleWorkspaceAxis();

        if (showCode)
        {
            CodeViewRow.MinHeight = 140;
            CodeViewColumn.MinWidth = 140;
            DiagramViewRow.MinHeight = 0;
            DiagramViewColumn.MinWidth = 0;
            PlaceWorkspaceElement(CodeViewHost, 0, 0);
            return;
        }

        CodeViewRow.MinHeight = 140;
        CodeViewColumn.MinWidth = 140;
        DiagramViewRow.MinHeight = 0;
        DiagramViewColumn.MinWidth = 0;
        PlaceWorkspaceElement(DiagramViewHost, 0, 0);
        EnsureDiagramViewportInitialized();
    }

    private void ApplySplitWorkspaceLayout()
    {
        WorkspaceViewSplitter.Visibility = Visibility.Visible;

        switch (_workspaceSplitOrientation)
        {
            case WorkspaceSplitOrientation.DiagramTop:
                SetVerticalWorkspaceAxis();
                PlaceWorkspaceElement(DiagramViewHost, 0, 0);
                PlaceWorkspaceElement(CodeViewHost, 2, 0);
                ConfigureWorkspaceSplitter(row: 1, column: 0, GridResizeDirection.Rows);
                break;

            case WorkspaceSplitOrientation.DiagramRight:
                SetHorizontalWorkspaceAxis();
                PlaceWorkspaceElement(CodeViewHost, 0, 0);
                PlaceWorkspaceElement(DiagramViewHost, 0, 2);
                ConfigureWorkspaceSplitter(row: 0, column: 1, GridResizeDirection.Columns);
                break;

            case WorkspaceSplitOrientation.DiagramBottom:
                SetVerticalWorkspaceAxis();
                PlaceWorkspaceElement(CodeViewHost, 0, 0);
                PlaceWorkspaceElement(DiagramViewHost, 2, 0);
                ConfigureWorkspaceSplitter(row: 1, column: 0, GridResizeDirection.Rows);
                break;

            case WorkspaceSplitOrientation.DiagramLeft:
                SetHorizontalWorkspaceAxis();
                PlaceWorkspaceElement(DiagramViewHost, 0, 0);
                PlaceWorkspaceElement(CodeViewHost, 0, 2);
                ConfigureWorkspaceSplitter(row: 0, column: 1, GridResizeDirection.Columns);
                break;
        }
    }

    private void SetActiveWorkspaceView(WorkspaceViewKind activeView)
    {
        if (_activeWorkspaceView == activeView)
        {
            return;
        }

        _activeWorkspaceView = activeView;
        UpdateWorkspaceCanvasBackgrounds(
            CodeViewToggle?.IsChecked == true,
            DiagramViewToggle?.IsChecked == true);
    }

    private void UpdateWorkspaceCanvasBackgrounds(bool showCode, bool showDiagram)
    {
        bool isSplitView = showCode && showDiagram;

        if (!isSplitView)
        {
            if (showCode)
            {
                _activeWorkspaceView = WorkspaceViewKind.Code;
            }
            else if (showDiagram)
            {
                _activeWorkspaceView = WorkspaceViewKind.Diagram;
            }

            WorkspaceCanvas.Background = ActiveCodeCanvasBrush;
            DiagramCanvas.Background = ActiveDiagramCanvasBrush;
            return;
        }

        WorkspaceCanvas.Background = _activeWorkspaceView == WorkspaceViewKind.Code
            ? ActiveCodeCanvasBrush
            : InactiveCanvasBrush;
        DiagramCanvas.Background = _activeWorkspaceView == WorkspaceViewKind.Diagram
            ? ActiveDiagramCanvasBrush
            : InactiveCanvasBrush;
    }

    private void SetSingleWorkspaceAxis()
    {
        CodeViewRow.Height = new GridLength(1, GridUnitType.Star);
        WorkspaceViewSplitterRow.Height = new GridLength(0);
        DiagramViewRow.Height = new GridLength(0);
        CodeViewColumn.Width = new GridLength(1, GridUnitType.Star);
        WorkspaceViewSplitterColumn.Width = new GridLength(0);
        DiagramViewColumn.Width = new GridLength(0);
    }

    private void SetVerticalWorkspaceAxis()
    {
        CodeViewRow.MinHeight = 140;
        DiagramViewRow.MinHeight = 140;
        CodeViewColumn.MinWidth = 0;
        DiagramViewColumn.MinWidth = 0;
        CodeViewRow.Height = new GridLength(1, GridUnitType.Star);
        WorkspaceViewSplitterRow.Height = new GridLength(5);
        DiagramViewRow.Height = new GridLength(1, GridUnitType.Star);
        CodeViewColumn.Width = new GridLength(1, GridUnitType.Star);
        WorkspaceViewSplitterColumn.Width = new GridLength(0);
        DiagramViewColumn.Width = new GridLength(0);
    }

    private void SetHorizontalWorkspaceAxis()
    {
        CodeViewRow.MinHeight = 0;
        DiagramViewRow.MinHeight = 0;
        CodeViewColumn.MinWidth = 140;
        DiagramViewColumn.MinWidth = 140;
        CodeViewRow.Height = new GridLength(1, GridUnitType.Star);
        WorkspaceViewSplitterRow.Height = new GridLength(0);
        DiagramViewRow.Height = new GridLength(0);
        CodeViewColumn.Width = new GridLength(1, GridUnitType.Star);
        WorkspaceViewSplitterColumn.Width = new GridLength(5);
        DiagramViewColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private static void PlaceWorkspaceElement(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private void ConfigureWorkspaceSplitter(int row, int column, GridResizeDirection resizeDirection)
    {
        Grid.SetRow(WorkspaceViewSplitter, row);
        Grid.SetColumn(WorkspaceViewSplitter, column);
        WorkspaceViewSplitter.ResizeDirection = resizeDirection;

        if (resizeDirection == GridResizeDirection.Rows)
        {
            WorkspaceViewSplitter.Height = 5;
            WorkspaceViewSplitter.Width = double.NaN;
            WorkspaceViewSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            WorkspaceViewSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        WorkspaceViewSplitter.Width = 5;
        WorkspaceViewSplitter.Height = double.NaN;
        WorkspaceViewSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
        WorkspaceViewSplitter.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void UpdateSplitOrientationButton(bool isEnabled)
    {
        SplitOrientationButton.IsEnabled = isEnabled;
        SplitOrientationIcon.Opacity = isEnabled ? 1 : 0.35;
        SplitOrientationIconRotation.Angle = GetSplitOrientationIconAngle();
        SplitOrientationButton.ToolTip = isEnabled
            ? GetSplitOrientationToolTip()
            : "Enable Code and Diagram to change split orientation";
    }

    private double GetSplitOrientationIconAngle()
    {
        return _workspaceSplitOrientation switch
        {
            WorkspaceSplitOrientation.DiagramLeft => 0,
            WorkspaceSplitOrientation.DiagramTop => 90,
            WorkspaceSplitOrientation.DiagramRight => 180,
            WorkspaceSplitOrientation.DiagramBottom => 270,
            _ => 270
        };
    }

    private string GetSplitOrientationToolTip()
    {
        return _workspaceSplitOrientation switch
        {
            WorkspaceSplitOrientation.DiagramTop => "Diagram on top, Code on bottom",
            WorkspaceSplitOrientation.DiagramRight => "Diagram on right, Code on left",
            WorkspaceSplitOrientation.DiagramBottom => "Diagram on bottom, Code on top",
            WorkspaceSplitOrientation.DiagramLeft => "Diagram on left, Code on right",
            _ => "Change split orientation"
        };
    }

    private void EnsureCodeViewVisible()
    {
        if (CodeViewToggle.IsChecked == true)
        {
            return;
        }

        _isUpdatingViewToggles = true;
        CodeViewToggle.IsChecked = true;
        _isUpdatingViewToggles = false;
        ApplyWorkspaceViewLayout();
    }

    private void EnsureDiagramViewVisible()
    {
        if (DiagramViewToggle.IsChecked != true)
        {
            _isUpdatingViewToggles = true;
            DiagramViewToggle.IsChecked = true;
            _isUpdatingViewToggles = false;
            ApplyWorkspaceViewLayout();
        }

        SetActiveWorkspaceView(WorkspaceViewKind.Diagram);
    }

    private void SetCurrentDiagramIdentity(string? diagramId, string diagramName)
    {
        _activeDiagramId = string.IsNullOrWhiteSpace(diagramId) ? null : diagramId;
        CurrentDiagramName = diagramName;
        OnPropertyChanged(nameof(CanSaveDiagramAs));
        OnPropertyChanged(nameof(CanDeleteDiagram));
    }

    private void EnsureDiagramViewportInitialized()
    {
        if (_diagramViewportInitialized)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(RestoreDiagramViewport), DispatcherPriority.ContextIdle);
    }

    private void RebuildReferenceIndexForActiveScope()
    {
        _referenceIndex = _activeScope == null
            ? ScopeReferenceIndex.Empty
            : _referenceIndexService.Build(_activeScope, _databaseSnapshots, _appSettings.CodeWindows, GetUnloadedResourceIds());
    }

    private void ApplySettingsToOpenWindows()
    {
        foreach (FloatingCodeWindow window in _openWindows.Values)
        {
            window.ApplySyntaxHighlighting(_syntaxHighlightingService.GetDefinition(
                window.State.FilePath,
                GetCodeLanguageForFile(window.State.FilePath)));
            window.ApplyCodeBackcolor(GetCodeWindowBackcolor(window.State.FilePath));
            window.ApplyKeyboardShortcutSettings(_appSettings.KeyboardShortcuts);
        }

        foreach (FloatingSpreadsheetWindow window in _openSpreadsheetWindows.Values)
        {
            window.ApplyGridBackcolor(GetCodeWindowBackcolor(window.State.FilePath));
        }

        foreach (OpenWindowItem tab in OpenTabs)
        {
            tab.Type = GetCodeTypeLabel(tab.FilePath);
            tab.TypeBackBrush = GetOpenTabTypeBackBrush(tab.FilePath);
        }
    }

    private void ApplySettingsToPreview()
    {
        if (string.IsNullOrWhiteSpace(_previewFilePath))
        {
            return;
        }

        Brush backcolor = GetCodeWindowBackcolor(_previewFilePath);
        PreviewEditor.Background = backcolor;
        PreviewEditor.TextArea.Background = backcolor;
        PreviewEditor.SyntaxHighlighting = _syntaxHighlightingService.GetDefinition(
            _previewFilePath,
            GetCodeLanguageForFile(_previewFilePath));
        ApplyReferenceHighlightsToPreview();
    }

    private void ApplyReferenceHighlightsToOpenWindows()
    {
        foreach (FloatingCodeWindow window in _openWindows.Values)
        {
            window.ApplyReferenceHighlights(GetReferenceHighlightStylesForFile(window.State.FilePath));
        }
    }

    private void ApplyReferenceHighlightsToPreview()
    {
        RemovePreviewReferenceHighlightColorizer();

        if (string.IsNullOrWhiteSpace(_previewFilePath))
        {
            PreviewEditor.TextArea.TextView.Redraw();
            return;
        }

        IReadOnlyDictionary<string, ReferenceHighlightStyleSetting> highlightStyles = GetReferenceHighlightStylesForFile(_previewFilePath);
        if (highlightStyles.Count == 0)
        {
            PreviewEditor.TextArea.TextView.Redraw();
            return;
        }

        _previewReferenceHighlightColorizer = new ReferenceHighlightColorizer(highlightStyles);
        PreviewEditor.TextArea.TextView.LineTransformers.Add(_previewReferenceHighlightColorizer);
        PreviewEditor.TextArea.TextView.Redraw();
    }

    private void RemovePreviewReferenceHighlightColorizer()
    {
        if (_previewReferenceHighlightColorizer == null)
        {
            return;
        }

        PreviewEditor.TextArea.TextView.LineTransformers.Remove(_previewReferenceHighlightColorizer);
        _previewReferenceHighlightColorizer = null;
    }

    private IReadOnlyDictionary<string, ReferenceHighlightStyleSetting> GetReferenceHighlightStylesForFile(string filePath)
    {
        string preferredLanguage = GetCodeLanguageForFile(filePath);
        var stylesByToken = new Dictionary<string, ReferenceHighlightStyleSetting>(StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, ReferenceEntity> group in _referenceIndex.HighlightableEntities
                     .GroupBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase))
        {
            ReferenceEntity? entity = SelectHighlightEntity(group, preferredLanguage);
            if (entity == null)
            {
                continue;
            }

            stylesByToken[group.Key] = _appSettings.ReferenceHighlights.GetStyle(entity.Language, entity.Kind);
        }

        return stylesByToken;
    }

    private static ReferenceEntity? SelectHighlightEntity(IEnumerable<ReferenceEntity> entities, string preferredLanguage)
    {
        List<ReferenceEntity> candidates = entities.ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        ReferenceEntity? preferredLanguageEntity = candidates
            .Where(entity => string.Equals(entity.Language, preferredLanguage, StringComparison.OrdinalIgnoreCase))
            .OrderBy(GetHighlightKindRank)
            .ThenBy(entity => entity.QualifiedName)
            .FirstOrDefault();

        return preferredLanguageEntity ?? candidates
            .OrderBy(GetHighlightKindRank)
            .ThenBy(entity => entity.Language)
            .ThenBy(entity => entity.QualifiedName)
            .FirstOrDefault();
    }

    private static int GetHighlightKindRank(ReferenceEntity entity)
    {
        return entity.Kind switch
        {
            ReferenceEntityKind.Class => 0,
            ReferenceEntityKind.Interface => 1,
            ReferenceEntityKind.Struct => 2,
            ReferenceEntityKind.Enum => 3,
            ReferenceEntityKind.Delegate => 4,
            ReferenceEntityKind.Method => 5,
            ReferenceEntityKind.Function => 6,
            ReferenceEntityKind.StoredProcedure => 7,
            ReferenceEntityKind.View => 8,
            ReferenceEntityKind.Trigger => 9,
            ReferenceEntityKind.Table => 10,
            ReferenceEntityKind.Field => 11,
            _ => 99
        };
    }

    private string GetCodeLanguageForFile(string filePath)
    {
        return _appSettings.CodeWindows.GetLanguageForFile(filePath);
    }

    private string GetCodeTypeLabel(string filePath)
    {
        return CodeWindowSettings.GetLanguageTypeLabel(GetCodeLanguageForFile(filePath));
    }

    private Brush GetOpenTabTypeBackBrush(string filePath)
    {
        Color color = GetCodeWindowBackcolorColor(filePath);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void SetDiagramOutlineColor(string colorText)
    {
        SetDiagramColor(
            colorText,
            value => _diagramOutlineColor = value,
            DiagramOutlineColorSwatch,
            DiagramOutlineColorTransparentSlash);
    }

    private void SetDiagramBackColor(string colorText)
    {
        SetDiagramColor(
            colorText,
            value => _diagramBackColor = value,
            DiagramBackColorSwatch,
            DiagramBackColorTransparentSlash);
    }

    private static void SetDiagramColor(
        string colorText,
        Action<string> updateValue,
        System.Windows.Shapes.Shape swatch,
        UIElement transparentSlash)
    {
        if (IsTransparentDiagramColor(colorText))
        {
            updateValue(TransparentDiagramColorText);
            swatch.Fill = Brushes.Transparent;
            transparentSlash.Visibility = Visibility.Visible;
            return;
        }

        Color color;
        try
        {
            if (ColorConverter.ConvertFromString(colorText) is not Color parsedColor)
            {
                return;
            }

            color = parsedColor;
        }
        catch (FormatException)
        {
            return;
        }

        if (color.A == 0)
        {
            updateValue(TransparentDiagramColorText);
            swatch.Fill = Brushes.Transparent;
            transparentSlash.Visibility = Visibility.Visible;
            return;
        }

        updateValue(ToRgbHex(color));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        swatch.Fill = brush;
        transparentSlash.Visibility = Visibility.Collapsed;
    }

    private static string GetColorPickerInitialColor(string colorText, string fallbackColor)
    {
        return IsTransparentDiagramColor(colorText) ? fallbackColor : colorText;
    }

    private static BitmapImage? CreateImageSource(string? imageDataBase64)
    {
        if (string.IsNullOrWhiteSpace(imageDataBase64))
        {
            return null;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(imageDataBase64);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static BitmapImage? CreateImageSourceFromFile(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(imagePath);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsTransparentDiagramColor(string colorText)
    {
        return string.Equals(colorText, TransparentDiagramColorText, StringComparison.OrdinalIgnoreCase);
    }

    private Brush GetCodeWindowBackcolor(string filePath)
    {
        Color color = GetCodeWindowBackcolorColor(filePath);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Color GetCodeWindowBackcolorColor(string filePath)
    {
        string backcolor = _appSettings.CodeWindows.GetBackcolorForFile(filePath);
        try
        {
            if (ColorConverter.ConvertFromString(backcolor) is Color color)
            {
                return color;
            }
        }
        catch (FormatException)
        {
        }

        return Colors.White;
    }

    private static string ToRgbHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
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

    private void RestoreDiagramViewport()
    {
        if (DiagramViewHost.Visibility != Visibility.Visible)
        {
            return;
        }

        DiagramScrollViewer.UpdateLayout();
        double horizontalOffset = Math.Max(0, (VirtualOriginX * _diagramCanvasZoom) - (DiagramScrollViewer.ViewportWidth / 2));
        double verticalOffset = Math.Max(0, (VirtualOriginY * _diagramCanvasZoom) - (DiagramScrollViewer.ViewportHeight / 2));
        DiagramScrollViewer.ScrollToHorizontalOffset(horizontalOffset);
        DiagramScrollViewer.ScrollToVerticalOffset(verticalOffset);
        _diagramViewportInitialized = true;
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

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void UpdateEmptyWorkspaceHint()
    {
        bool hasOpenWindows = _openWindows.Count > 0 || _openSpreadsheetWindows.Count > 0;
        EmptyWorkspaceHint.Visibility = _codeViewMode == CodeViewMode.Canvas && !hasOpenWindows
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyCodeTabsHint.Visibility = _codeViewMode == CodeViewMode.Tabs && !hasOpenWindows
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void NotifyNavigationHistoryStateChanged()
    {
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
        OnPropertyChanged(nameof(NavigationHistory));
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

    private async Task SaveApplicationStateForShutdownAsync()
    {
        var cancellation = new CancellationTokenSource(ShutdownSaveTimeout);
        Task saveTask = Task.Run(() => SaveApplicationStateAsync(cancellation.Token), cancellation.Token);
        _ = saveTask.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
        _ = saveTask.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);

        Task completedTask = await Task.WhenAny(saveTask, Task.Delay(ShutdownSaveTimeout));
        if (completedTask != saveTask)
        {
            cancellation.Cancel();
            return;
        }

        await saveTask;
    }

    private Task SaveApplicationStateAsync(CancellationToken cancellationToken)
    {
        return Task.WhenAll(
            _scopeStore.SaveAsync(_scopeLibrary, cancellationToken),
            _databaseMetadataStore.SaveAsync(_databaseSnapshots, cancellationToken),
            _diagramStore.SaveAsync(_diagramLibrary, cancellationToken),
            _workbenchStore.SaveAsync(_workbenchLibrary, cancellationToken),
            _settingsStore.SaveAsync(_appSettings, cancellationToken),
            _workspaceStore.SaveAsync(_workspaceState, cancellationToken));
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        InternalLogService.Info(
            "MainWindow closing.",
            ("ShutdownSaveCompleted", _shutdownSaveCompleted),
            ("ShutdownRequested", _shutdownRequested),
            ("OpenTabsCount", OpenTabs.Count),
            ("OpenCodeWindows", _openWindows.Count),
            ("OpenSpreadsheetWindows", _openSpreadsheetWindows.Count),
            ("CodeViewMode", _codeViewMode),
            ("ActiveCodeWindow", _activeCodeWindow?.State.FilePath));

        if (_shutdownSaveCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;
        StopCodeShiftPan();
        StopCodeCtrlShiftZoom();
        StopDiagramShiftPan();
        StopDiagramCtrlShiftZoom();
        IsEnabled = false;
        StatusText = "Saving application state...";

        try
        {
            await SaveApplicationStateForShutdownAsync();
            InternalLogService.Info("Saved application state during shutdown.");
        }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "Failed to save application state during shutdown.");
            // Avoid blocking application shutdown if persistence fails.
        }
        finally
        {
            _shutdownSaveCompleted = true;
            InternalLogService.Info("Closing MainWindow after shutdown save attempt.");
            Close();
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

            current = GetDependencyParent(current);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? start) where T : DependencyObject
    {
        if (start == null)
        {
            return null;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(start);
        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(start, i);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }
}
