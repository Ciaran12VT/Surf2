using System.Windows;
using System.Windows.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

public sealed record SnapshotImportTargetChoice(bool Cancelled, SnapshotSummary? Target);

public sealed class SnapshotImportTargetWindow : Window
{
    private readonly ListBox _targets = new() { DisplayMemberPath = nameof(SnapshotSummary.DisplayName), MinHeight = 100 };
    private SnapshotSummary? _selected;
    private SnapshotImportTargetWindow(IReadOnlyList<SnapshotSummary> matches)
    {
        Title = "A snapshot from this database already exists"; Width = 600; Height = 330; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Services.RelationalSnapshots.SnapshotWindowStyles.ThemeWindow(this);
        var panel = new StackPanel { Margin = new(18) };
        panel.Children.Add(new TextBlock { Text = "The database origin matches an existing snapshot. Add to a selected history, or create a separate snapshot resource with its own history. Matching never merges histories automatically.", TextWrapping = TextWrapping.Wrap });
        _targets.ItemsSource = matches; _targets.SelectedIndex = 0; _targets.Margin = new(0, 12, 0, 12); panel.Children.Add(_targets);
        var buttons = new WrapPanel();
        var append = new Button { Content = "Add a version to selected snapshot", Margin = new(4), Padding = new(6) };
        append.Click += (_, _) => { if (_targets.SelectedItem is SnapshotSummary target) { _selected = target; DialogResult = true; } };
        var separate = new Button { Content = "Create separate snapshot", Margin = new(4), Padding = new(6) };
        separate.Click += (_, _) => { _selected = null; DialogResult = true; };
        buttons.Children.Add(append); buttons.Children.Add(separate); buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new(4), Padding = new(6) }); panel.Children.Add(buttons); Content = panel;
    }
    public static async Task<SnapshotImportTargetChoice> ChooseAsync(Window owner, RelationalRuntime runtime,
        DatabaseOrigin? origin, SnapshotSummary? explicitTarget = null, CancellationToken ct = default)
    {
        if (explicitTarget != null)
        {
            var previous = await runtime.Snapshots.ReadSnapshotOriginAsync(explicitTarget.SnapshotKey, ct);
            if (origin?.IsComplete == true && previous?.IsComplete == true && !origin.AgreesWith(previous) &&
                MessageBox.Show(owner, $"Recorded origin: {previous.Server}/{previous.Database}\nImported origin: {origin.Server}/{origin.Database}\n\nThese origins differ. Continue with the explicitly selected snapshot?", "Confirm snapshot origin", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return new(true, explicitTarget);
            return new(false, explicitTarget);
        }
        if (origin?.IsComplete != true) return new(false, null);
        var matches = new List<SnapshotSummary>(); SnapshotCursor? cursor = null; int seen = 0;
        do
        {
            var page = await runtime.Snapshots.ListSnapshotsAsync(pageSize: 100, cursor: cursor, ct: ct);
            foreach (var candidate in page.Items)
            {
                if (++seen > 100000) throw new InvalidOperationException("Snapshot matching exceeds the supported catalogue limit.");
                if (await runtime.Snapshots.ReadSnapshotOriginAsync(candidate.SnapshotKey, ct) is { } existing && origin.AgreesWith(existing)) matches.Add(candidate);
            }
            cursor = page.Next;
        } while (cursor != null);
        if (matches.Count == 0) return new(false, null);
        ct.ThrowIfCancellationRequested(); var dialog = new SnapshotImportTargetWindow(matches) { Owner = owner };
        return dialog.ShowDialog() == true ? new(false, dialog._selected) : new(true, null);
    }
}
