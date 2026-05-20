using System.Windows;
using System.Windows.Media;
using Surf2.Controls;

namespace Surf2;

public partial class DiagramLineDetailsWindow : Window
{
    private string _lineColorText;

    public DiagramLineDetailsWindow(string lineColorText, bool hasEndArrow)
    {
        InitializeComponent();

        _lineColorText = NormalizeColorText(lineColorText, "#000000");
        HasEndArrowCheckBox.IsChecked = hasEndArrow;
        UpdateColorSwatch();
    }

    public string LineColorText { get; private set; } = "#000000";

    public bool HasEndArrow { get; private set; }

    private void LineColorButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPickerWindow(GetColorPickerInitialColor(_lineColorText, "#000000"))
        {
            Owner = this
        };

        if (picker.ShowDialog() == true)
        {
            _lineColorText = NormalizeColorText(picker.SelectedColor, "#000000");
            UpdateColorSwatch();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        LineColorText = _lineColorText;
        HasEndArrow = HasEndArrowCheckBox.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void UpdateColorSwatch()
    {
        try
        {
            if (ColorConverter.ConvertFromString(GetColorPickerInitialColor(_lineColorText, "#000000")) is not Color color)
            {
                return;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            LineColorSwatch.Fill = brush;
        }
        catch (FormatException)
        {
        }
    }

    private static string GetColorPickerInitialColor(string colorText, string fallbackColor)
    {
        return string.Equals(colorText, DiagramShapeControl.TransparentColorText, StringComparison.OrdinalIgnoreCase)
            ? fallbackColor
            : colorText;
    }

    private static string NormalizeColorText(string colorText, string fallbackColor)
    {
        if (string.Equals(colorText, DiagramShapeControl.TransparentColorText, StringComparison.OrdinalIgnoreCase))
        {
            return DiagramShapeControl.TransparentColorText;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorText) is not Color color)
            {
                return fallbackColor;
            }

            return color.A == 0
                ? DiagramShapeControl.TransparentColorText
                : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch (FormatException)
        {
            return fallbackColor;
        }
    }
}
