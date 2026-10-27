using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Surf2.Models;
using Surf2.Services;

namespace Surf2;

public partial class DatabaseResourceWindow : Window
{
    private enum QueryOutputMode
    {
        ClipboardText,
        ClipboardFile,
        FileExplorer
    }

    private static readonly Brush EmptyCountBrush = Brushes.Gray;
    private static readonly Brush PopulatedCountBrush = Brushes.DarkGreen;

    private readonly DatabaseMetadataImportService _importService = new();
    private readonly DatabaseMetadataQueryService _queryService = new();
    private readonly DatabaseQueryFileService _queryFileService = new();
    private readonly Dictionary<DatabaseEntityEditKind, int> _pendingEntityEditCounts = new();
    private readonly string? _editingSnapshotId;
    private QueryOutputMode _queryOutputMode = QueryOutputMode.ClipboardText;

    public DatabaseResourceWindow()
    {
        InitializeComponent();
        UpdateCounts(DatabaseImportCounts.FromSnapshot(new DatabaseMetadataSnapshot()));
    }

    public DatabaseResourceWindow(DatabaseMetadataSnapshot snapshot)
        : this()
    {
        _editingSnapshotId = snapshot.SnapshotId;
        Snapshot = snapshot.Clone();
        _importService.NormalizeSnapshot(Snapshot);
        Title = "Edit Database";
        AddButton.Content = "Save";
        AddButton.IsEnabled = true;
        SnapshotNameTextBox.Text = Snapshot.DisplayName;
        FullDataTablesTextBox.Text = string.Join(Environment.NewLine, GetPersistedFullDataTableNames(Snapshot));
        UpdateCounts(Snapshot.Counts);
        ImportMessagesTextBox.Text = $"Loaded database snapshot '{Snapshot.DisplayName}' for editing.";
        StatusTextBlock.Text = "Edit the name, rerun the full query, or click a count to update individual entities.";
    }

    public DatabaseMetadataSnapshot? Snapshot { get; private set; }

    private void CopyQueryButton_Click(object sender, RoutedEventArgs e)
    {
        string? filePath = null;
        string operation = "generate the metadata query";
        try
        {
            string query = _queryService.CreateImportQuery(GetFullDataTableNames());
            if (_queryOutputMode == QueryOutputMode.ClipboardText)
            {
                operation = "copy the query text to the clipboard";
                TextClipboardService.CopyText(query);
                StatusTextBlock.Text = "Copied metadata query to the clipboard.";
                return;
            }

            operation = "create the query file";
            filePath = _queryFileService.CreateQueryFile(query);
            if (_queryOutputMode == QueryOutputMode.ClipboardFile)
            {
                operation = "copy the query file to the clipboard";
                _queryFileService.CopyFileToClipboard(filePath);
                StatusTextBlock.Text = "Copied the query file. Paste it to transfer the file.";
            }
            else
            {
                operation = "open the query file in File Explorer";
                _queryFileService.OpenInExplorer(filePath);
                StatusTextBlock.Text = "Opened the query file in File Explorer for dragging.";
            }

            ImportMessagesTextBox.Text = $"Created metadata query file:{Environment.NewLine}{filePath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or
                                       InvalidOperationException or ArgumentException or NotSupportedException or SecurityException)
        {
            StatusTextBlock.Text = "Query action failed. See the details above.";
            ImportMessagesTextBox.Text = $"Could not {operation}: {ex.Message}";
            if (filePath != null)
            {
                ImportMessagesTextBox.AppendText($"{Environment.NewLine}The query file is still available at:{Environment.NewLine}{filePath}");
            }
        }
    }

    private void QueryModeButton_Click(object sender, RoutedEventArgs e)
    {
        QueryModeButton.ContextMenu.PlacementTarget = QueryModeButton;
        QueryModeButton.ContextMenu.IsOpen = true;
    }

