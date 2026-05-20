using System.Windows;
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

    public DatabaseResourceWindow()
    {
        InitializeComponent();
        UpdateCounts(DatabaseImportCounts.FromSnapshot(new DatabaseMetadataSnapshot()));
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
        SetCount(StoredProceduresCountText, "Stored Procedures", counts.StoredProcedures);
        SetCount(ViewsCountText, "Views", counts.Views);
        SetCount(FunctionsCountText, "Functions", counts.Functions);
        SetCount(TriggersCountText, "Triggers", counts.Triggers);
        SetCount(TablesCountText, "Tables", counts.Tables);
        SetCount(FieldsCountText, "Fields", counts.Fields);
        SetCount(PrimaryKeysCountText, "Primary Keys", counts.PrimaryKeys);
        SetCount(FullDataTablesCountText, "Full Data Tables", counts.FullDataTables);
        SetCount(DataRowsCountText, "Data Rows", counts.DataRows);
    }

    private static void SetCount(System.Windows.Controls.TextBlock textBlock, string label, long count)
    {
        textBlock.Text = $"{label}: {count}";
        textBlock.Foreground = count > 0 ? PopulatedCountBrush : EmptyCountBrush;
        textBlock.FontWeight = count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
