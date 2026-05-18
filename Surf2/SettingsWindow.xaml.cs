using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Surf2.Models;
using MediaColor = System.Windows.Media.Color;

namespace Surf2;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<ExtensionBackcolorSetting> _backcolors;
    private bool _loadingSelection;
    private ExtensionBackcolorSetting? _activeSetting;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        Settings = settings.Clone();
        Settings.CodeWindows.EnsureDefaultBackcolorEntries();
        _backcolors = new ObservableCollection<ExtensionBackcolorSetting>(
            Settings.CodeWindows.BackcolorsByExtension.Select(setting => new ExtensionBackcolorSetting
            {
                Extension = setting.Extension,
                Backcolor = setting.Backcolor
            }));

        BackcolorList.ItemsSource = _backcolors;

        if (_backcolors.Count > 0)
        {
            BackcolorList.SelectedIndex = 0;
        }
        else
        {
            LoadSelectedBackcolor(null);
        }
    }

    public AppSettings Settings { get; }

    private void BackcolorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingSelection && _activeSetting != null)
        {
            _ = CommitFieldsToSetting(_activeSetting, requireValid: false);
        }

        _activeSetting = BackcolorList.SelectedItem as ExtensionBackcolorSetting;
        LoadSelectedBackcolor(_activeSetting);
    }

    private void LoadSelectedBackcolor(ExtensionBackcolorSetting? setting)
    {
        _loadingSelection = true;

        bool hasSelection = setting != null;
        ExtensionTextBox.IsEnabled = hasSelection;
        ColorTextBox.IsEnabled = hasSelection;

        ExtensionTextBox.Text = setting?.Extension ?? string.Empty;
        ColorTextBox.Text = setting?.Backcolor ?? string.Empty;
        PickColorButton.IsEnabled = hasSelection;
        RemoveButton.IsEnabled = hasSelection;
        ValidationText.Text = string.Empty;
        UpdatePreview(setting?.Backcolor);

        _loadingSelection = false;
    }

    private void ColorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingSelection)
        {
            return;
        }

        UpdatePreview(ColorTextBox.Text);
    }

    private void PickColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeBackcolor(ColorTextBox.Text, out string normalizedBackcolor, out _))
        {
            ValidationText.Text = "Backcolor must be a valid color.";
            return;
        }

        ColorTextBox.Text = normalizedBackcolor;
        UpdatePreview(normalizedBackcolor);

        if (_activeSetting != null)
        {
            _ = CommitFieldsToSetting(_activeSetting, requireValid: false);
        }
    }

    private void PresetColorButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string backcolor)
        {
            return;
        }

        ColorTextBox.Text = backcolor;
        UpdatePreview(backcolor);

        if (_activeSetting != null)
        {
            _ = CommitFieldsToSetting(_activeSetting, requireValid: false);
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSetting != null)
        {
            _ = CommitFieldsToSetting(_activeSetting, requireValid: false);
        }

        var setting = new ExtensionBackcolorSetting
        {
            Extension = ".txt",
            Backcolor = Settings.CodeWindows.DefaultBackcolor
        };

        _backcolors.Add(setting);
        BackcolorList.SelectedItem = setting;
        ExtensionTextBox.Focus();
        ExtensionTextBox.SelectAll();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (BackcolorList.SelectedItem is not ExtensionBackcolorSetting setting)
        {
            return;
        }

        int index = BackcolorList.SelectedIndex;
        _backcolors.Remove(setting);

        if (_backcolors.Count == 0)
        {
            _activeSetting = null;
            LoadSelectedBackcolor(null);
            return;
        }

        BackcolorList.SelectedIndex = Math.Min(index, _backcolors.Count - 1);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSetting != null && !CommitFieldsToSetting(_activeSetting, requireValid: true))
        {
            return;
        }

        var normalizedSettings = new List<ExtensionBackcolorSetting>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ExtensionBackcolorSetting setting in _backcolors)
        {
            string extension = NormalizeExtension(setting.Extension);
            if (string.IsNullOrWhiteSpace(extension))
            {
                ValidationText.Text = "Every setting needs an extension.";
                return;
            }

            if (!extensions.Add(extension))
            {
                ValidationText.Text = $"Duplicate extension: {extension}";
                return;
            }

            if (!TryNormalizeBackcolor(setting.Backcolor, out string backcolor, out _))
            {
                ValidationText.Text = $"Invalid backcolor for {extension}.";
                return;
            }

            normalizedSettings.Add(new ExtensionBackcolorSetting
            {
                Extension = extension,
                Backcolor = backcolor
            });
        }

        Settings.CodeWindows.BackcolorsByExtension = normalizedSettings;
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool CommitFieldsToSetting(ExtensionBackcolorSetting setting, bool requireValid)
    {
        string extension = NormalizeExtension(ExtensionTextBox.Text);
        if (string.IsNullOrWhiteSpace(extension))
        {
            if (requireValid)
            {
                ValidationText.Text = "Extension cannot be blank.";
            }

            return false;
        }

        if (!TryNormalizeBackcolor(ColorTextBox.Text, out string backcolor, out _))
        {
            if (requireValid)
            {
                ValidationText.Text = "Backcolor must be a valid color.";
            }

            return false;
        }

        setting.Extension = extension;
        setting.Backcolor = backcolor;
        BackcolorList.Items.Refresh();
        ValidationText.Text = string.Empty;
        return true;
    }

    private void UpdatePreview(string? colorText)
    {
        if (!TryNormalizeBackcolor(colorText, out _, out MediaColor color))
        {
            PreviewSwatch.Background = Brushes.Transparent;
            return;
        }

        PreviewSwatch.Background = new SolidColorBrush(color);
    }

    private static string NormalizeExtension(string? extension)
    {
        string normalized = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.StartsWith(".", StringComparison.Ordinal) ? normalized : $".{normalized}";
    }

    private static bool TryNormalizeBackcolor(string? colorText, out string normalized, out MediaColor color)
    {
        normalized = "#FFFFFF";
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

            color = parsedColor;
            normalized = ToHex(parsedColor);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToHex(MediaColor color)
    {
        return color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
