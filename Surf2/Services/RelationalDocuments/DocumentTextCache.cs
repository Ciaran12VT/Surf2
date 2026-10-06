using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalDocuments;

/// <summary>Only immutable text is cached. Editors, previews and their copies have separate residency.</summary>
public sealed class DocumentTextCache : IAsyncDisposable
{
    private readonly ByteBoundedCache<DocumentTextKey, SelectedDocumentText> _cache;
    private readonly SingleFlight<DocumentTextKey, SelectedDocumentText> _reads;
    private readonly DocumentOpenLimits _limits;
    private readonly QueryDiagnostics? _diagnostics;
    private readonly Guid _epoch;
    private static readonly QueryFingerprint DefinitionFingerprint = QueryFingerprint.FromTemplate("DocumentTextCache.definition-output.v1");
    private static readonly QueryFingerprint TableFingerprint = QueryFingerprint.FromTemplate("DocumentTextCache.table-output.v1");

    public DocumentTextCache(DocumentOpenLimits? limits = null, QueryDiagnostics? diagnostics = null, Guid epoch = default)
    {
        _limits = limits ?? new();
        _limits.Validate();
        if (diagnostics != null && epoch == Guid.Empty) throw new ArgumentException("Document diagnostics require the session epoch.", nameof(epoch));
        _diagnostics = diagnostics;
        _epoch = epoch;
        _cache = new(_limits.CacheBytes, _limits.CacheEntries);
        _reads = new(_limits.ConcurrentReads);
    }
    public ByteCacheUsage Usage => _cache.Usage;
    public int ActiveReads => _reads.ActiveCount;
    internal bool TryDescribe(DocumentTextKey key, out long textBytes)
    {
        using var lease = _cache.TryAcquire(key);
        textBytes = lease?.Value.EstimatedBytes ?? 0;
        return lease != null;
    }

    public Task<SelectedDocumentText> ReadAsync(DocumentTextKey key,
        Func<CancellationToken, Task<string>> read, CancellationToken ct = default) => _reads.RunAsync(key, async token =>
    {
        bool table = key.RendererPolicy == RelationalIndexStore.TableRendererVersion;
        using var measurement = _diagnostics?.Begin(table ? QueryOperation.TableMetadataRead : QueryOperation.DefinitionRead,
            table ? TableFingerprint : DefinitionFingerprint, _epoch);
        var outcome = QueryOutcome.Completed;
        try
        {
            using var lease = _cache.TryAcquire(key);
            if (lease != null) { measurement?.RecordCacheHit(); return lease.Value; }
            string text = await read(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            long bytes = checked((long)text.Length * 2);
            if (bytes > _limits.MaximumTextBytes) throw new DocumentTextLimitException(_limits.MaximumTextBytes);
            // This measures one validated document output, not transport bytes or
            // SQL roundtrips. Joined callers never start another measurement.
            measurement?.RecordRead(1, bytes, queryCount: 0);
            var result = new SelectedDocumentText(text, bytes);
            long keyBytes = checked(192 + (long)(key.PhysicalPath?.Length ?? 0) * 2);
            _cache.TryStore(key, result, checked(bytes + keyBytes + (long)key.RendererPolicy.Length * 2 + 256));
            return result;
        }
        catch (OperationCanceledException) { outcome = QueryOutcome.Cancelled; throw; }
        catch (Exception) { outcome = QueryOutcome.Failed; throw; }
        finally { measurement?.Complete(outcome); }
    }, ct);

    public async ValueTask DisposeAsync()
    {
        await _reads.DisposeAsync().ConfigureAwait(false);
        _cache.Dispose();
    }
}
