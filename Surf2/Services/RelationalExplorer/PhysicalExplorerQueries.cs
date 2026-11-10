using System.Collections.Immutable;
using System.IO;

namespace Surf2.Services.RelationalExplorer;

// Directory enumeration has no asynchronous BCL API. Only this bounded metadata operation uses a worker.
public sealed class PhysicalExplorerQueries : IPhysicalExplorerQueries
{
    public Task<ExplorerAvailability> ProbeAsync(string path, bool isDirectory, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var attributes = File.GetAttributes(path);
            return ((attributes & FileAttributes.Directory) != 0) == isDirectory
                ? ExplorerAvailability.Present : ExplorerAvailability.Missing;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return ExplorerAvailability.Missing; }
        catch (UnauthorizedAccessException) { return ExplorerAvailability.Inaccessible; }
        catch (IOException) { return ExplorerAvailability.Failed; }
    }, ct);

    public Task<ImmutableArray<PhysicalExplorerEntry>> ReadDirectoryAsync(string path, string sortCultureName,
        ExplorerLimits limits, CancellationToken ct) => Task.Run(() =>
    {
        var budget = new ExplorerMetadataBudget(limits);
        var entries = new List<PhysicalExplorerEntry>();
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            ct.ThrowIfCancellationRequested();
            budget.Add(child);
            var attributes = File.GetAttributes(child);
            entries.Add(new(child, (attributes & FileAttributes.Directory) != 0, ExplorerAvailability.Present,
                (attributes & FileAttributes.ReparsePoint) != 0));
        }
        return entries.OrderBy(e => !e.IsDirectory).ThenBy(e => Path.GetFileName(e.Path),
            ExplorerCompatibility.Comparer(sortCultureName)).ToImmutableArray();
    }, ct);
}
