using System.Windows;
using System.Windows.Controls;
using Surf2.Models;
using Surf2.Services.RemoteFolders;

namespace Surf2;

public sealed class AddBlobSourceWindow : Window
{
    private readonly ResourceSourceDefinition _initial;
    private readonly TextBox _name, _endpoint, _container, _prefix;
    private readonly ComboBox _auth;
    private readonly PasswordBox _sas;
    private readonly TextBlock _status;
    private readonly Button _test, _create, _save;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly bool _editing;
    private bool _closed;
    private string? _testedKey;
    public ResourceSourceDefinition? Result { get; private set; }
    public string DisplayName => _name.Text.Trim();
    public AddBlobSourceWindow(ResourceSourceDefinition? initial = null)
    {
        _initial = initial?.Copy() ?? new() { SourceId = Guid.NewGuid(), Provider = SourceProvider.AzureBlob, ConfigurationRevision = 1, AuthenticationKind = "Entra" };
        _editing = initial != null;
        var body = RemoteFolderDialogUi.Body(this, "Azure Blob Storage", height: 680);
        RemoteFolderDialogUi.Note(body, "Connect to an existing container, or explicitly create a private container in an existing storage account. File copies remain on this desktop for fast browsing and search.");
        _name = RemoteFolderDialogUi.Field(body, "Resource name", "Blob Storage");
        _endpoint = RemoteFolderDialogUi.Field(body, "Blob service endpoint", _initial.Endpoint);
        _container = RemoteFolderDialogUi.Field(body, "Container", _initial.Container);
        _prefix = RemoteFolderDialogUi.Field(body, "Folder prefix (optional)", _initial.Prefix);
        body.Children.Add(new TextBlock { Text = "Authentication", Margin = new Thickness(0, 10, 0, 3) });
        _auth = new ComboBox { ItemsSource = new[] { "Entra", "SAS", "Anonymous" }, SelectedItem = string.IsNullOrEmpty(_initial.AuthenticationKind) ? "Entra" : _initial.AuthenticationKind, MinHeight = 28 };
        body.Children.Add(_auth);
        body.Children.Add(new TextBlock { Text = "SAS token (stored securely on this desktop only)", Margin = new Thickness(0, 10, 0, 3) });
        _sas = new PasswordBox { MinHeight = 28 }; body.Children.Add(_sas);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; body.Children.Add(buttons);
        _test = RemoteFolderDialogUi.Button(buttons, "Test access", (_, _) => Run(false));
        _create = RemoteFolderDialogUi.Button(buttons, "Create container", (_, _) => Run(true));
        _save = RemoteFolderDialogUi.Button(buttons, "Save connection", Save_Click); _save.IsEnabled = false;
        RemoteFolderDialogUi.Button(buttons, "Cancel", (_, _) => Close());
        _status = RemoteFolderDialogUi.Note(body, "Test access before saving. The complete size is checked before downloading content.");
        foreach (var text in new[] { _endpoint, _container, _prefix }) text.TextChanged += (_, _) => InvalidateTest();
        _auth.SelectionChanged += (_, _) => InvalidateTest(); _sas.PasswordChanged += (_, _) => InvalidateTest();
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); };
    }
    private void InvalidateTest() { _testedKey = null; _save.IsEnabled = false; }
    private ResourceSourceDefinition ReadSource()
    {
        if (DisplayName.Length == 0) throw new ArgumentException("Enter a resource name.");
        var source = _initial.Copy(); source.Provider = SourceProvider.AzureBlob;
        source.Endpoint = _endpoint.Text.Trim().TrimEnd('/'); source.Container = _container.Text.Trim(); source.Prefix = FolderSourcePaths.NormalizePrefix(_prefix.Text.Trim());
        source.AuthenticationKind = (string?)_auth.SelectedItem ?? "Entra";
        if (_editing || source.Endpoint != _initial.Endpoint || source.Container != _initial.Container || source.Prefix != _initial.Prefix || source.AuthenticationKind != _initial.AuthenticationKind)
            source.ConfigurationRevision = Math.Max(1, _initial.ConfigurationRevision + (_initial.Endpoint.Length == 0 ? 0 : 1));
        BlobSourceService.Validate(source); return source;
    }
    private void SplitPastedUrl()
    {
        if (!Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        if (uri.UserInfo.Length != 0) throw new ArgumentException("The Blob endpoint cannot contain a username or password.");
        if (uri.Query.Length != 0) { _sas.Password = uri.Query.TrimStart('?'); _auth.SelectedItem = "SAS"; }
        var path = uri.AbsolutePath.Trim('/').Split('/', 2);
        if (path[0].Length != 0)
        {
            if (_container.Text.Length == 0) _container.Text = Uri.UnescapeDataString(path[0]);
            if (path.Length == 2 && _prefix.Text.Length == 0) _prefix.Text = Uri.UnescapeDataString(path[1]);
        }
        _endpoint.Text = new UriBuilder(uri) { Path = "", Query = "", Fragment = "" }.Uri.AbsoluteUri.TrimEnd('/');
    }
    private async void Run(bool create)
    {
        _test.IsEnabled = _create.IsEnabled = _save.IsEnabled = false;
        try
        {
            SplitPastedUrl(); var source = ReadSource();
            if (create && MessageBox.Show(this, "Create private container '" + source.Container + "' at " + source.Endpoint + "?", "Create container", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            if (source.AuthenticationKind == "SAS" && _sas.Password.Length != 0) RemoteFolderServices.Default.Blob.SaveSas(source, _sas.Password);
            if (source.AuthenticationKind == "Entra") { _status.Text = "Signing in…"; await RemoteFolderServices.Default.Blob.SignInAsync(source, _cancellation.Token); }
            _status.Text = create ? "Creating container…" : "Testing container access…";
            if (create) await RemoteFolderServices.Default.Blob.CreateContainerAsync(source, _cancellation.Token);
            var result = await RemoteFolderServices.Default.Blob.TestAsync(source, _cancellation.Token);
            if (_closed) return;
            if (FolderSourcePaths.ConfigurationKey(source) != FolderSourcePaths.ConfigurationKey(ReadSource())) { _status.Text = "The connection changed. Test it again."; return; }
            _testedKey = FolderSourcePaths.ConfigurationKey(source); _save.IsEnabled = true; _status.Text = result.Description;
        }
        catch (Exception ex) { if (!_closed) _status.Text = RemoteFolderServices.DescribeError(ex); }
        finally { if (!_closed) _test.IsEnabled = _create.IsEnabled = true; }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { var source = ReadSource(); if (_testedKey != FolderSourcePaths.ConfigurationKey(source)) throw new InvalidOperationException("Test this connection before saving."); Result = source; DialogResult = true; }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
