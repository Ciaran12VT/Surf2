namespace Surf2.Models;

public sealed class AppSettings
{
    public CodeWindowSettings CodeWindows { get; set; } = new();

    public ReferenceHighlightSettings ReferenceHighlights { get; set; } = new();

    public DiagramImageSettings DiagramImages { get; set; } = new();

    public KeyboardShortcutSettings KeyboardShortcuts { get; set; } = new();

    public bool LoadMostRecentWorkbenchOnStartup { get; set; }

    public bool EnsureDefaults()
    {
        bool changed = false;
        CodeWindows ??= new CodeWindowSettings();
        ReferenceHighlights ??= new ReferenceHighlightSettings();
        if (KeyboardShortcuts == null)
        {
            KeyboardShortcuts = new KeyboardShortcutSettings();
            changed = true;
        }
        else
        {
            changed |= KeyboardShortcuts.EnsureDefaults();
        }

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
        KeyboardShortcuts ??= new KeyboardShortcutSettings();
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
            DiagramImages = DiagramImages.Clone(),
            KeyboardShortcuts = KeyboardShortcuts.Clone()
        };
    }
}

public sealed class KeyboardShortcutSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public bool EnableCanvasCtrlMousePanning { get; set; } = true;

    public bool EnableTabCtrlMouseScrolling { get; set; } = true;

    public bool EnableTabCtrlShiftMouseAutoscrolling { get; set; } = true;

    public bool EnableCodeViewCtrlPlusMinusNavigation { get; set; } = true;

    public bool EnableCtrlNumberViewSwitching { get; set; } = true;

    public bool EnsureDefaults()
    {
        if (Version >= CurrentVersion)
        {
            return false;
        }

        EnableCodeViewCtrlPlusMinusNavigation = true;
        EnableCtrlNumberViewSwitching = true;
        Version = CurrentVersion;
        return true;
    }

    public KeyboardShortcutSettings Clone()
    {
        return new KeyboardShortcutSettings
        {
            Version = Version,
            EnableCanvasCtrlMousePanning = EnableCanvasCtrlMousePanning,
            EnableTabCtrlMouseScrolling = EnableTabCtrlMouseScrolling,
            EnableTabCtrlShiftMouseAutoscrolling = EnableTabCtrlShiftMouseAutoscrolling,
            EnableCodeViewCtrlPlusMinusNavigation = EnableCodeViewCtrlPlusMinusNavigation,
            EnableCtrlNumberViewSwitching = EnableCtrlNumberViewSwitching
        };
    }
}
