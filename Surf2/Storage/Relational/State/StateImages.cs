using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

internal static class StateImages
{
    internal sealed record Prepared(IReadOnlyDictionary<int, byte[]> Pasted,
        IReadOnlyDictionary<int, PastedImageFallback> Fallbacks);
    public static byte[]? Decode(string encoded, StateLimits limits, StateBudget budget, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        // Bound the allocation before decoding even if the source contains base64 whitespace.
        if ((long)encoded.Length * 3 / 4 > limits.MaximumAssetBytes + 2L)
            throw new InvalidDataException("An encoded image exceeds its byte budget.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException ex) { throw new InvalidDataException("Invalid image base64; no object was omitted.", ex); }
        budget.Asset(bytes.LongLength);
        Validate(bytes, limits, cancellationToken: cancellationToken);
        return bytes;
    }

    public static string Validate(byte[] bytes, StateLimits limits, bool requirePng = false, CancellationToken cancellationToken = default)
    {
        if (bytes.Length == 0 || bytes.Length > limits.MaximumAssetBytes)
            throw new InvalidDataException("Invalid or oversized image bytes.");
        bool png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (requirePng && !png) throw new InvalidDataException("A pasted image is not a PNG.");
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            long pixels = 0;
            foreach (var frame in decoder.Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pixels = checked(pixels + (long)frame.PixelWidth * frame.PixelHeight);
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || pixels > limits.MaximumImagePixels)
                    throw new InvalidDataException("An image exceeds its decoded pixel budget.");
                // Force decoding in small row buffers, without changing the authoritative encoded bytes.
                int stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
                var row = new byte[stride];
                for (int y = 0; y < frame.PixelHeight; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    frame.CopyPixels(new System.Windows.Int32Rect(0, y, frame.PixelWidth, 1), row, stride, 0);
                }
            }
            return png ? "image/png" : decoder.CodecInfo.MimeTypes.Split(',')[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException or COMException or OverflowException)
        {
            throw new InvalidDataException("Image bytes could not be validated; no object was omitted.", ex);
        }
    }

    public static async Task<Prepared> ReadPastedAsync(DiagramDocument? diagram,
        string directory, StateLimits limits, StateBudget budget,
        Func<string, CancellationToken, Task<byte[]?>> definitionBytes, CancellationToken ct,
        Func<string, CancellationToken, Task<byte[]?>>? frozenPngReader = null)
    {
        var result = new Dictionary<int, byte[]>();
        var fallbacks = new Dictionary<int, PastedImageFallback>();
        if (diagram == null) return new(result, fallbacks);
        for (int i = 0; i < diagram.Objects.Count; i++)
        {
            string name = diagram.Objects[i].PastedImageFileName;
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                string originalPath = LeafPath(directory, name);
                byte[]? frozen = frozenPngReader == null ? null : await frozenPngReader(originalPath, ct);
                if (frozen != null)
                {
                    budget.Asset(frozen.LongLength);
                    Validate(frozen, limits, true, ct);
                    result.Add(i, frozen);
                    continue;
                }
                string path = ResolveLeaf(directory, name);
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                // Verify the opened handle too: a replacement reparse point must not escape the allowed directory.
                if (OperatingSystem.IsWindows())
                {
                    var final = new StringBuilder(32768);
                    uint length = GetFinalPathNameByHandle(file.SafeFileHandle, final, (uint)final.Capacity, 0);
                    if (length == 0 || length >= final.Capacity ||
                        !string.Equals(final.ToString(), "\\\\?\\" + path, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The opened pasted image is outside its protected directory.");
                }
                budget.Asset(file.Length);
                if (file.Length > int.MaxValue) throw new InvalidDataException("Pasted image is too large.");
                var bytes = new byte[(int)file.Length];
                await file.ReadExactlyAsync(bytes, ct);
                if (file.Position != file.Length) throw new InvalidDataException("The pasted image changed while reading.");
                Validate(bytes, limits, true, ct);
                result.Add(i, bytes);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                byte[]? fallback = Decode(diagram.Objects[i].ImageDataBase64, limits, budget, ct);
                var resolution = PastedImageResolution.MissingUseInline;
                if (fallback == null)
                {
                    fallback = await definitionBytes(diagram.Objects[i].ImageDefinitionId, ct);
                    resolution = PastedImageResolution.MissingUseDefinition;
                }
                if (fallback == null)
                    throw new InvalidDataException($"Pasted image for object ordinal {i} is missing and has no embedded/definition fallback; import must not publish.", ex);
                Validate(fallback, limits, cancellationToken: ct);
                fallbacks.Add(i, new(resolution, fallback));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new InvalidDataException($"Pasted image for object ordinal {i} is missing, inaccessible, or invalid; import must not publish.", ex);
            }
        }
        return new(result, fallbacks);
    }

    internal static string ResolveLeaf(string directory, string name)
    {
        string path = LeafPath(directory, name);
        string root = Path.GetDirectoryName(path)!;
        for (var ancestor = new DirectoryInfo(root); ancestor != null; ancestor = ancestor.Parent)
            if ((File.GetAttributes(ancestor.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Pasted-image directories cannot contain reparse points.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Pasted-image path leaves its protected directory.");
        return path;
    }

    internal static string LeafPath(string directory, string name)
    {
        if (name is "." or ".." || name.Length == 0 || name != Path.GetFileName(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') ||
            name.EndsWith('.') || name.EndsWith(' ') || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PastedImageFileName must be a leaf PNG filename.");
        string stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("PastedImageFileName cannot address a Windows device.");
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(root), "PastedDiagramImages", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Supply the dedicated app-data/PastedDiagramImages directory.");
        string path = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pasted-image path leaves its protected directory.");
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
