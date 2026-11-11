using System.Data;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static Task RunScopePickerUiChecksAsync(Action<bool, string> check, bool snapshotLabelsOnly = false)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                Surf2.App? app = null;
                Exception? failure = null;
                try
                {
                    if (Application.Current != null) throw new InvalidOperationException("Scope UI checks need an isolated process.");
                    var logging = typeof(Surf2.App).Assembly.GetType("Surf2.Services.InternalLogService", true)!;
                    if (logging.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not false)
                        throw new InvalidOperationException("Scope UI checks must not initialize user-file logging.");
                    app = new Surf2.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    if (snapshotLabelsOnly) await CheckScopeSnapshotLabelsAsync(check);
                    else
                        foreach (string result in await Surf2.RelationalScopePickerUiChecks.RunOffscreenAsync(
                            Path.Combine(AppContext.BaseDirectory, "scope-picker-visual-checks"))) check(true, result);
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try { app?.Shutdown(); }
                    catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
                    dispatcher.InvokeShutdown();
                    if (failure == null) finished.TrySetResult(); else finished.TrySetException(failure);
                }
            }));
            try { Dispatcher.Run(); } catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true, Name = "Surf2 scope picker offscreen checks" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return finished.Task;
    }

    private static async Task CheckScopeSnapshotLabelsAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        var connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        {
            ApplicationName = "Surf2_Regression_StateAccess_ScopeLabels_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        const string snapshotId = "e6425a4bf2684f4495ef96c82f6fd827";
        long captured = 0, exported = 0;
        await InTransactionAsync(runtime.Session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, new RelationalContentStore());
            captured = await writer.CreateSnapshotAsync(new(snapshotId, "Warehouse snapshot", "Warehouse", EvidenceTime, 0));
            exported = await writer.CreateSnapshotAsync(new(snapshotId, "Export folder", "", EvidenceTime, 1));
            await writer.PublishSnapshotAsync(captured, null);
            await writer.PublishSnapshotAsync(exported, null);
        });
        await fixture.MarkTestFixtureReadyAsync();

        var scope = new Scope { Name = "Snapshot labels" };
        scope.Resources.Add(new() { Kind = ResourceKind.DatabaseSnapshot, Path = snapshotId });
        scope.Resources.Add(new() { Kind = ResourceKind.DatabaseSnapshot, Path = snapshotId, DisplayNameOverride = "User alias" });
        scope.Resources.Add(new() { Kind = ResourceKind.DatabaseSnapshot, Path = snapshotId });
        var store = runtime.StateStore;
        var owner = await store.CreateScopeAsync(scope, 0, Guid.NewGuid());
        ScopeResourceTarget[] targets = [new(0, captured, null), new(1, captured, null), new(2, exported, null)];
        await InTransactionAsync(runtime.Session, async (c, t) =>
            owner = await store.ResolveScopeResourceTargetsAsync(c, t, owner, targets, Guid.NewGuid()));
        await fixture.DestinationSqlAsync("UPDATE surf.ScopeResource SET SortOrdinal=SortOrdinal*2+10 WHERE ScopeKey=@Scope;",
            RelationalSession.Parameter("@Scope", SqlDbType.BigInt, owner.Key));

        var picker = new Surf2.RelationalScopePickerWindow(runtime);
        try
        {
            var grid = RuntimeWpfField<DataGrid>(picker, "_resources");
            var pickerType = typeof(Surf2.RelationalQueryPickerWindow<ScopeSummary>);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var list = (ListBox)pickerType.GetField("_list", fields)!.GetValue(picker)!;
            var status = (TextBlock)pickerType.GetField("_status", fields)!.GetValue(picker)!;
            using var trace = new StateAccessSqlTrace(connection);
            await RuntimeWpfInvokeTaskAsync(picker, "ReloadAsync");
            check(list.Items.Count == 1, "The Scopes picker lists the owned scope");
            list.SelectedIndex = 0;
            await RuntimeWpfWaitAsync(() => picker.SelectedSummary != null || !string.IsNullOrEmpty(status.Text),
                "the selected scope's database labels");
            check(grid.ItemsSource != null, "The Scopes picker loads the resource grid");
            var rows = grid.ItemsSource!.Cast<ScopedResource>().ToArray();
            check(rows.Length == 3 && rows[0].DisplayName == "Warehouse snapshot" && rows[0].Details == "Warehouse",
                "An existing scope row displays the linked snapshot title and database name");
            check(rows[1].DisplayName == "User alias" && rows[1].Details == "Warehouse",
                "A user alias takes precedence over the linked snapshot title");
            check(rows[2].DisplayName == "Export folder" && rows[2].Details == "Export folder",
                "A typed link distinguishes duplicate snapshot IDs and displays the export title despite gaps in resource order");
            check(rows.All(row => row.Path == snapshotId), "Snapshot display labels preserve the original resource path");

            await RuntimeWpfInvokeTaskAsync(picker, "BeforeAcceptAsync", CancellationToken.None);
            var persisted = Required(await store.ReadScopeAsync(owner.Key), "scope after displaying snapshot labels");
            check(StateAccessSameToken(owner, persisted.Token) && picker.ChangedScopeKeys.Count == 0 &&
                persisted.Value.Resources.Select(row => row.DisplayNameOverride).SequenceEqual(new[] { "", "User alias", "" }) &&
                persisted.Value.Resources.All(row => row.DetailsOverride == ""),
                "Displaying inferred names leaves saved overrides and the scope version intact");
            check(StateAccessReads(trace.Commands, "DatabaseSnapshot").Count == 1,
                "The scope grid loads all linked snapshot names in one query");
            StateAccessNoReads(trace.Commands, check, "Scope snapshot labels", "FullTableData", "DatabaseObjectRevision", "Asset");
        }
        finally { await picker.DrainQueriesAsync(); picker.Close(); }
        await fixture.VerifySourceUnchangedAsync(check);
    }
}
