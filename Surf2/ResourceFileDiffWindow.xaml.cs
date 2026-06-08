using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiffPlex.DiffBuilder.Model;
using Surf2.Models;
using Surf2.Services;

namespace Surf2;

public partial class ResourceFileDiffWindow : Window
{
    private sealed record DiffAnchor(int LeftLineNumber, int RightLineNumber);

    private readonly string _leftContent;
    private readonly string _rightContent;
    private readonly string _leftFileNameSeed;
    private readonly string _rightFileNameSeed;
    private readonly List<DiffAnchor> _anchors = [];

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
        _leftFileNameSeed = left.DisplayName;
        _rightFileNameSeed = right.DisplayName;
        Title = $"Compare {left.DisplayName} and {right.DisplayName}";
        HeaderText.Text = $"{left.TypeDisplay}: {left.DisplayName}  <->  {right.DisplayName}";
        DiffViewer.OldTextHeader = left.DisplayName;
        DiffViewer.NewTextHeader = right.DisplayName;
        IgnoreWhitespaceCheckBox.IsChecked = ignoreWhitespaceByDefault;
        IgnoreCaseCheckBox.IsChecked = ignoreCaseByDefault;
        ApplyDiffText();
        UpdateAnchorStatusText();
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

    private void CopyLeftButton_Click(object sender, RoutedEventArgs e)
    {
        CopyTextToClipboard(_leftContent, "left");
    }

    private void CopyLeftAsTextFileButton_Click(object sender, RoutedEventArgs e)
    {
        CopyTextFileToClipboard(_leftContent, _leftFileNameSeed, "left");
    }

    private void CopyRightButton_Click(object sender, RoutedEventArgs e)
    {
        CopyTextToClipboard(_rightContent, "right");
    }

