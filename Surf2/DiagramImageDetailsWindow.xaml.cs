using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Surf2.Controls;
using Surf2.Models;

namespace Surf2;

public partial class DiagramImageDetailsWindow : Window
{
    public DiagramImageDetailsWindow(
        string labelText,
        double labelFontSize = DiagramTextStyle.DefaultFontSize,
        IEnumerable<DiagramImageDefinition>? availableImages = null,
        ImageSource? imageSource = null,
        string imageName = "")
    {
        InitializeComponent();
        LabelTextBox.Text = labelText;
        LabelFontSize = DiagramTextStyle.NormalizeFontSize(labelFontSize);
        LabelFontSizeComboBox.ItemsSource = DiagramTextStyle.FontSizes.Append(LabelFontSize).Distinct().Order().ToList();
        LabelFontSizeComboBox.SelectedItem = LabelFontSize;
        ImagePreview.Source = imageSource;
        ImageNameText.Text = imageName;
        ImageNameText.ToolTip = imageName;

        List<ImageChoice> choices = (availableImages ?? [])
            .Where(image => !string.IsNullOrWhiteSpace(image.Name))
            .Select((image, index) => new { Image = image, Index = index })
            .OrderBy(item => item.Image.SortOrder > 0 ? item.Image.SortOrder : item.Index + 1)
            .ThenBy(item => item.Index)
            .Select(item => new { item.Image, Source = CreateImageSource(item.Image.ImageDataBase64) })
            .Where(item => item.Source != null)
            .Select(item => new ImageChoice(item.Image, item.Source!))
            .ToList();
        SavedImagesList.ItemsSource = choices;
        NoImagesText.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedImagesList.Visibility = choices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public string LabelText { get; private set; } = string.Empty;

    public double LabelFontSize { get; private set; } = DiagramTextStyle.DefaultFontSize;

    public DiagramImageDefinition? SelectedImage { get; private set; }

    public ImageSource? SelectedImageSource { get; private set; }

    private sealed record ImageChoice(DiagramImageDefinition Definition, ImageSource Source);

    private void ChangeImageButton_Click(object sender, RoutedEventArgs e)
    {
        ImagePickerPopup.IsOpen = !ImagePickerPopup.IsOpen;
    }

    private void ImagePickerPopup_Opened(object? sender, EventArgs e)
    {
        if (SavedImagesList.Items.Count > 0)
        {
            SavedImagesList.Focus();
        }
        else
        {
            BrowseImageButton.Focus();
        }
    }

    private void ImagePickerPopup_Closed(object? sender, EventArgs e)
    {
        ChangeImageButton.Focus();
    }

    private void ImagePicker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ImagePickerPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void SavedImagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedImagesList.SelectedItem is ImageChoice choice)
        {
            SelectImage(choice.Definition, choice.Source);
            SavedImagesList.SelectedItem = null;
            ImagePickerPopup.IsOpen = false;
        }
    }

    private void SelectImage(DiagramImageDefinition definition, ImageSource source)
    {
        SelectedImage = definition.Clone();
        SelectedImageSource = source;
        ImagePreview.Source = source;
        ImageNameText.Text = definition.Name;
        ImageNameText.ToolTip = definition.Name;
        ImageErrorText.Visibility = Visibility.Collapsed;
    }

    private void BrowseImageButton_Click(object sender, RoutedEventArgs e)
    {
        ImagePickerPopup.IsOpen = false;
        var dialog = new OpenFileDialog
        {
            Title = "Change Diagram Image",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SelectImageFromFile(dialog.FileName);
        }
    }

    private void SelectImageFromFile(string filePath)
    {
        try
        {
            string encoded = Convert.ToBase64String(File.ReadAllBytes(filePath));
            ImageSource? source = CreateImageSource(encoded);
            if (source == null)
            {
                ShowImageError("The selected file is not a supported image.");
                return;
            }

            SelectImage(new DiagramImageDefinition
            {
                Id = string.Empty,
                Name = Path.GetFileNameWithoutExtension(filePath),
                OriginalFileName = Path.GetFileName(filePath),
                ImageDataBase64 = encoded
            }, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowImageError($"Could not load image: {ex.Message}");
        }
    }

    private void ShowImageError(string message)
    {
        ImageErrorText.Text = message;
        ImageErrorText.Visibility = Visibility.Visible;
    }

    private static BitmapImage? CreateImageSource(string? imageDataBase64)
    {
        if (string.IsNullOrWhiteSpace(imageDataBase64))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(imageDataBase64));
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        LabelText = LabelTextBox.Text;
        LabelFontSize = (double)LabelFontSizeComboBox.SelectedItem;
        DialogResult = true;
        Close();
    }
}
