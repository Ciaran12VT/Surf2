using System.Windows;

namespace Surf2;

public partial class DiagramImageDetailsWindow : Window
{
    public DiagramImageDetailsWindow(string labelText)
    {
        InitializeComponent();
        LabelTextBox.Text = labelText;
    }

    public string LabelText { get; private set; } = string.Empty;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        LabelText = LabelTextBox.Text;
        DialogResult = true;
        Close();
    }
}
