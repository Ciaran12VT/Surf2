namespace Surf2.Models;

public sealed class AppSettings
{
    public CodeWindowSettings CodeWindows { get; set; } = new();

    public ReferenceHighlightSettings ReferenceHighlights { get; set; } = new();

    public DiagramImageSettings DiagramImages { get; set; } = new();

    public bool LoadMostRecentWorkbenchOnStartup { get; set; }

    public bool EnsureDefaults()
    {
        bool changed = false;
        CodeWindows ??= new CodeWindowSettings();
        ReferenceHighlights ??= new ReferenceHighlightSettings();
        if (DiagramImages == null)
        {
            DiagramImages = new DiagramImageSettings();
            changed = true;
        }

        if (DiagramImages.Images == null)
        {
            DiagramImages.Images = [];
            changed = true;
        }

        changed |= CodeWindows.EnsureDefaultBackcolorEntries();
        changed |= ReferenceHighlights.EnsureDefaultStyleEntries();
        return changed;
    }

    public AppSettings Clone()
    {
        CodeWindows ??= new CodeWindowSettings();
        ReferenceHighlights ??= new ReferenceHighlightSettings();
        DiagramImages ??= new DiagramImageSettings();
        CodeWindows.BackcolorsByExtension ??= [];
        ReferenceHighlights.Styles ??= [];
        DiagramImages.Images ??= [];

        return new AppSettings
        {
            LoadMostRecentWorkbenchOnStartup = LoadMostRecentWorkbenchOnStartup,
            CodeWindows = new CodeWindowSettings
            {
                DefaultBackcolor = CodeWindows.DefaultBackcolor,
                BackcolorsByExtension = CodeWindows.BackcolorsByExtension
                    .Select(setting => new ExtensionBackcolorSetting
                    {
                        Extension = setting.Extension,
                        Backcolor = setting.Backcolor,
                        Language = setting.Language
                    })
                    .ToList()
            },
            ReferenceHighlights = new ReferenceHighlightSettings
            {
                Styles = ReferenceHighlights.Styles
                    .Select(setting => setting.Clone())
                    .ToList()
            },
            DiagramImages = DiagramImages.Clone()
        };
    }
}
