using System.IO;
using System.Windows;
using Surf2.Models;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

public partial class MainWindow
{
    private Task<IReadOnlyList<SnapshotSummary>> ReadRelationalHistoryScopeSnapshotsAsync(
        SnapshotWorkflow work, bool useAliases = true)
    {
        RequireRelationalSnapshotContext(work);
        if (work.Scope == null || _relationalExplorerScope == null) return Task.FromResult<IReadOnlyList<SnapshotSummary>>([]);
        return Task.FromResult<IReadOnlyList<SnapshotSummary>>(_relationalExplorerScope.Resources
            .Where(resource => resource.IsLoaded && resource.Snapshot != null).Select(resource =>
                useAliases && !string.IsNullOrWhiteSpace(resource.Alias)
                    ? resource.Snapshot! with { DisplayName = resource.Alias } : resource.Snapshot!).ToArray());
    }

    private async Task CompareRelationalHistoryWithAsync(SnapshotWorkflow work, RelationalComparisonTarget source)
    {
        var snapshots = await ReadRelationalHistoryScopeSnapshotsAsync(work);
        if (snapshots.Count == 0) { StatusText = "No database versions are available to compare."; return; }
        RelationalComparisonTarget? selected;
        var service = new RelationalSnapshotHistoryService(work.Runtime);
        if (source.Resource.IsCollection)
        {
            var choices = new List<RelationalComparisonTarget>();
            long bytes = 0;
            foreach (var snapshot in snapshots)
            {
                Add(snapshot.DisplayName + " (Current)", "current://" + snapshot.SnapshotId, snapshot, null);
                foreach (var version in await service.GetDisplayedVersionsAsync(snapshot, work.Token))
                    Add(snapshot.DisplayName + " " + version.VersionName,
                        "version://" + snapshot.SnapshotId + "/" + version.VersionId, snapshot, version.VersionKey);
            }
            RequireRelationalSnapshotContext(work);
            var picker = new ComparisonResourcePickerWindow(choices.Select(choice => choice.Resource))
                { Owner = this, Title = "Select Database or Version to Compare" };
            OwnRelationalSnapshotWindow(work, picker, () => { picker.Close(); return Task.CompletedTask; });
            if (picker.ShowDialog() != true || picker.SelectedResource is not { } resource) return;
            selected = choices.First(choice => ReferenceEquals(choice.Resource, resource));

            void Add(string name, string path, SnapshotSummary snapshot, long? versionKey)
            {
                bytes = checked(bytes + 512 + 2L * (name.Length + path.Length));
                if (choices.Count >= 100_000 || bytes > 32L * 1024 * 1024)
                    throw new InvalidDataException("The version picker exceeds its metadata budget.");
                choices.Add(new(new(name, "Database Version", path, ComparisonResourceKind.DatabaseSnapshot,
                    "DatabaseSnapshot", true, false, false, path, SnapshotId: snapshot.SnapshotId),
                    work.Runtime.Session.Epoch, snapshot.SnapshotKey, VersionKey: versionKey));
            }
        }
        else
        {
            var picker = new RelationalHistoryFilePickerWindow(work.Runtime, snapshots, source) { Owner = this, Title = "Compare With" };
            OwnRelationalSnapshotWindow(work, picker, picker.CloseAndDrainAsync);
            bool? accepted;
            try { accepted = picker.ShowDialog(); } finally { await picker.CloseAndDrainAsync(); }
            if (accepted != true || picker.SelectedTarget == null) return;
            selected = picker.SelectedTarget;
        }
        RequireRelationalSnapshotContext(work);
        if (source.Resource.IsCollection)
            await OpenRelationalComparisonAsync(source, selected);
        else
        {
            // Master compares history files as text, including their CSV/table projections.
            // Only the two files explicitly selected by the user are materialized here.
            await using var left = await OpenRelationalHistoricalTargetAsync(work, source);
            var leftEntry = await FindRelationalHistoricalEntryAsync(left, source, work.Token);
            string leftText = await Task.Run(() => service.ReadFileTextAsync(left, leftEntry, work.Token), work.Token);
            await using var right = await OpenRelationalHistoricalTargetAsync(work, selected);
            var rightEntry = await FindRelationalHistoricalEntryAsync(right, selected, work.Token);
            string rightText = await Task.Run(() => service.ReadFileTextAsync(right, rightEntry, work.Token), work.Token);
            RequireRelationalSnapshotContext(work);
            ShowRelationalHistoryFileDiff(work, source.Resource.DisplayName, selected.Resource.DisplayName, leftText, rightText);
        }
    }

    private void ShowRelationalHistoryFileDiff(SnapshotWorkflow work, string leftName, string rightName,
        string leftContent, string rightContent, bool viewOnly = false)
    {
        _appSettings.ResourceComparison ??= new ResourceComparisonSettings();
        var window = new ResourceFileDiffWindow(CreateTextComparisonResource(leftName), CreateTextComparisonResource(rightName),
            leftContent, rightContent,
            ignoreWhitespaceByDefault: !viewOnly && _appSettings.ResourceComparison.IgnoreWhitespaceByDefault,
            ignoreCaseByDefault: !viewOnly && _appSettings.ResourceComparison.IgnoreCaseByDefault,
            startInUnifiedMode: viewOnly)
        { Owner = this, Title = viewOnly ? leftName : leftName + " vs " + rightName };
        OwnRelationalSnapshotWindow(work, window, () => { window.Close(); return Task.CompletedTask; });
        ShowRelationalSnapshotWindow(window);
        StatusText = window.Title + ".";
    }
}
