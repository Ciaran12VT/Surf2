using System.IO;
using PdfSharp.Fonts;

namespace Surf2.Services;

internal sealed class DocumentationPdfFontResolver : IFontResolver
{
    public static DocumentationPdfFontResolver Instance { get; } = new();

    private readonly Dictionary<string, FontFamilyFiles> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _facePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _fontCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fontCacheLock = new();

    private DocumentationPdfFontResolver()
    {
        string fontsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Fonts");

        AddFamily(
            "Segoe UI",
            fontsDirectory,
            regular: "segoeui.ttf",
            bold: "segoeuib.ttf",
            italic: "segoeuii.ttf",
            boldItalic: "segoeuiz.ttf",
            aliases: ["SegoeUI", "Segoe UI Symbol", "Segoe UI Historic", "sans-serif", "sans serif"]);
        AddFamily(
            "Arial",
            fontsDirectory,
            regular: "arial.ttf",
            bold: "arialbd.ttf",
            italic: "ariali.ttf",
            boldItalic: "arialbi.ttf",
            aliases: ["Helvetica"]);
        AddFamily(
            "Calibri",
            fontsDirectory,
            regular: "calibri.ttf",
            bold: "calibrib.ttf",
            italic: "calibrii.ttf",
            boldItalic: "calibriz.ttf");
        AddFamily(
            "Times New Roman",
            fontsDirectory,
            regular: "times.ttf",
            bold: "timesbd.ttf",
            italic: "timesi.ttf",
            boldItalic: "timesbi.ttf",
            aliases: ["Times", "serif"]);
        AddFamily(
            "Courier New",
            fontsDirectory,
            regular: "cour.ttf",
            bold: "courbd.ttf",
            italic: "couri.ttf",
            boldItalic: "courbi.ttf",
            aliases: ["Courier", "monospace"]);
        AddFamily(
            "Verdana",
            fontsDirectory,
            regular: "verdana.ttf",
            bold: "verdanab.ttf",
            italic: "verdanai.ttf",
            boldItalic: "verdanaz.ttf");
        AddFamily(
            "Tahoma",
            fontsDirectory,
            regular: "tahoma.ttf",
            bold: "tahomabd.ttf",
            italic: null,
            boldItalic: null);
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        foreach (string candidate in GetFamilyCandidates(familyName))
        {
            if (_families.TryGetValue(candidate, out FontFamilyFiles? familyFiles) &&
                TryCreateFontResolverInfo(familyFiles, bold, italic, out FontResolverInfo? info))
            {
                return info;
            }
        }

        return TryCreateFontResolverInfo(GetFallbackFamily(), bold, italic, out FontResolverInfo? fallbackInfo)
            ? fallbackInfo
            : null;
    }

    public byte[]? GetFont(string faceName)
    {
        if (!_facePaths.TryGetValue(faceName, out string? path))
        {
            return null;
        }

        lock (_fontCacheLock)
        {
            if (!_fontCache.TryGetValue(faceName, out byte[]? bytes))
            {
                bytes = File.ReadAllBytes(path);
                _fontCache[faceName] = bytes;
            }

            return bytes;
        }
    }

    private void AddFamily(
        string familyName,
        string fontsDirectory,
        string regular,
        string? bold,
        string? italic,
        string? boldItalic,
        IEnumerable<string>? aliases = null)
    {
        var files = new FontFamilyFiles(
            RegisterFace(familyName, "regular", Path.Combine(fontsDirectory, regular)),
            RegisterFace(familyName, "bold", bold == null ? null : Path.Combine(fontsDirectory, bold)),
            RegisterFace(familyName, "italic", italic == null ? null : Path.Combine(fontsDirectory, italic)),
            RegisterFace(familyName, "bold-italic", boldItalic == null ? null : Path.Combine(fontsDirectory, boldItalic)));

        if (files.Regular == null)
        {
            return;
        }

        _families[NormalizeFamilyName(familyName)] = files;
        if (aliases == null)
        {
            return;
        }

        foreach (string alias in aliases)
        {
            _families[NormalizeFamilyName(alias)] = files;
        }
    }

    private string? RegisterFace(string familyName, string styleName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        string faceName = $"{NormalizeFamilyName(familyName)}#{styleName}";
        _facePaths[faceName] = path!;
        return faceName;
    }

    private bool TryCreateFontResolverInfo(
        FontFamilyFiles familyFiles,
        bool bold,
        bool italic,
        out FontResolverInfo? info)
    {
        string? faceName;
        bool simulateBold = false;
        bool simulateItalic = false;

        if (bold && italic)
        {
            faceName = familyFiles.BoldItalic ?? familyFiles.Bold ?? familyFiles.Italic ?? familyFiles.Regular;
            simulateBold = faceName == familyFiles.Italic || faceName == familyFiles.Regular;
            simulateItalic = faceName == familyFiles.Bold || faceName == familyFiles.Regular;
        }
        else if (bold)
        {
            faceName = familyFiles.Bold ?? familyFiles.Regular;
            simulateBold = faceName == familyFiles.Regular;
        }
        else if (italic)
        {
            faceName = familyFiles.Italic ?? familyFiles.Regular;
            simulateItalic = faceName == familyFiles.Regular;
        }
        else
        {
            faceName = familyFiles.Regular;
        }

        if (string.IsNullOrWhiteSpace(faceName))
        {
            info = null;
            return false;
        }

        info = new FontResolverInfo(faceName, simulateBold, simulateItalic);
        return true;
    }

    private FontFamilyFiles GetFallbackFamily()
    {
        if (_families.TryGetValue(NormalizeFamilyName("Segoe UI"), out FontFamilyFiles? segoe))
        {
            return segoe;
        }

        if (_families.TryGetValue(NormalizeFamilyName("Arial"), out FontFamilyFiles? arial))
        {
            return arial;
        }

        return _families.Values.First();
    }

    private static IEnumerable<string> GetFamilyCandidates(string familyName)
    {
        foreach (string candidate in familyName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return NormalizeFamilyName(candidate);
        }
    }

    private static string NormalizeFamilyName(string familyName)
    {
        return familyName
            .Trim()
            .Trim('"', '\'')
            .Replace("  ", " ", StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private sealed record FontFamilyFiles(
        string? Regular,
        string? Bold,
        string? Italic,
        string? BoldItalic);
}
