using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Surf2.Models;

namespace Surf2.Services;

public static class AppThemeService
{
    public const string WindowBackgroundBrushKey = "Theme.WindowBackgroundBrush";
    public const string SurfaceBrushKey = "Theme.SurfaceBrush";
    public const string SurfaceAltBrushKey = "Theme.SurfaceAltBrush";
    public const string PanelBrushKey = "Theme.PanelBrush";
    public const string BorderBrushKey = "Theme.BorderBrush";
    public const string StrongBorderBrushKey = "Theme.StrongBorderBrush";
    public const string TextBrushKey = "Theme.TextBrush";
    public const string SubtleTextBrushKey = "Theme.SubtleTextBrush";
    public const string MutedTextBrushKey = "Theme.MutedTextBrush";
    public const string InputBackgroundBrushKey = "Theme.InputBackgroundBrush";
    public const string InputTextBrushKey = "Theme.InputTextBrush";
    public const string SelectionBrushKey = "Theme.SelectionBrush";
    public const string SelectionTextBrushKey = "Theme.SelectionTextBrush";
    public const string AccentBrushKey = "Theme.AccentBrush";
    public const string ErrorTextBrushKey = "Theme.ErrorTextBrush";
    public const string OverlayBrushKey = "Theme.OverlayBrush";
    public const string ActiveCodeCanvasBrushKey = "Theme.ActiveCodeCanvasBrush";
    public const string ActiveDiagramCanvasBrushKey = "Theme.ActiveDiagramCanvasBrush";
    public const string InactiveCanvasBrushKey = "Theme.InactiveCanvasBrush";
    public const string IconStrokeBrushKey = "Theme.IconStrokeBrush";
    public const string ButtonChromeBrushKey = "Theme.ButtonChromeBrush";
    public const string ButtonChromeHoverBrushKey = "Theme.ButtonChromeHoverBrush";
    public const string ButtonChromePressedBrushKey = "Theme.ButtonChromePressedBrush";
    public const string ToolButtonHoverBrushKey = "Theme.ToolButtonHoverBrush";
    public const string ToolButtonCheckedBrushKey = "Theme.ToolButtonCheckedBrush";
    public const string ToolButtonHoverBorderBrushKey = "Theme.ToolButtonHoverBorderBrush";
    public const string ToolButtonCheckedBorderBrushKey = "Theme.ToolButtonCheckedBorderBrush";
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private static readonly Dictionary<string, string> LightPalette = new(StringComparer.Ordinal)
    {
        [WindowBackgroundBrushKey] = "#F6F7F9",
        [SurfaceBrushKey] = "#FFFFFF",
        [SurfaceAltBrushKey] = "#F8FAFC",
        [PanelBrushKey] = "#EEF1F5",
        [BorderBrushKey] = "#D7DCE2",
        [StrongBorderBrushKey] = "#CBD5E1",
        [TextBrushKey] = "#111827",
        [SubtleTextBrushKey] = "#4B5563",
        [MutedTextBrushKey] = "#64748B",
        [InputBackgroundBrushKey] = "#FFFFFF",
        [InputTextBrushKey] = "#111827",
        [SelectionBrushKey] = "#DBEAFE",
        [SelectionTextBrushKey] = "#111827",
        [AccentBrushKey] = "#2563EB",
        [ErrorTextBrushKey] = "#B91C1C",
        [OverlayBrushKey] = "#BFFFFFFF",
        [ActiveCodeCanvasBrushKey] = "#EEF1F5",
        [ActiveDiagramCanvasBrushKey] = "#F4F6F8",
        [InactiveCanvasBrushKey] = "#D1D5DB",
        [IconStrokeBrushKey] = "#111827",
        [ButtonChromeBrushKey] = "#F3F4F6",
        [ButtonChromeHoverBrushKey] = "#E5E7EB",
        [ButtonChromePressedBrushKey] = "#D1D5DB",
        [ToolButtonHoverBrushKey] = "#E0ECFF",
        [ToolButtonCheckedBrushKey] = "#BFDBFE",
        [ToolButtonHoverBorderBrushKey] = "#93C5FD",
        [ToolButtonCheckedBorderBrushKey] = "#2563EB"
    };

