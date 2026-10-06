using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2.Services;

/// <summary>One explicitly selected database and connection epoch; never initializes or migrates it.</summary>
public sealed class RelationalRuntime
{
    public RelationalRuntime(SqlServerConnectionOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Session = new(options.ConnectionString);
        Session.Metrics.Enable();
        var content = new RelationalContentStore();
        StateStore = new(Session, content);
        State = new(Session);
        Preferences = new(Session);
        Snapshots = new(Session, content);
        CapturedData = new(Session);
        Index = new(Session, content);
        Explorer = new(new RelationalExplorerMetadataQueries(Session, Index));
        References = new(Session, Index);
        SearchSources = new(Session, Index, Snapshots, CapturedData);
    }

    public SqlServerConnectionOptions Options { get; }
    public RelationalSession Session { get; }
    public RelationalQueryMetrics QueryMetrics { get; } = new();
    public SelectedStateAccess State { get; }
    public RelationalStateStore StateStore { get; }
    public StatePreferenceAccess Preferences { get; }
    public RelationalSnapshotStore Snapshots { get; }
    public RelationalCaptureStore CapturedData { get; }
    public RelationalIndexStore Index { get; }
    public RelationalExplorerService Explorer { get; }
    public RelationalReferenceService References { get; }
    public RelationalExplorerSearchSources SearchSources { get; }
}
