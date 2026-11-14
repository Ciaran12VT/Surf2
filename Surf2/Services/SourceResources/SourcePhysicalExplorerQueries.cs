using System.Collections.Immutable;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RemoteFolders;

namespace Surf2.Services.SourceResources;

/// <summary>Projects prepared source manifests, preserving Blob names and excluding managed Git metadata.</summary>
public sealed class SourcePhysicalExplorerQueries : IPhysicalExplorerQueries
{
    private readonly PhysicalExplorerQueries _files = new();
    public Task<ExplorerAvailability> ProbeAsync(string path, bool isDirectory, CancellationToken ct) =>
        isDirectory && ResourceWorkspace.FindDirectory(path) != null ? Task.FromResult(ExplorerAvailability.Present) : _files.ProbeAsync(path, isDirectory, ct);
    public Task<ImmutableArray<PhysicalExplorerEntry>> ReadDirectoryAsync(string path, string culture, ExplorerLimits limits, CancellationToken ct)
    {
        if (ResourceWorkspace.FindDirectory(path) is not { } directory) return _files.ReadDirectoryAsync(path, culture, limits, ct);
        ct.ThrowIfCancellationRequested();
        var children = directory.Prepared.Index.Children(directory.LogicalDirectory);
        if (children.Length > limits.MaximumMetadataRows) throw new ExplorerLimitException("The source folder exceeds the explorer metadata limit.");
        bool virtualDirectories = ResourceWorkspace.Find(directory.Member)?.Source?.Provider == Surf2.Models.SourceProvider.AzureBlob;
        return Task.FromResult(children.Select(child => new PhysicalExplorerEntry(child.Path(virtualDirectories), child.IsDirectory,
            ExplorerAvailability.Present, DisplayName: child.Name, LogicalKey: child.IsDirectory
                ? ResourceWorkspace.DirectoryLocator(directory.Member, child.LogicalPath)
                : ResourceWorkspace.Locator(directory.Member, child.LogicalPath))).ToImmutableArray());
    }
}
