using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Surf2.Controls;

namespace Surf2;

public partial class DiagramShapeDetailsWindow : Window
{
    private string _outlineColorText;
    private string _backColorText;

    public DiagramShapeDetailsWindow(string labelText, string outlineColorText, string backColorText)
    {
        InitializeComponent();

        LabelTextBox.Text = labelText;
        _outlineColorText = NormalizeColorText(outlineColorText, "#000000");
        _backColorText = NormalizeColorText(backColorText, "#FFFFFF");

        UpdateColorSwatches();
    }

    public string LabelText { get; private set; } = string.Empty;

    public string OutlineColorText { get; private set; } = "#000000";

    public string BackColorText { get; private set; } = "#FFFFFF";

    private void OutlineColorButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPickerWindow(GetColorPickerInitialColor(_outlineColorText, "#000000"))
        {
            Owner = this
        };

        if (picker.ShowDialog() == true)
        {
            _outlineColorText = NormalizeColorText(picker.SelectedColor, "#000000");
            UpdateColorSwatches();
        }
    }

    private void BackColorButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPickerWindow(GetColorPickerInitialColor(_backColorText, "#FFFFFF"))
        {
            Owner = this
        };

        if (picker.ShowDialog() == true)
        {
            _backColorText = NormalizeColorText(picker.SelectedColor, "#FFFFFF");
            UpdateColorSwatches();
        }
    }

    private void OutlineTransparentButton_Click(object sender, RoutedEventArgs e)
    {
        _outlineColorText = DiagramShapeControl.TransparentColorText;
        UpdateColorSwatches();
    }

    private void BackTransparentButton_Click(object sender, RoutedEventArgs e)
    {
        _backColorText = DiagramShapeControl.TransparentColorText;
        UpdateColorSwatches();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        LabelText = LabelTextBox.Text;
        OutlineColorText = _outlineColorText;
        BackColorText = _backColorText;
        DialogResult = true;
        Close();
    }

    private void UpdateColorSwatches()
    {
        UpdateColorSwatch(_outlineColorText, OutlineColorSwatch, OutlineColorTransparentSlash);
        UpdateColorSwatch(_backColorText, BackColorSwatch, BackColorTransparentSlash);
    }

    private static void UpdateColorSwatch(string colorText, Rectangle swatch, Line transparentSlash)
    {
        if (IsTransparentColor(colorText))
        {
            swatch.Fill = Brushes.Transparent;
            transparentSlash.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorText) is not Color color)
            {
                return;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            swatch.Fill = brush;
            transparentSlash.Visibility = Visibility.Collapsed;
        }
        catch (FormatException)
        {
        }
    }

    private static string GetColorPickerInitialColor(string colorText, string fallbackColor)
    {
        return IsTransparentColor(colorText) ? fallbackColor : colorText;
    }

    private static string NormalizeColorText(string colorText, string fallbackColor)
    {
        if (IsTransparentColor(colorText))
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

    private static bool IsTransparentColor(string colorText)
    {
        return string.Equals(
            colorText,
            DiagramShapeControl.TransparentColorText,
            StringComparison.OrdinalIgnoreCase);
    }
}
