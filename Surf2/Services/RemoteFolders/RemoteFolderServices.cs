using Azure;
using Azure.Identity;
using Surf2.Models;

namespace Surf2.Services.RemoteFolders;

public sealed class RemoteFolderServices
{
    public static RemoteFolderServices Default { get; } = new();
    public FolderCacheStore Cache { get; }
    public FolderCredentialStore Credentials { get; }
    public GitSourceService Git { get; }
    public BlobSourceService Blob { get; }
    public RemoteFolderServices(FolderCacheStore? cache = null, FolderCredentialStore? credentials = null)
    { Cache = cache ?? new(); Credentials = credentials ?? new(); Git = new(Cache); Blob = new(Cache, Credentials); }
    public Task<PreparedFolderSource> PrepareResourceAsync(ScopedResource resource, bool refresh = false,
        IProgress<FolderSourceProgress>? progress = null, CancellationToken ct = default)
    {
        var source = resource.Source ?? throw new ArgumentException("The resource has no remote source configuration.");
        return source.Provider switch
        {
            SourceProvider.Git => Git.PrepareAsync(source, resource.DisplayName, refresh, progress, ct),
            SourceProvider.AzureBlob when resource.PairedResourceId == null => Blob.PrepareAsync(source, resource.DisplayName, refresh, progress, ct),
            SourceProvider.AzureBlob => throw new InvalidOperationException("A paired Blob resource must pass the reviewed reconciliation gate before it can load."),
            _ => throw new ArgumentException("The resource is not a remote folder.")
        };
    }
    public static string DescribeError(Exception error) => error switch
    {
        OperationCanceledException => "The operation was cancelled. The previous published copy was retained.",
        AuthenticationRequiredException => "Sign in to this source before retrying.",
        AuthenticationFailedException => "Authentication failed. Reconnect this source and check the selected account.",
        RequestFailedException { Status: 401 or 403 } => "Access was denied. Check this account's permissions or reconnect an expired SAS credential.",
        RequestFailedException { Status: 404 } => "The container or Blob item was not found.",
        RequestFailedException { Status: 409 or 412 } => "The remote item changed or a conflicting item exists. Refresh and review again.",
        RequestFailedException failed => "Azure Storage request failed (HTTP " + failed.Status + ", " + failed.ErrorCode + "). Check connectivity and source access.",
        _ => error.Message
    };
}
