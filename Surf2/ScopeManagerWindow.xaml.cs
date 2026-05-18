using System.IO;
using System.Windows;
using Microsoft.Win32;
using Surf2.Models;

namespace Surf2;

public partial class ScopeManagerWindow : Window
{
    private readonly ScopeLibrary _scopeLibrary;
    private bool _isLoadingScope;
    private Scope? _selectedScope;

    public ScopeManagerWindow(ScopeLibrary scopeLibrary)
    {
        InitializeComponent();
        _scopeLibrary = scopeLibrary;

        ScopeList.ItemsSource = _scopeLibrary.Scopes;
        RefreshMergeScopes();

        if (!string.IsNullOrWhiteSpace(_scopeLibrary.LastActiveScopeId))
        {
            ScopeList.SelectedItem = _scopeLibrary.Scopes.FirstOrDefault(scope => scope.ScopeId == _scopeLibrary.LastActiveScopeId);
        }

        if (ScopeList.SelectedItem == null && _scopeLibrary.Scopes.Count > 0)
        {
            ScopeList.SelectedIndex = 0;
        }
    }

    public string? SelectedScopeId { get; private set; }

    public bool WasChanged { get; private set; }

    private void ScopeList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingScope)
        {
            return;
        }

        SaveSelectedScopeFromFields();
        _selectedScope = ScopeList.SelectedItem as Scope;
        LoadSelectedScopeIntoFields();
        RefreshMergeScopes();
    }

    private void NewScopeButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSelectedScopeFromFields();

        var scope = new Scope
        {
            Name = GetUniqueScopeName("New Scope")
        };

        _scopeLibrary.Scopes.Add(scope);
        ScopeList.SelectedItem = scope;
        WasChanged = true;
        StatusTextBlock.Text = "Created a new scope.";
    }

    private void DeleteScopeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScopeList.SelectedItem is not Scope scope)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"Delete scope '{scope.Name}'?",
            "Delete Scope",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _scopeLibrary.Scopes.Remove(scope);
        if (_scopeLibrary.LastActiveScopeId == scope.ScopeId)
        {
            _scopeLibrary.LastActiveScopeId = null;
        }

        _selectedScope = null;
        ClearFields();
        RefreshMergeScopes();
        WasChanged = true;
        StatusTextBlock.Text = "Deleted scope.";
    }

    private void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Scope? scope = EnsureSelectedScope();
        if (scope == null)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Add a folder to this scope",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddResource(scope, ResourceKind.Folder, dialog.FolderName);
        }
    }

    private void AddFileButton_Click(object sender, RoutedEventArgs e)
    {
        Scope? scope = EnsureSelectedScope();
        if (scope == null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Add a file to this scope",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddResource(scope, ResourceKind.File, dialog.FileName);
        }
    }

    private void AddProjectButton_Click(object sender, RoutedEventArgs e)
    {
        Scope? scope = EnsureSelectedScope();
        if (scope == null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Add a project to this scope",
            CheckFileExists = true,
            Filter = "Project files|*.csproj;*.vbproj;*.fsproj;*.sln;*.slnx|All files|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddResource(scope, ResourceKind.Project, dialog.FileName);
        }
    }

    private void RemoveResourceButton_Click(object sender, RoutedEventArgs e)
    {
        Scope? scope = EnsureSelectedScope();
        if (scope == null || ResourceList.SelectedItem is not ScopedResource resource)
        {
            return;
        }

        scope.Resources.Remove(resource);
        ResourceList.Items.Refresh();
        WasChanged = true;
        StatusTextBlock.Text = "Removed resource.";
    }

    private void AddScopeResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        Scope? targetScope = EnsureSelectedScope();
        if (targetScope == null || MergeScopeComboBox.SelectedItem is not Scope sourceScope)
        {
            return;
        }

        int added = 0;
        foreach (ScopedResource resource in sourceScope.Resources)
        {
            if (AddResource(targetScope, resource.Kind, resource.Path, updateStatus: false))
            {
                added++;
            }
        }

        ResourceList.Items.Refresh();
        WasChanged = WasChanged || added > 0;
        StatusTextBlock.Text = $"Added {added} resource(s) from '{sourceScope.Name}'.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSelectedScopeFromFields();
        ScopeList.Items.Refresh();
        RefreshMergeScopes();
        WasChanged = true;
        StatusTextBlock.Text = "Saved changes.";
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (EnsureSelectedScope() == null)
        {
            return;
        }

        SaveSelectedScopeFromFields();
        SelectedScopeId = _selectedScope?.ScopeId;
        WasChanged = true;
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSelectedScopeFromFields();
        Close();
    }

    private Scope? EnsureSelectedScope()
    {
        if (_selectedScope != null)
        {
            return _selectedScope;
        }

        StatusTextBlock.Text = "Select or create a scope first.";
        return null;
    }

    private void LoadSelectedScopeIntoFields()
    {
        _isLoadingScope = true;
        try
        {
            if (_selectedScope == null)
            {
                ClearFields();
                return;
            }

            NameTextBox.Text = _selectedScope.Name;
            DescriptionTextBox.Text = _selectedScope.Description;
            ResourceList.ItemsSource = _selectedScope.Resources;
        }
        finally
        {
            _isLoadingScope = false;
        }
    }

    private void SaveSelectedScopeFromFields()
    {
        if (_selectedScope == null)
        {
            return;
        }

        string name = NameTextBox.Text.Trim();
        _selectedScope.Name = string.IsNullOrWhiteSpace(name) ? "Untitled Scope" : name;
        _selectedScope.Description = DescriptionTextBox.Text.Trim();
        WasChanged = true;
    }

    private void ClearFields()
    {
        NameTextBox.Text = string.Empty;
        DescriptionTextBox.Text = string.Empty;
        ResourceList.ItemsSource = null;
    }

    private bool AddResource(Scope scope, ResourceKind kind, string path, bool updateStatus = true)
    {
        string normalizedPath = NormalizePath(path);
        bool alreadyExists = scope.Resources.Any(resource =>
            resource.Kind == kind &&
            string.Equals(NormalizePath(resource.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));

        if (alreadyExists)
        {
            if (updateStatus)
            {
                StatusTextBlock.Text = "That resource is already in this scope.";
            }

            return false;
        }

        scope.Resources.Add(new ScopedResource
        {
            Kind = kind,
            Path = path,
            IncludeChildren = kind == ResourceKind.Folder
        });

        ResourceList.Items.Refresh();
        WasChanged = true;

        if (updateStatus)
        {
            StatusTextBlock.Text = "Added resource.";
        }

        return true;
    }

    private void RefreshMergeScopes()
    {
        Scope? selected = ScopeList.SelectedItem as Scope;
        MergeScopeComboBox.ItemsSource = _scopeLibrary.Scopes
            .Where(scope => selected == null || scope.ScopeId != selected.ScopeId)
            .ToList();
    }

    private string GetUniqueScopeName(string baseName)
    {
        string candidate = baseName;
        int index = 2;

        while (_scopeLibrary.Scopes.Any(scope => string.Equals(scope.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} {index}";
            index++;
        }

        return candidate;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }
}
