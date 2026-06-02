using System.Windows;
using System.Windows.Input;
using Surf2.Models;

namespace Surf2;

public partial class ResourceFileDiffWindow : Window
{
    private readonly string _leftContent;
    private readonly string _rightContent;

    public ResourceFileDiffWindow(
        ComparisonResource left,
        ComparisonResource right,
        string leftContent,
        string rightContent,
        bool ignoreWhitespaceByDefault,
        bool ignoreCaseByDefault)
    {
        InitializeComponent();
        _leftContent = leftContent;
        _rightContent = rightContent;
        Title = $"Compare {left.DisplayName} and {right.DisplayName}";
        HeaderText.Text = $"{left.TypeDisplay}: {left.DisplayName}  <->  {right.DisplayName}";
        DiffViewer.OldTextHeader = left.DisplayName;
        DiffViewer.NewTextHeader = right.DisplayName;
        IgnoreWhitespaceCheckBox.IsChecked = ignoreWhitespaceByDefault;
        IgnoreCaseCheckBox.IsChecked = ignoreCaseByDefault;
        ApplyDiffText();
    }

    private void DiffOptionCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        ApplyDiffText();
        DiffViewer.Refresh();
    }

    private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
    {
        DiffViewer.IsSideBySide = !DiffViewer.IsSideBySide;
        SwitchModeButton.Content = DiffViewer.IsSideBySide ? "Unified" : "Split";
    }

    private void PreviousDiffButton_Click(object sender, RoutedEventArgs e)
    {
        DiffViewer.PreviousDiff();
    }

    private void NextDiffButton_Click(object sender, RoutedEventArgs e)
    {
        DiffViewer.NextDiff();
    }

    private void GoToButton_Click(object sender, RoutedEventArgs e)
    {
        GoToLine();
    }

    private void GoToLineTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        GoToLine();
    }

    private void GoToLine()
    {
        if (!int.TryParse(GoToLineTextBox.Text.Trim(), out int lineNumber) || lineNumber <= 0)
        {
            return;
        }

        DiffViewer.GoTo(lineNumber - 1, isLeftLine: true);
    }

    private void ApplyDiffText()
    {
        bool ignoreWhitespace = IgnoreWhitespaceCheckBox.IsChecked == true;
        bool ignoreCase = IgnoreCaseCheckBox.IsChecked == true;
        DiffViewer.IgnoreWhiteSpace = ignoreWhitespace;
        DiffViewer.IgnoreCase = ignoreCase;
        string leftText = NormalizeContent(_leftContent, ignoreWhitespace, ignoreCase);
        string rightText = NormalizeContent(_rightContent, ignoreWhitespace, ignoreCase);

        DiffViewer.OldText = string.Empty;
        DiffViewer.NewText = string.Empty;
        DiffViewer.OldText = leftText;
        DiffViewer.NewText = rightText;
    }

    private static string NormalizeContent(string content, bool ignoreWhitespace, bool ignoreCase)
    {
        IEnumerable<string> lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        if (ignoreWhitespace)
        {
            lines = lines
                .Select(line => line.TrimEnd())
                .Where(line => line.Trim().Length > 0);
        }

        if (ignoreCase)
        {
            lines = lines.Select(line => line.ToUpperInvariant());
        }

        return string.Join(Environment.NewLine, lines);
    }
}
