using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational.Migration;

public static partial class StorageRegressionSuite
{
    public static void RunMigrationProgressChecks(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        VerifyMigrationProgressEstimator(check);
        VerifyMigrationProgressBounds(check);
        VerifyMigrationProgressLogs(check);
        VerifyMigrationProgressDialog(check);
    }

    private static void VerifyMigrationProgressEstimator(Action<bool, string> check)
    {
        var clock = new MigrationManualTime();
        var updates = new MigrationProgressCapture();
        var messages = new List<string>();
        using var reporter = new MigrationProgressReporter(new MigrationMessageCapture(messages.Add), updates, clock);
        const string phase = "Converting captured databases and reverse history";
        reporter.StartPhase(phase, 100, "rows");
        check(messages.SequenceEqual([phase]), "Progress: legacy phase callback is exact and immediate");
        check(updates.Latest is { Completed: 0, Total: 100, EstimatedRemaining: null },
            "Progress: known total has no speculative ETA before throughput");
        clock.Advance(TimeSpan.FromSeconds(1));
        reporter.Advance("First batch", 10);
        clock.Advance(TimeSpan.FromSeconds(1));
        check(updates.Latest is { Completed: 10, EstimatedRemaining: null },
            "Progress: one measured batch does not establish ETA");
        reporter.Advance("Second batch", 10);
        clock.Advance(TimeSpan.FromSeconds(1));
        check(updates.Latest?.EstimatedRemaining == null,
            "Progress: measurement shorter than three seconds leaves ETA unavailable");
        reporter.Advance("Third batch", 10);
        clock.Advance(TimeSpan.FromSeconds(1));
        MigrationProgressUpdate measured = updates.Latest!;
        check(measured.Completed == 30 && measured.EstimatedRemaining == TimeSpan.FromSeconds(7),
            "Progress: deterministic phase ETA uses measured units and phase duration");
        long emissions = updates.Count;
        clock.Advance(TimeSpan.FromSeconds(20));
        MigrationProgressUpdate stalled = updates.Latest!;
        check(updates.Count == emissions + 20 && stalled.Completed == measured.Completed &&
            stalled.Detail == measured.Detail && stalled.EstimatedRemaining == measured.EstimatedRemaining,
            "Progress: SQL-stall heartbeats arrive without inventing work or counting down ETA");
        check(stalled.LastActivityElapsed == TimeSpan.FromSeconds(21) &&
            stalled.Elapsed == TimeSpan.FromSeconds(24) && stalled.PhaseElapsed == stalled.Elapsed,
            "Progress: heartbeat measures elapsed and real activity age monotonically");
        reporter.Advance("No completed work", 0);
        clock.Advance(TimeSpan.FromSeconds(1));
        check(updates.Latest?.LastActivityElapsed == TimeSpan.FromSeconds(22),
            "Progress: zero-unit update cannot clear a stall");
        reporter.Advance("Resumed batch", 10);
        clock.Advance(TimeSpan.FromSeconds(1));
        check(updates.Latest?.EstimatedRemaining == TimeSpan.FromSeconds(37.5) &&
            updates.Latest.LastActivityElapsed == TimeSpan.FromSeconds(1),
            "Progress: resumed real work updates ETA using the measured phase including its stall");

        reporter.StartPhase("Validating preservation and publishing the destination");
        check(updates.Latest is { Completed: 0, Total: null, EstimatedRemaining: null, PhaseElapsed.Ticks: 0 } &&
            updates.Latest.Elapsed == TimeSpan.FromSeconds(26),
            "Progress: phase reset discards old count/ETA while retaining overall elapsed");
        clock.Advance(TimeSpan.FromSeconds(5));
        reporter.Advance("Validation batch", 500);
        clock.Advance(TimeSpan.FromSeconds(1));
        check(updates.Latest is { Completed: 500, Total: null, EstimatedRemaining: null },
            "Progress: unknown totals remain unknown after measured work");
        reporter.SetDetail("Checking SQL hashes");
        clock.Advance(TimeSpan.FromSeconds(1));
        check(updates.Latest is { Completed: 500, Detail: "Checking SQL hashes" } &&
            updates.Latest.LastActivityElapsed == TimeSpan.FromSeconds(1),
            "Progress: real detail activity never changes units or throughput");
        reporter.StartPhase("Empty phase", 0);
        check(updates.Latest is { Completed: 0, Total: 0 } && updates.Latest.EstimatedRemaining == TimeSpan.Zero,
            "Progress: an explicitly empty phase is complete without division by zero");
        reporter.Finish("Conversion cancelled without activation.");
        check(updates.Latest is { Completed: 0, Detail: "Conversion cancelled without activation." } &&
            messages.Last() == "Conversion cancelled without activation.",
            "Progress: terminal cancellation is immediate and does not manufacture completed work");
        emissions = updates.Count;
        reporter.StartPhase("Late phase");
        reporter.Advance("Late work");
        reporter.SetDetail("Late detail");
        reporter.Finish("Late finish");
        clock.Advance(TimeSpan.FromMinutes(1));
        check(updates.Count == emissions && clock.ActiveTimers == 0,
            "Progress: finish stops the heartbeat and rejects late callbacks");
    }

