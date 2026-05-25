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
    private static readonly Brush EmptyCountBrush = Brushes.Gray;
    private static readonly Brush PopulatedCountBrush = Brushes.DarkGreen;

    private readonly DatabaseMetadataImportService _importService = new();
    private readonly DatabaseMetadataQueryService _queryService = new();
    private readonly Dictionary<DatabaseEntityEditKind, int> _pendingEntityEditCounts = new();
    private readonly string? _editingSnapshotId;

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
        string query = _queryService.CreateImportQuery(GetFullDataTableNames());
        Clipboard.SetText(query);
        StatusTextBlock.Text = "Copied metadata query to the clipboard.";
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

        Snapshot.FullDataTableNames = GetFullDataTableNames()
            .Concat(GetPersistedFullDataTableNames(Snapshot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

        return configuredNames
            .Concat(importedNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
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
