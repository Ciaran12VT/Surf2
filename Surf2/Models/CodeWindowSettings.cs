using System.IO;

namespace Surf2.Models;

public sealed class CodeWindowSettings
{
    private static readonly string[] DefaultExtensions = [".cs", ".vb", ".vbs", ".sql", ".js", ".xml", ".csv"];

    public const string PlainTextLanguage = "Plain Text";
    public const string CSharpLanguage = "C#";
    public const string VisualBasicLanguage = "VB";
    public const string SqlServerLanguage = "SQL Server";
    public const string JavaScriptLanguage = "JavaScript";
    public const string XmlLanguage = "XML";
    public const string XlsLanguage = "XLS";

    public static readonly string[] SupportedLanguages =
    [
        PlainTextLanguage,
        CSharpLanguage,
        VisualBasicLanguage,
        SqlServerLanguage,
        JavaScriptLanguage,
        XmlLanguage,
        XlsLanguage
    ];

    public string DefaultBackcolor { get; set; } = "#FFFFFF";

    public List<ExtensionBackcolorSetting> BackcolorsByExtension { get; set; } = [];

    public bool EnsureDefaultBackcolorEntries()
    {
        bool changed = false;
        BackcolorsByExtension ??= [];

        foreach (ExtensionBackcolorSetting setting in BackcolorsByExtension)
        {
            string normalizedLanguage = string.IsNullOrWhiteSpace(setting.Language)
                ? GetDefaultLanguageForExtension(setting.Extension)
                : NormalizeLanguage(setting.Language);
            if (!string.Equals(setting.Language, normalizedLanguage, StringComparison.Ordinal))
            {
                setting.Language = normalizedLanguage;
                changed = true;
            }
        }

        foreach (string extension in DefaultExtensions)
        {
            if (BackcolorsByExtension.Any(setting =>
                    string.Equals(setting.Extension, extension, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            BackcolorsByExtension.Add(new ExtensionBackcolorSetting
            {
                Extension = extension,
                Backcolor = DefaultBackcolor,
                Language = GetDefaultLanguageForExtension(extension)
            });
            changed = true;
        }

        return changed;
    }

    public string GetBackcolorForFile(string filePath)
    {
        BackcolorsByExtension ??= [];
        string extension = Path.GetExtension(filePath);
        ExtensionBackcolorSetting? setting = BackcolorsByExtension.FirstOrDefault(candidate =>
            string.Equals(candidate.Extension, extension, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(setting?.Backcolor) ? DefaultBackcolor : setting.Backcolor;
    }

    public string GetLanguageForFile(string filePath)
    {
        BackcolorsByExtension ??= [];
        string extension = Path.GetExtension(filePath);
        ExtensionBackcolorSetting? setting = BackcolorsByExtension.FirstOrDefault(candidate =>
            string.Equals(candidate.Extension, extension, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(setting?.Language)
            ? GetDefaultLanguageForExtension(extension)
            : NormalizeLanguage(setting.Language);
    }

    public static string GetDefaultLanguageForExtension(string filePathOrExtension)
    {
        string extension = GetExtension(filePathOrExtension);
        return extension.ToLowerInvariant() switch
        {
            ".cs" => CSharpLanguage,
            ".vb" or ".vbs" => VisualBasicLanguage,
            ".sql" => SqlServerLanguage,
            ".js" => JavaScriptLanguage,
            ".xml" => XmlLanguage,
            ".csv" => XlsLanguage,
            _ => PlainTextLanguage
        };
    }

    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return PlainTextLanguage;
        }

        return SupportedLanguages.FirstOrDefault(candidate =>
            string.Equals(candidate, language.Trim(), StringComparison.OrdinalIgnoreCase)) ?? PlainTextLanguage;
    }

    public static string GetLanguageTypeLabel(string language)
    {
        return NormalizeLanguage(language) switch
        {
            CSharpLanguage => "C#",
            VisualBasicLanguage => "VB",
            SqlServerLanguage => "SQL",
            JavaScriptLanguage => "JS",
            XmlLanguage => "XML",
            XlsLanguage => "XLS",
            _ => "TEXT"
        };
    }

    private static string GetExtension(string filePathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(filePathOrExtension))
        {
            return string.Empty;
        }

        string trimmed = filePathOrExtension.Trim();
        if (trimmed.StartsWith(".", StringComparison.Ordinal) &&
            trimmed.IndexOf(Path.DirectorySeparatorChar) < 0 &&
            trimmed.IndexOf(Path.AltDirectorySeparatorChar) < 0)
        {
            return trimmed;
        }

        return Path.GetExtension(trimmed);
    }
}