    private void QueryModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } ||
            !Enum.TryParse(tag, out QueryOutputMode mode))
        {
            return;
        }

        _queryOutputMode = mode;
        CopyQueryTextMenuItem.IsChecked = mode == QueryOutputMode.ClipboardText;
        CopyQueryFileMenuItem.IsChecked = mode == QueryOutputMode.ClipboardFile;
        OpenQueryFileMenuItem.IsChecked = mode == QueryOutputMode.FileExplorer;
        CopyQueryButton.Content = mode switch
        {
            QueryOutputMode.ClipboardFile => "Copy Query File to Clipboard",
            QueryOutputMode.FileExplorer => "Create Query File and Open Folder",
            _ => "Copy Query to Clipboard"
        };
        CopyQueryButton.ToolTip = mode switch
        {
            QueryOutputMode.ClipboardFile => "Create the complete query as a .txt file and copy the file for transfer.",
            QueryOutputMode.FileExplorer => "Create the complete query as a .txt file and select it in File Explorer.",
            _ => "Copy the complete metadata query as text."
        };
    }

    private void PasteClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        string clipboardText = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            StatusTextBlock.Text = "Clipboard is empty.";
            return;
        }

        bool hasManualName = !string.IsNullOrWhiteSpace(SnapshotNameTextBox.Text);
        DatabaseImportResult result = _importService.ParseClipboardResult(clipboardText, SnapshotNameTextBox.Text);
        if (result.Snapshot == null)
        {
            StatusTextBlock.Text = "No database metadata could be parsed from the clipboard.";
            return;
        }

        Snapshot = result.Snapshot;
        if (!string.IsNullOrWhiteSpace(_editingSnapshotId))
        {
            Snapshot.SnapshotId = _editingSnapshotId;
        }

        Snapshot.FullDataTableNames = GetFullDataTableNames().ToList();
        if (!hasManualName && !string.IsNullOrWhiteSpace(Snapshot.DatabaseName))
        {
            SnapshotNameTextBox.Text = Snapshot.DatabaseName;
            Snapshot.DisplayName = Snapshot.DatabaseName;
        }

        UpdateCounts(Snapshot.Counts);
        AddButton.IsEnabled = true;

        ImportMessagesTextBox.Text = result.Warnings.Count == 0
            ? $"Imported database snapshot '{Snapshot.DisplayName}' at {Snapshot.ImportedAtUtc.LocalDateTime:g}."
            : $"Imported database snapshot with {result.Warnings.Count} warning(s):{Environment.NewLine}{string.Join(Environment.NewLine, result.Warnings)}";
        StatusTextBlock.Text = "Parsed database metadata from the clipboard.";
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (Snapshot == null)
        {
            return;
        }

        string displayName = SnapshotNameTextBox.Text.Trim();
        Snapshot.DisplayName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : string.IsNullOrWhiteSpace(Snapshot.DatabaseName)
                ? "Database Snapshot"
                : Snapshot.DatabaseName;
        Snapshot.FullDataTableNames = GetFullDataTableNames().ToList();
        _importService.NormalizeSnapshot(Snapshot);

        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private IEnumerable<string> GetFullDataTableNames()
    {
        return FullDataTablesTextBox.Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private void FullDataTableSnippetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string snippet })
        {
            return;
        }

        string text = FullDataTablesTextBox.Text;
        int lineStart = FullDataTablesTextBox.CaretIndex;
        while (lineStart > 0 && text[lineStart - 1] != '\r' && text[lineStart - 1] != '\n')
        {
            lineStart--;
        }

        int lineEnd = text.IndexOfAny(['\r', '\n'], FullDataTablesTextBox.CaretIndex);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        int selectionStart = FullTableDataSelection.GetSelectionStart(text[lineStart..lineEnd]);
        int insertionIndex = selectionStart < 0 ? lineEnd : lineStart + selectionStart;
        while (insertionIndex > lineStart && char.IsWhiteSpace(text[insertionIndex - 1]))
        {
            insertionIndex--;
        }

        FullDataTablesTextBox.BeginChange();
        try
        {
            FullDataTablesTextBox.Select(insertionIndex, lineEnd - insertionIndex);
            FullDataTablesTextBox.SelectedText = snippet;
            int predicateOffset = snippet.IndexOf("Id = 1", StringComparison.Ordinal);
            if (predicateOffset >= 0)
            {
                FullDataTablesTextBox.Select(insertionIndex + predicateOffset, "Id = 1".Length);
            }
            else
            {
                FullDataTablesTextBox.CaretIndex = insertionIndex + snippet.Length;
            }
        }
        finally
        {
            FullDataTablesTextBox.EndChange();
        }

        FullDataTablesTextBox.Focus();
    }

    private void UpdateCounts(DatabaseImportCounts counts)
    {
        SetCount(StoredProceduresCountText, "Stored Procedures", counts.StoredProcedures, GetPendingEditCount(DatabaseEntityEditKind.StoredProcedures));
        SetCount(ViewsCountText, "Views", counts.Views, GetPendingEditCount(DatabaseEntityEditKind.Views));
        SetCount(FunctionsCountText, "Functions", counts.Functions, GetPendingEditCount(DatabaseEntityEditKind.Functions));
        SetCount(TriggersCountText, "Triggers", counts.Triggers, GetPendingEditCount(DatabaseEntityEditKind.Triggers));
        SetCount(TablesCountText, "Tables", counts.Tables, GetPendingEditCount(DatabaseEntityEditKind.Tables));
        SetCount(FieldsCountText, "Fields", counts.Fields, GetPendingEditCount(DatabaseEntityEditKind.Fields));
        SetCount(PrimaryKeysCountText, "Primary Keys", counts.PrimaryKeys, GetPendingEditCount(DatabaseEntityEditKind.PrimaryKeys));
        SetCount(FullDataTablesCountText, "Full Data Tables", counts.FullDataTables, GetPendingEditCount(DatabaseEntityEditKind.FullDataTables));
        SetCount(DataRowsCountText, "Data Rows", counts.DataRows, 0);
    }

    private void EntityCountText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Snapshot == null)
        {
            StatusTextBlock.Text = "Paste database metadata before editing individual entities.";
            return;
        }

        if ((sender as FrameworkElement)?.Tag is not string tag ||
            !Enum.TryParse(tag, out DatabaseEntityEditKind kind))
        {
            return;
        }

        Snapshot.FullDataTableNames = GetFullDataTableNames().ToList();
        var editWindow = new DatabaseEntityEditWindow(Snapshot, kind)
        {
            Owner = this
        };

        if (editWindow.ShowDialog() != true)
        {
            return;
        }

        Snapshot = editWindow.Snapshot;
        Snapshot.DisplayName = SnapshotNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(Snapshot.DisplayName))
        {
            Snapshot.DisplayName = string.IsNullOrWhiteSpace(Snapshot.DatabaseName)
                ? "Database Snapshot"
                : Snapshot.DatabaseName;
        }

        Snapshot.FullDataTableNames = FullTableDataSelection.MergeTableNames(
            GetFullDataTableNames(), GetPersistedFullDataTableNames(Snapshot));

        if (editWindow.UpdatedCount > 0)
        {
            _pendingEntityEditCounts[kind] = GetPendingEditCount(kind) + editWindow.UpdatedCount;
        }

        UpdateCounts(Snapshot.Counts);
        AddButton.IsEnabled = true;
        ImportMessagesTextBox.Text = editWindow.UpdatedCount == 0
            ? $"No {GetKindLabel(kind).ToLowerInvariant()} were changed."
            : $"Prepared {editWindow.UpdatedCount} {GetKindLabel(kind).ToLowerInvariant()} update(s). Click Save to persist them.";
        StatusTextBlock.Text = "Entity updates are staged. Click Save to persist them.";
    }

    private int GetPendingEditCount(DatabaseEntityEditKind kind)
    {
        return _pendingEntityEditCounts.TryGetValue(kind, out int count) ? count : 0;
    }

    private static IEnumerable<string> GetPersistedFullDataTableNames(DatabaseMetadataSnapshot snapshot)
    {
        IEnumerable<string> configuredNames = snapshot.FullDataTableNames ?? [];
        IEnumerable<string> importedNames = snapshot.TableDataSets
            .Select(dataSet => SqlName.FormatPlainMultipartName(dataSet.SchemaName, dataSet.TableName));

        return FullTableDataSelection.MergeTableNames(configuredNames, importedNames);
    }

    private static string GetKindLabel(DatabaseEntityEditKind kind)
    {
        return kind switch
        {
            DatabaseEntityEditKind.StoredProcedures => "Stored Procedures",
            DatabaseEntityEditKind.Views => "Views",
            DatabaseEntityEditKind.Functions => "Functions",
            DatabaseEntityEditKind.Triggers => "Triggers",
            DatabaseEntityEditKind.Tables => "Tables",
            DatabaseEntityEditKind.Fields => "Fields",
            DatabaseEntityEditKind.PrimaryKeys => "Primary Keys",
            DatabaseEntityEditKind.FullDataTables => "Full Data Tables",
            _ => "Entities"
        };
    }

    private static void SetCount(TextBlock textBlock, string label, long count, int pendingEditCount)
    {
        textBlock.Inlines.Clear();
        textBlock.Inlines.Add(new Run($"{label}: {count}"));
        if (pendingEditCount > 0)
        {
            textBlock.Inlines.Add(new Run($" (+{pendingEditCount})")
            {
                Foreground = Brushes.RoyalBlue,
                FontWeight = FontWeights.SemiBold
            });
        }

        textBlock.Foreground = count > 0 ? PopulatedCountBrush : EmptyCountBrush;
        textBlock.FontWeight = count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
