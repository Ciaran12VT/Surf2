using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;

public static partial class StorageRegressionSuite
{
    public static Task RunMigrationVisualChecksAsync(Action<bool, string> check, string outputDir)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        if (!Path.IsPathFullyQualified(outputDir))
            throw new ArgumentException("Offscreen PNG output requires an explicit absolute directory.", nameof(outputDir));
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir));
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RenderMigrationCasesOnSta(check, directory);
                completion.TrySetResult(true);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Surf2 migration offscreen regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RenderMigrationCasesOnSta(Action<bool, string> check, string outputDir)
    {
        if (Application.Current != null)
            throw new InvalidOperationException("Offscreen migration checks require a fresh console process without a WPF Application.");
        // OnExit logs only when enabled. Inspect the public read-only flag, never initialize/configure logging or its paths.
        Type log = typeof(Surf2.App).Assembly.GetType("Surf2.Services.InternalLogService", throwOnError: true)!;
        PropertyInfo enabled = log.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Cannot verify that application exit logging is disabled.");
        if (enabled.GetValue(null) is not false)
            throw new InvalidOperationException("Offscreen migration checks require disabled application logging to avoid user-file writes.");

        var app = new Surf2.App();
        try
        {
            // No Application.Run, window Show, dispatcher pump, or OnStartup: only application resources are loaded.
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Directory.CreateDirectory(outputDir);
            check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
                "Offscreen migration renderer owns a dedicated STA thread");
            string source = new SqlConnectionStringBuilder
            {
                DataSource = "Surf2_Regression_Offscreen_" + new string('s', 140) + "\\FixtureOnly",
                InitialCatalog = "Surf2_Regression_" + Guid.NewGuid().ToString("N"),
                IntegratedSecurity = true, TrustServerCertificate = true, Pooling = false, ConnectTimeout = 1
            }.ConnectionString;
            // Reviewed fixed deductions exercise a smaller client allocation without creating a native window.
            // They are an approximation, not a measurement of this machine's caption/borders or DPI.
            const int frameWidth = 16, frameHeight = 40;
            check(true, $"SmallClient offscreen approximation deducts {frameWidth} DIP width and {frameHeight} DIP height " +
                "from declared outer window dimensions; this is not an exact native-client measurement.");
            var hashes = new Dictionary<(string Theme, bool Minimum, bool SmallClient, bool Resume), string>();
            var viewportLabels = new Dictionary<(bool Minimum, bool SmallClient), string>();
            foreach (string theme in new[] { AppearanceSettings.LightTheme, AppearanceSettings.DarkTheme })
            {
                AppThemeService.Apply(theme);
                foreach (bool minimum in new[] { false, true })
                foreach (bool smallClient in new[] { false, true })
                foreach (bool resume in new[] { false, true })
                {
                    var window = new Surf2.RelationalMigrationWindow(source);
                    try
                    {
                        int outerWidth = MigrationDeclaredDimension(minimum ? window.MinWidth : window.Width);
                        int outerHeight = MigrationDeclaredDimension(minimum ? window.MinHeight : window.Height);
                        int width = outerWidth - (smallClient ? frameWidth : 0);
                        int height = outerHeight - (smallClient ? frameHeight : 0);
                        if (width <= 0 || height <= 0) throw new InvalidOperationException("Invalid reduced migration client dimensions.");
                        string viewport = smallClient ? $"{outerWidth}x{outerHeight}-small-client-approx-{width}x{height}" : $"{width}x{height}";
                        viewportLabels[(minimum, smallClient)] = viewport;
                        string scenario = $"migration-{theme.ToLowerInvariant()}-{viewport}-{(resume ? "resume" : "normal")}";
                        if (window.Content is not Grid content)
                            throw new InvalidOperationException("Migration window content is no longer the expected Grid.");
                        var controls = new MigrationVisualControls(
                            MigrationNamed<TextBlock>(window, "SourceText"), MigrationNamed<TextBox>(window, "DestinationName"),
                            MigrationNamed<ComboBox>(window, "Operation"), MigrationNamed<TextBlock>(window, "RecoveryLabel"),
                            MigrationNamed<DockPanel>(window, "RecoveryPanel"), MigrationNamed<TextBox>(window, "RecoveryPath"),
                            MigrationNamed<TextBox>(window, "Status"), MigrationNamed<Button>(window, "ConvertButton"),
                            MigrationNamed<Button>(window, "CloseButton"));
                        controls.Destination.Text = "Offscreen destination";
                        controls.RecoveryPath.Text = "Offscreen recovery fixture";
                        controls.Operation.SelectedIndex = resume ? 2 : 0;
                        controls.Status.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 80).Select(i =>
                            $"Fixture status {i:D2}: original source preserved; destination remains unavailable until validation completes. " +
                            "A deliberately long progress message verifies wrapping and the status viewport without starting migration."));
                        check(window.MinWidth >= 540 && window.MinHeight >= 360 &&
                            window.Width >= window.MinWidth && window.Height >= window.MinHeight,
                            scenario + ": declared default/minimum dimensions preserve the supported size floor");

                        // Detach from the unshown Window so inherited hidden visibility cannot blank the render.
                        // This surface supplies only the window background and the real content Grid's existing margin.
                        window.Content = null;
                        var surface = new Grid { Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey) };
                        surface.Children.Add(content);
                        SetMigrationLayoutSize(surface, content, width, height);
                        string path = Path.Combine(outputDir, scenario + ".png");
                        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), outputDir, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("PNG output escaped the explicit output directory.");
                        var pixels = SaveMigrationRender(surface, path, width, height);
                        hashes.Add((theme, minimum, smallClient, resume), pixels.Hash);
                        check(true, $"Offscreen WPF PNG: {path}; size={width}x{height}; opaque_pixels={pixels.OpaquePixels}; " +
                            $"nonbackground_pixels={pixels.NonBackgroundPixels}; sha256={pixels.Hash}. This is content rendering, not a native-window screenshot.");
                        check(pixels.OpaquePixels >= (long)width * height * 95 / 100 &&
                            pixels.NonBackgroundPixels > (long)width * height / 100,
                            scenario + ": CPU pixel counts show opaque background and nonblank content");
                        if (smallClient) CheckMigrationSmallClientBounds(surface, content, controls, resume, width, height, scenario, check);
                        CheckMigrationLayout(content, controls, resume, scenario, check);
                        check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero &&
                            !controls.Operation.IsDropDownOpen && window.Result == null && window.DestinationConnectionString == null,
                            scenario + ": window remains unshown without HWND, popup or migration execution");
                        surface.Children.Remove(content);
                    }
                    finally { window.Close(); }
                }
            }
            foreach (bool minimum in new[] { false, true })
            foreach (bool smallClient in new[] { false, true })
            {
                string viewport = viewportLabels[(minimum, smallClient)];
                foreach (bool resume in new[] { false, true })
                    check(hashes[(AppearanceSettings.LightTheme, minimum, smallClient, resume)] !=
                        hashes[(AppearanceSettings.DarkTheme, minimum, smallClient, resume)],
                        $"Offscreen {viewport} {(resume ? "resume" : "normal")}: Light and Dark pixels differ");
                foreach (string theme in new[] { AppearanceSettings.LightTheme, AppearanceSettings.DarkTheme })
                    check(hashes[(theme, minimum, smallClient, false)] != hashes[(theme, minimum, smallClient, true)],
                        $"Offscreen {theme} {viewport}: normal and resume pixels differ");
            }
            RenderSnapshotWorkflowCasesOnSta(check, outputDir);
        }
        finally
        {
            // Unstarted pipe fields are null; logging was verified disabled. Discard queued startup, never pump it.
            try { app.Shutdown(); }
            finally { app.Dispatcher.InvokeShutdown(); }
        }
    }

    private sealed record MigrationVisualControls(TextBlock Source, TextBox Destination, ComboBox Operation,
        TextBlock RecoveryLabel, DockPanel RecoveryPanel, TextBox RecoveryPath, TextBox Status, Button Convert, Button Close);

    private static T MigrationNamed<T>(FrameworkElement window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing migration visual control: " + name);

    private static int MigrationDeclaredDimension(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 4096 || Math.Abs(value - Math.Round(value)) > 0.001)
            throw new InvalidOperationException("Offscreen migration fixture requires bounded integer-DIP window dimensions.");
        return checked((int)Math.Round(value));
    }

    private static void SetMigrationLayoutSize(Grid surface, Grid content, int width, int height)
    {
        var size = new Size(width, height);
        surface.Width = width;
        surface.Height = height;
        content.Measure(size);
        surface.Measure(size);
        surface.Arrange(new Rect(new Point(0, 0), size));
        // The surface has arranged the content with its real 16-DIP margin. Explicitly reapply that same slot.
        content.Arrange(new Rect(new Point(0, 0), size));
        content.UpdateLayout();
        surface.UpdateLayout();
    }

    private static void CheckMigrationLayout(Grid content, MigrationVisualControls c, bool resume,
        string scenario, Action<bool, string> check)
    {
        const double tolerance = 1;
        var viewport = new Rect(new Point(0, 0), content.RenderSize);
        FrameworkElement[] sections = content.Children.OfType<FrameworkElement>()
            .Where(element => element.Visibility == Visibility.Visible).ToArray();
        bool contained = viewport.Width > 0 && viewport.Height > 0;
        foreach (var element in sections)
            contained &= MigrationContains(viewport, MigrationBounds(element, content), tolerance);
        Control[] controls = [c.Destination, c.Operation, c.Status, c.Convert, c.Close];
        foreach (var control in controls)
            contained &= MigrationContains(viewport, MigrationBounds(control, content), tolerance);
        if (resume)
        {
            contained &= MigrationContains(viewport, MigrationBounds(c.RecoveryPath, content), tolerance);
            foreach (var button in c.RecoveryPanel.Children.OfType<Button>())
                contained &= MigrationContains(viewport, MigrationBounds(button, content), tolerance);
        }
        check(contained, scenario + ": header, input, status and action bounds stay within the content viewport");
        bool separated = true;
        for (int i = 0; i < sections.Length; i++)
        for (int j = i + 1; j < sections.Length; j++)
            separated &= !MigrationOverlaps(MigrationBounds(sections[i], content), MigrationBounds(sections[j], content));
        var actions = controls.Where(control => control != c.Status).Cast<FrameworkElement>().ToList();
        if (resume)
        {
            actions.Add(c.RecoveryPath);
            actions.AddRange(c.RecoveryPanel.Children.OfType<Button>());
        }
        for (int i = 0; i < actions.Count; i++)
        for (int j = i + 1; j < actions.Count; j++)
            separated &= !MigrationOverlaps(MigrationBounds(actions[i], content), MigrationBounds(actions[j], content));
        check(separated, scenario + ": sibling sections and input/action controls do not overlap");

        bool headersFit = true;
        foreach (var text in sections.OfType<TextBlock>().Where(text => text != c.Source))
        {
            var probe = new TextBlock
            {
                Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
                FontWeight = text.FontWeight, FontStyle = text.FontStyle, FontStretch = text.FontStretch,
                FlowDirection = text.FlowDirection, TextWrapping = text.TextWrapping, Padding = text.Padding,
                LineHeight = text.LineHeight, LineStackingStrategy = text.LineStackingStrategy
            };
            probe.Measure(new Size(text.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : text.ActualWidth,
                double.PositiveInfinity));
            headersFit &= probe.DesiredSize.Width <= text.ActualWidth + tolerance &&
                probe.DesiredSize.Height <= text.ActualHeight + tolerance;
        }
        check(headersFit, scenario + ": header labels and wrapped preservation text fit without clipping");
        bool captionsFit = MigrationControlTextFits(c.Destination, c.Destination.Text) &&
            MigrationControlTextFits(c.Convert, Convert.ToString(c.Convert.Content) ?? "") &&
            MigrationControlTextFits(c.Close, Convert.ToString(c.Close.Content) ?? "") &&
            c.Operation.SelectedItem is ComboBoxItem selected &&
            MigrationControlTextFits(c.Operation, Convert.ToString(selected.Content) ?? "");
        if (resume)
        {
            captionsFit &= MigrationControlTextFits(c.RecoveryPath, c.RecoveryPath.Text);
            foreach (var button in c.RecoveryPanel.Children.OfType<Button>())
                captionsFit &= MigrationControlTextFits(button, Convert.ToString(button.Content) ?? "");
        }
        check(captionsFit, scenario + ": input values, selected operation and button captions fit their chrome");
        check(c.RecoveryPanel.Visibility == (resume ? Visibility.Visible : Visibility.Collapsed) &&
            c.RecoveryLabel.Visibility == c.RecoveryPanel.Visibility && c.Operation.SelectedIndex == (resume ? 2 : 0),
            scenario + ": normal/resume selection controls recovery visibility");
        check(c.Source.Text.Length > 100 && Equals(c.Source.ToolTip, c.Source.Text) &&
            (c.Source.TextWrapping == TextWrapping.NoWrap || double.IsFinite(c.Source.MaxHeight)) &&
            c.Source.TextTrimming == TextTrimming.CharacterEllipsis &&
            c.Source.ActualHeight <= c.Source.MaxHeight + tolerance,
            scenario + ": long source retains full tooltip with intentional bounded ellipsis");
        if (c.Status.Template.FindName("PART_ContentHost", c.Status) is not ScrollViewer statusViewport)
            throw new InvalidOperationException("Migration status lost its scrolling content host.");
        check(c.Status.IsReadOnly && c.Status.TextWrapping == TextWrapping.Wrap &&
            c.Status.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && statusViewport.ViewportHeight > 0 &&
            statusViewport.ExtentHeight > statusViewport.ViewportHeight + tolerance,
            scenario + ": long status stays in a positive, wrapped scrolling viewport rather than expanding the footer");
    }

    private static void CheckMigrationSmallClientBounds(Grid surface, Grid content, MigrationVisualControls c,
        bool resume, int width, int height, string scenario, Action<bool, string> check)
    {
        var client = new Rect(0, 0, width, height);
        var allocation = new Rect(content.Margin.Left, content.Margin.Top,
            Math.Max(0, width - content.Margin.Left - content.Margin.Right),
            Math.Max(0, height - content.Margin.Top - content.Margin.Bottom));
        var sections = content.Children.OfType<FrameworkElement>().Where(x => x.Visibility == Visibility.Visible).ToArray();
        var elements = sections.Concat(new FrameworkElement[] { c.Source, c.Destination, c.Operation, c.Status, c.Convert, c.Close }).ToList();
        if (resume)
        {
            elements.Add(c.RecoveryPath);
            elements.AddRange(c.RecoveryPanel.Children.OfType<Button>());
        }
        var overflow = new List<string>();
        Rect rootBounds = MigrationBounds(content, surface);
        if (!MigrationContains(client, rootBounds, 1) || !MigrationContains(allocation, rootBounds, 1))
            overflow.Add("ContentGrid=" + MigrationRectText(rootBounds));
        foreach (var element in elements.Distinct())
        {
            Rect bounds = MigrationBounds(element, surface);
            if (!MigrationContains(allocation, bounds, 1))
            {
                string name = string.IsNullOrEmpty(element.Name) ? element.GetType().Name + "(row=" + Grid.GetRow(element) + ")" : element.Name;
                overflow.Add(name + "=" + MigrationRectText(bounds));
            }
        }
        // Grid.RenderSize/DesiredSize can expand when Auto rows and star-row minima exceed their slot.
        // Compare against the supplied client allocation instead, so that expansion cannot hide clipping.
        check(overflow.Count == 0, scenario + ": CheckSmallClient keeps header/input/status/buttons inside allocated " +
            MigrationRectText(allocation) + (overflow.Count == 0 ? "" : "; overflow: " + string.Join("; ", overflow) +
                ". Review MinHeight=400/default Height=440, or a single-line ellipsized SourceText with its full tooltip; native client size remains unmeasured."));
        bool separated = true;
        for (int i = 0; i < sections.Length; i++)
        for (int j = i + 1; j < sections.Length; j++)
            separated &= !MigrationOverlaps(MigrationBounds(sections[i], surface), MigrationBounds(sections[j], surface));
        check(separated, scenario + ": CheckSmallClient header/status/footer sections do not overlap");
    }

    private static string MigrationRectText(Rect bounds) => FormattableString.Invariant(
        $"[{bounds.Left:F1},{bounds.Top:F1},{bounds.Width:F1},{bounds.Height:F1}]");

    private static Rect MigrationBounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

    private static bool MigrationContains(Rect parent, Rect child, double tolerance) =>
        child.Width > 0 && child.Height > 0 && double.IsFinite(child.Left) && double.IsFinite(child.Top) &&
        child.Left >= parent.Left - tolerance && child.Top >= parent.Top - tolerance &&
        child.Right <= parent.Right + tolerance && child.Bottom <= parent.Bottom + tolerance;

    private static bool MigrationOverlaps(Rect left, Rect right)
    {
        Rect overlap = Rect.Intersect(left, right);
        return !overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5;
    }

    private static bool MigrationControlTextFits(Control control, string text)
    {
        var probe = new TextBlock
        {
            Text = text, FontFamily = control.FontFamily, FontSize = control.FontSize,
            FontWeight = control.FontWeight, FontStyle = control.FontStyle, FontStretch = control.FontStretch,
            FlowDirection = control.FlowDirection
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width <= control.ActualWidth - control.Padding.Left - control.Padding.Right -
            control.BorderThickness.Left - control.BorderThickness.Right + 1 &&
            probe.DesiredSize.Height <= control.ActualHeight - control.Padding.Top - control.Padding.Bottom -
            control.BorderThickness.Top - control.BorderThickness.Bottom + 1;
    }

    private sealed record MigrationRenderPixels(long OpaquePixels, long NonBackgroundPixels, string Hash);

    private static MigrationRenderPixels SaveMigrationRender(Grid surface, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels(pixels, stride, 0);
        Color background = (surface.Background as SolidColorBrush)?.Color
            ?? throw new InvalidOperationException("Offscreen migration background must be an opaque theme color.");
        long opaque = 0, different = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 255) opaque++;
            if (pixels[i + 3] != 0 && (Math.Abs(pixels[i] - background.B) > 2 ||
                Math.Abs(pixels[i + 1] - background.G) > 2 || Math.Abs(pixels[i + 2] - background.R) > 2)) different++;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        // Only these fixed sidecar filenames in the caller's explicit directory are written; nothing is enumerated or deleted.
        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) encoder.Save(output);
        return new(opaque, different, Convert.ToHexString(SHA256.HashData(pixels)));
    }
}
