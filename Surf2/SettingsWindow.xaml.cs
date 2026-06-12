using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using Surf2.Models;
using Surf2.Storage;
using MediaColor = System.Windows.Media.Color;

namespace Surf2;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<ExtensionBackcolorSetting> _backcolors = [];
    private readonly ObservableCollection<ReferenceHighlightStyleSetting> _highlightStyles = [];
    private readonly ObservableCollection<DiagramImageDefinition> _diagramImages = [];
    private readonly LocalConnectionSettingsStore _connectionSettingsStore = new();
    private readonly PersistencePackageService _persistencePackageService = new();
    private readonly DiagramImagePackageService _diagramImagePackageService = new();
    private bool _loadingSelection;
    private bool _loadingHighlightSelection;
    private bool _loadingDiagramImageSelection;
    private ExtensionBackcolorSetting? _activeSetting;
    private ReferenceHighlightStyleSetting? _activeHighlightStyle;
    private DiagramImageDefinition? _activeDiagramImage;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        Settings = settings.Clone();
        Settings.EnsureDefaults();
        ConnectionSettings = _connectionSettingsStore.Load();
        LanguageComboBox.ItemsSource = CodeWindowSettings.SupportedLanguages;

        foreach (ExtensionBackcolorSetting setting in Settings.CodeWindows.BackcolorsByExtension)
        {
            _backcolors.Add(new ExtensionBackcolorSetting
            {
                Extension = setting.Extension,
                Backcolor = setting.Backcolor,
                Language = string.IsNullOrWhiteSpace(setting.Language)
                    ? CodeWindowSettings.GetDefaultLanguageForExtension(setting.Extension)
                    : CodeWindowSettings.NormalizeLanguage(setting.Language)
            });
        }

        foreach (ReferenceHighlightStyleSetting setting in Settings.ReferenceHighlights.Styles
                     .OrderBy(style => style.Language)
                     .ThenBy(style => style.Kind))
        {
            _highlightStyles.Add(setting.Clone());
        }

        foreach (DiagramImageDefinition image in Settings.DiagramImages.Images
                     .OrderBy(image => image.SortOrder)
                     .ThenBy(image => image.Name))
        {
            _diagramImages.Add(image.Clone());
        }

        BackcolorList.ItemsSource = _backcolors;
        HighlightStyleList.ItemsSource = _highlightStyles;
        DiagramImageList.ItemsSource = _diagramImages;
        RefreshDiagramImageSortOrders();
        LoadConnectionSettings();
        LoadKeyboardShortcutSettings();
        LoadResourceComparisonSettings();
        LoadAppearanceSettings();
        LoadDiagnosticsSettings();
        LoadMostRecentWorkbenchCheckBox.IsChecked = Settings.LoadMostRecentWorkbenchOnStartup;

        if (_backcolors.Count > 0)
        {
            BackcolorList.SelectedIndex = 0;
        }
        else
        {
            LoadSelectedBackcolor(null);
        }

        if (_highlightStyles.Count > 0)
        {
            HighlightStyleList.SelectedIndex = 0;
        }
        else
        {
            LoadSelectedHighlightStyle(null);
        }

        if (_diagramImages.Count > 0)
        {
            DiagramImageList.SelectedIndex = 0;
        }
        else
        {
            LoadSelectedDiagramImage(null);
        }
    }

    public AppSettings Settings { get; }

    public PersistenceConnectionSettings ConnectionSettings { get; }

    public bool ConnectionSettingsWereChanged { get; private set; }

    public bool PersistenceDatabaseImported { get; private set; }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CodeWindowSettingsPanel == null ||
            ReferenceHighlightSettingsPanel == null ||
            DiagramImageSettingsPanel == null ||
            KeyboardShortcutSettingsPanel == null ||
            ResourceComparisonSettingsPanel == null ||
            WorkbenchSettingsPanel == null ||
            AppearanceSettingsPanel == null ||
            PersistenceSettingsPanel == null ||
            DiagnosticsSettingsPanel == null)
        {
            return;
        }

        CodeWindowSettingsPanel.Visibility = SectionList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReferenceHighlightSettingsPanel.Visibility = SectionList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        DiagramImageSettingsPanel.Visibility = SectionList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        KeyboardShortcutSettingsPanel.Visibility = SectionList.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        ResourceComparisonSettingsPanel.Visibility = SectionList.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        WorkbenchSettingsPanel.Visibility = SectionList.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSettingsPanel.Visibility = SectionList.SelectedIndex == 6 ? Visibility.Visible : Visibility.Collapsed;
        PersistenceSettingsPanel.Visibility = SectionList.SelectedIndex == 7 ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsSettingsPanel.Visibility = SectionList.SelectedIndex == 8 ? Visibility.Visible : Visibility.Collapsed;
    }

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
        LanguageComboBox.IsEnabled = hasSelection;

        ExtensionTextBox.Text = setting?.Extension ?? string.Empty;
        ColorTextBox.Text = setting?.Backcolor ?? string.Empty;
        LanguageComboBox.SelectedItem = setting == null
            ? null
            : string.IsNullOrWhiteSpace(setting.Language)
                ? CodeWindowSettings.GetDefaultLanguageForExtension(setting.Extension)
                : CodeWindowSettings.NormalizeLanguage(setting.Language);
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

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection || _activeSetting == null)
        {
            return;
        }

        _ = CommitFieldsToSetting(_activeSetting, requireValid: false);
    }

    private void ColorTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ColorTextBox.IsEnabled)
        {
            return;
        }

        e.Handled = true;
        OpenBackcolorPicker();
    }

    private void PickColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeColor(ColorTextBox.Text, out string normalizedBackcolor, out _))
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

    private void OpenBackcolorPicker()
    {
        string initialColor = ColorTextBox.Text;
        if (!TryNormalizeColor(initialColor, out string normalizedBackcolor, out _))
        {
            normalizedBackcolor = "#FFFFFF";
        }

        var picker = new ColorPickerWindow(normalizedBackcolor)
        {
            Owner = this
        };

        if (picker.ShowDialog() != true)
        {
            return;
        }

        ColorTextBox.Text = picker.SelectedColor;
        UpdatePreview(picker.SelectedColor);

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
            Backcolor = Settings.CodeWindows.DefaultBackcolor,
            Language = CodeWindowSettings.PlainTextLanguage
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
        if (!SaveBackcolorSettings())
        {
            return;
        }

        if (!SaveReferenceHighlightSettings(validateActive: false) ||
            !SaveDiagramImageSettings(validateActive: false))
        {
            return;
        }

        SaveWorkbenchSettings();
        SaveKeyboardShortcutSettings();
        SaveAppearanceSettings();
        SaveDiagnosticsSettings();
        DialogResult = true;
        Close();
    }

    private void HighlightStyleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingHighlightSelection && _activeHighlightStyle != null)
        {
            _ = CommitHighlightFieldsToSetting(_activeHighlightStyle, requireValid: false);
        }

        _activeHighlightStyle = HighlightStyleList.SelectedItem as ReferenceHighlightStyleSetting;
        LoadSelectedHighlightStyle(_activeHighlightStyle);
    }

    private void LoadSelectedHighlightStyle(ReferenceHighlightStyleSetting? setting)
    {
        _loadingHighlightSelection = true;

        bool hasSelection = setting != null;
        HighlightColorTextBox.IsEnabled = hasSelection;
        ApplyHighlightColorButton.IsEnabled = hasSelection;
        HighlightBoldCheckBox.IsEnabled = hasSelection;
        HighlightItalicCheckBox.IsEnabled = hasSelection;
        HighlightUnderlineCheckBox.IsEnabled = hasSelection;

        HighlightTargetText.Text = setting?.DisplayLabel ?? string.Empty;
        HighlightColorTextBox.Text = setting?.Foreground ?? string.Empty;
        HighlightBoldCheckBox.IsChecked = setting?.IsBold ?? false;
        HighlightItalicCheckBox.IsChecked = setting?.IsItalic ?? false;
        HighlightUnderlineCheckBox.IsChecked = setting?.IsUnderline ?? false;
        HighlightValidationText.Text = string.Empty;
        UpdateHighlightPreview();

        _loadingHighlightSelection = false;
    }

    private void HighlightColorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingHighlightSelection)
        {
            return;
        }

        UpdateHighlightPreview();
    }

    private void ApplyHighlightColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeColor(HighlightColorTextBox.Text, out string normalizedColor, out _))
        {
            HighlightValidationText.Text = "Font color must be a valid color.";
            return;
        }

        HighlightColorTextBox.Text = normalizedColor;
        UpdateHighlightPreview();

        if (_activeHighlightStyle != null)
        {
            _ = CommitHighlightFieldsToSetting(_activeHighlightStyle, requireValid: false);
        }
    }

    private void HighlightPresetColorButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string color)
        {
            return;
        }

        HighlightColorTextBox.Text = color;
        UpdateHighlightPreview();

        if (_activeHighlightStyle != null)
        {
            _ = CommitHighlightFieldsToSetting(_activeHighlightStyle, requireValid: false);
        }
    }

    private void HighlightStyleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingHighlightSelection)
        {
            return;
        }

        UpdateHighlightPreview();

        if (_activeHighlightStyle != null)
        {
            _ = CommitHighlightFieldsToSetting(_activeHighlightStyle, requireValid: false);
        }
    }

    private void SaveHighlightButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveReferenceHighlightSettings(validateActive: true))
        {
            return;
        }

        if (!SaveBackcolorSettings() ||
            !SaveDiagramImageSettings(validateActive: false))
        {
            return;
        }

        SaveWorkbenchSettings();
        SaveKeyboardShortcutSettings();
        SaveAppearanceSettings();
        SaveDiagnosticsSettings();
        DialogResult = true;
        Close();
    }

    private void DiagramImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingDiagramImageSelection && _activeDiagramImage != null)
        {
            _ = CommitDiagramImageFieldsToSetting(_activeDiagramImage, requireValid: false);
        }

        _activeDiagramImage = DiagramImageList.SelectedItem as DiagramImageDefinition;
        LoadSelectedDiagramImage(_activeDiagramImage);
    }

    private void LoadSelectedDiagramImage(DiagramImageDefinition? image)
    {
        _loadingDiagramImageSelection = true;

        bool hasSelection = image != null;
        DiagramImageNameTextBox.IsEnabled = hasSelection;
        DiagramImageNameRegexTextBox.IsEnabled = hasSelection;
        DiagramImageContentRegexTextBox.IsEnabled = hasSelection;
        DiagramImageResourceTypeComboBox.IsEnabled = hasSelection;
        ImportDiagramImageButton.IsEnabled = hasSelection;
        RemoveDiagramImageButton.IsEnabled = hasSelection;
        MoveDiagramImageUpButton.IsEnabled = hasSelection && DiagramImageList.SelectedIndex > 0;
        MoveDiagramImageDownButton.IsEnabled = hasSelection && DiagramImageList.SelectedIndex >= 0 && DiagramImageList.SelectedIndex < _diagramImages.Count - 1;

        DiagramImageNameTextBox.Text = image?.Name ?? string.Empty;
        DiagramImageNameRegexTextBox.Text = image?.NameRegex ?? string.Empty;
        DiagramImageContentRegexTextBox.Text = image?.ContentRegex ?? string.Empty;
        SelectDiagramImageResourceType(image?.ResourceTypeFilter);
        DiagramImageFileText.Text = image == null
            ? string.Empty
            : string.IsNullOrWhiteSpace(image.OriginalFileName)
                ? "No image imported."
                : image.OriginalFileName;
        DiagramImagePreview.Source = CreateImageSource(image?.ImageDataBase64);
        ClearDiagramImageStatus();

        _loadingDiagramImageSelection = false;
    }

    private void DiagramImageNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingDiagramImageSelection || _activeDiagramImage == null)
        {
            return;
        }

        _ = CommitDiagramImageFieldsToSetting(_activeDiagramImage, requireValid: false);
    }

    private void DiagramImageNameRegexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingDiagramImageSelection || _activeDiagramImage == null)
        {
            return;
        }

        _ = CommitDiagramImageFieldsToSetting(_activeDiagramImage, requireValid: false);
    }

    private void DiagramImageContentRegexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingDiagramImageSelection || _activeDiagramImage == null)
        {
            return;
        }

        _ = CommitDiagramImageFieldsToSetting(_activeDiagramImage, requireValid: false);
    }

    private void DiagramImageResourceTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDiagramImageSelection || _activeDiagramImage == null)
        {
            return;
        }

        _ = CommitDiagramImageFieldsToSetting(_activeDiagramImage, requireValid: false);
    }

    private void SelectDiagramImageResourceType(string? resourceTypeFilter)
    {
        string normalizedFilter = DiagramImageDefinition.NormalizeResourceTypeFilter(resourceTypeFilter);
        foreach (object item in DiagramImageResourceTypeComboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                string.Equals(comboBoxItem.Content?.ToString(), normalizedFilter, StringComparison.OrdinalIgnoreCase))
            {
                DiagramImageResourceTypeComboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        DiagramImageResourceTypeComboBox.SelectedIndex = 0;
    }

    private void ImportDiagramImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDiagramImage == null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import Diagram Image",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(dialog.FileName);
            string encoded = Convert.ToBase64String(bytes);

            if (CreateImageSource(encoded) == null)
            {
                SetDiagramImageError("The selected file is not a supported image.");
                return;
            }

            _activeDiagramImage.ImageDataBase64 = encoded;
            _activeDiagramImage.OriginalFileName = Path.GetFileName(dialog.FileName);
            if (string.IsNullOrWhiteSpace(DiagramImageNameTextBox.Text) ||
                string.Equals(DiagramImageNameTextBox.Text, "New Image", StringComparison.OrdinalIgnoreCase))
            {
                DiagramImageNameTextBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }

            CommitDiagramImageFieldsToSetting(_activeDiagramImage, requireValid: false);
            DiagramImageList.Items.Refresh();
            LoadSelectedDiagramImage(_activeDiagramImage);
        }
        catch (IOException ex)
        {
            SetDiagramImageError($"Could not import image: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            SetDiagramImageError($"Could not import image: {ex.Message}");
        }
    }

    private async void ExportDiagramImagePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveDiagramImageSettings(validateActive: true))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Image Package",
            Filter = "Surf2 image package (*.surfimages.zip)|*.surfimages.zip|Zip files (*.zip)|*.zip|All files (*.*)|*.*",
            DefaultExt = ".surfimages.zip",
            FileName = $"surf-images-{DateTime.Now:yyyyMMdd-HHmmss}.surfimages.zip"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetDiagramImagePackageActionsEnabled(false);
        SetDiagramImageStatus("Exporting image package...", isError: false);

        try
        {
            DiagramImagePackageExportResult result = await _diagramImagePackageService.ExportAsync(
                dialog.FileName,
                Settings.DiagramImages.Images);
            SetDiagramImageStatus($"Exported {result.ImageCount} diagram image(s).", isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            SetDiagramImageError($"Export failed: {ex.Message}");
        }
        finally
        {
            SetDiagramImagePackageActionsEnabled(true);
        }
    }

    private async void ImportDiagramImagePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDiagramImage != null)
        {
            _ = CommitDiagramImageFieldsToSetting(_activeDiagramImage, requireValid: false);
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import Image Package",
            Filter = "Surf2 image package (*.surfimages.zip)|*.surfimages.zip|Zip files (*.zip)|*.zip|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetDiagramImagePackageActionsEnabled(false);
        SetDiagramImageStatus("Importing image package...", isError: false);

        try
        {
            DiagramImagePackageImportResult result = await _diagramImagePackageService.ImportAsync(dialog.FileName);
            int importedCount = AddImportedDiagramImages(result.Images, out int skippedCount, out DiagramImageDefinition? firstImported);

            if (importedCount == 0)
            {
                SetDiagramImageError(skippedCount == 0
                    ? "The selected image package did not contain any images."
                    : $"No images were imported. Skipped {skippedCount} invalid image(s).");
                return;
            }

            DiagramImageList.Items.Refresh();
            if (firstImported != null)
            {
                DiagramImageList.SelectedItem = firstImported;
            }

            string skippedText = skippedCount > 0 ? $" Skipped {skippedCount} invalid image(s)." : string.Empty;
            SetDiagramImageStatus($"Imported {importedCount} diagram image(s). Click Save to apply them.{skippedText}", isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            SetDiagramImageError($"Import failed: {ex.Message}");
        }
        finally
        {
            SetDiagramImagePackageActionsEnabled(true);
        }
    }

    private int AddImportedDiagramImages(
        IEnumerable<DiagramImageDefinition> importedImages,
        out int skippedCount,
        out DiagramImageDefinition? firstImported)
    {
        var usedIds = new HashSet<string>(
            _diagramImages
                .Select(image => image.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(
            _diagramImages
                .Select(image => image.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        int importedCount = 0;
        skippedCount = 0;
        firstImported = null;

        foreach (DiagramImageDefinition importedImage in importedImages)
        {
            if (CreateImageSource(importedImage.ImageDataBase64) == null ||
                !IsDiagramImageRegexValid(importedImage.NameRegex) ||
                !IsDiagramImageRegexValid(importedImage.ContentRegex))
            {
                skippedCount++;
                continue;
            }

            string originalFileName = Path.GetFileName(importedImage.OriginalFileName) ?? string.Empty;
            var image = new DiagramImageDefinition
            {
                Id = CreateUniqueDiagramImageId(importedImage.Id, usedIds),
                Name = CreateUniqueDiagramImageName(importedImage.Name, usedNames),
                NameRegex = importedImage.NameRegex?.Trim() ?? string.Empty,
                ContentRegex = importedImage.ContentRegex?.Trim() ?? string.Empty,
                ResourceTypeFilter = DiagramImageDefinition.NormalizeResourceTypeFilter(importedImage.ResourceTypeFilter),
                SortOrder = _diagramImages.Count + 1,
                OriginalFileName = originalFileName,
                ImageDataBase64 = importedImage.ImageDataBase64
            };
            image.Regex = DiagramImageDefinition.GetLegacyRegex(image.NameRegex, image.ContentRegex);
            image.MatchTarget = DiagramImageDefinition.GetLegacyMatchTarget(image.NameRegex, image.ContentRegex);

            _diagramImages.Add(image);
            RefreshDiagramImageSortOrders();
            firstImported ??= image;
            importedCount++;
        }

        return importedCount;
    }

    private void SetDiagramImagePackageActionsEnabled(bool isEnabled)
    {
        ExportDiagramImagePackageButton.IsEnabled = isEnabled;
        ImportDiagramImagePackageButton.IsEnabled = isEnabled;
    }

    private void AddDiagramImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDiagramImage != null)
        {
            _ = CommitDiagramImageFieldsToSetting(_activeDiagramImage, requireValid: false);
        }

        var image = new DiagramImageDefinition
        {
            Name = "New Image",
            ResourceTypeFilter = DiagramImageDefinition.AnyResourceTypeFilter,
            SortOrder = _diagramImages.Count + 1
        };

        _diagramImages.Add(image);
        RefreshDiagramImageSortOrders();
        DiagramImageList.SelectedItem = image;
        DiagramImageNameTextBox.Focus();
        DiagramImageNameTextBox.SelectAll();
    }

    private void RemoveDiagramImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiagramImageList.SelectedItem is not DiagramImageDefinition image)
        {
            return;
        }

        int index = DiagramImageList.SelectedIndex;
        _diagramImages.Remove(image);
        RefreshDiagramImageSortOrders();

        if (_diagramImages.Count == 0)
        {
            _activeDiagramImage = null;
            LoadSelectedDiagramImage(null);
            return;
        }

        DiagramImageList.SelectedIndex = Math.Min(index, _diagramImages.Count - 1);
    }

    private void MoveDiagramImageUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedDiagramImage(-1);
    }

    private void MoveDiagramImageDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedDiagramImage(1);
    }

    private void MoveSelectedDiagramImage(int offset)
    {
        if (DiagramImageList.SelectedItem is not DiagramImageDefinition image)
        {
            return;
        }

        if (!CommitDiagramImageFieldsToSetting(image, requireValid: false))
        {
            return;
        }

        int currentIndex = DiagramImageList.SelectedIndex;
        int newIndex = currentIndex + offset;
        if (newIndex < 0 || newIndex >= _diagramImages.Count)
        {
            return;
        }

        _diagramImages.Move(currentIndex, newIndex);
        RefreshDiagramImageSortOrders();
        DiagramImageList.SelectedItem = image;
        LoadSelectedDiagramImage(image);
    }

    private void SaveDiagramImagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveBackcolorSettings() ||
            !SaveReferenceHighlightSettings(validateActive: false) ||
            !SaveDiagramImageSettings(validateActive: true))
        {
            return;
        }

        SaveWorkbenchSettings();
        SaveKeyboardShortcutSettings();
        SaveAppearanceSettings();
        SaveDiagnosticsSettings();
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadConnectionSettings()
    {
        string? environmentConnectionString = Environment.GetEnvironmentVariable(SqlServerConnectionOptions.EnvironmentVariableName);
        bool hasEnvironmentOverride = !string.IsNullOrWhiteSpace(environmentConnectionString);

        PersistenceConnectionStringTextBox.Text = hasEnvironmentOverride
            ? environmentConnectionString
            : string.IsNullOrWhiteSpace(ConnectionSettings.ConnectionString)
                ? SqlServerConnectionOptions.DefaultConnectionString
                : ConnectionSettings.ConnectionString;

        PersistenceConnectionStringTextBox.IsEnabled = !hasEnvironmentOverride;
        ConnectionOverrideText.Text = hasEnvironmentOverride
            ? $"Using {SqlServerConnectionOptions.EnvironmentVariableName}; local changes are disabled while that environment variable is set."
            : "Using local connection settings.";
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeConnectionString(PersistenceConnectionStringTextBox.Text, out string connectionString, out string validationMessage))
        {
            PersistenceValidationText.Foreground = Brushes.Firebrick;
            PersistenceValidationText.Text = validationMessage;
            return;
        }

        PersistenceValidationText.Foreground = Brushes.DimGray;
        PersistenceValidationText.Text = "Testing connection...";

        try
        {
            await SqlServerDocumentStore.TestConnectionAsync(connectionString);
            PersistenceValidationText.Foreground = Brushes.DarkGreen;
            PersistenceValidationText.Text = "Connection succeeded. Surf2 schema is available.";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            PersistenceValidationText.Foreground = Brushes.Firebrick;
            PersistenceValidationText.Text = $"Connection failed: {ex.Message}";
        }
    }

    private void UseDefaultConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PersistenceConnectionStringTextBox.IsEnabled)
        {
            PersistenceValidationText.Foreground = Brushes.Firebrick;
            PersistenceValidationText.Text = $"Unset {SqlServerConnectionOptions.EnvironmentVariableName} to edit the local connection string.";
            return;
        }

        PersistenceConnectionStringTextBox.Text = SqlServerConnectionOptions.DefaultConnectionString;
        PersistenceValidationText.Foreground = Brushes.DimGray;
        PersistenceValidationText.Text = "Default LocalDB connection restored.";
    }

    private async void ExportDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeConnectionString(PersistenceConnectionStringTextBox.Text, out string connectionString, out string validationMessage))
        {
            PersistenceValidationText.Foreground = Brushes.Firebrick;
            PersistenceValidationText.Text = validationMessage;
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Database",
            Filter = "Surf2 database export (*.surf2db.zip)|*.surf2db.zip|Zip files (*.zip)|*.zip|All files (*.*)|*.*",
            DefaultExt = ".surf2db.zip",
            FileName = $"surf2-export-{DateTime.Now:yyyyMMdd-HHmmss}.surf2db.zip"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetPersistenceActionsEnabled(false);
        PersistenceValidationText.Foreground = Brushes.DimGray;
        PersistenceValidationText.Text = "Exporting database...";

        try
        {
            PersistenceExportResult result = await _persistencePackageService.ExportAsync(dialog.FileName, connectionString);
            PersistenceValidationText.Foreground = Brushes.DarkGreen;
            PersistenceValidationText.Text = $"Exported {result.DocumentCount} database document(s) and {result.LocalFileCount} local file(s).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqlException or InvalidOperationException or ArgumentException)
        {
            PersistenceValidationText.Foreground = Brushes.Firebrick;
            PersistenceValidationText.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            SetPersistenceActionsEnabled(true);
        }
    }

    private async void ImportDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeConnectionString(PersistenceConnectionStringTextBox.Text, out string connectionString, out string validationMessage))
        {
            PersistenceValidationText.Foreground = Brushes.Firebrick;
            PersistenceValidationText.Text = validationMessage;
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import Database",
            Filter = "Surf2 database export (*.surf2db.zip)|*.surf2db.zip|Zip files (*.zip)|*.zip|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            this,
            "Import this database export into the currently configured Surf2 database?",
            "Import Database",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SetPersistenceActionsEnabled(false);
        PersistenceValidationText.Foreground = Brushes.DimGray;
        PersistenceValidationText.Text = "Importing database...";

        try
        {
            PersistenceImportResult result = await _persistencePackageService.ImportAsync(dialog.FileName, connectionString);
            PersistenceDatabaseImported = true;
            MessageBox.Show(
                this,
                $"Imported {result.DocumentCount} database document(s) and restored {result.RestoredLocalFileCount} local file(s). Restart Surf2 to load the imported setup.",
                "Import Database",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqlException or InvalidOperationException or InvalidDataException or ArgumentException)
        {
            PersistenceValidationText.Foreground = Brushes.Firebrick;
            PersistenceValidationText.Text = $"Import failed: {ex.Message}";
        }
        finally
        {
            SetPersistenceActionsEnabled(true);
        }
    }

    private void SetPersistenceActionsEnabled(bool isEnabled)
    {
        TestConnectionButton.IsEnabled = isEnabled;
        UseDefaultConnectionButton.IsEnabled = isEnabled;
        ExportDatabaseButton.IsEnabled = isEnabled;
        ImportDatabaseButton.IsEnabled = isEnabled;
    }

    private void SavePersistenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PersistenceConnectionStringTextBox.IsEnabled)
        {
            PersistenceValidationText.Foreground = Brushes.Firebrick;
            PersistenceValidationText.Text = $"Unset {SqlServerConnectionOptions.EnvironmentVariableName} to save local connection settings.";
            return;
        }

        if (!TryNormalizeConnectionString(PersistenceConnectionStringTextBox.Text, out string connectionString, out string validationMessage))
        {
            PersistenceValidationText.Foreground = Brushes.Firebrick;
            PersistenceValidationText.Text = validationMessage;
            return;
        }

        ConnectionSettings.ConnectionString = connectionString;
        _connectionSettingsStore.Save(ConnectionSettings);
        ConnectionSettingsWereChanged = true;
        SaveWorkbenchSettings();
        SaveKeyboardShortcutSettings();
        SaveAppearanceSettings();
        SaveDiagnosticsSettings();

        DialogResult = true;
        Close();
    }

    private void SaveKeyboardShortcutSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveBackcolorSettings() ||
            !SaveReferenceHighlightSettings(validateActive: false) ||
            !SaveDiagramImageSettings(validateActive: false))
        {
            return;
        }

        SaveWorkbenchSettings();
        SaveKeyboardShortcutSettings();
        SaveAppearanceSettings();
        SaveDiagnosticsSettings();
        DialogResult = true;
        Close();
    }

    private void SaveResourceComparisonSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveBackcolorSettings() ||
            !SaveReferenceHighlightSettings(validateActive: false) ||
            !SaveDiagramImageSettings(validateActive: false))
        {
            return;
        }

        SaveWorkbenchSettings();
        SaveKeyboardShortcutSettings();
        SaveAppearanceSettings();
        SaveDiagnosticsSettings();
        DialogResult = true;
        Close();
    }

    private void SaveWorkbenchSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveBackcolorSettings() ||
            !SaveReferenceHighlightSettings(validateActive: false) ||
            !SaveDiagramImageSettings(validateActive: false))
        {
            return;
        }

        SaveWorkbenchSettings();
        SaveKeyboardShortcutSettings();
        SaveAppearanceSettings();
        SaveDiagnosticsSettings();
        DialogResult = true;
        Close();
    }

    private void SaveAppearanceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveBackcolorSettings() ||
            !SaveReferenceHighlightSettings(validateActive: false) ||
            !SaveDiagramImageSettings(validateActive: false))
        {
            return;
        }

        SaveWorkbenchSettings();
        SaveKeyboardShortcutSettings();
        SaveAppearanceSettings();
        SaveDiagnosticsSettings();
        DialogResult = true;
        Close();
    }

    private void SaveDiagnosticsSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveBackcolorSettings() ||
            !SaveReferenceHighlightSettings(validateActive: false) ||
            !SaveDiagramImageSettings(validateActive: false))
        {
            return;
        }

        SaveWorkbenchSettings();
        SaveKeyboardShortcutSettings();
        SaveAppearanceSettings();
        SaveDiagnosticsSettings();
        DialogResult = true;
        Close();
    }

    private void LoadKeyboardShortcutSettings()
    {
        Settings.KeyboardShortcuts ??= new KeyboardShortcutSettings();
        Settings.KeyboardShortcuts.EnsureDefaults();
        CanvasCtrlMousePanningCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableCanvasCtrlMousePanning;
        TabCtrlMouseScrollingCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableTabCtrlMouseScrolling;
        CodeShiftAutoscrollCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableCodeShiftMouseAutoscrolling;
        CodeCtrlShiftScrollbarLockedScrollCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableCodeCtrlShiftMouseScrollbarLockedScrolling;
        CodeCanvasShiftMousePanningCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableCodeCanvasShiftMousePanning;
        CodeCanvasCtrlShiftMouseZoomingCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableCodeCanvasCtrlShiftMouseZooming;
        CodeViewHistoryNavigationCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableCodeViewCtrlPlusMinusNavigation;
        ViewSwitchingCtrlNumberCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableCtrlNumberViewSwitching;
        CodeTabCtrlASNavigationCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableCodeTabCtrlASNavigation;
        DiagramSidebarToggleCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableDiagramCtrlQSidebarToggle;
        DiagramWorkflowSidebarCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableDiagramCtrlWWorkflowSidebar;
        DiagramShiftMousePanningCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableDiagramShiftMousePanning;
        DiagramCtrlShiftMouseZoomingCheckBox.IsChecked = Settings.KeyboardShortcuts.EnableDiagramCtrlShiftMouseZooming;
    }

    private void SaveKeyboardShortcutSettings()
    {
        Settings.KeyboardShortcuts ??= new KeyboardShortcutSettings();
        Settings.KeyboardShortcuts.Version = KeyboardShortcutSettings.CurrentVersion;
        Settings.KeyboardShortcuts.EnableCanvasCtrlMousePanning = CanvasCtrlMousePanningCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableTabCtrlMouseScrolling = TabCtrlMouseScrollingCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableCodeShiftMouseAutoscrolling = CodeShiftAutoscrollCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableCodeCtrlShiftMouseScrollbarLockedScrolling = CodeCtrlShiftScrollbarLockedScrollCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableCodeCanvasShiftMousePanning = CodeCanvasShiftMousePanningCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableCodeCanvasCtrlShiftMouseZooming = CodeCanvasCtrlShiftMouseZoomingCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableTabCtrlShiftMouseAutoscrolling = Settings.KeyboardShortcuts.EnableCodeShiftMouseAutoscrolling;
        Settings.KeyboardShortcuts.EnableCodeViewCtrlPlusMinusNavigation = CodeViewHistoryNavigationCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableCtrlNumberViewSwitching = ViewSwitchingCtrlNumberCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableCodeTabCtrlASNavigation = CodeTabCtrlASNavigationCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableDiagramCtrlQSidebarToggle = DiagramSidebarToggleCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableDiagramCtrlWWorkflowSidebar = DiagramWorkflowSidebarCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableDiagramShiftMousePanning = DiagramShiftMousePanningCheckBox.IsChecked == true;
        Settings.KeyboardShortcuts.EnableDiagramCtrlShiftMouseZooming = DiagramCtrlShiftMouseZoomingCheckBox.IsChecked == true;
        SaveResourceComparisonSettings();
    }

    private void SaveWorkbenchSettings()
    {
        Settings.LoadMostRecentWorkbenchOnStartup = LoadMostRecentWorkbenchCheckBox.IsChecked == true;
    }

    private void LoadResourceComparisonSettings()
    {
        Settings.ResourceComparison ??= new ResourceComparisonSettings();
        IgnoreWhitespaceDefaultCheckBox.IsChecked = Settings.ResourceComparison.IgnoreWhitespaceByDefault;
        IgnoreCaseDefaultCheckBox.IsChecked = Settings.ResourceComparison.IgnoreCaseByDefault;
    }

    private void SaveResourceComparisonSettings()
    {
        Settings.ResourceComparison ??= new ResourceComparisonSettings();
        Settings.ResourceComparison.IgnoreWhitespaceByDefault = IgnoreWhitespaceDefaultCheckBox.IsChecked == true;
        Settings.ResourceComparison.IgnoreCaseByDefault = IgnoreCaseDefaultCheckBox.IsChecked == true;
    }

    private void LoadDiagnosticsSettings()
    {
        Settings.Diagnostics ??= new DiagnosticsSettings();
        EnableInternalLoggingCheckBox.IsChecked = Settings.Diagnostics.EnableInternalLogging;
    }

    private void LoadAppearanceSettings()
    {
        Settings.Appearance ??= new AppearanceSettings();
        SelectAppearanceTheme(Settings.Appearance.Theme);
    }

    private void SaveAppearanceSettings()
    {
        Settings.Appearance ??= new AppearanceSettings();
        Settings.Appearance.Theme = AppearanceThemeComboBox.SelectedItem is ComboBoxItem item
            ? AppearanceSettings.NormalizeTheme(item.Content?.ToString())
            : AppearanceSettings.LightTheme;
    }

    private void SelectAppearanceTheme(string? theme)
    {
        string normalizedTheme = AppearanceSettings.NormalizeTheme(theme);
        foreach (object item in AppearanceThemeComboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                string.Equals(comboBoxItem.Content?.ToString(), normalizedTheme, StringComparison.OrdinalIgnoreCase))
            {
                AppearanceThemeComboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        AppearanceThemeComboBox.SelectedIndex = 0;
    }

    private void SaveDiagnosticsSettings()
    {
        Settings.Diagnostics ??= new DiagnosticsSettings();
        Settings.Diagnostics.EnableInternalLogging = EnableInternalLoggingCheckBox.IsChecked == true;
    }

    private bool SaveBackcolorSettings()
    {
        if (_activeSetting != null && !CommitFieldsToSetting(_activeSetting, requireValid: true))
        {
            return false;
        }

        var normalizedSettings = new List<ExtensionBackcolorSetting>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ExtensionBackcolorSetting setting in _backcolors)
        {
            string extension = NormalizeExtension(setting.Extension);
            if (string.IsNullOrWhiteSpace(extension))
            {
                ValidationText.Text = "Every setting needs an extension.";
                return false;
            }

            if (!extensions.Add(extension))
            {
                ValidationText.Text = $"Duplicate extension: {extension}";
                return false;
            }

            if (!TryNormalizeColor(setting.Backcolor, out string backcolor, out _))
            {
                ValidationText.Text = $"Invalid backcolor for {extension}.";
                return false;
            }

            normalizedSettings.Add(new ExtensionBackcolorSetting
            {
                Extension = extension,
                Backcolor = backcolor,
                Language = string.IsNullOrWhiteSpace(setting.Language)
                    ? CodeWindowSettings.GetDefaultLanguageForExtension(extension)
                    : CodeWindowSettings.NormalizeLanguage(setting.Language)
            });
        }

        Settings.CodeWindows.BackcolorsByExtension = normalizedSettings;
        return true;
    }

    private bool SaveReferenceHighlightSettings(bool validateActive)
    {
        if (_activeHighlightStyle != null && !CommitHighlightFieldsToSetting(_activeHighlightStyle, validateActive))
        {
            return !validateActive;
        }

        var normalizedSettings = new List<ReferenceHighlightStyleSetting>();
        var styleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ReferenceHighlightStyleSetting setting in _highlightStyles)
        {
            if (string.IsNullOrWhiteSpace(setting.Language))
            {
                HighlightValidationText.Text = "Every highlight style needs a language.";
                return false;
            }

            string styleKey = $"{setting.Language.Trim()}|{setting.Kind}";
            if (!styleKeys.Add(styleKey))
            {
                HighlightValidationText.Text = $"Duplicate highlight style: {setting.DisplayLabel}";
                return false;
            }

            if (!TryNormalizeColor(setting.Foreground, out string foreground, out _))
            {
                HighlightValidationText.Text = $"Invalid font color for {setting.DisplayLabel}.";
                return false;
            }

            normalizedSettings.Add(new ReferenceHighlightStyleSetting
            {
                Language = setting.Language.Trim(),
                Kind = setting.Kind,
                Foreground = foreground,
                IsBold = setting.IsBold,
                IsItalic = setting.IsItalic,
                IsUnderline = setting.IsUnderline
            });
        }

        Settings.ReferenceHighlights.Styles = normalizedSettings;
        return true;
    }

    private bool SaveDiagramImageSettings(bool validateActive)
    {
        if (_activeDiagramImage != null && !CommitDiagramImageFieldsToSetting(_activeDiagramImage, validateActive))
        {
            return !validateActive;
        }

        var normalizedImages = new List<DiagramImageDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < _diagramImages.Count; index++)
        {
            DiagramImageDefinition image = _diagramImages[index];
            string name = (image.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                SetDiagramImageError("Every diagram image needs a name.");
                return false;
            }

            if (!names.Add(name))
            {
                SetDiagramImageError($"Duplicate diagram image name: {name}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(image.ImageDataBase64) ||
                CreateImageSource(image.ImageDataBase64) == null)
            {
                SetDiagramImageError($"Import a valid image for {name}.");
                return false;
            }

            string nameRegex = image.NameRegex?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(nameRegex))
            {
                try
                {
                    _ = new Regex(nameRegex);
                }
                catch (ArgumentException ex)
                {
                    SetDiagramImageError($"Name regex for {name} is invalid: {ex.Message}");
                    return false;
                }
            }

            string contentRegex = image.ContentRegex?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(contentRegex))
            {
                try
                {
                    _ = new Regex(contentRegex);
                }
                catch (ArgumentException ex)
                {
                    SetDiagramImageError($"Content regex for {name} is invalid: {ex.Message}");
                    return false;
                }
            }

            normalizedImages.Add(new DiagramImageDefinition
            {
                Id = string.IsNullOrWhiteSpace(image.Id) ? Guid.NewGuid().ToString("N") : image.Id,
                Name = name,
                Regex = DiagramImageDefinition.GetLegacyRegex(nameRegex, contentRegex),
                MatchTarget = DiagramImageDefinition.GetLegacyMatchTarget(nameRegex, contentRegex),
                NameRegex = nameRegex,
                ContentRegex = contentRegex,
                ResourceTypeFilter = DiagramImageDefinition.NormalizeResourceTypeFilter(image.ResourceTypeFilter),
                SortOrder = index + 1,
                OriginalFileName = image.OriginalFileName?.Trim() ?? string.Empty,
                ImageDataBase64 = image.ImageDataBase64
            });
        }

        Settings.DiagramImages.Images = normalizedImages;
        return true;
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

        if (!TryNormalizeColor(ColorTextBox.Text, out string backcolor, out _))
        {
            if (requireValid)
            {
                ValidationText.Text = "Backcolor must be a valid color.";
            }

            return false;
        }

        setting.Extension = extension;
        setting.Backcolor = backcolor;
        setting.Language = LanguageComboBox.SelectedItem is string selectedLanguage
            ? CodeWindowSettings.NormalizeLanguage(selectedLanguage)
            : CodeWindowSettings.GetDefaultLanguageForExtension(extension);
        BackcolorList.Items.Refresh();
        ValidationText.Text = string.Empty;
        return true;
    }

    private bool CommitHighlightFieldsToSetting(ReferenceHighlightStyleSetting setting, bool requireValid)
    {
        if (!TryNormalizeColor(HighlightColorTextBox.Text, out string foreground, out _))
        {
            if (requireValid)
            {
                HighlightValidationText.Text = "Font color must be a valid color.";
            }

            return false;
        }

        setting.Foreground = foreground;
        setting.IsBold = HighlightBoldCheckBox.IsChecked == true;
        setting.IsItalic = HighlightItalicCheckBox.IsChecked == true;
        setting.IsUnderline = HighlightUnderlineCheckBox.IsChecked == true;
        HighlightStyleList.Items.Refresh();
        HighlightValidationText.Text = string.Empty;
        return true;
    }

    private bool CommitDiagramImageFieldsToSetting(DiagramImageDefinition image, bool requireValid)
    {
        string name = (DiagramImageNameTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            if (requireValid)
            {
                SetDiagramImageError("Image name cannot be blank.");
            }

            return false;
        }

        image.Name = name;
        image.NameRegex = (DiagramImageNameRegexTextBox.Text ?? string.Empty).Trim();
        image.ContentRegex = (DiagramImageContentRegexTextBox.Text ?? string.Empty).Trim();
        image.ResourceTypeFilter = DiagramImageResourceTypeComboBox.SelectedItem is ComboBoxItem selectedResourceType
            ? DiagramImageDefinition.NormalizeResourceTypeFilter(selectedResourceType.Content?.ToString())
            : DiagramImageDefinition.AnyResourceTypeFilter;
        image.Regex = DiagramImageDefinition.GetLegacyRegex(image.NameRegex, image.ContentRegex);
        image.MatchTarget = DiagramImageDefinition.GetLegacyMatchTarget(image.NameRegex, image.ContentRegex);

        if (!string.IsNullOrWhiteSpace(image.NameRegex))
        {
            try
            {
                _ = new Regex(image.NameRegex);
            }
            catch (ArgumentException ex)
            {
                if (requireValid)
                {
                    SetDiagramImageError($"Name regex is invalid: {ex.Message}");
                }

                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(image.ContentRegex))
        {
            try
            {
                _ = new Regex(image.ContentRegex);
            }
            catch (ArgumentException ex)
            {
                if (requireValid)
                {
                    SetDiagramImageError($"Content regex is invalid: {ex.Message}");
                }

                return false;
            }
        }

        DiagramImageList.Items.Refresh();
        ClearDiagramImageStatus();
        return true;
    }

    private void RefreshDiagramImageSortOrders()
    {
        for (int index = 0; index < _diagramImages.Count; index++)
        {
            _diagramImages[index].SortOrder = index + 1;
        }

        DiagramImageList.Items.Refresh();
    }

    private void SetDiagramImageError(string message)
    {
        SetDiagramImageStatus(message, isError: true);
    }

    private void SetDiagramImageStatus(string message, bool isError)
    {
        DiagramImageValidationText.Foreground = isError ? Brushes.Firebrick : Brushes.DarkGreen;
        DiagramImageValidationText.Text = message;
    }

    private void ClearDiagramImageStatus()
    {
        DiagramImageValidationText.Foreground = Brushes.Firebrick;
        DiagramImageValidationText.Text = string.Empty;
    }

    private static string CreateUniqueDiagramImageId(string? candidateId, HashSet<string> usedIds)
    {
        string id = string.IsNullOrWhiteSpace(candidateId)
            ? Guid.NewGuid().ToString("N")
            : candidateId.Trim();

        while (!usedIds.Add(id))
        {
            id = Guid.NewGuid().ToString("N");
        }

        return id;
    }

    private static string CreateUniqueDiagramImageName(string? candidateName, HashSet<string> usedNames)
    {
        string baseName = string.IsNullOrWhiteSpace(candidateName)
            ? "Imported Image"
            : candidateName.Trim();
        string name = baseName;
        int suffix = 2;

        while (!usedNames.Add(name))
        {
            name = $"{baseName} ({suffix})";
            suffix++;
        }

        return name;
    }

    private static bool IsDiagramImageRegexValid(string? regex)
    {
        if (string.IsNullOrWhiteSpace(regex))
        {
            return true;
        }

        try
        {
            _ = new Regex(regex);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void UpdatePreview(string? colorText)
    {
        // The selected row and color picker now provide the visual preview.
    }

    private void UpdateHighlightPreview()
    {
        if (!TryNormalizeColor(HighlightColorTextBox.Text, out _, out MediaColor color))
        {
            HighlightPreviewSwatch.Background = Brushes.Transparent;
            HighlightPreviewText.Foreground = Brushes.Black;
        }
        else
        {
            var brush = new SolidColorBrush(color);
            HighlightPreviewSwatch.Background = brush;
            HighlightPreviewText.Foreground = brush;
        }

        HighlightPreviewText.FontWeight = HighlightBoldCheckBox.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        HighlightPreviewText.FontStyle = HighlightItalicCheckBox.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
        HighlightPreviewText.TextDecorations = HighlightUnderlineCheckBox.IsChecked == true
            ? TextDecorations.Underline
            : new TextDecorationCollection();
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

    private static BitmapImage? CreateImageSource(string? imageDataBase64)
    {
        if (string.IsNullOrWhiteSpace(imageDataBase64))
        {
            return null;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(imageDataBase64);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static bool TryNormalizeColor(string? colorText, out string normalized, out MediaColor color)
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

    private static bool TryNormalizeConnectionString(string? connectionString, out string normalized, out string validationMessage)
    {
        normalized = string.Empty;
        validationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            validationMessage = "Connection string cannot be blank.";
            return false;
        }

        try
        {
            normalized = SqlServerConnectionOptions.FromConnectionString(connectionString.Trim()).ConnectionString;
            return true;
        }
        catch (ArgumentException ex)
        {
            validationMessage = $"Invalid connection string: {ex.Message}";
            return false;
        }
    }
}
