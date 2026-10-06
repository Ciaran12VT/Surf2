using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Surf2.Models;
using Surf2.Services;

namespace Surf2;

/// <summary>Dispatcher-only offscreen checks. Does not show windows, open SQL, or read connection settings.</summary>
public static class RelationalStatePickerVisualChecks
{
    // Pass a prepared SettingsWindow fixture to include its selected lazy preferences page.
    public static async Task<IReadOnlyList<string>> RunOffscreenAsync(string outputDirectory, SettingsWindow? preparedSettings = null,
        CancellationToken ct = default)
    {
        var app = Application.Current ?? throw new InvalidOperationException("Load App resources before rendering picker fixtures.");
        app.Dispatcher.VerifyAccess();
        if (!Path.IsPathFullyQualified(outputDirectory)) throw new ArgumentException("An absolute owned fixture output directory is required.", nameof(outputDirectory));
        string directory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(directory);
        string previous = AppThemeService.CurrentTheme;
        var passed = new List<string>();
        try
        {
            passed.AddRange(await RelationalScopePickerUiChecks.RunOffscreenAsync(directory, ct));
            foreach (string theme in new[] { AppearanceSettings.LightTheme, AppearanceSettings.DarkTheme })
            {
                ct.ThrowIfCancellationRequested(); AppThemeService.Apply(theme);
                if (preparedSettings != null) Render(preparedSettings, "settings", theme, required: ["Next"]);
            }
        }
        finally
        {
            AppThemeService.Apply(previous);
        }
        return passed;

        void Render(Window window, string name, string theme, string[] required)
        {
            ct.ThrowIfCancellationRequested();
            var root = window.Content as FrameworkElement ?? throw new InvalidOperationException("The fixture has no visual root.");
            root.Measure(new(window.Width, window.Height)); root.Arrange(new Rect(0, 0, window.Width, window.Height)); root.UpdateLayout();
            var buttons = Descendants(root).OfType<Button>().ToArray();
            foreach (string label in required)
            {
                var button = buttons.FirstOrDefault(b => b.Content as string == label)
                    ?? throw new InvalidOperationException(name + " fixture is missing button: " + label);
                if (button.Style == null || button.Background is not SolidColorBrush background || button.Foreground is not SolidColorBrush foreground ||
                    Contrast(background.Color, foreground.Color) < 4.5)
                    throw new InvalidOperationException(name + " button has no conforming style/contrast: " + label);
                if (button.Visibility == Visibility.Visible && button.ActualWidth > 0)
                {
                    var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, button.Foreground, 1);
                    if (text.Width > button.ActualWidth - button.Padding.Left - button.Padding.Right + 1)
                        throw new InvalidOperationException(name + " button text is clipped: " + label);
                }
            }
            int width = checked((int)Math.Ceiling(window.Width)), height = checked((int)Math.Ceiling(window.Height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var backgroundVisual = new DrawingVisual();
            using (var drawing = backgroundVisual.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            bitmap.Render(backgroundVisual); bitmap.Render(root);
            byte[] pixels = new byte[checked(width * height * 4)]; bitmap.CopyPixels(pixels, width * 4, 0);
            if (!pixels.Where((_, i) => i % 4 != 3).Any(b => b != pixels[0])) throw new InvalidOperationException("The picker render is blank.");
            string path = Path.Combine(directory, name + "-" + theme.ToLowerInvariant() + ".png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) encoder.Save(stream);
            passed.Add(name + " " + theme + " styles, contrast, labels and offscreen render: " + path);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Light(Color value) => .2126 * Channel(value.R) + .7152 * Channel(value.G) + .0722 * Channel(value.B);
        double a = Light(first), b = Light(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
