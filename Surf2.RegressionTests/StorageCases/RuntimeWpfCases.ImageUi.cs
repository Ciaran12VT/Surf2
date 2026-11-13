using System.Collections;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckImageUiAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_ImageUi_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var settings = new AppSettings { LoadMostRecentWorkbenchOnStartup = false };
        settings.EnsureDefaults();
        settings.DiagramImages.Images = Enumerable.Range(1, 10).Select(index => new DiagramImageDefinition
        {
            Id = "image-" + index, Name = "Image " + index, SortOrder = index <= 8 ? index : int.MaxValue,
            NameRegex = "name-" + index, ContentRegex = "content-" + index, OriginalFileName = "image-" + index + ".png",
            ImageDataBase64 = Convert.ToBase64String(Png((byte)index))
        }).ToList();
        await InTransactionAsync(runtime.Session, async (c, t) =>
            await runtime.StateStore.ImportSettingsAsync(c, t, settings, Guid.NewGuid()));
        await fixture.MarkTestFixtureReadyAsync();

        var page = Required((await runtime.Preferences.ListImageSettingsAsync()).Value, "complete image settings page");
        RuntimeWpfAssert(page.Items.Select(item => item.SortOrder).SequenceEqual(Enumerable.Range(1, 10)), check,
            "Image settings displays sequential numbers for legacy and int.MaxValue rows without changing their IDs");
        RuntimeWpfAssert(page.Items[8].Id == "image-9" && page.Items.All(item => item.CatalogueCount == 10), check,
            "Image summaries retain stable reference IDs and the whole catalogue count");
        var settingsWindow = new Surf2.SettingsWindow(settings);
        var main = new Surf2.MainWindow(runtime.Options);
        RuntimeWpfSetField(main, "_relational", runtime);
        try
        {
            await settingsWindow.InitializeRelationalPreferencesAsync(runtime, page.Owner);
            var list = RuntimeWpfNamed<ListBox>(settingsWindow, "DiagramImageList");
            var first = (ImageDefinitionSummary)list.Items[0];
            RuntimeWpfAssert(first.NameRegex == "name-1" && first.ContentRegex == "content-1" && first.OriginalFileName == "image-1.png", check,
                "The unselected settings summary contains both regexes and the original filename");
            var row = (Grid)list.ItemTemplate.LoadContent(); row.DataContext = first;
            using var rowHost = new HwndSource(new HwndSourceParameters("Surf2 image settings row fixture")
            { PositionX = -32000, PositionY = -32000, Width = 1000, Height = 50,
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            rowHost.RootVisual = row;
            RuntimeWpfArrange(row, 1000, 50);
            await row.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            foreach (var cell in row.Children.OfType<TextBlock>()) cell.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            RuntimeWpfAssert(list.SelectedIndex == -1 && row.Children.OfType<TextBlock>().Any(cell => cell.Text == "name-1") &&
                row.Children.OfType<TextBlock>().Any(cell => cell.Text == "content-1") &&
                row.Children.OfType<TextBlock>().Any(cell => cell.Text == "image-1.png"), check,
                "The image grid binds both regex columns and the filename before any row is selected (selected=" + list.SelectedIndex +
                ", values=" + string.Join("|", row.Children.OfType<TextBlock>().Select(cell => cell.Text)) + ")");
            rowHost.RootVisual = null;
            var pager = ((Grid)list.Parent).Children.OfType<StackPanel>().Single(panel => panel.Children.OfType<Button>().Any(button => Equals(button.Content, "Next")));
            RuntimeWpfAssert(pager.Visibility == Visibility.Collapsed, check, "A single image page has no inactive Next button");
            RuntimeWpfInvoke(settingsWindow, "AddRelationalPreference", 2);
            RuntimeWpfAssert(list.Items.OfType<DiagramImageDefinition>().Single().SortOrder == 11, check,
                "A new image continues after the repaired existing image numbers");

            await RuntimeWpfInvokeTaskAsync(main, "ReadRelationalImageMenuAsync", null, true);
            var menu = RuntimeWpfNamed<FrameworkElement>(main, "DiagramImageToolButton").ContextMenu!;
            RuntimeWpfAssert(menu.Items.OfType<MenuItem>().Count() == 10 && menu.Items.OfType<MenuItem>().All(item =>
                item.Header is StackPanel header && header.Children.OfType<Image>().Single().Source is BitmapSource image && image.IsFrozen && image.PixelWidth <= 48), check,
                "Every SQL-backed image menu entry includes a real frozen thumbnail");
            using (var trace = new StateAccessSqlTrace(connection))
            {
                await RuntimeWpfInvokeTaskAsync(main, "ReadRelationalImageMenuAsync", null, true);
                StateAccessNoReads(trace.Commands, check, "Reopening the unchanged image menu", "Asset");
            }
            var assets = await runtime.Preferences.ReadImageAssetsAsync([first.Key], page.Owner);
            RuntimeWpfAssert(assets[first.Key].CopyBytes().SequenceEqual(Png(1)), check,
                "Batched image assets preserve exact source bytes for diagram use");
            var tiny = new StatePreferenceAccess(runtime.Session, new StateLimits { MaximumAssetBytes = 1 });
            await ThrowsAsync<System.IO.InvalidDataException>(() => tiny.ReadImageAssetsAsync([first.Key], page.Owner), check,
                "Batched image reads retain the per-asset size limit");

            var inserted = new DiagramImageDefinition { Id = "new-eleven", Name = "Eleventh", SortOrder = int.MaxValue };
            var owner = await runtime.StateStore.SavePreferenceSelectionAsync(settings, page.Owner, [], [], [new(null, inserted)], [], Guid.NewGuid());
            var saved = Required(await runtime.StateStore.ReadSettingsAsync(), "saved ordered image library").Value;
            RuntimeWpfAssert(saved.DiagramImages.Images.Select(image => image.SortOrder).SequenceEqual(Enumerable.Range(1, 11)) &&
                saved.DiagramImages.Images[8].Id == "image-9" && saved.DiagramImages.Images[10].Id == "new-eleven", check,
                "Saving image changes repairs old maximum-int orders and assigns the next number without changing reference IDs");
            await ThrowsAsync<StateConflictException>(() => runtime.Preferences.ReadImageAssetsAsync([first.Key], page.Owner), check,
                "A stale image preview owner cannot read assets from a newer preference generation");
            var additions = Enumerable.Range(12, 90).Select(index => new PreferenceChildChange<DiagramImageDefinition>(null,
                new() { Id = "image-" + index, Name = "Image " + index, SortOrder = int.MaxValue })).ToArray();
            owner = await runtime.StateStore.SavePreferenceSelectionAsync(settings, owner, [], [], additions, [], Guid.NewGuid());
            var pagedWindow = new Surf2.SettingsWindow(settings);
            try
            {
                await pagedWindow.InitializeRelationalPreferencesAsync(runtime, owner);
                var pages = RuntimeWpfNamed<ListBox>(pagedWindow, "DiagramImageList");
                RuntimeWpfAssert(pages.Items.Count == 100 && ((ImageDefinitionSummary)pages.Items[0]).SortOrder == 1, check,
                    "A larger image library starts with its first hundred entries");
                await RuntimeWpfInvokeTaskAsync(pagedWindow, "NavigateRelationalPreferencePageAsync", 2, 1);
                RuntimeWpfAssert(pages.Items.Count == 1 && ((ImageDefinitionSummary)pages.Items[0]).SortOrder == 101, check,
                    "Next shows the remaining image with its catalogue-wide sequence number");
                await RuntimeWpfInvokeTaskAsync(pagedWindow, "NavigateRelationalPreferencePageAsync", 2, 1);
                RuntimeWpfAssert(pages.Items.Count == 1 && ((ImageDefinitionSummary)pages.Items[0]).SortOrder == 101, check,
                    "Next on the last page cannot wrap back to the beginning");
                await RuntimeWpfInvokeTaskAsync(pagedWindow, "NavigateRelationalPreferencePageAsync", 2, -1);
                RuntimeWpfAssert(pages.Items.Count == 100 && ((ImageDefinitionSummary)pages.Items[0]).SortOrder == 1, check,
                    "Previous returns to the original image page");
                RuntimeWpfInvoke(pagedWindow, "AddRelationalPreference", 2);
                RuntimeWpfAssert(pages.Items.OfType<DiagramImageDefinition>().Single().SortOrder == 102, check,
                    "Adding from the first page numbers after every image, including unloaded pages");
            }
            finally { await pagedWindow.DrainRelationalPreferenceQueriesAsync(); pagedWindow.Close(); }
        }
        finally
        {
            await settingsWindow.DrainRelationalPreferenceQueriesAsync(); settingsWindow.Close();
            await RuntimeWpfInvokeTaskAsync(main, "DrainRelationalStateQueriesAsync");
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            RuntimeWpfSetField(main, "_shutdownSaveCompleted", true); main.Close();
        }
        await fixture.VerifySourceUnchangedAsync(check);
    }
}
