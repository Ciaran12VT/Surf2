using System.IO;
using System.Text.Json;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Services.RelationalGrid;

public sealed class CapturedGridSource : GridSourceBase
{
    private CapturedGridSource(GridDescriptor descriptor, GridLimits limits, GridOwnedWorkspace workspace, GridDiskStore store)
        : base(descriptor, limits, workspace, store) { }

    public static async Task<CapturedGridSource> OpenAsync(RelationalCaptureStore capture, long dataSetKey,
        GridLimits? limits = null, CancellationToken cancellationToken = default,
        IReadOnlyList<GridColumn>? displayColumns = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        limits ??= new();
        limits.Validate();
        if (displayColumns?.Count > limits.MaxColumns) throw new GridLimitException("Too many effective display columns.");
        GridColumn[]? suppliedColumns = displayColumns?.ToArray();
        var captured = await capture.GetDescriptorAsync(dataSetKey, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (captured.Columns.Count > limits.MaxColumns || captured.Summary.DisplayFormatVersion != CaptureDisplay.FormatVersion)
            throw new GridLimitException("Captured layout/display format is not supported by this grid provider.");
        var columns = CapturedGridValues.PrepareColumns(suppliedColumns ??
            captured.Columns.Select((c, i) => new GridColumn(i, c.SourceName, c.SourceName)).ToArray());
        var descriptor = new GridDescriptor(Guid.NewGuid(),
            $"capture-v1:{captured.Epoch:N}:{captured.Summary.RevisionKey}:{dataSetKey}:{captured.Summary.LayoutKey}:{captured.Summary.DisplayFormatVersion}",
            Array.AsReadOnly(columns), captured.Summary.ReportedRowCount, captured.Summary.ActualRowCount, CaptureDisplay.FormatVersion);
        var workspace = new GridOwnedWorkspace(limits);
        GridDiskStore? store = null;
        CapturedGridSource source;
        try { store = new GridDiskStore(workspace, limits); source = new(descriptor, limits, workspace, store); }
        catch (Exception error)
        {
            if (store != null)
                try { await store.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { error.Data["GridStoreCleanupFailed"] = cleanup; }
            try { workspace.Dispose(); } catch (Exception cleanup) { error.Data["GridWorkspaceCleanupFailed"] = cleanup; }
            throw;
        }
        source.StartBuild(async ct =>
        {
            long previous = -1;
            long actual = 0;
            long visible = 0;
            // No query here: storage must not re-sort/rescan for every grid page.
            await foreach (var row in capture.StreamRowsAsync(dataSetKey, cancellationToken: ct).ConfigureAwait(false))
            {
                if (row.RowOrdinal != previous + 1) throw new InvalidDataException("Captured source has a missing/repeated ordinal.");
                previous = row.RowOrdinal;
                actual++;
                string[]? cells = CapturedGridValues.Cells(row.Value, columns);
                if (!CapturedGridValues.ShouldDisplay(cells)) continue;
                await store.AppendAsync(row.RowOrdinal, cells!, ct).ConfigureAwait(false);
                if (++visible == 1 || visible % limits.PageRows == 0) await store.PublishAsync(ct).ConfigureAwait(false);
            }
            if (actual != captured.Summary.ActualRowCount) throw new InvalidDataException("Captured source count changed or is incomplete.");
            await store.FinishAsync(ct).ConfigureAwait(false);
        });
        return source;
    }
}
