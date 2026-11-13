using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.Packages;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunPreferenceThemeChecksAsync(Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("""
{"CodeWindows":{"DefaultBackcolor":"#F0F1F2","BackcolorsByExtension":[{"Extension":".sql","Backcolor":"#DEFACE","Language":"SQL Server"}]}}
""")!;
        legacy.EnsureDefaults();
        check(legacy.Preferences.OpenDiagramDocumentationOnDoubleClick && legacy.Preferences.ShowExplorerPreview &&
            legacy.CodeWindows.GetBackcolorForFile("old.sql", true) == "#DEFACE" && legacy.CodeWindows.DefaultDarkBackcolor == "#F0F1F2",
            "Legacy settings preserve both enabled preferences and the former background in both theme slots");
        var clone = legacy.Clone();
        clone.Preferences.ShowExplorerPreview = false;
        clone.CodeWindows.BackcolorsByExtension[0].DarkBackcolor = "#102030";
        var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(clone))!;
        check(legacy.Preferences.ShowExplorerPreview && legacy.CodeWindows.GetBackcolorForFile("old.sql", true) == "#DEFACE" &&
            !roundtrip.Preferences.ShowExplorerPreview && roundtrip.CodeWindows.GetBackcolorForFile("old.sql", true) == "#102030" &&
            roundtrip.CodeWindows.GetBackcolorForFile("old.sql") == "#DEFACE",
            "Theme colors and preferences survive JSON persistence and clone without sharing mutable settings");

        await using var source = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeNewDatabaseAsync(source.DestinationConnectionString);
        // Only the isolated fixture is put back into the exact preceding preference schema.
        await source.DestinationSqlAsync("""
UPDATE surf.ApplicationPreference SET DefaultBackcolor=N'#F0F1F2';
UPDATE surf.ExtensionAppearance SET Backcolor=N'#DEFACE' WHERE Extension=N'.sql';
ALTER TABLE surf.ApplicationPreference DROP CONSTRAINT DF_ApplicationPreference_DefaultDarkBackcolor;
ALTER TABLE surf.ApplicationPreference DROP CONSTRAINT DF_ApplicationPreference_OpenDiagramDocumentation;
ALTER TABLE surf.ApplicationPreference DROP CONSTRAINT DF_ApplicationPreference_ShowExplorerPreview;
ALTER TABLE surf.ExtensionAppearance DROP CONSTRAINT DF_ExtensionAppearance_DarkBackcolor;
ALTER TABLE surf.ApplicationPreference DROP COLUMN DefaultDarkBackcolor,OpenDiagramDocumentationOnDoubleClick,ShowExplorerPreview;
ALTER TABLE surf.ExtensionAppearance DROP COLUMN DarkBackcolor;
""");
        string oldPath = Path.Combine(source.OwnedDirectory, "old-preferences.surf2.zip");
        var oldExport = await new RelationalPackageExporter(new(source.DestinationConnectionString)).ExportAsync(oldPath,
            new(AllowBlockingConsistentFallback: true));
        check(!oldExport.Manifest.Tables.Single(t => t.Table.Name == "ApplicationPreference").Table.Columns.Any(c => c.Name == "DefaultDarkBackcolor"),
            "Upgrade regression package carries the preceding schema without new preference fields");

        var session = new RelationalSession(source.DestinationConnectionString);
        var store = new RelationalStateStore(session);
        var selected = Required(await store.ReadSettingsAsync(), "automatically upgraded preferences");
        check(selected.Value.CodeWindows.DefaultDarkBackcolor == "#F0F1F2" &&
            selected.Value.CodeWindows.BackcolorsByExtension.Single(c => c.Extension == ".sql").DarkBackcolor == "#DEFACE" &&
            selected.Value.Preferences.OpenDiagramDocumentationOnDoubleClick && selected.Value.Preferences.ShowExplorerPreview,
            "Opening an older relational store automatically adds preferences and copies existing background values");
        var changed = selected.Value.Clone();
        changed.CodeWindows.DefaultDarkBackcolor = "#1C1D1E";
        changed.Preferences.OpenDiagramDocumentationOnDoubleClick = false;
        changed.Preferences.ShowExplorerPreview = false;
        changed.CodeWindows.BackcolorsByExtension.Single(c => c.Extension == ".sql").DarkBackcolor = "#102030";
        var beforeSave = Required((await new StatePreferenceAccess(session).ListExtensionAppearanceAsync()).Value, "selected appearance key");
        long sqlAppearanceKey = beforeSave.Items.Single(c => c.Extension == ".sql").Key;
        await store.SavePreferenceSelectionAsync(changed, selected.Token,
            [new(sqlAppearanceKey, changed.CodeWindows.BackcolorsByExtension.Single(c => c.Extension == ".sql"))], [], [], [], Guid.NewGuid());
        var freshSession = new RelationalSession(source.DestinationConnectionString);
        var fresh = Required(await new RelationalStateStore(freshSession).ReadSettingsAsync(), "reopened preferences");
        check(!fresh.Value.Preferences.ShowExplorerPreview && !fresh.Value.Preferences.OpenDiagramDocumentationOnDoubleClick &&
            fresh.Value.CodeWindows.DefaultDarkBackcolor == "#1C1D1E" && fresh.Value.CodeWindows.GetBackcolorForFile("new.sql", true) == "#102030",
            "Upgrade is idempotent on restart and preserves independently saved dark colors and disabled preferences");
        await using var scalar = StateAccessEdit(await new StatePreferenceAccess(freshSession).LoadStartupPreferencesAsync(), check, "new scalar preferences");
        var runtime = new AppSettings();
        scalar.Snapshot().ApplyToRuntime(runtime);
        var appearancePage = Required((await new StatePreferenceAccess(freshSession).ListExtensionAppearanceAsync()).Value, "appearance page");
        check(!runtime.Preferences.OpenDiagramDocumentationOnDoubleClick && !runtime.Preferences.ShowExplorerPreview &&
            runtime.CodeWindows.DefaultDarkBackcolor == "#1C1D1E" && appearancePage.Items.Single(c => c.Extension == ".sql").DarkBackcolor == "#102030",
            "Narrow startup and extension catalogue projections retain the new preference and theme fields");

        string newPath = Path.Combine(source.OwnedDirectory, "new-preferences.surf2.zip");
        RelationalPackageExportResult? newExport = null;
        foreach (bool previousFormat in new[] { true, false })
        {
            await using var restored = await SqlFixture.CreateAsync();
            var exported = previousFormat ? oldExport : newExport!;
            await new RelationalPackageImporter(restored.DestinationConnectionString).ImportAsync(
                previousFormat ? oldPath : newPath, PackageImportOptions(exported.ManifestSha256));
            var restoredSession = new RelationalSession(restored.DestinationConnectionString);
            var restoredStore = new RelationalStateStore(restoredSession);
            var restoredSettings = Required(await restoredStore.ReadSettingsAsync(), "restored preferences");
            var imported = restoredSettings.Value;
            check(imported.Preferences.ShowExplorerPreview == previousFormat && imported.Preferences.OpenDiagramDocumentationOnDoubleClick == previousFormat &&
                imported.CodeWindows.GetBackcolorForFile("restored.sql", true) == (previousFormat ? "#DEFACE" : "#102030") &&
                imported.CodeWindows.DefaultDarkBackcolor == (previousFormat ? "#F0F1F2" : "#1C1D1E"),
                previousFormat ? "Old packages import with preserved colors and compatible preference defaults" : "New packages preserve separate theme colors and both preference choices");
            if (previousFormat)
            {
                // Export from a pristine installed schema. The earlier DROP/add emulation leaves
                // SQL's column-ID watermark behind, unlike a real first-time schema upgrade.
                await restoredStore.SaveSettingsAsync(changed, restoredSettings.Token, Guid.NewGuid());
                newExport = await new RelationalPackageExporter(restoredSession).ExportAsync(newPath, new(AllowBlockingConsistentFallback: true));
            }
        }
    }

    private static void RunPreferenceThemeUiChecks(Surf2.MainWindow main, Action<bool, string> check)
    {
        var runtime = RuntimeWpfField<AppSettings>(main, "_appSettings");
        var originalPreferences = runtime.Preferences.Clone();
        string originalTheme = runtime.Appearance.Theme;
        var preview = RuntimeWpfNamed<TabItem>(main, "PreviewTabItem");
        var tabs = RuntimeWpfNamed<TabControl>(main, "ExplorerDetailTabs");
        object? selectedTab = tabs.SelectedItem;
        string pinned = (string)RuntimeWpfInvoke(main, "GetPinnedExplorerDetailTabKey")!;
        var color = new ExtensionBackcolorSetting { Extension = ".theme-test", Backcolor = "#FAFBFC", DarkBackcolor = "#182838", Language = CodeWindowSettings.PlainTextLanguage };
        runtime.CodeWindows.BackcolorsByExtension.Add(color);
        Surf2.SettingsWindow? settings = null;
        try
        {
            RuntimeWpfInvoke(main, "SetPinnedExplorerDetailTab", preview);
            runtime.Preferences.ShowExplorerPreview = false;
            RuntimeWpfInvoke(main, "ApplyPreferencesToRuntimeSurfaces");
            RuntimeWpfInvoke(main, "SelectExplorerDetailTabAutomatically", preview);
            check(preview.Visibility == Visibility.Collapsed && ReferenceEquals(tabs.SelectedItem, RuntimeWpfNamed<TabItem>(main, "OpenWindowsTabItem")),
                "Disabling Preview hides its tab, clears its pin, and prevents automatic selection of a hidden preview");
            runtime.Preferences.ShowExplorerPreview = true;
            RuntimeWpfInvoke(main, "ApplyPreferencesToRuntimeSurfaces");
            check(preview.Visibility == Visibility.Visible, "Enabling Preview restores its tab immediately");
            runtime.Appearance.Theme = AppearanceSettings.LightTheme;
            var light = (SolidColorBrush)RuntimeWpfInvoke(main, "GetCodeWindowBackcolor", "sample.theme-test")!;
            runtime.Appearance.Theme = AppearanceSettings.DarkTheme;
            var dark = (SolidColorBrush)RuntimeWpfInvoke(main, "GetCodeWindowBackcolor", "sample.theme-test")!;
            check(light.Color == Color.FromRgb(250, 251, 252) && dark.Color == Color.FromRgb(24, 40, 56),
                "Runtime code background lookup selects the independently configured active-theme color");
            settings = new Surf2.SettingsWindow(runtime);
            RuntimeWpfNamed<ListBox>(settings, "SectionList").SelectedIndex = 9;
            check(RuntimeWpfNamed<Grid>(settings, "PreferencesSettingsPanel").Visibility == Visibility.Visible &&
                RuntimeWpfNamed<CheckBox>(settings, "ShowExplorerPreviewCheckBox").IsChecked == true,
                "Settings exposes the Preferences blade with loaded values");
            RuntimeWpfNamed<CheckBox>(settings, "ShowExplorerPreviewCheckBox").IsChecked = false;
            RuntimeWpfNamed<CheckBox>(settings, "OpenDiagramDocumentationCheckBox").IsChecked = false;
            RuntimeWpfInvoke(settings, "SaveWorkbenchSettings");
            check(!settings.Settings.Preferences.ShowExplorerPreview && !settings.Settings.Preferences.OpenDiagramDocumentationOnDoubleClick && runtime.Preferences.ShowExplorerPreview,
                "Preferences controls save both choices into detached settings without altering the workspace before Save");
            var colors = RuntimeWpfNamed<ListBox>(settings, "BackcolorList");
            colors.SelectedItem = colors.Items.OfType<ExtensionBackcolorSetting>().Single(c => c.Extension == ".theme-test");
            check(RuntimeWpfNamed<TextBox>(settings, "ColorTextBox").Text == "#FAFBFC" && RuntimeWpfNamed<TextBox>(settings, "DarkColorTextBox").Text == "#182838",
                "Code Windows settings displays distinct editable light and dark backgrounds for the selected extension");
            RuntimeWpfNamed<TextBox>(settings, "DarkColorTextBox").Text = "#223344";
            check((bool)RuntimeWpfInvoke(settings, "SaveBackcolorSettings")! &&
                settings.Settings.CodeWindows.GetBackcolorForFile("sample.theme-test") == "#FAFBFC" &&
                settings.Settings.CodeWindows.GetBackcolorForFile("sample.theme-test", true) == "#223344",
                "Saving the dark editor preserves the corresponding light background independently");
        }
        finally
        {
            settings?.Close();
            runtime.Preferences = originalPreferences;
            runtime.Appearance.Theme = originalTheme;
            runtime.CodeWindows.BackcolorsByExtension.Remove(color);
            RuntimeWpfInvoke(main, "ApplyPreferencesToRuntimeSurfaces");
            RuntimeWpfInvoke(main, "RestorePinnedExplorerDetailTab", pinned);
            tabs.SelectedItem = selectedTab;
        }
    }
}
