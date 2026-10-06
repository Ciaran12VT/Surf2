using System.Windows;
using Surf2.Controls;

namespace Surf2;

public partial class DiagramImageDetailsWindow : Window
{
    public DiagramImageDetailsWindow(string labelText, double labelFontSize = DiagramTextStyle.DefaultFontSize)
    {
        InitializeComponent();
        LabelTextBox.Text = labelText;
        LabelFontSize = DiagramTextStyle.NormalizeFontSize(labelFontSize);
        LabelFontSizeComboBox.ItemsSource = DiagramTextStyle.FontSizes.Append(LabelFontSize).Distinct().Order().ToList();
        LabelFontSizeComboBox.SelectedItem = LabelFontSize;
    }

    public string LabelText { get; private set; } = string.Empty;

    public double LabelFontSize { get; private set; } = DiagramTextStyle.DefaultFontSize;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        LabelText = LabelTextBox.Text;
        LabelFontSize = (double)LabelFontSizeComboBox.SelectedItem;
        DialogResult = true;
        Close();
    }
}
