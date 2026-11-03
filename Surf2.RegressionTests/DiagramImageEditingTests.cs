using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Surf2;
using Surf2.Controls;
using Surf2.Models;

namespace Surf2.RegressionTests;

internal static class DiagramImageEditingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        main.Loaded -= (RoutedEventHandler)Method("Window_Loaded").CreateDelegate(typeof(RoutedEventHandler), main);
        main.Closing -= (CancelEventHandler)Method("Window_Closing").CreateDelegate(typeof(CancelEventHandler), main);
        string filePath = Path.Combine(Path.GetTempPath(), "Surf2-image-test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            main.Show();
            Method("SetDiagramLockState").Invoke(main, [false, true, false]);
            byte[] originalBytes = CreatePng(Colors.Red);
            byte[] replacementBytes = CreatePng(Colors.Blue);
            string originalData = Convert.ToBase64String(originalBytes);
            string replacementData = Convert.ToBase64String(replacementBytes);
            string pastedFileName = Guid.NewGuid().ToString("N") + ".png";
            var image = new DiagramImageControl("original", "Original image", Decode(originalBytes), originalData, pastedFileName);
            image.SetCanvasBounds(40, 60, 150, 110);
            image.ApplyDetails("Keep this label", 16);
            image.ApplyMetadata(new DiagramObjectMetadata { Link = "https://example.com/image" });
            Panel.SetZIndex(image, 7);
            ((Canvas)main.FindName("DiagramCanvas")).Children.Add(image);
            Flush();
            DiagramObjectSnapshot original = Snapshot(main, image);

            AppSettings settings = (AppSettings)typeof(MainWindow).GetField("_appSettings", Private)!.GetValue(main)!;
            settings.DiagramImages.Images = Enumerable.Range(1, 30).Select(index => new DiagramImageDefinition
            {
                Id = "saved-" + index,
                Name = "Saved image " + index,
                SortOrder = 31 - index,
                ImageDataBase64 = replacementData
            }).ToList();
            settings.DiagramImages.Images.Add(new DiagramImageDefinition { Name = "Broken image", ImageDataBase64 = "invalid" });

            Edit(main, image, dialog =>
            {
                Require(dialog.SelectedImage == null, "Opening the dialog must not stage a replacement.");
                ((Button)dialog.FindName("ChangeImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Flush();
                var list = (ListBox)dialog.FindName("SavedImagesList");
                Require(list.Items.Count == 30, "The picker should include saved images and skip corrupt entries.");
                var scroll = Descendants<ScrollViewer>(list).Single();
                Require(scroll.ScrollableHeight > 0, "A large library must scroll within the picker.");
                var popup = (Popup)dialog.FindName("ImagePickerPopup");
                Require(popup.IsOpen && ((Button)dialog.FindName("BrowseImageButton")).IsVisible,
                    "Browse computer should remain visible below the scrollable library.");
                Require(Descendants<Image>(list).Any(preview => preview.Source != null), "Saved images should show thumbnails.");
                list.SelectedIndex = 0;
                Require(dialog.SelectedImage?.Id == "saved-30", "The picker should respect library sort order.");
                Require(!popup.IsOpen, "Choosing a saved image should close the menu.");
                Require(image.ImageDataBase64 == originalData, "Choosing an image must wait for Save before changing the diagram.");
            }, save: true);

            Require(image.ImageDefinitionId == "saved-30" && image.ImageDataBase64 == replacementData,
                "Save should apply the selected image and its library identity.");
            Require(image.PastedImageFileName == string.Empty, "A replacement must clear the previous pasted-image path.");
            Require(image.LabelText == original.LabelText && image.LabelFontSize == original.LabelFontSize,
                "Swapping a picture should preserve its label and font size.");
            Require(Canvas.GetLeft(image) == 40 && Canvas.GetTop(image) == 60 && image.Width == 150 && image.Height == 110 &&
                    Panel.GetZIndex(image) == 7 && image.Metadata.Link == original.Metadata.Link && image.DiagramObjectId == original.Id,
                "Swapping a picture should preserve geometry, stacking, metadata and object identity.");
            Require(Snapshot(main, image).ImageDataBase64 == replacementData, "Saved diagram snapshots must embed the replacement.");
            Method("UndoDiagramAction").Invoke(main, null);
            Require(image.ImageDefinitionId == original.ImageDefinitionId && image.ImageDataBase64 == originalData &&
                    image.PastedImageFileName == pastedFileName && Pixels(image.ImageSource).SequenceEqual(Pixels(Decode(originalBytes))),
                "Undo must restore the original image pixels, data, identity and pasted-image path.");

            Edit(main, image, dialog => ((ListBox)dialog.FindName("SavedImagesList")).SelectedIndex = 0, save: false);
            Require(image.ImageDataBase64 == originalData, "Cancel must discard a staged library replacement.");

            File.WriteAllBytes(filePath, replacementBytes);
            Edit(main, image, dialog =>
            {
                typeof(DiagramImageDetailsWindow).GetMethod("SelectImageFromFile", Private)!.Invoke(dialog, [filePath]);
                Require(dialog.SelectedImage?.ImageDataBase64 == replacementData, "Browsing should stage the chosen file.");
                File.Delete(filePath); // Decoding must release the source file and embed its bytes.
            }, save: true);
            Require(image.ImageDefinitionId == string.Empty && image.ImageDataBase64 == replacementData && image.PastedImageFileName == string.Empty,
                "A local replacement should embed its data without a stale library or pasted-image reference.");
            DiagramObjectSnapshot saved = JsonSerializer.Deserialize<DiagramObjectSnapshot>(JsonSerializer.Serialize(Snapshot(main, image)))!;
            var restored = (DiagramImageControl)Method("CreateDiagramObjectFromSnapshot").Invoke(main, [saved])!;
            Require(restored.ImageDataBase64 == replacementData && Pixels(restored.ImageSource).SequenceEqual(Pixels(Decode(replacementBytes))),
                "A diagram must reopen the uploaded picture after the original local file has been deleted.");

            File.WriteAllText(filePath, "This is not an image.");
            settings.DiagramImages.Images.Clear();
            Edit(main, image, dialog =>
            {
                Require(((TextBlock)dialog.FindName("NoImagesText")).Visibility == Visibility.Visible,
                    "An empty library should explain that no saved images are available.");
                typeof(DiagramImageDetailsWindow).GetMethod("SelectImageFromFile", Private)!.Invoke(dialog, [filePath]);
                Require(dialog.SelectedImage == null && ((TextBlock)dialog.FindName("ImageErrorText")).Visibility == Visibility.Visible,
                    "Invalid files should show an error and preserve the current image.");
                File.Delete(filePath);
                typeof(DiagramImageDetailsWindow).GetMethod("SelectImageFromFile", Private)!.Invoke(dialog, [filePath]);
                Require(dialog.SelectedImage == null, "Unreadable files should preserve the current image.");
            }, save: true);
            Require(image.ImageDataBase64 == replacementData, "Saving after an invalid upload must preserve the current picture.");
        }
        finally
        {
            main.Close();
            File.Delete(filePath);
            Flush();
        }
    }

    private static void Edit(MainWindow main, DiagramImageControl image, Action<DiagramImageDetailsWindow> edit, bool save)
    {
        Exception? failure = null;
        DispatcherOperation pending = main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            DiagramImageDetailsWindow? dialog = null;
            try
            {
                dialog = Application.Current.Windows.OfType<DiagramImageDetailsWindow>().Single();
                dialog.ShowInTaskbar = false;
                dialog.Opacity = 0;
                edit(dialog);
                if (save)
                {
                    typeof(DiagramImageDetailsWindow).GetMethod("SaveButton_Click", Private)!.Invoke(dialog, [dialog, new RoutedEventArgs()]);
                }
                else
                {
                    dialog.DialogResult = false;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                if (dialog != null) dialog.DialogResult = false;
            }
        }));
        try
        {
            Method("DiagramImage_EditRequested").Invoke(main, [image, EventArgs.Empty]);
        }
        finally
        {
            pending.Abort();
        }
        if (failure != null) throw new InvalidOperationException("Image edit dialog check failed.", failure);
    }

    private static byte[] CreatePng(Color color)
    {
        var pixels = new byte[] { color.B, color.G, color.R, 255 };
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(ImageSource source)
    {
        var bitmap = new FormatConvertedBitmap((BitmapSource)source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[4];
        bitmap.CopyPixels(bytes, 4, 0);
        return bytes;
    }

    private static DiagramObjectSnapshot Snapshot(MainWindow main, DiagramImageControl image) =>
        (DiagramObjectSnapshot)Method("CreateDiagramObjectSnapshot").Invoke(main, [image])!;

    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, Private)!;

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