    private static readonly Dictionary<string, string> DarkPalette = new(StringComparer.Ordinal)
    {
        [WindowBackgroundBrushKey] = "#111827",
        [SurfaceBrushKey] = "#1F2937",
        [SurfaceAltBrushKey] = "#273244",
        [PanelBrushKey] = "#172033",
        [BorderBrushKey] = "#374151",
        [StrongBorderBrushKey] = "#4B5563",
        [TextBrushKey] = "#F9FAFB",
        [SubtleTextBrushKey] = "#D1D5DB",
        [MutedTextBrushKey] = "#9CA3AF",
        [InputBackgroundBrushKey] = "#111827",
        [InputTextBrushKey] = "#F9FAFB",
        [SelectionBrushKey] = "#1D4ED8",
        [SelectionTextBrushKey] = "#FFFFFF",
        [AccentBrushKey] = "#3B82F6",
        [ErrorTextBrushKey] = "#FCA5A5",
        [OverlayBrushKey] = "#C0111827",
        [ActiveCodeCanvasBrushKey] = "#172033",
        [ActiveDiagramCanvasBrushKey] = "#1B2537",
        [InactiveCanvasBrushKey] = "#0F172A",
        [IconStrokeBrushKey] = "#F9FAFB",
        [ButtonChromeBrushKey] = "#273244",
        [ButtonChromeHoverBrushKey] = "#374151",
        [ButtonChromePressedBrushKey] = "#4B5563",
        [ToolButtonHoverBrushKey] = "#1E3A5F",
        [ToolButtonCheckedBrushKey] = "#1D4ED8",
        [ToolButtonHoverBorderBrushKey] = "#3B82F6",
        [ToolButtonCheckedBorderBrushKey] = "#60A5FA"
    };

    public static string CurrentTheme { get; private set; } = AppearanceSettings.LightTheme;

    public static void Apply(string? theme)
    {
        CurrentTheme = AppearanceSettings.NormalizeTheme(theme);
        Dictionary<string, string> palette = CurrentTheme == AppearanceSettings.DarkTheme
            ? DarkPalette
            : LightPalette;

        ResourceDictionary resources = Application.Current.Resources;
        foreach ((string key, string color) in palette)
        {
            resources[key] = CreateBrush(color);
        }

        ApplyWindowChromeToOpenWindows();
    }

    public static Brush GetBrush(string key)
    {
        return Application.Current.Resources[key] as Brush ?? Brushes.Transparent;
    }

    private static SolidColorBrush CreateBrush(string colorText)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorText));
        brush.Freeze();
        return brush;
    }

    public static void ApplyWindowChromeToOpenWindows()
    {
        if (Application.Current == null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            ApplyWindowChrome(window);
        }
    }

    public static void ApplyWindowChrome(Window? window)
    {
        if (window == null ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        try
        {
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle == 0)
            {
                return;
            }

            int useDarkMode = CurrentTheme == AppearanceSettings.DarkTheme ? 1 : 0;
            int result = DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkMode,
                ref useDarkMode,
                Marshal.SizeOf<int>());

            if (result != 0)
            {
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref useDarkMode,
                    Marshal.SizeOf<int>());
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                bool useDarkTheme = CurrentTheme == AppearanceSettings.DarkTheme;
                TrySetWindowColorAttribute(
                    handle,
                    DwmwaCaptionColor,
                    useDarkTheme ? "#111827" : "#F6F7F9");
                TrySetWindowColorAttribute(
                    handle,
                    DwmwaBorderColor,
                    useDarkTheme ? "#374151" : "#D7DCE2");
                TrySetWindowColorAttribute(
                    handle,
                    DwmwaTextColor,
                    useDarkTheme ? "#F9FAFB" : "#111827");
            }
        }
        catch
        {
            // Title bar theming is cosmetic and depends on OS support.
        }
    }

    private static void TrySetWindowColorAttribute(nint handle, int attribute, string colorText)
    {
        int colorRef = ToColorRef(colorText);
        _ = DwmSetWindowAttribute(handle, attribute, ref colorRef, Marshal.SizeOf<int>());
    }

    private static int ToColorRef(string colorText)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorText);
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
