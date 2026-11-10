using System.IO;
using System.Windows;
using System.Windows.Controls;
using Surf2;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private static void VerifyDatabaseWorkflowChoices(Action<bool, string> check, string ownedDirectory)
    {
        var chooser = new DatabaseResourceWorkflowWindow();
        try
        {
            var generated = RuntimeWpfField<RadioButton>(chooser, "_generatedSql");
            var direct = RuntimeWpfField<RadioButton>(chooser, "_directConnection");
            var folder = RuntimeWpfField<RadioButton>(chooser, "_exportFolder");
            var continueButton = RuntimeWpfField<Button>(chooser, "_continue");
            var folderFields = RuntimeWpfField<Grid>(chooser, "_folderFields");
            var path = RuntimeWpfField<TextBox>(chooser, "_folderPath");
            check(chooser.SelectedWorkflow == DatabaseResourceWorkflow.GeneratedSql && generated.IsChecked == true &&
                continueButton.IsEnabled && folderFields.Visibility == Visibility.Collapsed,
                "Database workflow defaults to generated SQL before loading or changing a snapshot");
            direct.IsChecked = true;
            check(chooser.SelectedWorkflow == DatabaseResourceWorkflow.DirectConnection && continueButton.IsEnabled &&
                folderFields.Visibility == Visibility.Collapsed, "Database workflow offers direct connection independently of a folder");
            folder.IsChecked = true;
            check(chooser.SelectedWorkflow == DatabaseResourceWorkflow.ExportFolder && !continueButton.IsEnabled &&
                folderFields.Visibility == Visibility.Visible, "Export-folder workflow requires a selected folder");
            path.Text = Path.Combine(ownedDirectory, "missing-workflow-folder");
            RuntimeWpfInvoke(chooser, "Continue");
            check(chooser.ExportFolderPath == null && !string.IsNullOrWhiteSpace(RuntimeWpfField<TextBlock>(chooser, "_status").Text),
                "Export-folder workflow reports a missing folder without accepting the choice");
            generated.IsChecked = true;
            check(chooser.SelectedWorkflow == DatabaseResourceWorkflow.GeneratedSql && continueButton.IsEnabled &&
                string.IsNullOrEmpty(RuntimeWpfField<TextBlock>(chooser, "_status").Text),
                "Switching back to generated SQL clears folder validation and remains available");
        }
        finally { chooser.Close(); }

        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(
            "Server=Surf2_Regression_Offscreen;Database=Surf2_Regression_Choice;Integrated Security=true;TrustServerCertificate=true;Pooling=false;Connect Timeout=1"));
        var token = new StateToken(1, runtime.Session.Epoch, new byte[8], Guid.NewGuid());
        var scope = new Scope { ScopeId = "workflow-choice", Name = "Workflow choice" };
        var selected = new SnapshotSummary(7, Guid.NewGuid(), "selected-database", "Selected database", "fixture",
            DateTimeOffset.UnixEpoch, 0, 1, new byte[8]);
        var capture = new RelationalDatabaseCaptureWindow(runtime, scope, token, selected);
        try
        {
            RuntimeWpfInvoke(capture, "SetBusy", true);
            RuntimeWpfInvoke(capture, "SetBusy", false);
            var replace = RuntimeWpfField<CheckBox>(capture, "_replace");
            var targets = RuntimeWpfField<ComboBox>(capture, "_snapshots");
            check(replace.IsChecked == true && !replace.IsEnabled && !targets.IsEnabled &&
                ReferenceEquals(targets.SelectedItem, selected) &&
                RuntimeWpfField<Button>(capture, "_nextSnapshots").Visibility == Visibility.Collapsed &&
                ReferenceEquals(RuntimeWpfField<SnapshotSummary>(capture, "_replacement"), selected),
                "Direct-connection editing keeps the selected snapshot fixed after busy-state changes");
            targets.Items.Add(selected with { SnapshotKey = 8 }); targets.SelectedIndex = 1;
            replace.IsChecked = false;
            check(RuntimeWpfInvoke(capture, "CaptureTarget") is SnapshotRuntimeToken fixedTarget && fixedTarget.SnapshotKey == selected.SnapshotKey,
                "The pinned edit target cannot be redirected by changing disabled controls");
        }
        finally { capture.Close(); }

        var create = new RelationalDatabaseCaptureWindow(runtime, scope, token, null, allowTargetSelection: false);
        try
        {
            RuntimeWpfInvoke(create, "SetBusy", true); RuntimeWpfInvoke(create, "SetBusy", false);
            var replace = RuntimeWpfField<CheckBox>(create, "_replace");
            var targets = RuntimeWpfField<ComboBox>(create, "_snapshots");
            check(replace.Parent is Grid { Visibility: Visibility.Collapsed } && !replace.IsEnabled && !targets.IsEnabled && targets.Items.Count == 0,
                "New database capture hides replacement controls and does not populate target snapshots");
            targets.Items.Add(selected); targets.SelectedItem = selected; replace.IsChecked = true;
            check(RuntimeWpfInvoke(create, "CaptureTarget") == null,
                "New database capture always creates a snapshot even if hidden target controls are changed");
        }
        finally { create.Close(); }
    }
}
