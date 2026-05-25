using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Surf2.Models;
using Surf2.Services;

namespace Surf2;

public partial class DatabaseEntityEditWindow : Window
{
    private readonly DatabaseEntityEditKind _kind;
    private readonly ObservableCollection<DatabaseEntityEditItem> _items = [];
    private readonly DatabaseMetadataImportService _importService = new();
    private readonly DatabaseMetadataQueryService _queryService = new();

    public DatabaseEntityEditWindow(DatabaseMetadataSnapshot snapshot, DatabaseEntityEditKind kind)
    {
        InitializeComponent();

        Snapshot = snapshot.Clone();
        _importService.NormalizeSnapshot(Snapshot);
        _kind = kind;
        Title = $"Edit {GetKindLabel(kind)}";
        TitleTextBlock.Text = $"Edit {GetKindLabel(kind)}";
        EntityList.ItemsSource = _items;

        foreach (DatabaseEntityEditItem item in CreateItems(Snapshot, kind))
        {
            _items.Add(item);
        }

        StatusTextBlock.Text = _items.Count == 0
            ? "No entities of this type are currently loaded."
            : "Copy a row query, run it against the database, copy the result set, then update that row from the clipboard.";
    }

    public DatabaseMetadataSnapshot Snapshot { get; }

    public int UpdatedCount => _items.Count(item => item.IsEdited);

