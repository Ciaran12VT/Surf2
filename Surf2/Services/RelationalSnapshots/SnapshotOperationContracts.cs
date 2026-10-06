using System.Data;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2.Services.RelationalSnapshots;

public sealed record RelationalSourceTable(int ObjectId, string SchemaName, string TableName)
{
    public string DisplayName => SqlName.FormatPlainMultipartName(SchemaName, TableName);
}
public sealed record RelationalSourceTablePage(IReadOnlyList<RelationalSourceTable> Items, int? NextObjectId);
public sealed record RelationalDatabaseCaptureRequest(string SourceConnectionString, string DisplayName,
    IReadOnlyList<RelationalSourceTable> FullDataTables, Scope SelectedScope, StateToken ExpectedScope,
    SnapshotRuntimeToken? ReplaceSnapshot = null, string? VersionName = null, bool AllowSerializableFallback = false);
public sealed record RelationalCaptureProgress(string Phase, long MetadataUnits, long CapturedRows);
public sealed record RelationalCaptureResult(SnapshotSummary Snapshot, StateToken UpdatedScopeToken,
    Scope UpdatedScope, long CapturedRows, long VersionKey)
{
    public IsolationLevel SourceIsolation { get; init; } = IsolationLevel.Unspecified;
}
public sealed class SourceSnapshotUnavailableException() : InvalidOperationException(
    "Snapshot isolation is unavailable on the source. Explicitly allow blocking capture to use a serializable source transaction.");

public sealed record RelationalSnapshotOperationLimits
{
    public int MaximumMetadataUnits { get; init; } = 100_000;
    public long MaximumMetadataBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumDefinitionBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumTableColumns { get; init; } = 1024;
    public int SourceCommandTimeoutSeconds { get; init; } = 120;
    public CaptureLimits Capture { get; init; } = new();

    internal void Validate()
    {
        if (MaximumMetadataUnits is < 1 or > 100_000 || MaximumMetadataBytes is < 1024 or > 64 * 1024 * 1024 ||
            MaximumDefinitionBytes is < 2 or > 16 * 1024 * 1024 || MaximumTableColumns is < 1 or > 1024 ||
            SourceCommandTimeoutSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(RelationalSnapshotOperationLimits));
        ArgumentNullException.ThrowIfNull(Capture);
        Capture.Validate();
    }
}

internal sealed class SnapshotOperationBudget(RelationalSnapshotOperationLimits limits)
{
    public long Units { get; private set; }
    private long _bytes;
    public void Add(long bytes)
    {
        if (++Units > limits.MaximumMetadataUnits || bytes < 0 || bytes > limits.MaximumMetadataBytes - _bytes)
            throw new InvalidOperationException("The selected snapshot exceeds the bounded metadata publication budget.");
        _bytes += bytes;
    }
    public void Add(params string[] values) => Add(checked(256 + values.Sum(x => 2L * x.Length)));
}
