using System.IO;
using System.Text;
using Surf2.Models;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services;

public sealed partial class SqlTraceService
{
    private sealed record TraceAddress(SqlTraceObject Summary, long RevisionKey);

    public async Task<SqlTraceResult> BuildRelationalTraceAsync(string rootName, string rootSql,
        RelationalSnapshotStore store, CancellationToken ct = default)
    {
        const long maximumMetadataBytes = 32 * 1024 * 1024;
        const int maximumOutputCharacters = 8 * 1024 * 1024;
        const int maximumDefinitions = 256;
        var addresses = new Dictionary<string, TraceAddress>(StringComparer.Ordinal);
        var heads = new List<SnapshotSummary>();
        long bytes = 0;
        SnapshotCursor? catalogueCursor = null;
        do
        {
            var catalogue = await store.ListSnapshotsAsync(cursor: catalogueCursor, ct: ct).ConfigureAwait(false);
            foreach (var snapshot in catalogue.Items)
            {
                bytes = checked(bytes + 256 + 2L * (snapshot.SnapshotId.Length + snapshot.DisplayName.Length));
                if (bytes > maximumMetadataBytes) throw new InvalidDataException("SQL trace metadata exceeds its budget.");
                heads.Add(snapshot);
                SnapshotCursor? cursor = null;
                do
                {
                    var page = await store.ListObjectsAsync(snapshot.SnapshotKey, DatabaseVersionedResourceKind.StoredProcedure,
                        cursor: cursor, ct: ct).ConfigureAwait(false);
                    foreach (var item in page.Items)
                    {
                        var resource = item.Resource;
                        var name = SqlName.FormatPlainMultipartName(resource.SchemaName, resource.ObjectName);
                        string key = snapshot.SnapshotKey.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + resource.ResourceKey;
                        bytes = checked(bytes + 512 + 2L * (name.Length + resource.ObjectName.Length + snapshot.DisplayName.Length));
                        if (bytes > maximumMetadataBytes) throw new InvalidDataException("SQL trace metadata exceeds its budget.");
                        if (!string.IsNullOrWhiteSpace(resource.ObjectName))
                            addresses.Add(key, new(new(key, resource.ObjectName, name, snapshot.DisplayName, string.Empty), resource.RevisionKey));
                    }
                    cursor = page.Next;
                } while (cursor != null);
            }
            catalogueCursor = catalogue.Next;
        } while (catalogueCursor != null);

        var summaries = addresses.Values.Select(a => a.Summary).ToArray();
        var builder = new StringBuilder();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activePath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counters = new SqlTraceCounters();
        int fetched = 0;
        long definitionBytes = 0;
        await AppendAsync(rootName, rootSql, NormalizeTraceName(rootName), 0).ConfigureAwait(false);
        foreach (var head in heads)
        {
            ct.ThrowIfCancellationRequested();
            var current = await store.GetSnapshotAsync(head.SnapshotKey, ct).ConfigureAwait(false);
            if (current == null || !head.RowVersion.AsSpan().SequenceEqual(current.RowVersion))
                throw new SnapshotConcurrencyException();
        }
        return new(builder.ToString().TrimEnd(), counters.QueryCount, counters.StoredProcedureCallCount);

        async Task AppendAsync(string name, string sql, string objectKey, int depth)
        {
            ct.ThrowIfCancellationRequested();
            var analysis = await Task.Run(() => (Crud: ExtractCrud(sql), Calls: FindCalledStoredProcedures(sql, summaries, objectKey, ct, bounded: true)
                .OrderBy(item => item.QualifiedName).ThenBy(item => item.SnapshotDisplayName).ToArray()), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            counters.QueryCount++;
            string indent = new(' ', depth * 2);
            builder.AppendLine($"{indent}Query: {name}");
            AppendTraceLine(builder, indent, "Selects", analysis.Crud.Selects);
            AppendTraceLine(builder, indent, "Updates", analysis.Crud.Updates);
            AppendTraceLine(builder, indent, "Inserts", analysis.Crud.Inserts);
            AppendTraceLine(builder, indent, "Deletes", analysis.Crud.Deletes);
            if (analysis.Calls.Length > 0)
            {
                counters.StoredProcedureCallCount += analysis.Calls.Length;
                AppendTraceLine(builder, indent, "Calls", analysis.Calls.Select(item => item.DisplayName));
            }
            if (analysis.Crud.IsEmpty && analysis.Calls.Length == 0)
                builder.AppendLine($"{indent}No table usage or stored procedure calls found.");
            builder.AppendLine();
            RequireOutputBudget();
            if (depth >= MaximumTraceDepth)
            {
                builder.AppendLine($"{indent}Trace stopped: maximum nested depth reached.");
                builder.AppendLine(); RequireOutputBudget(); return;
            }
            foreach (var procedure in analysis.Calls)
            {
                ct.ThrowIfCancellationRequested();
                if (activePath.Contains(procedure.Key))
                    builder.AppendLine($"{indent}  Query: {procedure.DisplayName} (recursive call already in progress)");
                else if (!visited.Add(procedure.Key))
                    builder.AppendLine($"{indent}  Query: {procedure.DisplayName} (already traced)");
                else
                {
                    if (++fetched > maximumDefinitions)
                        throw new InvalidDataException("SQL trace exceeds its 256-definition budget. No partial trace was published.");
                    activePath.Add(procedure.Key);
                    var definition = await store.ReadObjectAsync(addresses[procedure.Key].RevisionKey, ct).ConfigureAwait(false);
                    definitionBytes = checked(definitionBytes + 2L * definition.Definition.Length);
                    if (definitionBytes > 64 * 1024 * 1024)
                        throw new InvalidDataException("SQL trace exceeds its 64 MiB definition-read budget. No partial trace was published.");
                    await AppendAsync(procedure.DisplayName, definition.Definition, procedure.Key, depth + 1).ConfigureAwait(false);
                    activePath.Remove(procedure.Key);
                    continue;
                }
                builder.AppendLine(); RequireOutputBudget();
            }
        }
        void RequireOutputBudget()
        {
            if (builder.Length > maximumOutputCharacters)
                throw new InvalidDataException("SQL trace exceeds its 16 MiB output budget. No partial trace was published.");
        }
    }
}