    private void CopyRightAsTextFileButton_Click(object sender, RoutedEventArgs e)
    {
        CopyTextFileToClipboard(_rightContent, _rightFileNameSeed, "right");
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

    private void CopyTextToClipboard(string content, string sideName)
    {
        try
        {
            TextClipboardService.CopyText(content);
            AnchorStatusTextBlock.Text = $"Copied {sideName} text to clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Unable to copy {sideName} text to the clipboard.\n\n{ex.Message}",
                "Copy failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CopyTextFileToClipboard(string content, string fileNameSeed, string sideName)
    {
        try
        {
            string filePath = TextClipboardService.CopyAsTxtFile(content, fileNameSeed);
            AnchorStatusTextBlock.Text = $"Copied {sideName} .txt file to clipboard: {Path.GetFileName(filePath)}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Unable to copy {sideName} .txt file to the clipboard.\n\n{ex.Message}",
                "Copy failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void DiffViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!DiffViewer.IsSideBySide)
        {
            AnchorStatusTextBlock.Text = "Switch to split view to click-select anchor lines.";
            return;
        }

        if (FindDataContext<DiffPiece>(e.OriginalSource as DependencyObject) is not { Position: int position } piece ||
            position < 0 ||
            IsAnchorMarker(piece.Text))
        {
            return;
        }

        int displayLineNumber = position + 1;
        bool isLeftLine = e.GetPosition(DiffViewer).X < DiffViewer.ActualWidth / 2;
        int lineNumber = ToOriginalLineNumber(displayLineNumber, isLeftLine);
        if (isLeftLine)
        {
            LeftAnchorLineTextBox.Text = lineNumber.ToString();
        }
        else
        {
            RightAnchorLineTextBox.Text = lineNumber.ToString();
        }

        UpdateAnchorStatusText();
    }

    private void AnchorLineTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        AddAnchorFromInputs();
    }

    private void AlignSelectedLinesButton_Click(object sender, RoutedEventArgs e)
    {
        AddAnchorFromInputs();
    }

    private void ClearAnchorsButton_Click(object sender, RoutedEventArgs e)
    {
        _anchors.Clear();
        ApplyDiffText();
        DiffViewer.Refresh();
        UpdateAnchorStatusText();
    }

    private void AddAnchorFromInputs()
    {
        IReadOnlyList<string> leftLines = NormalizeContentLines(
            _leftContent,
            IgnoreWhitespaceCheckBox.IsChecked == true,
            IgnoreCaseCheckBox.IsChecked == true);
        IReadOnlyList<string> rightLines = NormalizeContentLines(
            _rightContent,
            IgnoreWhitespaceCheckBox.IsChecked == true,
            IgnoreCaseCheckBox.IsChecked == true);

        if (!TryReadAnchorLine(LeftAnchorLineTextBox.Text, leftLines.Count, "left", out int leftLineNumber) ||
            !TryReadAnchorLine(RightAnchorLineTextBox.Text, rightLines.Count, "right", out int rightLineNumber))
        {
            return;
        }

        var candidateAnchors = _anchors
            .Where(anchor => anchor.LeftLineNumber != leftLineNumber &&
                             anchor.RightLineNumber != rightLineNumber)
            .Append(new DiffAnchor(leftLineNumber, rightLineNumber))
            .OrderBy(anchor => anchor.LeftLineNumber)
            .ToList();

        if (!AreAnchorsMonotonic(candidateAnchors))
        {
            AnchorStatusTextBlock.Text = "Anchors cannot cross. Pick a left/right pair that follows the existing anchor order.";
            return;
        }

        _anchors.Clear();
        _anchors.AddRange(candidateAnchors);
        ApplyDiffText();
        DiffViewer.Refresh();
        DiffViewer.GoTo(ToDisplayLineNumber(leftLineNumber, isLeft: true) - 1, isLeftLine: true);
        UpdateAnchorStatusText();
    }

    private void ApplyDiffText()
    {
        bool ignoreWhitespace = IgnoreWhitespaceCheckBox.IsChecked == true;
        bool ignoreCase = IgnoreCaseCheckBox.IsChecked == true;
        DiffViewer.IgnoreWhiteSpace = ignoreWhitespace;
        DiffViewer.IgnoreCase = ignoreCase;
        string leftText = NormalizeContent(_leftContent, ignoreWhitespace, ignoreCase, isLeft: true);
        string rightText = NormalizeContent(_rightContent, ignoreWhitespace, ignoreCase, isLeft: false);

        DiffViewer.OldText = string.Empty;
        DiffViewer.NewText = string.Empty;
        DiffViewer.OldText = leftText;
        DiffViewer.NewText = rightText;
    }

    private string NormalizeContent(string content, bool ignoreWhitespace, bool ignoreCase, bool isLeft)
    {
        IReadOnlyList<string> lines = NormalizeContentLines(content, ignoreWhitespace, ignoreCase);
        if (_anchors.Count == 0)
        {
            return string.Join(Environment.NewLine, lines);
        }

        return string.Join(Environment.NewLine, AddAnchorMarkers(lines, isLeft));
    }

    private static IReadOnlyList<string> NormalizeContentLines(string content, bool ignoreWhitespace, bool ignoreCase)
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

        return lines.ToList();
    }

    private IEnumerable<string> AddAnchorMarkers(IReadOnlyList<string> lines, bool isLeft)
    {
        var anchorsByLine = _anchors
            .Select((anchor, index) => new
            {
                LineNumber = isLeft ? anchor.LeftLineNumber : anchor.RightLineNumber,
                AnchorNumber = index + 1
            })
            .Where(anchor => anchor.LineNumber >= 1 && anchor.LineNumber <= lines.Count)
            .OrderBy(anchor => anchor.LineNumber)
            .ToList();

        int anchorIndex = 0;
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            int lineNumber = lineIndex + 1;
            while (anchorIndex < anchorsByLine.Count &&
                   anchorsByLine[anchorIndex].LineNumber == lineNumber)
            {
                yield return CreateAnchorMarker(anchorsByLine[anchorIndex].AnchorNumber);
                anchorIndex++;
            }

            yield return lines[lineIndex];
        }
    }

    private static string CreateAnchorMarker(int anchorNumber)
    {
        return $"---- Surf2 alignment anchor {anchorNumber} ----";
    }

    private static bool IsAnchorMarker(string? text)
    {
        return text?.StartsWith("---- Surf2 alignment anchor ", StringComparison.Ordinal) == true;
    }

    private bool TryReadAnchorLine(string text, int lineCount, string sideName, out int lineNumber)
    {
        lineNumber = 0;
        if (!int.TryParse(text.Trim(), out lineNumber))
        {
            AnchorStatusTextBlock.Text = $"Enter a {sideName} line number.";
            return false;
        }

        if (lineNumber < 1 || lineNumber > lineCount)
        {
            AnchorStatusTextBlock.Text = $"The {sideName} line must be between 1 and {lineCount}.";
            return false;
        }

        return true;
    }

    private static bool AreAnchorsMonotonic(IReadOnlyList<DiffAnchor> anchors)
    {
        int lastRightLine = 0;
        foreach (DiffAnchor anchor in anchors.OrderBy(anchor => anchor.LeftLineNumber))
        {
            if (anchor.RightLineNumber <= lastRightLine)
            {
                return false;
            }

            lastRightLine = anchor.RightLineNumber;
        }

        return true;
    }

    private int ToOriginalLineNumber(int displayLineNumber, bool isLeft)
    {
        int originalLineNumber = displayLineNumber;
        int insertedMarkers = 0;
        foreach (int anchorLineNumber in GetAnchorLineNumbers(isLeft))
        {
            int markerDisplayLineNumber = anchorLineNumber + insertedMarkers;
            if (markerDisplayLineNumber >= displayLineNumber)
            {
                break;
            }

            originalLineNumber--;
            insertedMarkers++;
        }

        return Math.Max(1, originalLineNumber);
    }

    private int ToDisplayLineNumber(int originalLineNumber, bool isLeft)
    {
        return originalLineNumber + GetAnchorLineNumbers(isLeft).Count(lineNumber => lineNumber <= originalLineNumber);
    }

    private IEnumerable<int> GetAnchorLineNumbers(bool isLeft)
    {
        return _anchors
            .Select(anchor => isLeft ? anchor.LeftLineNumber : anchor.RightLineNumber)
            .OrderBy(lineNumber => lineNumber);
    }

    private void UpdateAnchorStatusText()
    {
        string selectedLeft = string.IsNullOrWhiteSpace(LeftAnchorLineTextBox.Text)
            ? "none"
            : LeftAnchorLineTextBox.Text.Trim();
        string selectedRight = string.IsNullOrWhiteSpace(RightAnchorLineTextBox.Text)
            ? "none"
            : RightAnchorLineTextBox.Text.Trim();

        if (_anchors.Count == 0)
        {
            AnchorStatusTextBlock.Text = $"Selected L {selectedLeft}, R {selectedRight}. No anchors.";
            return;
        }

        string anchorText = string.Join(
            "; ",
            _anchors.Select((anchor, index) => $"{index + 1}: L{anchor.LeftLineNumber}<->R{anchor.RightLineNumber}"));
        AnchorStatusTextBlock.Text = $"Selected L {selectedLeft}, R {selectedRight}. Anchors {anchorText}.";
    }

    private static T? FindDataContext<T>(DependencyObject? source)
        where T : class
    {
        DependencyObject? current = source;
        while (current != null)
        {
            if (current is FrameworkElement { DataContext: T dataContext })
            {
                return dataContext;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
