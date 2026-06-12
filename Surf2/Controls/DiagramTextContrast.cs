using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Surf2.Models;
using Surf2.Services;

namespace Surf2.Controls;

internal static class DiagramTextContrast
{
    private const string TransparentColorText = "Transparent";

    public static Brush GetReadableTextBrush(string? backColorText, DependencyObject? context)
    {
        Color background = ResolveBackgroundColor(backColorText, context);
        double luminance = GetRelativeLuminance(background);
        double blackContrast = (luminance + 0.05) / 0.05;
        double whiteContrast = 1.05 / (luminance + 0.05);

        return whiteContrast >= blackContrast ? Brushes.White : Brushes.Black;
    }

    public static Brush GetCanvasReadableTextBrush(DependencyObject? context)
    {
        return GetReadableTextBrush(TransparentColorText, context);
    }

    private static Color ResolveBackgroundColor(string? backColorText, DependencyObject? context)
    {
        Color canvasColor = GetNearestOpaqueBackgroundColor(context) ?? GetDefaultDiagramCanvasColor();

        if (IsTransparent(backColorText) || !TryParseColor(backColorText, out Color color))
        {
            return canvasColor;
        }

        if (color.A == 0)
        {
            return canvasColor;
        }

        if (color.A < 255)
        {
            return Blend(color, canvasColor);
        }

        return color;
    }

    private static Color? GetNearestOpaqueBackgroundColor(DependencyObject? context)
    {
        DependencyObject? current = context;
        while (current != null)
        {
            Brush? background = current switch
            {
                Panel panel => panel.Background,
                Control control => control.Background,
                Border border => border.Background,
                _ => null
            };

            if (TryGetOpaqueBrushColor(background, out Color color))
            {
                return color;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        try
        {
            DependencyObject? visualParent = VisualTreeHelper.GetParent(current);
            if (visualParent != null)
            {
                return visualParent;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return current is FrameworkElement element
            ? element.Parent
            : null;
    }

    private static Color GetDefaultDiagramCanvasColor()
    {
        if (TryGetOpaqueBrushColor(AppThemeService.GetBrush(AppThemeService.ActiveDiagramCanvasBrushKey), out Color color))
        {
            return color;
        }

        return AppThemeService.CurrentTheme == AppearanceSettings.DarkTheme
            ? Color.FromRgb(0x1B, 0x25, 0x37)
            : Color.FromRgb(0xF4, 0xF6, 0xF8);
    }

    private static bool TryParseColor(string? colorText, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(colorText))
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorText) is Color parsedColor)
            {
                color = parsedColor;
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }

    private static bool TryGetOpaqueBrushColor(Brush? brush, out Color color)
    {
        color = default;
        if (brush is SolidColorBrush { Color.A: > 0 } solidColorBrush)
        {
            color = solidColorBrush.Color;
            return true;
        }

        return false;
    }

    private static bool IsTransparent(string? colorText)
    {
        return string.Equals(colorText, TransparentColorText, StringComparison.OrdinalIgnoreCase);
    }

    private static Color Blend(Color foreground, Color background)
    {
        double alpha = foreground.A / 255d;
        byte red = BlendChannel(foreground.R, background.R, alpha);
        byte green = BlendChannel(foreground.G, background.G, alpha);
        byte blue = BlendChannel(foreground.B, background.B, alpha);
        return Color.FromRgb(red, green, blue);
    }

    private static byte BlendChannel(byte foreground, byte background, double alpha)
    {
        return (byte)Math.Round((foreground * alpha) + (background * (1 - alpha)));
    }

    private static double GetRelativeLuminance(Color color)
    {
        double red = ToLinear(color.R);
        double green = ToLinear(color.G);
        double blue = ToLinear(color.B);

        return (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
    }

    private static double ToLinear(byte value)
    {
        double channel = value / 255d;
        return channel <= 0.03928
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}
