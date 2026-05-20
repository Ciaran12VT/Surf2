using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace Surf2;

public partial class ColorPickerWindow : Window
{
    private const int SpectrumPixelWidth = 300;
    private const int SpectrumPixelHeight = 220;
    private const int HuePixelWidth = 24;
    private const int HuePixelHeight = 220;

    private bool _isUpdatingText;
    private bool _isPickingSpectrum;
    private bool _isPickingHue;
    private double _hue;
    private double _saturation;
    private double _value;

    public ColorPickerWindow(string initialColorText)
    {
        InitializeComponent();

        if (!TryParseColor(initialColorText, out MediaColor initialColor))
        {
            initialColor = Colors.White;
        }

        SetHsvFromColor(initialColor);
        SelectedColor = ToHex(initialColor);
    }

    public string SelectedColor { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RenderHueImage();
        RenderSpectrumImage();
        UpdateControlsFromHsv(updateText: true);
    }

    private void SpectrumImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPickingSpectrum = true;
        SpectrumImage.CaptureMouse();
        UpdateSpectrumSelection(e.GetPosition(SpectrumImage));
        e.Handled = true;
    }

    private void SpectrumImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPickingSpectrum)
        {
            return;
        }

        UpdateSpectrumSelection(e.GetPosition(SpectrumImage));
    }

    private void HueImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPickingHue = true;
        HueImage.CaptureMouse();
        UpdateHueSelection(e.GetPosition(HueImage));
        e.Handled = true;
    }

    private void HueImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPickingHue)
        {
            return;
        }

        UpdateHueSelection(e.GetPosition(HueImage));
    }

    private void Picker_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleasePickerCapture();
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleasePickerCapture();
    }

    private void HexColorTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isUpdatingText)
        {
            return;
        }

        if (!TryParseColor(HexColorTextBox.Text, out MediaColor color))
        {
            ValidationText.Text = string.IsNullOrWhiteSpace(HexColorTextBox.Text)
                ? string.Empty
                : "Invalid color.";
            return;
        }

        ValidationText.Text = string.Empty;
        SetHsvFromColor(color);
        RenderSpectrumImage();
        UpdateControlsFromHsv(updateText: false);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseColor(HexColorTextBox.Text, out MediaColor color))
        {
            ValidationText.Text = "Invalid color.";
            return;
        }

        SelectedColor = ToHex(color);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ReleasePickerCapture()
    {
        if (_isPickingSpectrum)
        {
            SpectrumImage.ReleaseMouseCapture();
        }

        if (_isPickingHue)
        {
            HueImage.ReleaseMouseCapture();
        }

        _isPickingSpectrum = false;
        _isPickingHue = false;
    }

    private void UpdateSpectrumSelection(Point position)
    {
        double x = Math.Clamp(position.X, 0, SpectrumPixelWidth - 1);
        double y = Math.Clamp(position.Y, 0, SpectrumPixelHeight - 1);

        _saturation = x / (SpectrumPixelWidth - 1);
        _value = 1 - (y / (SpectrumPixelHeight - 1));
        UpdateControlsFromHsv(updateText: true);
    }

    private void UpdateHueSelection(Point position)
    {
        double y = Math.Clamp(position.Y, 0, HuePixelHeight - 1);
        _hue = (y / (HuePixelHeight - 1)) * 360;
        RenderSpectrumImage();
        UpdateControlsFromHsv(updateText: true);
    }

    private void UpdateControlsFromHsv(bool updateText)
    {
        MediaColor color = HsvToRgb(_hue, _saturation, _value);
        SelectedColor = ToHex(color);

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        PreviewSwatch.Background = brush;

        if (updateText)
        {
            _isUpdatingText = true;
            HexColorTextBox.Text = SelectedColor;
            HexColorTextBox.CaretIndex = HexColorTextBox.Text.Length;
            _isUpdatingText = false;
        }

        UpdatePins();
    }

    private void UpdatePins()
    {
        double spectrumX = (_saturation * (SpectrumPixelWidth - 1)) - (SpectrumPin.Width / 2);
        double spectrumY = ((1 - _value) * (SpectrumPixelHeight - 1)) - (SpectrumPin.Height / 2);
        Canvas.SetLeft(SpectrumPin, Math.Clamp(spectrumX, -SpectrumPin.Width / 2, SpectrumPixelWidth - SpectrumPin.Width / 2));
        Canvas.SetTop(SpectrumPin, Math.Clamp(spectrumY, -SpectrumPin.Height / 2, SpectrumPixelHeight - SpectrumPin.Height / 2));

        double hueY = ((_hue / 360) * (HuePixelHeight - 1)) - (HuePin.Height / 2);
        Canvas.SetLeft(HuePin, -3);
        Canvas.SetTop(HuePin, Math.Clamp(hueY, -HuePin.Height / 2, HuePixelHeight - HuePin.Height / 2));
    }

    private void RenderSpectrumImage()
    {
        byte[] pixels = new byte[SpectrumPixelWidth * SpectrumPixelHeight * 4];
        int index = 0;

        for (int y = 0; y < SpectrumPixelHeight; y++)
        {
            double value = 1 - (y / (double)(SpectrumPixelHeight - 1));
            for (int x = 0; x < SpectrumPixelWidth; x++)
            {
                double saturation = x / (double)(SpectrumPixelWidth - 1);
                MediaColor color = HsvToRgb(_hue, saturation, value);
                pixels[index++] = color.B;
                pixels[index++] = color.G;
                pixels[index++] = color.R;
                pixels[index++] = 255;
            }
        }

        var bitmap = new WriteableBitmap(
            SpectrumPixelWidth,
            SpectrumPixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        bitmap.WritePixels(
            new Int32Rect(0, 0, SpectrumPixelWidth, SpectrumPixelHeight),
            pixels,
            SpectrumPixelWidth * 4,
            0);
        bitmap.Freeze();
        SpectrumImage.Source = bitmap;
    }

    private void RenderHueImage()
    {
        byte[] pixels = new byte[HuePixelWidth * HuePixelHeight * 4];
        int index = 0;

        for (int y = 0; y < HuePixelHeight; y++)
        {
            double hue = (y / (double)(HuePixelHeight - 1)) * 360;
            MediaColor color = HsvToRgb(hue, 1, 1);
            for (int x = 0; x < HuePixelWidth; x++)
            {
                pixels[index++] = color.B;
                pixels[index++] = color.G;
                pixels[index++] = color.R;
                pixels[index++] = 255;
            }
        }

        var bitmap = new WriteableBitmap(
            HuePixelWidth,
            HuePixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        bitmap.WritePixels(
            new Int32Rect(0, 0, HuePixelWidth, HuePixelHeight),
            pixels,
            HuePixelWidth * 4,
            0);
        bitmap.Freeze();
        HueImage.Source = bitmap;
    }

    private void SetHsvFromColor(MediaColor color)
    {
        double red = color.R / 255.0;
        double green = color.G / 255.0;
        double blue = color.B / 255.0;
        double max = Math.Max(red, Math.Max(green, blue));
        double min = Math.Min(red, Math.Min(green, blue));
        double delta = max - min;

        if (delta == 0)
        {
            _hue = 0;
        }
        else if (max == red)
        {
            _hue = 60 * (((green - blue) / delta) % 6);
        }
        else if (max == green)
        {
            _hue = 60 * (((blue - red) / delta) + 2);
        }
        else
        {
            _hue = 60 * (((red - green) / delta) + 4);
        }

        if (_hue < 0)
        {
            _hue += 360;
        }

        _saturation = max == 0 ? 0 : delta / max;
        _value = max;
    }

    private static MediaColor HsvToRgb(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        double m = value - chroma;

        double red1;
        double green1;
        double blue1;

        if (hue < 60)
        {
            red1 = chroma;
            green1 = x;
            blue1 = 0;
        }
        else if (hue < 120)
        {
            red1 = x;
            green1 = chroma;
            blue1 = 0;
        }
        else if (hue < 180)
        {
            red1 = 0;
            green1 = chroma;
            blue1 = x;
        }
        else if (hue < 240)
        {
            red1 = 0;
            green1 = x;
            blue1 = chroma;
        }
        else if (hue < 300)
        {
            red1 = x;
            green1 = 0;
            blue1 = chroma;
        }
        else
        {
            red1 = chroma;
            green1 = 0;
            blue1 = x;
        }

        return MediaColor.FromRgb(
            ToByte((red1 + m) * 255),
            ToByte((green1 + m) * 255),
            ToByte((blue1 + m) * 255));
    }

    private static byte ToByte(double value)
    {
        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }

    private static bool TryParseColor(string? colorText, out MediaColor color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(colorText))
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorText.Trim()) is not MediaColor parsedColor)
            {
                return false;
            }

            color = MediaColor.FromRgb(parsedColor.R, parsedColor.G, parsedColor.B);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToHex(MediaColor color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
