using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using static Surf2.Services.RelationalSnapshots.SnapshotWindowStyles;

namespace Surf2;

public enum DatabaseResourceWorkflow { GeneratedSql, DirectConnection, ExportFolder }

/// <summary>Chooses the source before a caller loads or changes a snapshot.</summary>
public sealed class DatabaseResourceWorkflowWindow : Window
{
    private readonly RadioButton _generatedSql = new() { GroupName = "DatabaseSource", IsChecked = true };
    private readonly RadioButton _directConnection = new() { GroupName = "DatabaseSource" };
    private readonly RadioButton _exportFolder = new() { GroupName = "DatabaseSource" };
    private readonly TextBox _folderPath = new() { MinWidth = 260 };
    private readonly Button _browse = Command("Browse…"), _continue = Command("Continue"),
        _cancel = Command("Cancel", "SurfCloseButtonStyle");
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _folderFields = new() { Margin = new(24, 4, 0, 8) };

    public DatabaseResourceWorkflow SelectedWorkflow => _exportFolder.IsChecked == true ? DatabaseResourceWorkflow.ExportFolder :
        _directConnection.IsChecked == true ? DatabaseResourceWorkflow.DirectConnection : DatabaseResourceWorkflow.GeneratedSql;
    public string? ExportFolderPath { get; private set; }

    public DatabaseResourceWorkflowWindow(string? editingSnapshotName = null)
    {
        Title = editingSnapshotName == null ? "Add Database" : "Edit Database";
        Width = 660; MinWidth = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeWindow(this); ThemeInput(_folderPath);
        var root = new StackPanel { Margin = new(20) };
        root.Children.Add(new TextBlock { Text = editingSnapshotName == null ? "Choose how to load the database." :
            "Choose how to update “" + editingSnapshotName + "”.", FontSize = 16, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) });
        AddChoice(root, _generatedSql, "SQL statement generated", "Generate the SQL statement, run it against your database, then paste the results into Surf2.");
        AddChoice(root, _directConnection, "Direct connection", "Connect to SQL Server and select the table data to capture.");
        AddChoice(root, _exportFolder, "Surf2 export folder", "Import a folder previously exported by Surf2. Review the imported contents before saving.");
        _folderFields.ColumnDefinitions.Add(new()); _folderFields.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _folderPath.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_folderPath, "Surf2 export folder path");
        _folderFields.Children.Add(_folderPath); Grid.SetColumn(_browse, 1); _folderFields.Children.Add(_browse);
        root.Children.Add(_folderFields);
        _status.Margin = new(0, 0, 0, 10); root.Children.Add(_status);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _continue.IsDefault = true; _cancel.IsCancel = true;
        footer.Children.Add(_continue); footer.Children.Add(_cancel); root.Children.Add(footer); Content = root;
        foreach (var choice in new[] { _generatedSql, _directConnection, _exportFolder })
            choice.Checked += (_, _) => UpdateChoice();
        _folderPath.TextChanged += (_, _) => UpdateChoice();
        _browse.Click += (_, _) => BrowseFolder();
        _continue.Click += (_, _) => Continue();
        _cancel.Click += (_, _) => Close();
        UpdateChoice();
    }

    private static void AddChoice(Panel root, RadioButton choice, string name, string description)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
        choice.Content = content; choice.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        choice.Margin = new(0, 4, 0, 12); AutomationProperties.SetName(choice, name); root.Children.Add(choice);
    }

    private void UpdateChoice()
    {
        bool folder = SelectedWorkflow == DatabaseResourceWorkflow.ExportFolder;
        _folderFields.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        _continue.IsEnabled = !folder || !string.IsNullOrWhiteSpace(_folderPath.Text);
        _status.Text = string.Empty;
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a Surf2 export folder", Multiselect = false };
        if (Directory.Exists(_folderPath.Text)) dialog.InitialDirectory = _folderPath.Text;
        if (dialog.ShowDialog(this) == true) _folderPath.Text = dialog.FolderName;
    }

    private void Continue()
    {
        if (SelectedWorkflow == DatabaseResourceWorkflow.ExportFolder)
        {
            try
            {
                string path = Path.GetFullPath(_folderPath.Text.Trim());
                if (!Directory.Exists(path)) { _status.Text = "Choose an existing Surf2 export folder."; return; }
                ExportFolderPath = path;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            { _status.Text = "The export folder could not be opened: " + error.Message; return; }
        }
        DialogResult = true;
    }
}
