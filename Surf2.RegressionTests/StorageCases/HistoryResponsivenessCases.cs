using System.Windows;
using System.Windows.Controls;
using Surf2;
using Surf2.Services;
using Surf2.Services.RelationalComparison;

public static partial class StorageRegressionSuite
{
    private static async Task VerifyHistoryActionsDuringCountsAsync(RelationalSnapshotHistoryWindow window,
        RelationalRuntime runtime, object version, Action<bool, string> check)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0;
        using var trace = new StateAccessSqlTrace(runtime.Options.ConnectionString, command =>
        {
            if (!command.CommandText.Contains("varbinary(max)", StringComparison.OrdinalIgnoreCase) ||
                !command.CommandText.Contains("DefinitionContentKey", StringComparison.OrdinalIgnoreCase) ||
                Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("History responsiveness gate was not released.");
        });
        RelationalComparisonTarget? opened = null;
        EventHandler<RelationalHistoryOpenEventArgs> handler = (_, e) => opened = e.Target;
        window.OpenRequested += handler;
        try
        {
            await RuntimeWpfInvokeTaskAsync(window, "ToggleRowAsync", version);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var row = ((RelationalSnapshotHistoryWindow.HistoryVersionRow)version).Children
                .First(file => file.ResourceTypeColumn == "Stored Procedure");
            var menu = (ContextMenu)RuntimeWpfInvoke(window, "CreateFileContextMenu", row)!;
            RuntimeWpfAssert(!row.CountsLoaded && menu.Items.OfType<MenuItem>().All(item => item.IsEnabled), check,
                "Historical file actions remain enabled while background line-count SQL is pending");
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await RuntimeWpfWaitAsync(() => opened != null, "view while history counts are blocked");
            RuntimeWpfAssert(opened?.ResourceKey == row.File.ResourceKey && !row.CountsLoaded, check,
                "View resolves the selected file without waiting for unrelated line-count reads");
        }
        finally
        {
            release.Set(); window.OpenRequested -= handler;
            await RuntimeWpfField<Task>(window, "_countsTask").WaitAsync(TimeSpan.FromSeconds(60));
        }
        await RuntimeWpfInvokeTaskAsync(window, "ToggleRowAsync", version);
    }
}
