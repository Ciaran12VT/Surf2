using System.Globalization;
using Surf2.Models;

namespace Surf2.Services.RelationalDocuments;

public sealed record DurableDocumentIdentity(Guid SnapshotId, Guid ResourceId, DatabaseVersionedResourceKind Kind);
public sealed record SessionDocumentIdentity(Guid Epoch, long SnapshotKey, long ResourceKey, DatabaseVersionedResourceKind Kind);

/// <summary>Public snapshot/resource identities are portable across sessions and persistence packages.</summary>
public static class DurableDocumentLink
{
    public static string Create(Guid snapshotPublicId, Guid resourcePublicId, DatabaseVersionedResourceKind kind)
    {
        if (snapshotPublicId == Guid.Empty || resourcePublicId == Guid.Empty)
            throw new ArgumentException("Persistent links require the saved public snapshot and resource identities.");
        string segment = kind switch
        {
            DatabaseVersionedResourceKind.TableMetadata => "table/",
            DatabaseVersionedResourceKind.TableData => "table-data/",
            DatabaseVersionedResourceKind.StoredProcedure or DatabaseVersionedResourceKind.View or
                DatabaseVersionedResourceKind.Function or DatabaseVersionedResourceKind.Trigger => "object/" + kind + "/",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return $"db://!{snapshotPublicId:N}/{segment}{resourcePublicId:N}" +
            (kind == DatabaseVersionedResourceKind.TableData ? ".csv" : ".sql");
    }

    public static bool TryParse(string path, out DurableDocumentIdentity identity)
    {
        identity = null!;
        if (!TryParts(path, out string owner, out string resource, out var kind) || owner.Length != 33 ||
            owner[0] != '!' || !Guid.TryParseExact(owner.AsSpan(1), "N", out var snapshotId) || snapshotId == Guid.Empty ||
            !Guid.TryParseExact(resource, "N", out var resourceId) || resourceId == Guid.Empty) return false;
        identity = new(snapshotId, resourceId, kind);
        return true;
    }

    public static bool TryParseSessionPath(string path, out SessionDocumentIdentity identity)
    {
        identity = null!;
        if (!TryParts(path, out string owner, out string resource, out var kind) || owner.Length < 35 ||
            owner[0] != '@' || owner[33] != ':' || !Guid.TryParseExact(owner.AsSpan(1, 32), "N", out var epoch) || epoch == Guid.Empty ||
            !long.TryParse(owner.AsSpan(34), NumberStyles.None, CultureInfo.InvariantCulture, out long snapshotKey) || snapshotKey <= 0 ||
            !long.TryParse(resource, NumberStyles.None, CultureInfo.InvariantCulture, out long resourceKey) || resourceKey <= 0) return false;
        identity = new(epoch, snapshotKey, resourceKey, kind);
        return true;
    }

    private static bool TryParts(string path, out string owner, out string resource, out DatabaseVersionedResourceKind kind)
    {
        owner = resource = ""; kind = default;
        if (!path.StartsWith("db://", StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = path[5..].Split('/');
        if (parts.Length is not (3 or 4)) return false;
        owner = parts[0];
        if (parts.Length == 3 && parts[1].Equals("table", StringComparison.OrdinalIgnoreCase)) kind = DatabaseVersionedResourceKind.TableMetadata;
        else if (parts.Length == 3 && parts[1].Equals("table-data", StringComparison.OrdinalIgnoreCase)) kind = DatabaseVersionedResourceKind.TableData;
        else if (parts.Length != 4 || !parts[1].Equals("object", StringComparison.OrdinalIgnoreCase) ||
            !Enum.TryParse(parts[2], true, out kind) || kind is not (DatabaseVersionedResourceKind.StoredProcedure or
                DatabaseVersionedResourceKind.View or DatabaseVersionedResourceKind.Function or DatabaseVersionedResourceKind.Trigger)) return false;
        string extension = kind == DatabaseVersionedResourceKind.TableData ? ".csv" : ".sql";
        if (!parts[^1].EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
        resource = parts[^1][..^extension.Length];
        return resource.Length > 0;
    }

    // Rewrite only the address. Keep line numbers, exact filter syntax, search options and text intact.
    public static string Rewrite(string link, Func<string, string> rewritePath)
    {
        if (CodeSearchReferenceService.TryDecode(link, out var search))
        {
            string rewritten = RewritePath(search.DocumentPath, rewritePath);
            return rewritten == search.DocumentPath ? link : CodeSearchReferenceService.Encode(search with { DocumentPath = rewritten });
        }
        return CodeSearchReferenceService.IsSearchReference(link) ? link : RewritePath(link, rewritePath);
    }

    public static async Task<string> RewriteAsync(string link, Func<string, Task<string>> rewritePath)
    {
        if (CodeSearchReferenceService.TryDecode(link, out var search))
        {
            string rewritten = await RewritePathAsync(search.DocumentPath, rewritePath).ConfigureAwait(false);
            return rewritten == search.DocumentPath ? link : CodeSearchReferenceService.Encode(search with { DocumentPath = rewritten });
        }
        return CodeSearchReferenceService.IsSearchReference(link) ? link : await RewritePathAsync(link, rewritePath).ConfigureAwait(false);
    }

    private static string RewritePath(string value, Func<string, string> rewritePath)
    {
        var (prefix, path, suffix) = Parts(value);
        string rewritten = rewritePath(path);
        return rewritten == path ? value : prefix + rewritten + suffix;
    }
    private static async Task<string> RewritePathAsync(string value, Func<string, Task<string>> rewritePath)
    {
        var (prefix, path, suffix) = Parts(value);
        string rewritten = await rewritePath(path).ConfigureAwait(false);
        return rewritten == path ? value : prefix + rewritten + suffix;
    }
    private static (string Prefix, string Path, string Suffix) Parts(string value)
    {
        int start = 0, end = value.Length;
        while (start < end && char.IsWhiteSpace(value[start])) start++;
        while (end > start && char.IsWhiteSpace(value[end - 1])) end--;
        string path = value[start..end];
        // Filter values may themselves contain ":["; the first occurrence is the address delimiter.
        int filter = path.IndexOf(":[", StringComparison.Ordinal);
        if (filter >= 0) { end = start + filter; path = path[..filter].TrimEnd(); end = start + path.Length; }
        int colon = path.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < path.Length && path.AsSpan(colon + 1).IndexOfAnyExceptInRange('0', '9') < 0)
            end = start + colon;
        return (value[..start], value[start..end], value[end..]);
    }
}
