using System.IO;
using Surf2.Models;

namespace Surf2.Services;

public sealed class ExistingScopeResourceService
{
    public IReadOnlyList<ExistingScopeResourceCandidate> CreateCandidates(
        Scope targetScope,
        ScopeLibrary scopeLibrary,
        DatabaseSnapshotLibrary databaseSnapshots,
        DiagramLibrary diagramLibrary)
    {
        HashSet<string> targetKeys = targetScope.Resources
            .Select(resource => CreateIdentityKey(resource.Kind, resource.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatesByKey = new Dictionary<string, ExistingScopeResourceCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (Scope scope in scopeLibrary.Scopes)
        {
            if (string.Equals(scope.ScopeId, targetScope.ScopeId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ScopedResource resource in scope.Resources)
            {
                AddCandidate(
                    candidatesByKey,
                    targetKeys,
                    CreateCandidate(resource, scope.Name, databaseSnapshots, diagramLibrary));
            }
        }

        foreach (DatabaseMetadataSnapshot snapshot in databaseSnapshots.Snapshots)
        {
            string name = string.IsNullOrWhiteSpace(snapshot.DisplayName)
                ? string.IsNullOrWhiteSpace(snapshot.DatabaseName) ? "Database" : snapshot.DatabaseName
                : snapshot.DisplayName;
            AddCandidate(
                candidatesByKey,
                targetKeys,
                new ExistingScopeResourceCandidate(
                    name,
                    GetResourceKindDisplay(ResourceKind.DatabaseSnapshot),
                    snapshot.DatabaseName,
                    "Database Snapshots",
                    ResourceKind.DatabaseSnapshot,
                    snapshot.SnapshotId,
                    name,
                    snapshot.DatabaseName,
                    IncludeChildren: true));
        }

        foreach (DiagramDocument diagram in diagramLibrary.Diagrams)
        {
            AddCandidate(
                candidatesByKey,
                targetKeys,
                new ExistingScopeResourceCandidate(
                    string.IsNullOrWhiteSpace(diagram.Name) ? "Untitled Diagram" : diagram.Name,
                    GetResourceKindDisplay(ResourceKind.Diagram),
                    "Diagram",
                    "Diagrams",
                    ResourceKind.Diagram,
                    diagram.DiagramId,
                    diagram.Name,
                    "Diagram",
                    IncludeChildren: true));
        }

        return candidatesByKey.Values
            .OrderBy(candidate => candidate.Type)
            .ThenBy(candidate => candidate.Name)
            .ThenBy(candidate => candidate.Source)
            .ThenBy(candidate => candidate.Details)
            .ToList();
    }

    public bool TryAddResource(Scope targetScope, ExistingScopeResourceCandidate candidate)
    {
        string candidateKey = CreateIdentityKey(candidate.Kind, candidate.Path);
        bool alreadyExists = targetScope.Resources.Any(resource =>
            string.Equals(CreateIdentityKey(resource.Kind, resource.Path), candidateKey, StringComparison.OrdinalIgnoreCase));
        if (alreadyExists)
        {
            return false;
        }

        targetScope.Resources.Add(candidate.CreateScopedResource());
        return true;
    }

    private static void AddCandidate(
        Dictionary<string, ExistingScopeResourceCandidate> candidatesByKey,
        HashSet<string> targetKeys,
        ExistingScopeResourceCandidate candidate)
    {
        string key = CreateIdentityKey(candidate.Kind, candidate.Path);
        if (targetKeys.Contains(key))
        {
            return;
        }

        candidatesByKey.TryAdd(key, candidate);
    }

    private static ExistingScopeResourceCandidate CreateCandidate(
        ScopedResource resource,
        string sourceScopeName,
        DatabaseSnapshotLibrary databaseSnapshots,
        DiagramLibrary diagramLibrary)
    {
        string name = GetResourceDisplayName(resource, databaseSnapshots, diagramLibrary);
        return new ExistingScopeResourceCandidate(
            name,
            GetResourceKindDisplay(resource.Kind),
            GetResourceDetails(resource, databaseSnapshots, diagramLibrary),
            string.IsNullOrWhiteSpace(sourceScopeName) ? "Scope" : sourceScopeName,
            resource.Kind,
            resource.Path,
            resource.DisplayNameOverride,
            resource.DetailsOverride,
            resource.IncludeChildren);
    }

    private static string GetResourceDisplayName(
        ScopedResource resource,
        DatabaseSnapshotLibrary databaseSnapshots,
        DiagramLibrary diagramLibrary)
    {
        if (!string.IsNullOrWhiteSpace(resource.DisplayNameOverride))
        {
            return resource.DisplayNameOverride;
        }

        if (resource.Kind == ResourceKind.DatabaseSnapshot)
        {
            DatabaseMetadataSnapshot? snapshot = databaseSnapshots.Snapshots.FirstOrDefault(candidate =>
                string.Equals(candidate.SnapshotId, resource.Path, StringComparison.OrdinalIgnoreCase));
            if (snapshot != null)
            {
                return string.IsNullOrWhiteSpace(snapshot.DisplayName)
                    ? snapshot.DatabaseName
                    : snapshot.DisplayName;
            }
        }

        if (resource.Kind == ResourceKind.Diagram)
        {
            DiagramDocument? diagram = diagramLibrary.Find(resource.Path);
            if (diagram != null)
            {
                return string.IsNullOrWhiteSpace(diagram.Name) ? "Untitled Diagram" : diagram.Name;
            }
        }

        return resource.DisplayName;
    }

    private static string GetResourceDetails(
        ScopedResource resource,
        DatabaseSnapshotLibrary databaseSnapshots,
        DiagramLibrary diagramLibrary)
    {
        if (!string.IsNullOrWhiteSpace(resource.DetailsOverride))
        {
            return resource.DetailsOverride;
        }

        if (resource.Kind == ResourceKind.DatabaseSnapshot)
        {
            DatabaseMetadataSnapshot? snapshot = databaseSnapshots.Snapshots.FirstOrDefault(candidate =>
                string.Equals(candidate.SnapshotId, resource.Path, StringComparison.OrdinalIgnoreCase));
            return snapshot?.DatabaseName ?? resource.Path;
        }

        if (resource.Kind == ResourceKind.Diagram)
        {
            DiagramDocument? diagram = diagramLibrary.Find(resource.Path);
            return diagram == null ? resource.Path : "Diagram";
        }

        return resource.Details;
    }

    private static string GetResourceKindDisplay(ResourceKind kind)
    {
        return kind switch
        {
            ResourceKind.DatabaseSnapshot => "Database",
            _ => kind.ToString()
        };
    }

    private static string CreateIdentityKey(ResourceKind kind, string path)
    {
        string normalizedPath = kind is ResourceKind.File or ResourceKind.Folder
            ? NormalizePath(path)
            : path.Trim();
        return $"{kind}|{normalizedPath}";
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
