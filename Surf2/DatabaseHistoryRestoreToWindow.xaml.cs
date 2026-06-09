using System.Windows;

namespace Surf2;

public partial class DatabaseHistoryRestoreToWindow : Window
{
    public DatabaseHistoryRestoreToWindow(
        IEnumerable<DatabaseRestoreTargetOption> targets,
        DatabaseRestoreTargetOption? defaultTarget,
        string defaultName,
        string content,
        bool canEditContent)
    {
        InitializeComponent();
        List<DatabaseRestoreTargetOption> targetList = targets.ToList();
        TargetComboBox.ItemsSource = targetList;
        TargetComboBox.SelectedItem = defaultTarget ?? targetList.FirstOrDefault();
        NameTextBox.Text = defaultName;
        NameTextBox.SelectAll();
        ContentTextBox.Text = content;
        ContentTextBox.IsReadOnly = !canEditContent;
        NameTextBox.Focus();
    }

    public DatabaseRestoreTargetOption? SelectedTarget { get; private set; }

    public string ResourceName { get; private set; } = string.Empty;

    public string EditedContent { get; private set; } = string.Empty;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (TargetComboBox.SelectedItem is not DatabaseRestoreTargetOption target)
        {
            ValidationTextBlock.Text = "Select a target database.";
            return;
        }

        string name = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationTextBlock.Text = "Name cannot be blank.";
            return;
        }

        SelectedTarget = target;
        ResourceName = name;
        EditedContent = ContentTextBox.Text;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public sealed record DatabaseRestoreTargetOption(string DisplayName, string SnapshotId);
