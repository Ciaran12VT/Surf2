using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using Surf2.Storage;
using Surf2.Storage.Relational.Migration;

namespace Surf2;

public partial class RelationalMigrationWindow : Window
{
    private readonly string _source;
    private CancellationTokenSource? _cancellation;
    private Task? _cancellationCallbacks;
    private bool _completed;
    public string? DestinationConnectionString { get; private set; }
    public RelationalMigrationResult? Result { get; private set; }

    public RelationalMigrationWindow(string sourceConnectionString)
    {
        _source = SqlServerConnectionOptions.FromConnectionString(sourceConnectionString).ConnectionString;
        InitializeComponent();
        var builder = new SqlConnectionStringBuilder(_source);
        SourceText.Text = builder.DataSource + " / " + builder.InitialCatalog;
        SourceText.ToolTip = SourceText.Text;
        DestinationName.Text = builder.InitialCatalog.Length > 100 ? "Surf2_Relational" : builder.InitialCatalog + "_Relational";
    }

    private void Operation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecoveryPanel == null) return;
        RecoveryLabel.Visibility = RecoveryPanel.Visibility = Operation.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseRecovery_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the migration staging directory" };
        if (dialog.ShowDialog(this) == true) RecoveryPath.Text = dialog.FolderName;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        string name = DestinationName.Text.Trim();
        if (name.Length is < 1 or > 128 || name.Any(char.IsControl))
        { Status.Text = "Enter a valid destination database name."; return; }
        var builder = new SqlConnectionStringBuilder(_source) { InitialCatalog = name };
        if (string.Equals(new SqlConnectionStringBuilder(_source).InitialCatalog, name, StringComparison.OrdinalIgnoreCase))
        { Status.Text = "Choose a different database. The source cannot be the destination."; return; }
        string? resume = Operation.SelectedIndex == 2 ? RecoveryPath.Text.Trim() : null;
        if (resume != null && (!Directory.Exists(resume) || !File.Exists(Path.Combine(resume, "manifest.json"))))
        { Status.Text = "Choose a recovery staging directory with its manifest."; return; }
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _cancellationCallbacks = null;
        SetRunning(true);
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Surf2");
        var request = new RelationalMigrationRequest(_source, builder.ConnectionString, appData,
            Path.Combine(appData, "Migrations"), Operation.SelectedIndex == 0, resume);
        var progress = new Progress<string>(message =>
        {
            if (_cancellation != null && !_completed) Status.Text = message;
        });
        try
        {
            // Token validation and row encoding are CPU work; SQL/file operations inside remain genuinely asynchronous.
            Result = await Task.Run(() => new RelationalMigrator().ConvertAsync(request, progress, cancellation.Token));
            DestinationConnectionString = builder.ConnectionString;
            _completed = true;
            Status.Text = "Migration validated. Original database preserved. Recovery staging: " + Result.StageDirectory;
            ConvertButton.Visibility = Visibility.Collapsed;
            CloseButton.Content = "Done";
        }
        catch (OperationCanceledException) { Status.AppendText(Environment.NewLine + "Cancelled. The destination has not been activated."); }
        catch (Exception ex) { Status.Text = ex.Message + (ex.InnerException == null ? "" : Environment.NewLine + ex.InnerException.Message); }
        finally
        {
            if (_cancellationCallbacks != null)
            {
                try { await _cancellationCallbacks; }
                catch (Exception error) { Services.InternalLogService.Error(error, "Migration cancellation callbacks failed."); }
            }
            cancellation.Dispose();
            _cancellation = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        ConvertButton.IsEnabled = DestinationName.IsEnabled = Operation.IsEnabled = RecoveryPanel.IsEnabled = !running;
        Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = true;
    }
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation != null) { RequestCancellation(); CloseButton.IsEnabled = false; return; }
        DialogResult = _completed;
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_cancellation == null) return;
        e.Cancel = true;
        RequestCancellation();
        CloseButton.IsEnabled = false;
    }

    private void RequestCancellation()
    {
        if (_cancellation != null) _cancellationCallbacks ??= _cancellation.CancelAsync();
    }
}
