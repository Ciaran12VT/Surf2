using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Surf2.Storage.Relational.Index;

public sealed class IndexDocumentTooLargeException : IOException
{
    public IndexDocumentTooLargeException() : base("The document exceeds this operation's supported size.") { }
}

public sealed class IndexTextMatcher
{
    private readonly string _query;
    private readonly Regex? _regex;
    public IndexTextMatcher(string query, bool useRegex, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length > 65536) throw new ArgumentException("Search query exceeds the supported size.");
        _query = query;
        TimeSpan duration = timeout ?? TimeSpan.FromMilliseconds(250);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (useRegex) _regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, duration);
    }
    public bool IsMatch(string text) => _regex?.IsMatch(text) ?? text.Contains(_query, StringComparison.OrdinalIgnoreCase);

    public static async Task<string> ReadBoundedAsync(TextReader reader, int maximumCharacters, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (maximumCharacters < 1 || maximumCharacters > RelationalContentStore.MaximumSingleContentBytes / 2)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        char[] buffer = new char[Math.Min(16384, maximumCharacters + 1)];
        var text = new StringBuilder(Math.Min(maximumCharacters, 16384));
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if ((long)text.Length + count > maximumCharacters) throw new IndexDocumentTooLargeException();
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}

// A locked read lease spans parse/publication. No source writes, discovery or root-model loads.
public sealed class IndexedFileRead : IAsyncDisposable
{
    private readonly FileStream _stream;
    private IndexedFileRead(FileStream stream, string text, FileFingerprint fingerprint, string encoding)
    {
        _stream = stream; Text = text; Fingerprint = fingerprint; EncodingName = encoding;
    }
    public string Text { get; }
    public FileFingerprint Fingerprint { get; }
    public string EncodingName { get; }
    public static async Task<IndexedFileRead> OpenAsync(string path, int maximumCharacters, CancellationToken token = default)
    {
        var before = new FileInfo(path);
        before.Refresh();
        DateTime write = before.LastWriteTimeUtc;
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length > RelationalContentStore.MaximumSingleContentBytes) throw new IndexDocumentTooLargeException();
            long length = stream.Length;
            byte[] hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 16384, leaveOpen: true);
            string text = await IndexTextMatcher.ReadBoundedAsync(reader, maximumCharacters, token).ConfigureAwait(false);
            var after = new FileInfo(path);
            after.Refresh();
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != write)
                throw new IOException("Source changed during its read.");
            return new(stream, text, new(Convert.ToHexString(hash), length, new DateTimeOffset(write, TimeSpan.Zero)),
                reader.CurrentEncoding.WebName);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
