using System.IO;

namespace Surf2.Storage.Relational.Packages;

internal static class PackageLocalFiles
{
    internal static string Entry(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string path = relativePath.Replace('\\', '/');
        if (path.Length > 4096 || path.StartsWith('/') || path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException("Local package paths must be bounded relative paths.");
        foreach (string component in path.Split('/'))
        {
            if (component.Length == 0 || component is "." or ".." || component.EndsWith(' ') || component.EndsWith('.') ||
                component.Any(c => c < 32 || c is '<' or '>' or '"' or '|' or '?' or '*') ||
                component.StartsWith("connection-settings", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe or bootstrap-configuration local package path.");
            string stem = component.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                    (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')))
                throw new InvalidDataException("Reserved local package path.");
        }
        try { _ = new System.Text.UnicodeEncoding(false, false, true).GetByteCount(path); }
        catch (System.Text.EncoderFallbackException ex) { throw new InvalidDataException("Invalid Unicode local package path.", ex); }
        return "local-files/" + path;
    }
}
