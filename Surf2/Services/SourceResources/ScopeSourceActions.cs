using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Surf2.Models;
using Surf2.Services.RemoteFolders;

namespace Surf2.Services.SourceResources;

public static class ScopeSourceActions
{
    public static async Task<ScopedResource?> AddGitAsync(Window owner, Scope scope, ScopedResource? local = null, CancellationToken ct = default)
    {
        GitCheckoutInfo? checkout = local == null ? null : await RemoteFolderServices.Default.Git.InspectAsync(local.Path, ct);
        if (local != null && checkout == null) throw new IOException("The selected folder is not a Git checkout.");
        string? remote = checkout == null ? null : ChooseRemote(owner, checkout);
        if (checkout != null && remote == null) return null;
        var initial = remote == null ? null : new ResourceSourceDefinition { Provider = SourceProvider.Git, Endpoint = remote,
            Ref = string.IsNullOrEmpty(checkout!.Branch) || checkout.Branch.StartsWith('(') ? checkout.Commit ?? "" : checkout.Branch, Subfolder = checkout.ScopedSubfolder };
        var dialog = new AddGitSourceWindow(initial, remote) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } source) return null;
        if (checkout != null && !checkout.Matches(source.Endpoint)) throw new IOException("The remote is not the repository configured by this local checkout.");
        var resource = new ScopedResource { Kind = ResourceKind.Folder, Source = source, Path = source.Endpoint,
            DisplayNameOverride = dialog.DisplayName, DetailsOverride = source.Endpoint };
        return await AddMemberAsync(scope, resource, local, ct, owner);
    }

    public static async Task<ScopedResource?> AddBlobAsync(Window owner, Scope scope, ScopedResource? local = null, CancellationToken ct = default)
    {
        if (local != null) RequirePairAvailable(scope, local);
        var dialog = new AddBlobSourceWindow { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } source) return null;
        var resource = new ScopedResource { Kind = ResourceKind.Folder, Source = source,
            Path = source.Endpoint.TrimEnd('/') + "/" + source.Container + "/" + source.Prefix,
            DisplayNameOverride = dialog.DisplayName, DetailsOverride = source.Container + "/" + source.Prefix };
        if (local != null && scope.Resources.FirstOrDefault(r => SameLocation(r, resource)) is { } existingBlob)
            RequirePairAvailable(scope, existingBlob, local.PublicId);
        var added = await AddMemberAsync(scope, resource, local, ct, owner);
        if (!ReferenceEquals(added, resource) && added.Source is { } existing)
        {
            // The new wizard may have selected a different account for the same location. Preserve the
            // resource/source identities but require a fresh cache and bind its credentials to that revision.
            string? secret = RemoteFolderServices.Default.Credentials.Read(source);
            var updated = source.Copy(); updated.SourceId = existing.SourceId;
            updated.ConfigurationRevision = Math.Max(source.ConfigurationRevision, existing.ConfigurationRevision + 1);
            if (secret != null) RemoteFolderServices.Default.Credentials.Save(updated, secret);
            added.Source = updated;
        }
        resource = added;
        if (local != null) RequirePairAvailable(scope, resource, local.PublicId);
        if (local != null) { resource.PairedResourceId = local.PublicId; local.PairedResourceId = resource.PublicId; }
        return resource;
    }

    public static ScopedResource? AddLiveSql(Window owner, Scope scope, ScopedResource? snapshot = null)
    {
        var dialog = new LiveSqlConnectionWindow { Owner = owner };
        if (dialog.ShowDialog() != true) return null;
        var source = new ResourceSourceDefinition { Provider = SourceProvider.LiveSql, Endpoint = dialog.Server,
            DatabaseName = dialog.Database, AuthenticationKind = "SqlClient" };
        RemoteFolderServices.Default.Credentials.Save(source, dialog.ConnectionString);
        var resource = new ScopedResource { Kind = ResourceKind.LiveDatabase, Source = source,
            Path = "sql://" + source.SourceId.ToString("N"), DisplayNameOverride = dialog.Database,
            DetailsOverride = dialog.Server + "/" + dialog.Database };
        scope.EnsureResourceContainers();
        if (snapshot != null)
        {
            resource.ContainerId = snapshot.ContainerId; resource.PairedResourceId = snapshot.PublicId;
            snapshot.PairedResourceId = resource.PublicId;
        }
        scope.Resources.Add(resource); scope.EnsureResourceContainers();
        return resource;
    }

    public static async Task<ScopedResource?> LinkLocalAsync(Window owner, Scope scope, ScopedResource remote, CancellationToken ct = default)
    {
        if (remote.Source?.Provider is not (SourceProvider.Git or SourceProvider.AzureBlob)) throw new ArgumentException("Choose a Git or Blob resource to link.");
        var picker = new OpenFolderDialog { Title = "Link a local folder" };
        if (picker.ShowDialog(owner) != true) return null;
        var local = new ScopedResource { Kind = ResourceKind.Folder, Path = Path.GetFullPath(picker.FolderName), DisplayNameOverride = remote.DisplayName };
        if (remote.Source?.Provider == SourceProvider.Git)
        {
            var git = await RemoteFolderServices.Default.Git.InspectAsync(local.Path, ct);
            if (git == null || !git.Matches(remote.Source.Endpoint)) throw new IOException("Choose a checkout of the same Git repository.");
        }
        if (remote.Source?.Provider == SourceProvider.AzureBlob)
        {
            var existing = scope.Resources.FirstOrDefault(r => SameLocation(r, local));
            RequirePairAvailable(scope, remote, existing?.PublicId);
            if (existing != null) RequirePairAvailable(scope, existing, remote.PublicId);
        }
        local = await AddMemberAsync(scope, local, remote, ct, owner);
        if (remote.Source?.Provider == SourceProvider.AzureBlob)
        { local.PairedResourceId = remote.PublicId; remote.PairedResourceId = local.PublicId; }
        return local;
    }

    public static async Task<ScopedResource> AddMemberAsync(Scope scope, ScopedResource resource, ScopedResource? alternate = null,
        CancellationToken ct = default, Window? owner = null)
    {
        scope.EnsureResourceContainers();
        if (alternate != null && !scope.Resources.Contains(alternate)) throw new ArgumentException("The alternate resource does not belong to this scope.");
        var existing = scope.Resources.FirstOrDefault(r => SameLocation(r, resource));
        if (existing != null) resource = existing;
        if (alternate == null && resource.Kind == ResourceKind.Folder)
        {
            string? identity = resource.Source?.Provider == SourceProvider.Git ? GitSourceService.RepositoryIdentity(resource.Source.Endpoint) : null;
            GitCheckoutInfo? local = IsLocal(resource) ? await TryInspectAsync(resource.Path, ct) : null;
            var identities = local?.Remotes.Values.Select(GitSourceService.RepositoryIdentity).ToHashSet(StringComparer.Ordinal) ?? [];
            if (identity != null) identities.Add(identity);
            var candidates = new List<ScopedResource>();
            if (identities.Count != 0)
                foreach (var member in scope.Resources.Where(r => r.Kind == ResourceKind.Folder))
                {
                    if (ReferenceEquals(member, resource)) continue;
                    if (member.Source?.Provider == SourceProvider.Git && identities.Contains(GitSourceService.RepositoryIdentity(member.Source.Endpoint)))
                    { candidates.Add(member); continue; }
                    if (IsLocal(member) && await TryInspectAsync(member.Path, ct) is { } git &&
                        git.Remotes.Values.Select(GitSourceService.RepositoryIdentity).Any(identities.Contains))
                        candidates.Add(member);
                }
            var containers = candidates.GroupBy(r => r.ContainerId).Select(g => g.First()).ToArray();
            if (containers.Length == 1) alternate = containers[0];
            else if (containers.Length > 1 && owner != null)
                alternate = ChooseAlternative(owner, scope, containers);
        }
        if (alternate != null && resource.ContainerId != alternate.ContainerId)
        {
            if (existing != null && (resource.Source?.Provider == SourceProvider.Git || alternate.Source?.Provider == SourceProvider.Git))
            {
                Guid previous = resource.ContainerId;
                foreach (var member in scope.Resources.Where(r => r.ContainerId == previous)) member.ContainerId = alternate.ContainerId;
            }
            else resource.ContainerId = alternate.ContainerId;
        }
        if (existing == null) scope.Resources.Add(resource);
        scope.EnsureResourceContainers();
        return resource;
    }

    internal static bool SameLocation(ScopedResource left, ScopedResource right)
    {
        if (left.Kind != right.Kind) return false;
        if (IsLocal(left) && IsLocal(right) && left.Kind is ResourceKind.Folder or ResourceKind.File)
        {
            try { return Path.GetFullPath(left.Path).TrimEnd('\\', '/').Equals(Path.GetFullPath(right.Path).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
        if (left.Source is not { } a || right.Source is not { } b || a.Provider != b.Provider) return false;
        return a.Provider is SourceProvider.Git or SourceProvider.AzureBlob &&
            a.Endpoint.TrimEnd('/').Equals(b.Endpoint.TrimEnd('/'), StringComparison.Ordinal) &&
            a.Container == b.Container && a.Prefix == b.Prefix && a.Ref == b.Ref && a.Subfolder == b.Subfolder && a.AuthenticationKind == b.AuthenticationKind;
    }
    private static bool IsLocal(ScopedResource resource) => resource.Source == null || resource.Source.Provider == SourceProvider.LocalFileSystem;
    private static async Task<GitCheckoutInfo?> TryInspectAsync(string path, CancellationToken ct)
    {
        try { return await RemoteFolderServices.Default.Git.InspectAsync(path, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        { return null; } // Git metadata is optional for ordinary local folders.
    }
    private static void RequirePairAvailable(Scope scope, ScopedResource resource, Guid? expected = null)
    {
        if (resource.PairedResourceId is not { } pair || pair == expected) return;
        string name = scope.Resources.FirstOrDefault(r => r.PublicId == pair)?.DisplayName ?? "another resource";
        throw new InvalidOperationException("This resource is already paired with '" + name + "'. Unlink that pairing before linking a different location.");
    }
    private static string? ChooseRemote(Window owner, GitCheckoutInfo checkout)
    {
        var remotes = checkout.Remotes.ToArray();
        if (remotes.Length == 0) throw new IOException("This local repository has no configured remote. Configure its remote using your Git tools, then add the direct connection.");
        if (remotes.Length == 1) return remotes[0].Value;
        var window = new Window { Owner = owner };
        var body = RemoteFolderDialogUi.Body(window, "Choose repository remote", 640, 260);
        RemoteFolderDialogUi.Note(body, "This checkout has several remotes. Choose the repository this resource represents.");
        var picker = new ComboBox { ItemsSource = remotes.Select(p => p.Key + " — " + p.Value).ToArray(), SelectedIndex = 0, MinHeight = 30 }; body.Children.Add(picker);
        RemoteFolderDialogUi.Button(body, "Continue", (_, _) => window.DialogResult = true);
        return window.ShowDialog() == true ? remotes[picker.SelectedIndex].Value : null;
    }
    private static ScopedResource ChooseAlternative(Window owner, Scope scope, ScopedResource[] alternatives)
    {
        var window = new Window { Owner = owner };
        var body = RemoteFolderDialogUi.Body(window, "Choose matching resource container", 640, 260);
        RemoteFolderDialogUi.Note(body, "Several existing containers match configured remotes. Choose which container should hold this alternative.");
        var picker = new ComboBox { ItemsSource = alternatives.Select(r => scope.ResourceContainers.First(c => c.ContainerId == r.ContainerId).Name + " — " + r.DisplayName).ToArray(), SelectedIndex = 0, MinHeight = 30 };
        body.Children.Add(picker); RemoteFolderDialogUi.Button(body, "Use container", (_, _) => window.DialogResult = true);
        if (window.ShowDialog() != true) throw new OperationCanceledException("Container selection was cancelled.");
        return alternatives[picker.SelectedIndex];
    }
}
