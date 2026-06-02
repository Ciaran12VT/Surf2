using System.Windows;
using System.Windows.Controls;
using Surf2.Models;

namespace Surf2;

public partial class ResourceCompareWithWindow : Window
{
    private readonly IReadOnlyList<CompareWithCollectionOption> _collections;

    public ResourceCompareWithWindow(
        string leftCollectionName,
        string rightCollectionName,
        IReadOnlyList<ResourceComparisonDocument> leftDocuments,
        IReadOnlyList<ResourceComparisonDocument> rightDocuments,
        ResourceComparisonDocument? defaultLeft,
        ResourceComparisonDocument? defaultRight)
    {
        InitializeComponent();
        _collections =
        [
            new CompareWithCollectionOption("Collection 1", leftCollectionName, leftDocuments),
            new CompareWithCollectionOption("Collection 2", rightCollectionName, rightDocuments)
        ];

        Collection1ComboBox.ItemsSource = _collections;
        Collection2ComboBox.ItemsSource = _collections;
        Collection1ComboBox.SelectedItem = _collections[0];
        Collection2ComboBox.SelectedItem = _collections[1];
        SetCompareItems(Compare1ComboBox, _collections[0], defaultLeft);
        SetCompareItems(Compare2ComboBox, _collections[1], defaultRight);
        UpdateStatus();
    }

    public ResourceComparisonDocument? SelectedDocument1 { get; private set; }

    public ResourceComparisonDocument? SelectedDocument2 { get; private set; }

    private void CollectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender == Collection1ComboBox && Collection1ComboBox.SelectedItem is CompareWithCollectionOption collection1)
        {
            SetCompareItems(Compare1ComboBox, collection1, null);
        }

        if (sender == Collection2ComboBox && Collection2ComboBox.SelectedItem is CompareWithCollectionOption collection2)
        {
            SetCompareItems(Compare2ComboBox, collection2, null);
        }

        UpdateStatus();
    }

    private static void SetCompareItems(
        ComboBox comboBox,
        CompareWithCollectionOption collection,
        ResourceComparisonDocument? preferredDocument)
    {
        List<CompareWithDocumentOption> options = collection.Documents
            .Select(document => new CompareWithDocumentOption(document))
            .ToList();

        comboBox.ItemsSource = options;
        CompareWithDocumentOption? selected = preferredDocument == null
            ? options.FirstOrDefault()
            : options.FirstOrDefault(option => ReferenceEquals(option.Document, preferredDocument)) ??
              options.FirstOrDefault(option =>
                  string.Equals(option.Document.RelativePath, preferredDocument.RelativePath, StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedItem = selected ?? options.FirstOrDefault();
    }

    private void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedDocument1 = (Compare1ComboBox.SelectedItem as CompareWithDocumentOption)?.Document;
        SelectedDocument2 = (Compare2ComboBox.SelectedItem as CompareWithDocumentOption)?.Document;
        if (SelectedDocument1 == null || SelectedDocument2 == null)
        {
            StatusTextBlock.Text = "Select two resources to compare.";
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateStatus()
    {
        StatusTextBlock.Text = "Choose any two resources from either collection.";
    }

    private sealed record CompareWithCollectionOption(
        string Label,
        string Name,
        IReadOnlyList<ResourceComparisonDocument> Documents)
    {
        public override string ToString() => Name;
    }

    private sealed record CompareWithDocumentOption(ResourceComparisonDocument Document)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Document.RelativePath)
            ? Document.DisplayName
            : $"{Document.DisplayName} ({Document.RelativePath})";
    }
}
