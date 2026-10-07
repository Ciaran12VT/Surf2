namespace Surf2.Models;

public sealed class AppSettings
{
    public CodeWindowSettings CodeWindows { get; set; } = new();

    public ReferenceHighlightSettings ReferenceHighlights { get; set; } = new();

    public DiagramImageSettings DiagramImages { get; set; } = new();

    public KeyboardShortcutSettings KeyboardShortcuts { get; set; } = new();

    public ResourceComparisonSettings ResourceComparison { get; set; } = new();

    public DiagnosticsSettings Diagnostics { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public bool LoadMostRecentWorkbenchOnStartup { get; set; }

    public bool EnsureDefaults()
    {
        bool changed = false;
        CodeWindows ??= new CodeWindowSettings();
        ReferenceHighlights ??= new ReferenceHighlightSettings();
        if (ResourceComparison == null)
        {
            ResourceComparison = new ResourceComparisonSettings();
            changed = true;
        }

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

        changed |= DiagramImages.EnsureDefaults();

        if (Diagnostics == null)
        {
            Diagnostics = new DiagnosticsSettings();
            changed = true;
        }

        if (Appearance == null)
        {
            Appearance = new AppearanceSettings();
            changed = true;
        }
        else
        {
            changed |= Appearance.EnsureDefaults();
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
        ResourceComparison ??= new ResourceComparisonSettings();
        Diagnostics ??= new DiagnosticsSettings();
        Appearance ??= new AppearanceSettings();
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
            KeyboardShortcuts = KeyboardShortcuts.Clone(),
            ResourceComparison = ResourceComparison.Clone(),
            Diagnostics = Diagnostics.Clone(),
            Appearance = Appearance.Clone()
        };
    }
}

public sealed class AppearanceSettings
{
    public const string LightTheme = "Light";

    public const string DarkTheme = "Dark";

    public string Theme { get; set; } = LightTheme;

    public bool EnsureDefaults()
    {
        string normalizedTheme = NormalizeTheme(Theme);
        if (string.Equals(Theme, normalizedTheme, StringComparison.Ordinal))
        {
            return false;
        }

        Theme = normalizedTheme;
        return true;
    }

    public AppearanceSettings Clone()
    {
        return new AppearanceSettings
        {
            Theme = NormalizeTheme(Theme)
        };
    }

    public static string NormalizeTheme(string? theme)
    {
        return string.Equals(theme, DarkTheme, StringComparison.OrdinalIgnoreCase)
            ? DarkTheme
            : LightTheme;
    }
}

public sealed class ResourceComparisonSettings
{
    public bool IgnoreWhitespaceByDefault { get; set; }

    public bool IgnoreCaseByDefault { get; set; }

    public ResourceComparisonSettings Clone()
    {
        return new ResourceComparisonSettings
        {
            IgnoreWhitespaceByDefault = IgnoreWhitespaceByDefault,
            IgnoreCaseByDefault = IgnoreCaseByDefault
        };
    }
}

public sealed class DiagnosticsSettings
{
    public bool EnableInternalLogging { get; set; } = true;

    public DiagnosticsSettings Clone()
    {
        return new DiagnosticsSettings
        {
            EnableInternalLogging = EnableInternalLogging
        };
    }
}

public sealed class KeyboardShortcutSettings
{
    public const int CurrentVersion = 7;

    public int Version { get; set; } = CurrentVersion;

    public bool EnableCanvasCtrlMousePanning { get; set; } = true;

    public bool EnableTabCtrlMouseScrolling { get; set; } = true;

    public bool EnableTabCtrlShiftMouseAutoscrolling { get; set; } = true;

    public bool EnableCodeShiftMouseAutoscrolling { get; set; } = true;

    public bool EnableCodeCtrlShiftMouseScrollbarLockedScrolling { get; set; } = true;

    public bool EnableCodeCanvasShiftMousePanning { get; set; } = true;

    public bool EnableCodeCanvasCtrlShiftMouseZooming { get; set; } = true;

    public bool EnableCodeViewCtrlPlusMinusNavigation { get; set; } = true;

    public bool EnableCtrlNumberViewSwitching { get; set; } = true;

    public bool EnableCodeTabCtrlASNavigation { get; set; } = true;

    public bool EnableDiagramCtrlQSidebarToggle { get; set; } = true;

    public bool EnableDiagramCtrlWWorkflowSidebar { get; set; } = true;

    public bool EnableDiagramShiftMousePanning { get; set; } = true;

    public bool EnableDiagramCtrlShiftMouseZooming { get; set; } = true;

    public bool EnsureDefaults()
    {
        if (Version >= CurrentVersion)
        {
            return false;
        }

        if (Version < 2)
        {
            EnableCodeViewCtrlPlusMinusNavigation = true;
            EnableCtrlNumberViewSwitching = true;
        }

        EnableCodeTabCtrlASNavigation = true;
        EnableDiagramCtrlQSidebarToggle = true;
        EnableDiagramCtrlWWorkflowSidebar = true;
        if (Version < 4)
        {
            EnableCodeShiftMouseAutoscrolling = EnableTabCtrlShiftMouseAutoscrolling;
            EnableCodeCtrlShiftMouseScrollbarLockedScrolling = true;
        }

        if (Version < 7)
        {
            EnableCodeCanvasShiftMousePanning = true;
            EnableCodeCanvasCtrlShiftMouseZooming = true;
        }

        EnableDiagramShiftMousePanning = true;
        if (Version < 6)
        {
            EnableDiagramCtrlShiftMouseZooming = true;
        }

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
            EnableCodeShiftMouseAutoscrolling = EnableCodeShiftMouseAutoscrolling,
            EnableCodeCtrlShiftMouseScrollbarLockedScrolling = EnableCodeCtrlShiftMouseScrollbarLockedScrolling,
            EnableCodeCanvasShiftMousePanning = EnableCodeCanvasShiftMousePanning,
            EnableCodeCanvasCtrlShiftMouseZooming = EnableCodeCanvasCtrlShiftMouseZooming,
            EnableCodeViewCtrlPlusMinusNavigation = EnableCodeViewCtrlPlusMinusNavigation,
            EnableCtrlNumberViewSwitching = EnableCtrlNumberViewSwitching,
            EnableCodeTabCtrlASNavigation = EnableCodeTabCtrlASNavigation,
            EnableDiagramCtrlQSidebarToggle = EnableDiagramCtrlQSidebarToggle,
            EnableDiagramCtrlWWorkflowSidebar = EnableDiagramCtrlWWorkflowSidebar,
            EnableDiagramShiftMousePanning = EnableDiagramShiftMousePanning,
            EnableDiagramCtrlShiftMouseZooming = EnableDiagramCtrlShiftMouseZooming
        };
    }
}
