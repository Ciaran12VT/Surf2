namespace Surf2.Controls;

internal static class DiagramTextStyle
{
    public const double DefaultFontSize = 12;
    public const double MinimumFontSize = 8;
    public const double MaximumFontSize = 96;

    public static IReadOnlyList<double> FontSizes { get; } =
        [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72, 96];

    public static double NormalizeFontSize(double fontSize)
    {
        return double.IsFinite(fontSize) && fontSize > 0
            ? Math.Clamp(fontSize, MinimumFontSize, MaximumFontSize)
            : DefaultFontSize;
    }
}