    private void CopyEntityQueryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DatabaseEntityEditItem item)
        {
            return;
        }

        string query = _queryService.CreateEntityQuery(
            _kind,
            item.SchemaName,
            item.EntityName,
            item.TableName,
            item.ColumnName,
            item.ConstraintName);
        Clipboard.SetText(query);
        StatusTextBlock.Text = $"Copied query for {item.DisplayName}.";
    }

    private void PasteEntityUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DatabaseEntityEditItem item)
        {
            return;
        }

        string clipboardText = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            StatusTextBlock.Text = "Clipboard is empty.";
            return;
        }

        DatabaseImportResult result = _importService.ParseClipboardResult(clipboardText, Snapshot.DisplayName);
        if (result.Snapshot == null)
        {
            StatusTextBlock.Text = "No database metadata could be parsed from the clipboard.";
            return;
        }

        if (!ApplyImportedEntity(item, result.Snapshot))
        {
            StatusTextBlock.Text = $"The pasted result did not contain {item.DisplayName}.";
            MessagesTextBox.Text = string.Join(Environment.NewLine, result.Warnings);
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.Snapshot.DatabaseName))
        {
            Snapshot.DatabaseName = result.Snapshot.DatabaseName;
        }

        _importService.NormalizeSnapshot(Snapshot);
        item.IsEdited = true;
        item.Status = "Edited";
        MessagesTextBox.Text = result.Warnings.Count == 0
            ? $"Updated {item.DisplayName}."
            : $"Updated {item.DisplayName} with {result.Warnings.Count} warning(s):{Environment.NewLine}{string.Join(Environment.NewLine, result.Warnings)}";
        StatusTextBlock.Text = $"Updated {item.DisplayName}.";
        EntityList.Items.Refresh();
    }

    private bool ApplyImportedEntity(DatabaseEntityEditItem item, DatabaseMetadataSnapshot imported)
    {
        return _kind switch
        {
            DatabaseEntityEditKind.StoredProcedures or
                DatabaseEntityEditKind.Views or
                DatabaseEntityEditKind.Functions or
                DatabaseEntityEditKind.Triggers => ApplyObjectUpdate(item, imported),
            DatabaseEntityEditKind.Tables => ApplyTableUpdate(item, imported, includeDataSet: false),
            DatabaseEntityEditKind.Fields => ApplyColumnUpdate(item, imported),
            DatabaseEntityEditKind.PrimaryKeys => ApplyPrimaryKeyUpdate(item, imported),
            DatabaseEntityEditKind.FullDataTables => ApplyTableUpdate(item, imported, includeDataSet: true),
            _ => false
        };
    }

    private bool ApplyObjectUpdate(DatabaseEntityEditItem item, DatabaseMetadataSnapshot imported)
    {
        SqlDatabaseObject? importedObject = imported.Objects.FirstOrDefault(candidate =>
            candidate.Kind == item.ObjectKind &&
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ObjectName, item.EntityName, StringComparison.OrdinalIgnoreCase));
        if (importedObject == null)
        {
            return false;
        }

        Snapshot.Objects.RemoveAll(candidate =>
            candidate.Kind == item.ObjectKind &&
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ObjectName, item.EntityName, StringComparison.OrdinalIgnoreCase));
        Snapshot.Objects.Add(importedObject);
        return true;
    }

    private bool ApplyTableUpdate(DatabaseEntityEditItem item, DatabaseMetadataSnapshot imported, bool includeDataSet)
    {
        SqlTable? importedTable = imported.Tables.FirstOrDefault(candidate =>
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, item.EntityName, StringComparison.OrdinalIgnoreCase));
        if (importedTable == null)
        {
            return false;
        }

        Snapshot.Tables.RemoveAll(candidate =>
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, item.EntityName, StringComparison.OrdinalIgnoreCase));
        Snapshot.Tables.Add(importedTable);

        Snapshot.Columns.RemoveAll(candidate =>
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, item.EntityName, StringComparison.OrdinalIgnoreCase));
        Snapshot.Columns.AddRange(imported.Columns.Where(candidate =>
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, item.EntityName, StringComparison.OrdinalIgnoreCase)));

        Snapshot.PrimaryKeys.RemoveAll(candidate =>
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, item.EntityName, StringComparison.OrdinalIgnoreCase));
        Snapshot.PrimaryKeys.AddRange(imported.PrimaryKeys.Where(candidate =>
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, item.EntityName, StringComparison.OrdinalIgnoreCase)));

        if (includeDataSet)
        {
            SqlTableDataSet? importedDataSet = imported.TableDataSets.FirstOrDefault(candidate =>
                string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.TableName, item.EntityName, StringComparison.OrdinalIgnoreCase));
            if (importedDataSet == null)
            {
                return false;
            }

            Snapshot.TableDataSets.RemoveAll(candidate =>
                string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.TableName, item.EntityName, StringComparison.OrdinalIgnoreCase));
            Snapshot.TableDataSets.Add(importedDataSet);

            string fullName = SqlName.FormatPlainMultipartName(item.SchemaName, item.EntityName);
            Snapshot.FullDataTableNames ??= [];
            if (!Snapshot.FullDataTableNames.Contains(fullName, StringComparer.OrdinalIgnoreCase))
            {
                Snapshot.FullDataTableNames.Add(fullName);
            }
        }

        return true;
    }

    private bool ApplyColumnUpdate(DatabaseEntityEditItem item, DatabaseMetadataSnapshot imported)
    {
        SqlColumn? importedColumn = imported.Columns.FirstOrDefault(candidate =>
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, item.TableName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ColumnName, item.ColumnName, StringComparison.OrdinalIgnoreCase));
        if (importedColumn == null)
        {
            return false;
        }

        Snapshot.Columns.RemoveAll(candidate =>
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, item.TableName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ColumnName, item.ColumnName, StringComparison.OrdinalIgnoreCase));
        Snapshot.Columns.Add(importedColumn);
        return true;
    }

    private bool ApplyPrimaryKeyUpdate(DatabaseEntityEditItem item, DatabaseMetadataSnapshot imported)
    {
        List<SqlPrimaryKeyColumn> importedPrimaryKey = imported.PrimaryKeys
            .Where(candidate =>
                string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.TableName, item.TableName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ConstraintName, item.ConstraintName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (importedPrimaryKey.Count == 0)
        {
            return false;
        }

        Snapshot.PrimaryKeys.RemoveAll(candidate =>
            string.Equals(candidate.SchemaName, item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, item.TableName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ConstraintName, item.ConstraintName, StringComparison.OrdinalIgnoreCase));
        Snapshot.PrimaryKeys.AddRange(importedPrimaryKey);
        return true;
    }

    private static IEnumerable<DatabaseEntityEditItem> CreateItems(DatabaseMetadataSnapshot snapshot, DatabaseEntityEditKind kind)
    {
        return kind switch
        {
            DatabaseEntityEditKind.StoredProcedures => CreateObjectItems(snapshot, SqlDatabaseObjectKind.StoredProcedure),
            DatabaseEntityEditKind.Views => CreateObjectItems(snapshot, SqlDatabaseObjectKind.View),
            DatabaseEntityEditKind.Functions => CreateObjectItems(snapshot, SqlDatabaseObjectKind.Function),
            DatabaseEntityEditKind.Triggers => CreateObjectItems(snapshot, SqlDatabaseObjectKind.Trigger),
            DatabaseEntityEditKind.Tables => snapshot.Tables
                .OrderBy(table => table.SchemaName)
                .ThenBy(table => table.TableName)
                .Select(table => DatabaseEntityEditItem.ForTable(table, fullDataOnly: false)),
            DatabaseEntityEditKind.Fields => snapshot.Columns
                .OrderBy(column => column.SchemaName)
                .ThenBy(column => column.TableName)
                .ThenBy(column => column.Ordinal)
                .Select(DatabaseEntityEditItem.ForColumn),
            DatabaseEntityEditKind.PrimaryKeys => snapshot.PrimaryKeys
                .GroupBy(
                    primaryKey => $"{primaryKey.SchemaName}|{primaryKey.TableName}|{primaryKey.ConstraintName}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.First().SchemaName)
                .ThenBy(group => group.First().TableName)
                .ThenBy(group => group.First().ConstraintName)
                .Select(DatabaseEntityEditItem.ForPrimaryKey),
            DatabaseEntityEditKind.FullDataTables => snapshot.TableDataSets
                .OrderBy(dataSet => dataSet.SchemaName)
                .ThenBy(dataSet => dataSet.TableName)
                .Select(dataSet => DatabaseEntityEditItem.ForDataSet(dataSet)),
            _ => []
        };
    }

    private static IEnumerable<DatabaseEntityEditItem> CreateObjectItems(DatabaseMetadataSnapshot snapshot, SqlDatabaseObjectKind kind)
    {
        return snapshot.Objects
            .Where(databaseObject => databaseObject.Kind == kind)
            .OrderBy(databaseObject => databaseObject.SchemaName)
            .ThenBy(databaseObject => databaseObject.ObjectName)
            .Select(DatabaseEntityEditItem.ForObject);
    }

    private static string GetKindLabel(DatabaseEntityEditKind kind)
    {
        return kind switch
        {
            DatabaseEntityEditKind.StoredProcedures => "Stored Procedures",
            DatabaseEntityEditKind.Views => "Views",
            DatabaseEntityEditKind.Functions => "Functions",
            DatabaseEntityEditKind.Triggers => "Triggers",
            DatabaseEntityEditKind.Tables => "Tables",
            DatabaseEntityEditKind.Fields => "Fields",
            DatabaseEntityEditKind.PrimaryKeys => "Primary Keys",
            DatabaseEntityEditKind.FullDataTables => "Full Data Tables",
            _ => "Entities"
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class DatabaseEntityEditItem : INotifyPropertyChanged
    {
        private bool _isEdited;
        private string _status = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string DisplayName { get; init; } = string.Empty;

        public string Details { get; init; } = string.Empty;

        public string SchemaName { get; init; } = "dbo";

        public string EntityName { get; init; } = string.Empty;

        public string TableName { get; init; } = string.Empty;

        public string ColumnName { get; init; } = string.Empty;

        public string ConstraintName { get; init; } = string.Empty;

        public SqlDatabaseObjectKind ObjectKind { get; init; }

        public bool IsEdited
        {
            get => _isEdited;
            set
            {
                _isEdited = value;
                OnPropertyChanged();
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
            }
        }

        public static DatabaseEntityEditItem ForObject(SqlDatabaseObject databaseObject)
        {
            return new DatabaseEntityEditItem
            {
                DisplayName = databaseObject.FullName,
                Details = databaseObject.TypeDescription,
                SchemaName = databaseObject.SchemaName,
                EntityName = databaseObject.ObjectName,
                ObjectKind = databaseObject.Kind
            };
        }

        public static DatabaseEntityEditItem ForTable(SqlTable table, bool fullDataOnly)
        {
            return new DatabaseEntityEditItem
            {
                DisplayName = table.FullName,
                Details = fullDataOnly && table.HasFullData
                    ? $"{table.FullDataRowCount} row(s)"
                    : "Table metadata",
                SchemaName = table.SchemaName,
                EntityName = table.TableName
            };
        }

        public static DatabaseEntityEditItem ForColumn(SqlColumn column)
        {
            return new DatabaseEntityEditItem
            {
                DisplayName = column.FullName,
                Details = $"{column.DataType} on {column.TableFullName}",
                SchemaName = column.SchemaName,
                EntityName = column.ColumnName,
                TableName = column.TableName,
                ColumnName = column.ColumnName
            };
        }

        public static DatabaseEntityEditItem ForPrimaryKey(IGrouping<string, SqlPrimaryKeyColumn> group)
        {
            SqlPrimaryKeyColumn first = group.First();
            string columns = string.Join(", ", group.OrderBy(key => key.KeyOrdinal).Select(key => key.ColumnName));
            return new DatabaseEntityEditItem
            {
                DisplayName = SqlName.FormatMultipartName(first.SchemaName, first.ConstraintName),
                Details = $"{SqlName.FormatMultipartName(first.SchemaName, first.TableName)} ({columns})",
                SchemaName = first.SchemaName,
                EntityName = first.ConstraintName,
                TableName = first.TableName,
                ConstraintName = first.ConstraintName
            };
        }

        public static DatabaseEntityEditItem ForDataSet(SqlTableDataSet dataSet)
        {
            return new DatabaseEntityEditItem
            {
                DisplayName = dataSet.FullName,
                Details = $"{Math.Max(dataSet.RowCount, dataSet.Rows.Count)} row(s)",
                SchemaName = dataSet.SchemaName,
                EntityName = dataSet.TableName
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
