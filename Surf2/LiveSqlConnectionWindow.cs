using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.SqlClient;
using Surf2.Services.LiveSql;

namespace Surf2;

public sealed class LiveSqlConnectionWindow : Window
{
    private readonly PasswordBox _connection = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _lifetime = new();
    public string ConnectionString { get; private set; } = "";
    public string Server { get; private set; } = "";
    public string Database { get; private set; } = "";
    public LiveSqlConnectionWindow(string? existingConnection = null)
    {
        Title = "Live SQL connection"; Width = 650; Height = 310; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Services.RelationalSnapshots.SnapshotWindowStyles.ThemeWindow(this);
        Services.RelationalSnapshots.SnapshotWindowStyles.ThemeInput(_connection);
        var panel = new StackPanel { Margin = new(18) };
        panel.Children.Add(new TextBlock { Text = "Connect to an existing SQL Server or Azure SQL Database. Surf2 verifies schema and table-data access without changing the source.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Connection string", Margin = new(0, 14, 0, 4) }); _connection.Password = existingConnection ?? ""; panel.Children.Add(_connection);
        var test = new Button { Content = "Test access and connect", Margin = new(0, 12, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false; _status.Text = "Verifying complete access…";
            try
            {
                string value = _connection.Password; var service = new LiveSqlService(value);
                var result = await service.QualifyAsync(_lifetime.Token); result.RequireQualified();
                if (_lifetime.IsCancellationRequested) return;
                ConnectionString = value; Server = service.Server; Database = service.Database; DialogResult = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _status.Text = e.Message; }
            finally { test.IsEnabled = true; }
        };
        panel.Children.Add(test); panel.Children.Add(_status); Content = panel;
        Closed += (_, _) => { _lifetime.Cancel(); _connection.Clear(); };
    }
}
