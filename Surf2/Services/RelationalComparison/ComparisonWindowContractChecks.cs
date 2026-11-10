using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Services.RelationalComparison;

// Run in a separate test process. Owns one STA dispatcher and no Application.
public static class ComparisonWindowContractChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(string? appXamlPath = null)
    {
        if (Application.Current != null)
            throw new InvalidOperationException("Comparison window checks require a process without a shared WPF Application.");
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { completion.SetResult(await CheckWindowsAsync(appXamlPath)); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Surf comparison lifecycle checks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { return await completion.Task.ConfigureAwait(false); }
        finally { await Task.Run(thread.Join).ConfigureAwait(false); }
    }

    private static async Task<IReadOnlyList<string>> CheckWindowsAsync(string? appXamlPath)
    {
        var passed = new List<string>();
        string root = Path.Combine(Path.GetTempPath(), "Surf2ComparisonWindowChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stores = new List<ComparisonResultStore>();
        var windows = new List<RelationalComparisonResultWindow>();
        var rebuildStarted = Signal();
        var releaseRebuild = new TaskCompletionSource<ComparisonResultStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationStarted = Signal();
        var callbackStarted = Signal();
        var releaseOperation = Signal();
        using var releaseCallback = new ManualResetEventSlim();
        try
        {
            Check(Application.Current == null, "WPF checks create no shared Application");
            var old = await ResultAsync("old");
            var replacement = await ResultAsync("stale");
            CancellationToken rebuildToken = default;
            var retired = new RelationalComparisonResultWindow(old, "Retired context", rebuild: (_, ct) =>
            {
                rebuildToken = ct; rebuildStarted.TrySetResult();
                return releaseRebuild.Task; // Deliberately ignores cancellation like a late provider.
            });
            windows.Add(retired);
            Start(retired, Method(retired, "RebuildAsync").CreateDelegate<Func<CancellationToken, Task>>(retired));
            await rebuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long generation = Field<long>(retired, "_generation");
            Task retirement = retired.CloseAndDisposeAsync();
            Check(Field<bool>(retired, "_closing") && Field<long>(retired, "_generation") > generation && rebuildToken.IsCancellationRequested,
                "Closing synchronously generation-fences and cancels delayed comparison work");
            Check(!retirement.IsCompleted, "Window retirement waits for a provider that ignores cancellation");
            var freshResult = await ResultAsync("fresh");
            var fresh = new RelationalComparisonResultWindow(freshResult, "New context");
            windows.Add(fresh);
            foreach (string field in new[] { "_previous", "_next", "_export" })
                Check(DependencyPropertyHelper.GetValueSource(Field<Button>(fresh, field), Control.StyleProperty).IsExpression,
                    "Comparison toolbar resolves its Surf style dynamically: " + field);
            if (appXamlPath != null)
            {
                var styles = ReadButtonStyles(appXamlPath);
                fresh.Resources.MergedDictionaries.Add(styles);
                foreach (string field in new[] { "_previous", "_next", "_export" })
                {
                    var button = Field<Button>(fresh, field);
                    string key = field == "_export" ? "SurfSaveButtonStyle" : "SurfPrimaryButtonStyle";
                    Check(ReferenceEquals(button.Style, styles[key]) && button.Foreground is SolidColorBrush foreground &&
                        foreground.Color == Colors.White && button.Background is SolidColorBrush background &&
                        Contrast(foreground.Color, background.Color) >= 4.5,
                        "Actual Surf toolbar style uses readable white text: " + field);
                }
            }
            releaseRebuild.TrySetResult(replacement);
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            Check(ReferenceEquals(Field<ComparisonResultStore>(retired, "_result"), old) &&
                Field<DataGrid>(retired, "_grid").ItemsSource == null,
                "Late rebuilt results are never bound to a retired WPF window");
            await DisposedAsync(old, "Retired window disposes its original spool");
            await DisposedAsync(replacement, "Retired window disposes the late replacement spool");
            Check(!Field<bool>(fresh, "_closing") && (await freshResult.ReadPageAsync()).Rows.Single().Key == "fresh" &&
                Directory.EnumerateDirectories(root).Count() == 1,
                "Completing old retirement leaves a subsequent new-context window and spool intact");
            // Two competing refreshes exercise cancellation/generation of actual result pages.
            Start(fresh, Method(fresh, "RefreshAsync").CreateDelegate<Func<CancellationToken, Task>>(fresh));
            Field<TextBox>(fresh, "_search").Text = "no-match";
            await WaitForTasksAsync(fresh);
            Check(Field<DataGrid>(fresh, "_grid").ItemsSource is IReadOnlyList<ComparisonResultRow> { Count: 0 },
                "Newer WPF search refresh wins over the cancelled earlier page");
            await fresh.CloseAndDisposeAsync();

            var callbacks = new RelationalComparisonResultWindow(await ResultAsync("callbacks"), "Cancellation callbacks");
            windows.Add(callbacks);
            CancellationToken operationToken = default;
            Start(callbacks, async ct =>
            {
                operationToken = ct;
                using var registration = ct.Register(() =>
                {
                    callbackStarted.TrySetResult();
                    if (!releaseCallback.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Callback release timed out.");
                });
                operationStarted.TrySetResult();
                await releaseOperation.Task;
            });
            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task cleanup = callbacks.CloseAndDisposeAsync();
            Check(operationToken.IsCancellationRequested && !cleanup.IsCompleted,
                "Window close flags cancellation immediately without blocking its STA on provider callbacks");
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseOperation.TrySetResult();
            Check(!cleanup.IsCompleted, "Cleanup retains ownership of in-flight cancellation callbacks");
            releaseCallback.Set();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Directory.EnumerateFileSystemEntries(root).Any() == false,
                "WPF retirement drains work and removes every owned scratch directory");
            Check(Application.Current == null, "WPF lifecycle checks leave no Application behind");
            return passed;
        }
        finally
        {
            if (stores.Count > 1) releaseRebuild.TrySetResult(stores[1]);
            releaseOperation.TrySetResult(); releaseCallback.Set();
            foreach (var window in windows) await window.CloseAndDisposeAsync();
            foreach (var store in stores) await store.DisposeAsync();
            Directory.Delete(root); // Exact UUID-owned directory, now empty; never recursive.
        }

        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); passed.Add(name); }
        async Task DisposedAsync(ComparisonResultStore store, string name)
        {
            try { await store.ReadPageAsync(); }
            catch (ObjectDisposedException) { Check(true, name); return; }
            throw new InvalidOperationException(name);
        }
        async Task<ComparisonResultStore> ResultAsync(string key)
        {
            var input = new ComparisonTableInput(["Id"], ["Id"], 1, Rows(key));
            var result = await ComparisonTableEngine.BuildAsync(input, input, ["Id"], new() { StagingDirectory = root });
            stores.Add(result); return result;
        }
    }

    private static async IAsyncEnumerable<CaptureRow> Rows(string key)
    {
        await Task.CompletedTask;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { Id = key }));
        yield return new(0, document.RootElement.Clone(), document.RootElement.GetRawText().Length * 2L);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ResourceDictionary ReadButtonStyles(string path)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] keys = ["SurfActionButtonStyle", "SurfPrimaryButtonStyle", "SurfSaveButtonStyle", "SurfCloseButtonStyle"];
        var source = XDocument.Load(path);
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName),
            source.Descendants(presentation + "Style").Where(style => keys.Contains((string?)style.Attribute(xaml + "Key")))
                .Select(style => new XElement(style)));
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }
    private static double Contrast(Color left, Color right)
    {
        double a = Luminance(left), b = Luminance(right);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        static double Channel(byte value)
        { double scaled = value / 255d; return scaled <= 0.04045 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4); }
    }
    private static MethodInfo Method(object target, string name) => target.GetType().GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(name);
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) ?? throw new MissingFieldException(name));
    private static void Start(RelationalComparisonResultWindow window, Func<CancellationToken, Task> action) =>
        Method(window, "Start").Invoke(window, [action]);
    private static async Task WaitForTasksAsync(RelationalComparisonResultWindow window)
    {
        while (Field<HashSet<Task>>(window, "_tasks").Count != 0)
        {
            await Task.WhenAll(Field<HashSet<Task>>(window, "_tasks").ToArray()).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }
    }
}
