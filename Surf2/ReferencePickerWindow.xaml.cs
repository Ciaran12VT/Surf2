using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Surf2.Models;

namespace Surf2;

public partial class ReferencePickerWindow : Window
{
    public ReferencePickerWindow(string token, IReadOnlyList<ReferenceEntity> references, string sourceFilePath)
    {
        InitializeComponent();

        List<ReferencePickerItem> items = references
            .Select(reference => new ReferencePickerItem(reference, IsSameFile(reference.FilePath, sourceFilePath)))
            .OrderByDescending(item => item.IsSameSourceFile)
            .ToList();

        PromptText.Text = $"Choose reference for '{token}'";
        ReferenceList.ItemsSource = items;

        if (items.Count > 0)
        {
            ReferenceList.SelectedIndex = 0;
        }
    }

    public ReferenceEntity? SelectedReference { get; private set; }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        SelectReference();
    }

    private void ReferenceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ListBoxItem? item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is ReferencePickerItem pickerItem)
        {
            ReferenceList.SelectedItem = pickerItem;
        }

        SelectReference();
        e.Handled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SelectReference()
    {
        if (ReferenceList.SelectedItem is not ReferencePickerItem item)
        {
            return;
        }

        SelectedReference = item.Reference;
        DialogResult = true;
        Close();
    }

    private static bool IsSameFile(string referenceFilePath, string sourceFilePath)
    {
        return !string.IsNullOrWhiteSpace(referenceFilePath) &&
               !string.IsNullOrWhiteSpace(sourceFilePath) &&
               string.Equals(referenceFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase);
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = GetDependencyParent(current);
        }

        return null;
    }

    private static DependencyObject? GetDependencyParent(DependencyObject dependencyObject)
    {
        try
        {
            DependencyObject? visualParent = VisualTreeHelper.GetParent(dependencyObject);
            if (visualParent != null)
            {
                return visualParent;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return LogicalTreeHelper.GetParent(dependencyObject);
    }

    private sealed class ReferencePickerItem
    {
        public ReferencePickerItem(ReferenceEntity reference, bool isSameSourceFile)
        {
            Reference = reference;
            IsSameSourceFile = isSameSourceFile;
        }

        public ReferenceEntity Reference { get; }

        public bool IsSameSourceFile { get; }

        public string DisplayLabel => Reference.DisplayLabel;

        public string FilePath => Reference.FilePath;
    }
}
