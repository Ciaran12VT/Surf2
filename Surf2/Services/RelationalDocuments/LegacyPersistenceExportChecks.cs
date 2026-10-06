using System.IO;
using System.Text.Json;

namespace Surf2.Services.RelationalDocuments;

/// <summary>Pure format-1 streaming checks; no SQL, UI, or source files.</summary>
public static class LegacyPersistenceExportChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        var passed = new List<string>();
        string text = new string('x', 16 * 1024) + "\U0001F600\r\n\t\"\\\u0000" + "{\"raw\":\"\\uD800\"}" + new string('y', 20 * 1024);
        foreach (int chunk in new[] { 1, 7, 16 * 1024 + 1 })
        {
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject(); writer.WritePropertyName("PayloadJson");
                using var input = new ChunkReader(text, chunk);
                await LegacyPersistenceExport.WriteTextValueAsync(writer, input, (long)text.Length * 2, ct);
                writer.WriteEndObject(); await writer.FlushAsync(ct);
            }
            using var json = JsonDocument.Parse(output.ToArray());
            Check(json.RootElement.GetProperty("PayloadJson").GetString() == text,
                "Streamed payload preserves all characters at chunk size " + chunk, passed);
        }
        foreach (string invalid in new[] { "x\uD800", "\uDC00x", "\uD800x" })
        {
            await ExpectInvalidAsync(invalid, (long)invalid.Length * 2, ct);
            passed.Add("Unpaired UTF-16 is explicitly rejected instead of silently replaced");
        }
        await ExpectInvalidAsync("actual", 2, ct);
        await ExpectInvalidAsync("actual", 100, ct);
        passed.Add("Declared-size overruns and underruns reject a changed payload");
        using (var output = new MemoryStream())
        using (var budget = new LegacyPersistenceExport.WriteBudgetStream(output, 3))
        {
            await budget.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            try { await budget.WriteAsync(new byte[] { 4 }, ct); throw new InvalidOperationException("The byte budget was ignored."); }
            catch (InvalidDataException) { }
            Check(output.Length == 3 && budget.BytesWritten == 3, "Output budgets reject before an oversized write reaches the archive", passed);
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            using var output = new MemoryStream(); using var writer = new Utf8JsonWriter(output);
            using var input = new ChunkReader("selected", 1);
            try
            {
                await LegacyPersistenceExport.WriteTextValueAsync(writer, input, 16, cancelled.Token);
                throw new InvalidOperationException("Cancelled text export continued.");
            }
            catch (OperationCanceledException) { passed.Add("Cancellation interrupts the streamed text operation"); }
        }
        foreach (string denied in new[]
        {
            "connection-settings.json", "a/CONNECTION-SETTINGS.JSON.bak", "a/connection-settings.backup.json",
            "InternalLogs/log.txt", "a/internalLOGS/log.txt", "Migrations/source/library.json", "a/MIGRATIONS/images/source.png",
            "export.zip.partial", "export.zip.pending", "cache/file.tmp", "cache/file.TEMP"
        })
            Check(LegacyPersistenceExport.LocalEntry(denied) == null, "Excluded local path: " + denied, passed);
        foreach (string unsafePath in new[]
        {
            "../outside.json", "/absolute.json", "C:/root.json", "a//empty.json", "a/name. ", "a/name.",
            "a/name?", "a/NUL.txt", "a/COM1.png", "a/LPT\u00b9.txt", "a/\uD800", "../connection-settings.json",
            "connection-settings/name?", "Migrations/../outside.json", "InternalLogs/name."
        })
        {
            try { _ = LegacyPersistenceExport.LocalEntry(unsafePath); throw new InvalidOperationException("Unsafe local path was accepted."); }
            catch (InvalidDataException) { passed.Add("Unsafe local package path explicitly rejected"); }
        }
        Check(LegacyPersistenceExport.LocalEntry("PastedImages\\source.png") == "local-files/PastedImages/source.png" &&
            LegacyPersistenceExport.LocalEntry("preferences.json") == "local-files/preferences.json",
            "Safe authoritative local files retain canonical package paths", passed);
        return passed.AsReadOnly();
    }

    private static async Task ExpectInvalidAsync(string text, long declaredBytes, CancellationToken ct)
    {
        using var output = new MemoryStream(); using var writer = new Utf8JsonWriter(output);
        using var input = new ChunkReader(text, 1);
        try { await LegacyPersistenceExport.WriteTextValueAsync(writer, input, declaredBytes, ct); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid text export was accepted.");
    }
    private static void Check(bool valid, string label, List<string> passed)
    {
        if (!valid) throw new InvalidOperationException(label);
        passed.Add(label);
    }
    private sealed class ChunkReader(string text, int chunk) : TextReader
    {
        private int _position;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(chunk, Math.Min(buffer.Length, text.Length - _position));
            text.AsMemory(_position, count).CopyTo(buffer); _position += count;
            return ValueTask.FromResult(count);
        }
    }
}
