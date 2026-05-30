using System.Windows;
using Surf2.Models;

namespace Surf2;

public partial class TableDataDiffWindow : Window
{
    public TableDataDiffWindow(TableDataDiffResult result)
    {
        InitializeComponent();
        Title = $"Compare {result.Left.DisplayName} and {result.Right.DisplayName}";
        HeaderText.Text = $"Table data: {result.Left.DisplayName}  <->  {result.Right.DisplayName}";
        RowsDataGrid.ItemsSource = result.Rows;

        int added = result.Rows.Count(row => row.Status == TableDataDiffStatus.Added);
        int removed = result.Rows.Count(row => row.Status == TableDataDiffStatus.Removed);
        int altered = result.Rows.Count(row => row.Status == TableDataDiffStatus.Altered);
        int identical = result.Rows.Count(row => row.Status == TableDataDiffStatus.Identical);
        StatusTextBlock.Text = $"Key: {string.Join(", ", result.KeyColumns)}. {identical} identical, {altered} altered, {added} added, {removed} removed.";
    }
}
