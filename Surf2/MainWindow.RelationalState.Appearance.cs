using System.IO;
using Surf2.Models;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

namespace Surf2;

public partial class MainWindow
{
    private async Task LoadRelationalAppearanceRulesAsync(StateToken expected, CancellationToken ct = default)
    {
        var budget = new StateBudget(new StateLimits { MaximumRows = 100_000, MaximumAggregateBytes = 8L * 1024 * 1024 });
        var extensions = new List<ExtensionBackcolorSetting>(); var styles = new List<ReferenceHighlightStyleSetting>();
        void Owner(StateToken token)
        {
            if (!SameRelationalOwner(token, expected)) throw new StateConflictException("appearance rule generation");
        }
        void Row(params string[] values)
        {
            budget.Row(); budget.Text(256);
            foreach (string value in values) budget.Text(checked((long)value.Length * 2));
        }
        PreferenceCursor<ExtensionAppearanceSummary>? extensionNext = null;
        do
        {
            var page = RequireRelationalLoad(await RelationalStateRuntime.Preferences.ListExtensionAppearanceAsync(500, extensionNext, ct), "extension appearance rules");
            Owner(page.Owner);
            foreach (var row in page.Items)
            {
                Row(row.Extension, row.Backcolor, row.Language);
                extensions.Add(new() { Extension = row.Extension, Backcolor = row.Backcolor, Language = row.Language });
            }
            extensionNext = page.Next;
        } while (extensionNext != null);
        PreferenceCursor<ReferenceStyleSummary>? styleNext = null;
        do
        {
            var page = RequireRelationalLoad(await RelationalStateRuntime.Preferences.ListStylesAsync(500, styleNext, ct), "reference highlight rules");
            Owner(page.Owner);
            foreach (var row in page.Items)
            {
                Row(row.Language, row.Foreground);
                styles.Add(new() { Language = row.Language, Kind = row.Kind, Foreground = row.Foreground,
                    IsBold = row.IsBold, IsItalic = row.IsItalic, IsUnderline = row.IsUnderline });
            }
            styleNext = page.Next;
        } while (styleNext != null);
        ct.ThrowIfCancellationRequested();
        if (_relationalStateClosing) throw new OperationCanceledException();
        // Install together only after all ordered summary pages share the same head revision.
        _appSettings.CodeWindows.BackcolorsByExtension = extensions;
        _appSettings.ReferenceHighlights.Styles = styles;
    }
}
