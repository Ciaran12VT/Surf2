using Surf2.Models;
using Surf2.Services.RelationalExplorer;

namespace Surf2.Services.RelationalComparison;

public sealed partial class RelationalComparisonService
{
    // Scope opening already acquired this immutable metadata catalogue. Walking
    // every explorer category again only repeats domain checks and SQL requests.
    private async Task<ComparisonCandidates> GetDatabaseCandidatesAsync(RelationalComparisonTarget source,
        ExplorerScope scope, CancellationToken ct)
    {
        await RequireScopeAsync(scope, ct).ConfigureAwait(false);
        var items = new List<RelationalComparisonTarget>(); long bytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool complete = true;
        foreach (var membership in scope.Resources.Where(resource => resource.IsLoaded && resource.Kind == ResourceKind.DatabaseSnapshot && resource.Snapshot != null))
        {
            if (!scope.DatabaseCatalogue!.TryGetValue(membership.Snapshot!.SnapshotKey, out var catalogue)) continue;
            var fullData = source.Resource.IsTableData
                ? catalogue.Where(item => item.Resource.Kind == DatabaseVersionedResourceKind.TableMetadata && item.HasFullData)
                    .Select(item => (item.Resource.SchemaName, item.Resource.ObjectName)).ToHashSet(TableNameComparer.Instance) : null;
            foreach (var item in catalogue)
            {
                ct.ThrowIfCancellationRequested();
                if (!item.Resource.Kind.HasValue) continue;
                var target = FromResource(item.Resource, source.Epoch);
                if (target.Resource.ComparisonTypeKey != source.Resource.ComparisonTypeKey || SameTarget(target, source)) continue;
                if (source.Resource.IsTableData)
                {
                    if (!fullData!.Contains((item.Resource.SchemaName, item.Resource.ObjectName))) continue;
                }
                else
                {
                    var address = RelationalExplorerService.AddressFromCatalogue(scope, membership, item.Resource, target.Category!.Value, false);
                    if (address == null) continue;
                    target = FromNode(source.Epoch, address.Node)!;
                }
                if (!seen.Add(TargetKey(target))) continue;
                long size = 512 + 2L * (target.Resource.DisplayName.Length + target.Resource.Path.Length + target.Resource.IdentityKey.Length);
                if (items.Count == Limits.MaximumCandidates || size > Limits.MaximumCandidateBytes - bytes)
                { complete = false; break; }
                items.Add(target); bytes += size;
            }
            if (!complete) break;
        }
        items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Resource.DisplayName + "\0" + a.Resource.Path, b.Resource.DisplayName + "\0" + b.Resource.Path));
        await RequireScopeAsync(scope, ct).ConfigureAwait(false);
        return new(items, complete, complete ? null : "The comparison picker reached its metadata limit. Narrow the active scope for further choices.");
    }
}
