using System.Windows;
using System.Windows.Controls;
using Surf2.Models;
using Surf2.Services.RemoteFolders;

namespace Surf2;

public sealed class AddGitSourceWindow : Window
{
    private readonly ResourceSourceDefinition _initial;
    private readonly string? _requiredRepository;
    private readonly TextBox _name, _endpoint, _revision, _subfolder;
    private readonly TextBlock _status;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Button _save, _test;
    private string? _testedKey;
    private bool _closed;
    public ResourceSourceDefinition? Result { get; private set; }
    public string DisplayName => _name.Text.Trim();
    public AddGitSourceWindow(ResourceSourceDefinition? initial = null, string? requiredRepository = null)
    {
        _initial = initial?.Copy() ?? new() { SourceId = Guid.NewGuid(), Provider = SourceProvider.Git, AuthenticationKind = "GitCredentialManager", ConfigurationRevision = 1 };
        _requiredRepository = requiredRepository;
        var body = RemoteFolderDialogUi.Body(this, "Git repository", height: 535);
        RemoteFolderDialogUi.Note(body, "Surf2 keeps a managed local checkout for browsing and search. Git Credential Manager or your SSH agent provides access; your existing checkout is never changed.");
        _name = RemoteFolderDialogUi.Field(body, "Resource name", "Git repository");
        _endpoint = RemoteFolderDialogUi.Field(body, "Repository URL (HTTPS or SSH)", _initial.Endpoint.Length == 0 ? requiredRepository ?? "" : _initial.Endpoint);
        _revision = RemoteFolderDialogUi.Field(body, "Branch, tag or commit (blank uses remote default)", _initial.Ref);
        _subfolder = RemoteFolderDialogUi.Field(body, "Repository subfolder (optional)", _initial.Subfolder);
        if (requiredRepository != null) RemoteFolderDialogUi.Note(body, "The repository must match the existing local Git resource.");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; body.Children.Add(buttons);
        _test = RemoteFolderDialogUi.Button(buttons, "Test access", Test_Click);
        _save = RemoteFolderDialogUi.Button(buttons, "Save connection", Save_Click); _save.IsEnabled = false;
        RemoteFolderDialogUi.Button(buttons, "Cancel", (_, _) => Close());
        _status = RemoteFolderDialogUi.Note(body, "Test access before saving.");
        foreach (var text in new[] { _endpoint, _revision, _subfolder }) text.TextChanged += (_, _) => { _testedKey = null; _save.IsEnabled = false; };
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); };
    }
    private ResourceSourceDefinition ReadSource()
    {
        if (DisplayName.Length == 0) throw new ArgumentException("Enter a resource name.");
        var source = _initial.Copy(); source.Provider = SourceProvider.Git; source.Endpoint = _endpoint.Text.Trim();
        source.Ref = _revision.Text.Trim(); source.Subfolder = _subfolder.Text.Trim().Replace('\\', '/').Trim('/');
        if (_requiredRepository != null && GitSourceService.RepositoryIdentity(source.Endpoint) != GitSourceService.RepositoryIdentity(_requiredRepository))
            throw new ArgumentException("This repository does not match the local Git resource.");
        if (source.Subfolder.Length != 0) FolderSourcePaths.RequireWindowsRelativePath(source.Subfolder);
        if (source.Endpoint != _initial.Endpoint || source.Ref != _initial.Ref || source.Subfolder != _initial.Subfolder)
            source.ConfigurationRevision = Math.Max(1, _initial.ConfigurationRevision + (_initial.Endpoint.Length == 0 ? 0 : 1));
        return source;
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        _test.IsEnabled = false; _save.IsEnabled = false;
        try
        {
            var source = ReadSource(); _status.Text = "Testing repository access…";
            var result = await RemoteFolderServices.Default.Git.TestAsync(source, _cancellation.Token);
            if (_closed) return;
            if (FolderSourcePaths.ConfigurationKey(ReadSource()) != FolderSourcePaths.ConfigurationKey(source)) { _status.Text = "The connection changed. Test it again."; return; }
            _testedKey = FolderSourcePaths.ConfigurationKey(source); _save.IsEnabled = true; _status.Text = result.Description;
        }
        catch (Exception ex) { if (!_closed) _status.Text = RemoteFolderServices.DescribeError(ex); }
        finally { if (!_closed) _test.IsEnabled = true; }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { var source = ReadSource(); if (_testedKey != FolderSourcePaths.ConfigurationKey(source)) throw new InvalidOperationException("Test this connection before saving."); Result = source; DialogResult = true; }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
