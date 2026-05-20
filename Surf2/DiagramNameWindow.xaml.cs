using System.Windows;

namespace Surf2;

public partial class DiagramNameWindow : Window
{
    public DiagramNameWindow(string initialName, string prompt = "Diagram name", string title = "Save Diagram")
    {
        InitializeComponent();
        Title = title;
        PromptTextBlock.Text = prompt;
        NameTextBox.Text = initialName;
        NameTextBox.SelectAll();
        NameTextBox.Focus();
    }

    public string DiagramName { get; private set; } = string.Empty;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string name = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationText.Text = "Name cannot be blank.";
            return;
        }

        DiagramName = name;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
