using System.Windows;

namespace Surf2;

public enum PortalTargetChoice
{
    None,
    PlaceNew,
    SelectExisting
}

public partial class PortalTargetChoiceWindow : Window
{
    public PortalTargetChoiceWindow(string targetDiagramName, bool hasUnpairedPortals)
    {
        InitializeComponent();
        PromptTextBlock.Text = $"Pair this portal in '{targetDiagramName}'.";
        SelectExistingButton.IsEnabled = hasUnpairedPortals;
    }

    public PortalTargetChoice Choice { get; private set; } = PortalTargetChoice.None;

    private void PlaceNewButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = PortalTargetChoice.PlaceNew;
        DialogResult = true;
        Close();
    }

    private void SelectExistingButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = PortalTargetChoice.SelectExisting;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
