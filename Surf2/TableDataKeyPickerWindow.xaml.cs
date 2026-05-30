using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Surf2;

public partial class TableDataKeyPickerWindow : Window
{
    private readonly ObservableCollection<SelectableColumn> _columns;

    public TableDataKeyPickerWindow(IEnumerable<string> columns)
    {
        InitializeComponent();
        _columns = new ObservableCollection<SelectableColumn>(
            columns.Select(column => new SelectableColumn(column)));
        ColumnsListBox.ItemsSource = _columns;
        StatusTextBlock.Text = $"{_columns.Count} common column(s).";
    }

    public IReadOnlyList<string> SelectedColumns { get; private set; } = [];

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedColumns = _columns
            .Where(column => column.IsSelected)
            .Select(column => column.Name)
            .ToList();
        if (SelectedColumns.Count == 0)
        {
            StatusTextBlock.Text = "Select at least one key column.";
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class SelectableColumn : INotifyPropertyChanged
    {
        private bool _isSelected;

        public SelectableColumn(string name)
        {
            Name = name;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
