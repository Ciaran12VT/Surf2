using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

public static partial class StorageRegressionSuite
{
    public static Task RunScopePickerUiChecksAsync(Action<bool, string> check)
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
}
