namespace Surf2.Models;

public sealed class AppSettings
{
    public CodeWindowSettings CodeWindows { get; set; } = new();

    public bool EnsureDefaults()
    {
        bool changed = false;
        CodeWindows ??= new CodeWindowSettings();
        changed |= CodeWindows.EnsureDefaultBackcolorEntries();
        return changed;
    }

    public AppSettings Clone()
    {
        CodeWindows ??= new CodeWindowSettings();
        CodeWindows.BackcolorsByExtension ??= [];

        return new AppSettings
        {
            CodeWindows = new CodeWindowSettings
            {
                DefaultBackcolor = CodeWindows.DefaultBackcolor,
                BackcolorsByExtension = CodeWindows.BackcolorsByExtension
                    .Select(setting => new ExtensionBackcolorSetting
                    {
                        Extension = setting.Extension,
                        Backcolor = setting.Backcolor
                    })
                    .ToList()
            }
        };
    }
}
