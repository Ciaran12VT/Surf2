using System.IO;

namespace Surf2.Models;

public sealed class CodeWindowSettings
{
    private static readonly string[] DefaultExtensions = [".cs", ".vb", ".sql", ".js", ".xml"];

    public string DefaultBackcolor { get; set; } = "#FFFFFF";

    public List<ExtensionBackcolorSetting> BackcolorsByExtension { get; set; } = [];

    public bool EnsureDefaultBackcolorEntries()
    {
        bool changed = false;
        BackcolorsByExtension ??= [];

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
                Backcolor = DefaultBackcolor
            });
            changed = true;
        }

        return changed;
    }

    public string GetBackcolorForFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        ExtensionBackcolorSetting? setting = BackcolorsByExtension.FirstOrDefault(candidate =>
            string.Equals(candidate.Extension, extension, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(setting?.Backcolor) ? DefaultBackcolor : setting.Backcolor;
    }
}