    private static void VerifyMigrationProgressBounds(Action<bool, string> check)
    {
        var clock = new MigrationManualTime();
        var updates = new MigrationProgressCapture();
        var reporter = new MigrationProgressReporter(null, updates, clock);
        reporter.StartPhase("Importing", 20, "rows");
        long initial = updates.Count;
        Parallel.For(0, 10000, _ => reporter.Advance("Batch", 1));
        check(updates.Count == initial, "Progress: busy worker callbacks are throttled within a second");
        clock.Advance(TimeSpan.FromSeconds(1));
        check(updates.Count == initial + 1 && updates.Latest?.Completed == 10000 &&
            updates.Latest.EstimatedRemaining == TimeSpan.Zero,
            "Progress: concurrent counting is serialized without losing or clamping real units");
        reporter.StartPhase(new string('p', 100000), null, new string('u', 100000));
        reporter.SetDetail(new string('d', 100000));
        clock.Advance(TimeSpan.FromSeconds(1));
        check(updates.Latest!.Phase.Length <= 1024 && updates.Latest.Detail.Length <= 1024 &&
            updates.Latest.UnitName.Length <= 64,
            "Progress: retained phase/detail/unit text has constant size bounds");
        reporter.Advance("Maximum", long.MaxValue);
        reporter.Advance("Saturating", long.MaxValue);
        clock.Advance(TimeSpan.FromSeconds(1));
        check(updates.Latest?.Completed == long.MaxValue, "Progress: long counters cannot overflow backwards");
        bool negativeRejected = false;
        try { reporter.Advance("Negative", -1); }
        catch (ArgumentOutOfRangeException) { negativeRejected = true; }
        check(negativeRejected, "Progress: negative work is rejected");
        reporter.Dispose();
        reporter.Dispose();
        long emissions = updates.Count;
        clock.Advance(TimeSpan.FromSeconds(30));
        reporter.Finish("Ignored");
        check(updates.Count == emissions && clock.ActiveTimers == 0,
            "Progress: idempotent disposal releases timer and ignores queued/late work");

        using var observerFailure = new MigrationProgressReporter(new MigrationMessageCapture(_ => throw new IOException()),
            new MigrationThrowingProgress(), clock);
        observerFailure.StartPhase("Safe reporting");
        observerFailure.Advance("Safe reporting", 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        observerFailure.Finish("Safe reporting complete");
        check(true, "Progress: presentation observer failures cannot abort migration or timer callbacks");
    }

    private static void VerifyMigrationProgressLogs(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Surf2_Progress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ownedFiles = new List<string>();
        try
        {
            Guid identity = Guid.NewGuid();
            var clock = new MigrationManualTime();
            var updates = new MigrationProgressCapture();
            using (var reporter = new MigrationProgressReporter(null, updates, clock))
            {
                reporter.StartPhase("Import rows", 100, "rows");
                reporter.AttachLog(directory, identity);
                string path = updates.Latest?.LogPath ?? throw new InvalidOperationException("Progress log was not attached.");
                ownedFiles.Add(path);
                reporter.AttachLog(directory, identity);
                check(Path.GetDirectoryName(path) == directory && Path.GetFileName(path).Contains(identity.ToString("N")) &&
                    Directory.GetFiles(directory).Length == 1,
                    "Progress log: unique attempt is inside the existing staging directory and attaches once");
                reporter.Advance("Batch", 10);
                clock.Advance(TimeSpan.FromSeconds(4));
                reporter.Advance("Batch", 10);
                clock.Advance(TimeSpan.FromSeconds(26));
                check(ReadMigrationProgressLog(path).Length == 4,
                    "Progress log: thirty heartbeat seconds yield only attachment and ten-second entries");
                using (JsonDocument entry = JsonDocument.Parse(ReadMigrationProgressLog(path).Last()))
                {
                    var root = entry.RootElement;
                    check(root.GetProperty("TimestampUtc").GetDateTimeOffset() == clock.GetUtcNow() &&
                        root.GetProperty("Completed").GetInt64() == 20 && root.GetProperty("Total").GetInt64() == 100 &&
                        root.GetProperty("PhaseEstimatedRemaining").GetString() == "00:00:16" &&
                        root.GetProperty("LastActivityElapsed").GetString() == "00:00:26" &&
                        root.GetProperty("Elapsed").GetString() == "00:00:30" && root.GetProperty("Detail").GetString() == "Batch",
                        "Progress log: structured entries retain UTC, counts, phase ETA, elapsed and real activity age");
                }
                reporter.SetDetail("Server=private-host;Database=private-db;User ID=private-user;Password=never-log-this");
                reporter.Finish("Failed before activation: Password=another-secret");
                check(!File.ReadAllText(path).Contains("never-log-this") && !File.ReadAllText(path).Contains("private-host") &&
                    !File.ReadAllText(path).Contains("another-secret") && ReadMigrationProgressLog(path).Last().Contains("Finished"),
                    "Progress log: terminal failure is persisted without credentials/connection strings");
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                check(exclusive.Length > 0, "Progress log: finish releases the file handle before disposal");
            }

            string cappedPath;
            var cappedClock = new MigrationManualTime();
            var cappedUpdates = new MigrationProgressCapture();
            using (var reporter = new MigrationProgressReporter(null, cappedUpdates, cappedClock))
            {
                reporter.StartPhase("Bounded attempt");
                reporter.AttachLog(directory, identity);
                cappedPath = cappedUpdates.Latest!.LogPath!;
                ownedFiles.Add(cappedPath);
                check(cappedPath != ownedFiles[0], "Progress log: retries of the same migration never overwrite an earlier attempt");
                for (int i = 0; i < 6000; i++)
                {
                    reporter.StartPhase("Phase " + i + new string('x', 1024));
                    reporter.SetDetail(new string('y', 1024));
                }
                check(new FileInfo(cappedPath).Length <= MigrationProgressReporter.MaxLogBytes,
                    "Progress log: even forced transition storms obey the hard attempt byte cap");
                reporter.Finish("Cancelled without activation");
            }
            check(new FileInfo(cappedPath).Length <= MigrationProgressReporter.MaxLogBytes &&
                ReadMigrationProgressLog(cappedPath).Last().Contains("Cancelled without activation"),
                "Progress log: reserved terminal entry survives exhausted logging capacity");

            var disposalUpdates = new MigrationProgressCapture();
            using (var reporter = new MigrationProgressReporter(null, disposalUpdates, new MigrationManualTime()))
            {
                reporter.StartPhase("Interrupted SQL");
                reporter.AttachLog(directory, identity);
                ownedFiles.Add(disposalUpdates.Latest!.LogPath!);
            }
            string disposedPath = ownedFiles.Last();
            using (var exclusive = new FileStream(disposedPath, FileMode.Open, FileAccess.Read, FileShare.None))
                check(exclusive.Length > 0, "Progress log: unfinished disposal releases the file handle");
            check(ReadMigrationProgressLog(disposedPath).Last().Contains("StoppedBeforeFinish"),
                "Progress log: unfinished disposal records an interrupted attempt without claiming success");
        }
        finally
        {
            // Delete only exact files produced by this fixture, never enumerate/delete a recovery directory.
            foreach (string path in ownedFiles) File.Delete(path);
            Directory.Delete(directory);
        }
        check(!Directory.Exists(directory), "Progress log: isolated temporary fixture and its exact attempt files are cleaned up");

        var failureClock = new MigrationManualTime();
        var failureUpdates = new MigrationProgressCapture();
        using var failedLog = new MigrationProgressReporter(null, failureUpdates, failureClock);
        failedLog.StartPhase("Continues without log");
        failedLog.AttachLog("\0invalid-stage-directory", Guid.NewGuid());
        failedLog.Advance("Still progressing", 1);
        failureClock.Advance(TimeSpan.FromSeconds(2));
        failedLog.Finish("Complete without diagnostics");
        check(failureUpdates.Latest is { Completed: 1, LogPath: null },
            "Progress log: invalid/unavailable paths cannot abort migration");
    }

    private static string[] ReadMigrationProgressLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is string line) lines.Add(line);
        return lines.ToArray();
    }

    private static void VerifyMigrationProgressDialog(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RenderMigrationProgressDialog(check); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "Surf2 isolated progress dialog checks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RenderMigrationProgressDialog(Action<bool, string> check)
    {
        if (Application.Current != null || InternalLogService.IsEnabled)
            throw new InvalidOperationException("Progress dialog checks require a fresh process with application logging disabled.");
        var app = new Surf2.App();
        try
        {
            // Resource initialization only: do not show a window, pump startup, connect SQL or run migration.
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string outputDirectory = Path.Combine(AppContext.BaseDirectory, "migration-progress-visual-checks");
            Directory.CreateDirectory(outputDirectory);
            foreach (string theme in new[] { AppearanceSettings.LightTheme, AppearanceSettings.DarkTheme })
            foreach (bool minimum in new[] { false, true })
            foreach (string state in new[] { "known", "unknown", "stalled", "error" })
            {
                AppThemeService.Apply(theme);
                string source = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
                {
                    DataSource = "Offscreen_" + new string('s', 140), InitialCatalog = "Surf2_ProgressFixture",
                    IntegratedSecurity = true, TrustServerCertificate = true
                }.ConnectionString;
                var window = new Surf2.RelationalMigrationWindow(source);
                using var cancellation = new CancellationTokenSource();
                try
                {
                    ProgressDialogField(window, "_cancellation", cancellation);
                    ProgressDialogField(window, "_attempt", 7L);
                    ProgressDialogInvoke(window, "SetRunning", true);
                    ((ComboBox)window.FindName("Operation")).SelectedIndex = 2;
                    ((TextBox)window.FindName("RecoveryPath")).Text = "Offscreen recovery staging";
                    var update = new MigrationProgressUpdate("Validating preservation and publishing the destination " + new string('p', 300),
                        "Checking mapped rows and preservation hashes. " + new string('d', 900), 12345,
                        state == "unknown" ? null : 50000, "mapped units", TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(2),
                        state == "unknown" ? null : TimeSpan.FromMinutes(6),
                        TimeSpan.FromSeconds(state == "stalled" ? 75 : 1),
                        Path.Combine(outputDirectory, new string('l', 180) + ".progress.jsonl"));
                    ProgressDialogInvoke(window, "PresentProgress", update);
                    var status = (TextBox)window.FindName("Status");
                    if (state == "error")
                    {
                        ProgressDialogInvoke(window, "SetRunning", false);
                        status.Text = "Conversion failed without activation. Recovery staging is preserved.";
                    }
                    string scenario = $"{theme}-{(minimum ? "minimum" : "default")}-{state}";
                    var eta = (TextBlock)window.FindName("EtaText");
                    var progress = (ProgressBar)window.FindName("Progress");
                    check(eta.Text.StartsWith("Phase remaining:", StringComparison.Ordinal) &&
                        (state != "unknown" || (progress.IsIndeterminate && eta.Text.EndsWith("unavailable", StringComparison.Ordinal))),
                        "Progress dialog " + scenario + ": only phase ETA is shown; unknown totals stay indeterminate");
                    check(state == "unknown" || Math.Abs(progress.Value - 24.69) < 0.001,
                        "Progress dialog " + scenario + ": determinate percentage uses only current phase counts");
                    check(((TextBlock)window.FindName("ActivityText")).Text ==
                        (state == "stalled" ? "Last activity: 00:01:15 ago" : "Last activity: 00:00:01 ago"),
                        "Progress dialog " + scenario + ": real activity age remains explicit during a stall");
                    check(((TextBox)window.FindName("ProgressLogPath")).Text == update.LogPath,
                        "Progress dialog " + scenario + ": full attempt log path remains selectable");
                    if (theme == AppearanceSettings.LightTheme && !minimum && state == "known")
                        VerifyMigrationDialogGuards(window, cancellation, check);

                    int width = (int)(minimum ? window.MinWidth : window.Width) - 16;
                    int height = (int)(minimum ? window.MinHeight : window.Height) - 40;
                    var content = (Grid)window.Content;
                    var elements = content.Children.OfType<FrameworkElement>().Where(x => x.Visibility == Visibility.Visible)
                        .Concat(new[] { "PhaseText", "CountText", "ElapsedText", "EtaText", "ActivityText", "ProgressLogPath",
                            "Status", "ConvertButton", "CloseButton" }.Select(name => (FrameworkElement)window.FindName(name))).ToArray();
                    window.Content = null;
                    var surface = new Grid { Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey) };
                    surface.Children.Add(content);
                    var size = new Size(width, height);
                    surface.Measure(size);
                    surface.Arrange(new Rect(size));
                    surface.UpdateLayout();
                    var allocation = new Rect(16, 16, width - 32, height - 32);
                    bool contained = elements.All(element =>
                    {
                        Rect bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
                        return bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= allocation.Left - 1 &&
                            bounds.Top >= allocation.Top - 1 && bounds.Right <= allocation.Right + 1 && bounds.Bottom <= allocation.Bottom + 1;
                    });
                    check(contained, "Progress dialog " + scenario + ": all active fields/status/actions fit the reduced client allocation");
                    if (status.Template.FindName("PART_ContentHost", status) is not ScrollViewer viewport)
                        throw new InvalidOperationException("Progress status lost its scrolling viewport.");
                    check(viewport.ViewportHeight > 0 && (state == "error" || viewport.ExtentHeight > viewport.ViewportHeight),
                        "Progress dialog " + scenario + ": latest detail has a bounded positive scrolling viewport");
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    byte[] pixels = new byte[width * height * 4];
                    bitmap.CopyPixels(pixels, width * 4, 0);
                    var background = ((SolidColorBrush)surface.Background).Color;
                    long opaque = 0, different = 0;
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        if (pixels[i + 3] == 255) opaque++;
                        if (Math.Abs(pixels[i] - background.B) > 2 || Math.Abs(pixels[i + 1] - background.G) > 2 ||
                            Math.Abs(pixels[i + 2] - background.R) > 2) different++;
                    }
                    check(opaque > width * height * 0.95 && different > width * height * 0.01,
                        "Progress dialog " + scenario + ": offscreen CPU pixels show nonblank opaque content");
                    string path = Path.Combine(outputDirectory, "migration-progress-" + scenario.ToLowerInvariant() + ".png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) encoder.Save(output);
                    check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero && window.Result == null,
                        "Progress dialog offscreen PNG: " + path + "; no native window, migration or SQL execution");
                    surface.Children.Remove(content);
                }
                finally
                {
                    ProgressDialogField(window, "_cancellation", null);
                    window.Close();
                }
            }
        }
        finally
        {
            try { app.Shutdown(); }
            finally { app.Dispatcher.InvokeShutdown(); }
        }
    }

    private static void VerifyMigrationDialogGuards(Surf2.RelationalMigrationWindow window,
        CancellationTokenSource cancellation, Action<bool, string> check)
    {
        bool Accept(long attempt, CancellationTokenSource token) =>
            (bool)ProgressDialogInvoke(window, "AcceptProgress", attempt, token)!;
        using var oldAttempt = new CancellationTokenSource();
        check(Accept(7, cancellation) && !Accept(6, cancellation) && !Accept(7, oldAttempt),
            "Progress dialog: attempt generation and cancellation-source identity reject callbacks from earlier retries");
        ProgressDialogField(window, "_running", false);
        check(!Accept(7, cancellation), "Progress dialog: failure/cleanup state rejects queued late progress");
        ProgressDialogField(window, "_running", true);
        ProgressDialogField(window, "_completed", true);
        check(!Accept(7, cancellation), "Progress dialog: success state rejects queued late progress");
        ProgressDialogField(window, "_completed", false);
        cancellation.Cancel();
        check(!Accept(7, cancellation), "Progress dialog: cancellation status cannot be overwritten by queued progress");
    }

    private static object? ProgressDialogInvoke(Surf2.RelationalMigrationWindow window, string method, params object[] arguments) =>
        typeof(Surf2.RelationalMigrationWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, arguments);

    private static void ProgressDialogField(Surf2.RelationalMigrationWindow window, string field, object? value) =>
        typeof(Surf2.RelationalMigrationWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private sealed class MigrationProgressCapture : IProgress<MigrationProgressUpdate>
    {
        public long Count { get; private set; }
        public MigrationProgressUpdate? Latest { get; private set; }
        public void Report(MigrationProgressUpdate value) { Count++; Latest = value; }
    }

    private sealed class MigrationMessageCapture(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }

    private sealed class MigrationThrowingProgress : IProgress<MigrationProgressUpdate>
    {
        public void Report(MigrationProgressUpdate value) => throw new IOException("Observer fixture");
    }

    private sealed class MigrationManualTime : TimeProvider
    {
        private readonly List<MigrationManualTimer> _timers = [];
        private long _ticks;
        public int ActiveTimers => _timers.Count(timer => !timer.Disposed && timer.Due != long.MaxValue);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new MigrationManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            long target = checked(_ticks + duration.Ticks);
            while (true)
            {
                MigrationManualTimer? next = _timers.Where(timer => !timer.Disposed && timer.Due <= target)
                    .MinBy(timer => timer.Due);
                if (next == null) break;
                _ticks = next.Due;
                next.Fire();
            }
            _ticks = target;
        }

        private sealed class MigrationManualTimer(MigrationManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; private set; } = long.MaxValue;
            public bool Disposed { get; private set; }
            private long _period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner._ticks + dueTime.Ticks);
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
            public void Fire()
            {
                Due = _period <= 0 ? long.MaxValue : checked(Due + _period);
                callback(state);
            }
            public void Dispose() { Disposed = true; Due = long.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
