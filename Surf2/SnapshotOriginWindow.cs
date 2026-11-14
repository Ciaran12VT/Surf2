using System.Windows;
using System.Windows.Controls;
using Surf2.Models;
using Surf2.Services;

namespace Surf2;

public sealed class SnapshotOriginWindow : Window
{
    private readonly DatabaseOrigin? _original;
    private DatabaseOrigin? _pasted;
    private readonly ComboBox _engine = new() { ItemsSource = new[] { "Unknown", "SqlServer", "AzureSqlDatabase", "AzureSqlManagedInstance" } };
    private readonly TextBox _server = new(), _database = new(), _endpoint = new(), _kind = new(), _identifier = new();
    private readonly TextBox _paste = new() { AcceptsReturn = true, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    public DatabaseOrigin? Origin { get; private set; }
    public SnapshotOriginWindow(DatabaseOrigin? origin = null, string databaseName = "")
    {
        _original = origin; Origin = origin; Title = "Snapshot origin is missing or incomplete";
        Services.RelationalSnapshots.SnapshotWindowStyles.ThemeWindow(this);
        Width = 680; Height = 690; MinWidth = 520; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(18) };
        panel.Children.Add(new TextBlock { Text = "This snapshot does not contain enough information to identify its source database reliably. Provide details, or continue with the missing information recorded. An identity query run now does not prove where an older snapshot was captured.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        Add("Database engine", _engine); Add("Reported server / Azure logical server", _server); Add("Database", _database);
        Add("Endpoint / alias (optional; no credentials)", _endpoint); Add("Identifier kind", _kind); Add("Identifier (optional)", _identifier);
        _engine.SelectedItem = origin?.Engine ?? "Unknown"; _server.Text = origin?.Server ?? ""; _database.Text = origin?.Database ?? databaseName;
        _endpoint.Text = origin?.Endpoint ?? ""; _kind.Text = origin?.IdentifierKind ?? ""; _identifier.Text = origin?.Identifier ?? "";
        var copy = new Button { Content = "Copy origin SQL query", Margin = new(0, 8, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += (_, _) => { try { Clipboard.SetText(DatabaseOriginQueryService.Query); _status.Text = "Run the query in the source database using SSMS or another SQL tool; paste its single JSON result cell below."; } catch (Exception e) { _status.Text = e.Message; } };
        panel.Children.Add(copy); Add("Paste query result", _paste);
        var read = new Button { Content = "Read pasted identity", HorizontalAlignment = HorizontalAlignment.Left };
        read.Click += (_, _) =>
        {
            try
            {
                var value = DatabaseOriginQueryService.Parse(_paste.Text); _pasted = value;
                _engine.SelectedItem = value.Engine; _server.Text = value.Server; _database.Text = value.Database;
                _kind.Text = value.IdentifierKind; _identifier.Text = value.Identifier;
                _status.Text = value.IsComplete ? "Identity read. Confirm this is the database the snapshot came from." : "Identity is partial; unavailable fields can remain empty.";
            }
            catch (Exception e) { _status.Text = e.Message; }
        };
        panel.Children.Add(read); panel.Children.Add(_status);
        var actions = new WrapPanel { Margin = new(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Save details and continue", Margin = new(4), Padding = new(8, 4, 8, 4) };
        save.Click += (_, _) =>
        {
            try
            {
                var value = new DatabaseOrigin { Engine = (string?)_engine.SelectedItem ?? "Unknown", Server = _server.Text.Trim(), Database = _database.Text.Trim(),
                    Endpoint = _endpoint.Text.Trim(), IdentifierKind = _kind.Text.Trim(), Identifier = _identifier.Text.Trim(), Acquisition = _pasted == null ? "Manual" : "PastedIdentityQuery",
                    ObservedAtUtc = _pasted?.ObservedAtUtc, Availability = _identifier.Text.Length == 0 ? "Unavailable" : "Available", UserConfirmed = true,
                    OriginalEvidence = _original == null ? null : _original with { OriginalEvidence = null } }.Validate();
                if (_pasted != null && (value.Engine != _pasted.Engine || value.Server != _pasted.Server ||
                    value.Database != _pasted.Database || value.Endpoint != _pasted.Endpoint ||
                    value.IdentifierKind != _pasted.IdentifierKind || value.Identifier != _pasted.Identifier)) value = value with
                    { Acquisition = "ManualCorrection", ObservedAtUtc = null, OriginalEvidence = value.OriginalEvidence ?? _pasted };
                if (_original?.ConflictsWith(value) == true &&
                    MessageBox.Show(this, "The supplied origin differs from the snapshot's recorded evidence. Confirm this association? The original evidence will be retained.", "Different origin", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                Origin = value; DialogResult = true;
            }
            catch (Exception e) { _status.Text = e.Message; }
        };
        var skip = new Button { Content = "Continue without completing origin", Margin = new(4), Padding = new(8, 4, 8, 4) };
        skip.Click += (_, _) => { Origin = _original; DialogResult = true; };
        var cancel = new Button { Content = "Cancel import", IsCancel = true, Margin = new(4), Padding = new(8, 4, 8, 4) };
        actions.Children.Add(save); actions.Children.Add(skip); actions.Children.Add(cancel); panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Add(string label, Control input) { Services.RelationalSnapshots.SnapshotWindowStyles.ThemeInput(input); panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 5, 0, 2) }); panel.Children.Add(input); }
    }
}
