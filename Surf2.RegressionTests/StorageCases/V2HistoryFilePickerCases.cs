using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Surf2;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalComparison;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    private static async Task VerifyV2HistoryFilePickerAsync(RelationalRuntime runtime, SnapshotSummary snapshot,
        RelationalComparisonTarget defaultTarget, string outputDir, Action<bool, string> check)
    {
        var picker = new RelationalHistoryFilePickerWindow(runtime, [snapshot], defaultTarget);
        var root = new Grid { Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey) };
        root.Resources.MergedDictionaries.Add(Application.Current.Resources);
        root.Resources.MergedDictionaries.Add(picker.Resources);
        using var host = new HwndSource(new HwndSourceParameters("Surf2 history file picker offscreen host")
        { PositionX = -32000, PositionY = -32000, Width = 860, Height = 560,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        try
        {
            using var trace = new StateAccessSqlTrace(runtime.Options.ConnectionString);
            await picker.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var content = (FrameworkElement)picker.Content; picker.Content = null;
            root.Children.Add(content); host.RootVisual = root;
            RuntimeWpfArrange(root, 860, 560); await RuntimeWpfIdleAsync();
            var databases = RuntimeWpfNamed<ComboBox>(picker, "DatabaseComboBox");
            var versions = RuntimeWpfNamed<ComboBox>(picker, "VersionComboBox");
            var files = RuntimeWpfNamed<ListView>(picker, "FilesListView");
            var filter = RuntimeWpfNamed<TextBox>(picker, "FilterTextBox");
            var choose = RuntimeWpfNamed<Button>(picker, "PickButton");
            var choices = versions.Items.Cast<RelationalHistoryFilePickerWindow.VersionChoice>().ToArray();
            RuntimeWpfAssert(picker.Title == "Select File" && picker.Width == 860 && picker.Height == 560 &&
                picker.MinWidth == 680 && picker.MinHeight == 420 && Equals(choose.Content, "Select") &&
                ((GridView)files.View).Columns.Select(column => column.Header?.ToString()).SequenceEqual(new[] { "Resource Type", "Name" }) &&
                ((GridView)files.View).Columns.Select(column => column.Width).SequenceEqual(new[] { 170d, 610d }), check,
                "History file comparison picker restores master's dimensions, columns and Select action");
            RuntimeWpfAssert(databases.Items.Count == 1 && databases.SelectedItem is SnapshotSummary selected && selected.SnapshotKey == snapshot.SnapshotKey &&
                choices.Length > 1 && choices.Select(choice => choice.Version.VersionNumber).SequenceEqual(
                    choices.OrderByDescending(choice => choice.Version.VersionNumber).Select(choice => choice.Version.VersionNumber)) &&
                ReferenceEquals(versions.SelectedItem, choices[0]) && choices[0].DisplayName == choices[0].Version.VersionName + " (Current)", check,
                "History file picker limits databases to scope and starts on the latest named Current version");
            RuntimeWpfAssert(files.SelectedItem is RelationalHistoryFilePickerWindow.FileChoice original &&
                original.Entry.ResourceKey == defaultTarget.ResourceKey && original.Kind == DatabaseVersionedResourceKind.StoredProcedure && choose.IsEnabled,
                check, "History file picker preselects the same resource and kind in the default database");
            RuntimeWpfSaveRender(root, outputDir, "v2-history-file-picker", 860, 560, check);

            filter.Text = "  TABLES/DBO.A.TXT  ";
            RuntimeWpfAssert(files.Items.Count == 1 && files.Items[0] is RelationalHistoryFilePickerWindow.FileChoice table &&
                table.Kind == DatabaseVersionedResourceKind.TableMetadata && table.FileNameDisplay == "dbo.A" && !choose.IsEnabled,
                check, "History file Find searches trimmed case-insensitive resource paths and clears the previous selection");
            filter.Clear();
            // The second change supersedes the first while its SQL work is pending.
            versions.SelectedItem = choices[1];
            versions.SelectedItem = choices[^1];
            await picker.CurrentOperation.WaitAsync(TimeSpan.FromSeconds(60));
            RuntimeWpfAssert(ReferenceEquals(versions.SelectedItem, choices[^1]) && files.Items.Count > 0 &&
                files.Items.Cast<RelationalHistoryFilePickerWindow.FileChoice>().All(file => file.Version.Version.VersionKey == choices[^1].Version.VersionKey), check,
                "Rapid version changes cancel stale picker loads and publish only the final version's file identities");
            RuntimeWpfAssert(trace.Commands.Count > 0, check, "History file picker fixture exercises real SQL metadata reads");
            StateAccessNoReads(trace.Commands, check, "History file picker browsing reads no file bodies or captured rows",
                "TextContent", "TextContentChunk", "CapturedRow", "CaptureRow", "CapturedDataRow", "Asset");
            versions.SelectedItem = choices[0];
            await picker.CloseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(60));
            RuntimeWpfAssert(picker.CurrentOperation.IsCompleted && picker.CloseCompletion.IsCompletedSuccessfully && picker.SelectedTarget == null,
                check, "Closing a loading history file picker drains its SQL work and returns no accidental selection");
        }
        finally
        {
            host.RootVisual = null; root.Children.Clear(); root.Resources.MergedDictionaries.Clear();
            await picker.CloseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
    }
}
