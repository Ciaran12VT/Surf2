using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Search;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;

namespace Surf2.RegressionTests;

internal static class MasterRegressionSuite
{
    private const string DocumentPath = @"C:\Source files\Pay & leave\Résumé #1.sql";
    private const string Prefix = "surf-search:v1:";
    private static int _failures;

    [STAThread]
    public static int Run(string[] args)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        Run("Search references preserve file paths, query text and options", ReferenceRoundTrips);
        Run("Search references preserve canonical database document identity", DatabaseReferenceRoundTrips);
        Run("Search references reject malformed and incomplete addresses", InvalidReferencesAreRejected);
        Run("Empty references cannot be encoded", InvalidReferencesCannotBeEncoded);

        // Load shared WPF styles without running Surf startup or touching database state.
        var app = new Surf2.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            if (args is ["--json"])
            {
                Run("JSON code highlighting, lossless display formatting and language overrides", JsonCodeRenderingTests.Run);
                return _failures == 0 ? 0 : 1;
            }

            Run("Opening a search selects the first match and navigation updates the counter", SearchNavigation);
            Run("Search options and counters use the same matches", SearchOptions);
            Run("A missing match preserves the active search", MissingSearch);
            Run("An invalid regex remains editable without crashing", InvalidRegexSearch);
            Run("The search reference icon exposes its action and raises the copy request", CopyReferenceButton);
            Run("Typing and changing visible search options updates matches and copied references", ManualSearchBindings);
            Run("Docking and search preserve visible, draggable editor scrollbar thumbs", DockedScrollbarThumbs);
            Run("Closing and reopening search removes and restores scrollbar markers", SearchLifecycle);
            Run("The search counter and copy icon fit a 320 pixel code window", NarrowSearchLayout);
            Run("Diagram search reference navigation and compatibility", SearchReferenceNavigationTests.Run);
            Run("Diagram image picker, upload, cancel, persistence and undo", DiagramImageEditingTests.Run);
            Run("Pending references and ready reference styling", V2ReferencePresentationTests.Run);
            Run("Relational table capture selections", V2CaptureSelectionTests.Run);
            Run("Paged grid range selection and selected clipboard output", V2GridSelectionTests.Run);
            Run("JSON code highlighting, lossless display formatting and language overrides", JsonCodeRenderingTests.Run);

            if (args.Length == 2 && args[0] == "--preview")
            {
                Run("Render search preview", () => RenderPreview(args[1]));
            }
            else if (args.Length != 0)
            {
                Console.Error.WriteLine("Usage: Surf2.RegressionTests [--preview <absolute PNG path> | --json]");
                _failures++;
            }
        }
        finally
        {
            app.Shutdown();
        }

        Console.WriteLine(_failures == 0 ? "All regression checks passed." : $"{_failures} regression check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void ReferenceRoundTrips()
    {
        foreach (CodeSearchReference expected in new[]
        {
            new CodeSearchReference(DocumentPath, "Client_Ref"),
            new CodeSearchReference(DocumentPath, "héllo \"quoted\" & #?% + / \\ 😀\r\nnext line", true, true, true),
            new CodeSearchReference(@"\\server\share\a b.sql", "   "),
            new CodeSearchReference(DocumentPath, "[") // Regex validation belongs to the editor.
        })
        {
            string address = CodeSearchReferenceService.Encode(expected);
            Check(address.StartsWith(Prefix, StringComparison.Ordinal), "Unexpected reference prefix.");
            Check(!address.Contains('\r') && !address.Contains('\n'), "A clipboard address must stay on one line.");
            Check(!address.Contains('=') && !address.Contains('+') && !address.Contains('/'), "Payload is not unpadded base64url.");
            Check(CodeSearchReferenceService.IsSearchReference(address), "Encoded reference was not recognized.");
            Check(CodeSearchReferenceService.TryDecode(address, out CodeSearchReference? actual), "Encoded reference did not decode.");
            Equal(expected, actual, "Reference round trip changed its meaning.");
            Check(CodeSearchReferenceService.TryDecode($"  {address}\r\n", out actual), "Pasted surrounding whitespace was not accepted.");
            Equal(expected, actual, "Pasted reference round trip changed its meaning.");
        }

        Check(CodeSearchReferenceService.TryDecode(Address("{\"DocumentPath\":\"C:\\\\code.sql\",\"SearchText\":\"needle\"}"), out CodeSearchReference? defaults), "A minimal v1 reference was not accepted.");
        Check(defaults is { MatchCase: false, WholeWords: false, UseRegex: false }, "Missing options must default to false.");
    }

    private static void DatabaseReferenceRoundTrips()
    {
        var snapshot = new DatabaseMetadataSnapshot
        {
            SnapshotId = "persistent-snapshot-identity",
            DisplayName = "Renamable display name",
            DatabaseName = "Payroll"
        };
        var procedure = new SqlDatabaseObject
        {
            SchemaName = "dbo",
            ObjectName = "CPL_Data_RunHolidays",
            Kind = SqlDatabaseObjectKind.StoredProcedure
        };
        string canonicalPath = DatabaseDocumentService.CreateCanonicalObjectDocumentPath(snapshot, procedure);
        var expected = new CodeSearchReference(canonicalPath, "Client_Ref", MatchCase: true);
        Check(CodeSearchReferenceService.TryDecode(CodeSearchReferenceService.Encode(expected), out CodeSearchReference? actual), "Database reference did not decode.");
        Equal(expected, actual, "Canonical database identity was changed.");
        snapshot.DisplayName = "A different display name";
        Equal(actual!.DocumentPath, DatabaseDocumentService.CreateCanonicalObjectDocumentPath(snapshot, procedure), "Reference identity depends on its database display name.");
    }

    private static void InvalidReferencesAreRejected()
    {
        string[] malformed =
        [
            string.Empty,
            "plain file.sql:42",
            "surf-search:v2:eyJ9",
            Prefix,
            Prefix + "!not-base64!",
            Prefix + "a",
            Prefix + "e30=",
            Address("not JSON"),
            Address("null"),
            Address("[]"),
            Address("{}"),
            Address("{\"DocumentPath\":\"C:\\\\code.sql\"}"),
            Address("{\"SearchText\":\"needle\"}"),
            Address("{\"DocumentPath\":null,\"SearchText\":\"needle\"}"),
            Address("{\"DocumentPath\":\"\",\"SearchText\":\"needle\"}"),
            Address("{\"DocumentPath\":\"  \",\"SearchText\":\"needle\"}"),
            Address("{\"DocumentPath\":\"C:\\\\code.sql\",\"SearchText\":null}"),
            Address("{\"DocumentPath\":\"C:\\\\code.sql\",\"SearchText\":\"\"}"),
            Address("{\"DocumentPath\":\"line1\\nline2\",\"SearchText\":\"needle\"}"),
            Address("{\"DocumentPath\":\"bad\\u0000path\",\"SearchText\":\"needle\"}"),
            Address("{\"DocumentPath\":\"C:\\\\code.sql\",\"SearchText\":\"needle\",\"MatchCase\":\"true\"}")
        ];
        foreach (string address in malformed)
        {
            Check(!CodeSearchReferenceService.TryDecode(address, out _), $"Malformed address was accepted: {address}");
        }

        Check(CodeSearchReferenceService.IsSearchReference("surf-search:v2:future-format"), "Future search references should be recognized so they cannot fall through as file paths.");
        Check(!CodeSearchReferenceService.IsSearchReference(DocumentPath), "A file path was misclassified as a search reference.");
    }

    private static void InvalidReferencesCannotBeEncoded()
    {
        foreach (CodeSearchReference reference in new[]
        {
            new CodeSearchReference(string.Empty, "needle"),
            new CodeSearchReference("   ", "needle"),
            new CodeSearchReference("line1\nline2", "needle"),
            new CodeSearchReference(DocumentPath, string.Empty)
        })
        {
            Throws<ArgumentException>(() => CodeSearchReferenceService.Encode(reference));
        }
    }

    private static void SearchNavigation()
    {
        const string text = "Client_Ref first\nclient_ref second\nClient_Ref third\n";
        using var view = new SearchView(text);
        Check(view.Code.GetSearchReference() == null, "A closed search should not produce a search reference.");
        var reference = new CodeSearchReference(DocumentPath, "Client_Ref");
        view.Code.ApplySearchReference(reference);
        view.Flush();
        Check(!view.Search.IsClosed, "Applying a search reference did not open search.");
        Equal(0, view.Editor.SelectionStart, "Search did not select the first match.");
        Equal("Client_Ref", view.Editor.SelectedText, "First match text was not selected.");
        Equal(reference, view.Code.GetSearchReference(), "The active search cannot be copied faithfully.");
        Counter(view, 1, 3);

        view.Search.FindNext();
        view.Flush();
        Equal(text.IndexOf("client_ref", StringComparison.Ordinal), view.Editor.SelectionStart, "Next did not select the second match.");
        Counter(view, 2, 3);
        view.Search.FindPrevious();
        view.Flush();
        Equal(0, view.Editor.SelectionStart, "Previous did not select the first match.");
        Counter(view, 1, 3);
        view.Search.FindPrevious();
        view.Flush();
        Equal(text.LastIndexOf("Client_Ref", StringComparison.Ordinal), view.Editor.SelectionStart, "Previous did not wrap to the final match.");
        Counter(view, 3, 3);
    }

    private static void SearchOptions()
    {
        using var view = new SearchView("Client_Ref client_ref PrefixClient_Ref Client_Ref\nA12 A34 B56");
        var literal = new CodeSearchReference(DocumentPath, "Client_Ref", MatchCase: true, WholeWords: true);
        view.Code.ApplySearchReference(literal);
        view.Flush();
        Equal(literal, view.Code.GetSearchReference(), "Case/word options changed when reopening a reference.");
        Check(view.Search.MatchCase && view.Search.WholeWords && !view.Search.UseRegex, "The editor did not receive literal search options.");
        Counter(view, 1, 2);

        var regex = new CodeSearchReference(DocumentPath, @"A\d+", UseRegex: true);
        view.Code.ApplySearchReference(regex);
        view.Flush();
        Equal(regex, view.Code.GetSearchReference(), "Regex options changed when reopening a reference.");
        Equal("A12", view.Editor.SelectedText, "Regex search did not select its first result.");
        Counter(view, 1, 2);
        view.Search.FindNext();
        view.Flush();
        Equal("A34", view.Editor.SelectedText, "Regex next selected the wrong result.");
        Counter(view, 2, 2);
    }

    private static void MissingSearch()
    {
        using var view = new SearchView("Client_Ref\nother text");
        var reference = new CodeSearchReference(DocumentPath, "no-longer-present");
        view.Code.ApplySearchReference(reference);
        view.Flush();
        Check(!view.Search.IsClosed, "A missing match closed the search bar.");
        Equal(reference, view.Code.GetSearchReference(), "A missing match discarded the copied query.");
        Counter(view, 0, 0);
    }

    private static void InvalidRegexSearch()
    {
        using var view = new SearchView("Client_Ref");
        var reference = new CodeSearchReference(DocumentPath, "[", UseRegex: true);
        view.Code.ApplySearchReference(reference);
        view.Flush();
        Check(!view.Search.IsClosed, "An invalid expression closed the search bar.");
        TextBox input = (TextBox)(view.Search.Template.FindName("PART_searchTextBox", view.Search)
            ?? throw new InvalidOperationException("Search input was not rendered."));
        Equal("[", input.Text, "Invalid expressions must remain available for correction.");
        Check(Descendants<TextBlock>(view.Search).Any(block => block.IsVisible && block.Text.Contains("Invalid", StringComparison.OrdinalIgnoreCase)),
            "The invalid expression has no visible explanation.");
    }

    private static void CopyReferenceButton()
    {
        using var view = new SearchView("Client_Ref");
        view.Code.ApplySearchReference(new CodeSearchReference(DocumentPath, "Client_Ref"));
        view.Flush();
        ButtonBase? copy = Descendants<ButtonBase>(view.Search).FirstOrDefault(button =>
            string.Equals(button.ToolTip?.ToString(), "Copy Search Reference", StringComparison.Ordinal));
        Check(copy != null, "The search reference icon has no discoverable Copy Search Reference tooltip.");
        Check(copy!.IsEnabled, "The search reference icon is disabled for a valid active query.");
        int requests = 0;
        view.Code.SearchReferenceCopyRequested += (_, _) => requests++;
        copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, copy));
        Equal(1, requests, "Clicking the search reference icon did not raise exactly one copy request.");
    }

    private static void ManualSearchBindings()
    {
        using var view = new SearchView("Client_Ref client_ref PrefixClient_Ref\nOther other\nA12 A34 B56");
        view.Code.ApplySearchReference(new CodeSearchReference(DocumentPath, "Client_Ref"));
        view.Flush();
        SetSearchText(view, "Other");
        Equal("Other", view.Search.SearchPattern, "Typing did not update AvalonEdit's highlighted search pattern.");
        Equal("Other", view.Code.GetSearchReference()!.SearchText, "Typing did not update the copied search query.");
        Counter(view, view.Code.CurrentSearchMatchNumber, 2);

        DropDownButton dropdown = Descendants<DropDownButton>(view.Search).Single();
        Popup popup = (Popup)dropdown.DropDownContent;
        // Popups use a separate HWND, so hide their child as well as the test host.
        popup.Child.Opacity = 0;
        popup.IsOpen = true;
        view.Flush();
        try
        {
            CheckBox[] options = Descendants<CheckBox>(popup.Child).ToArray();
            Equal(3, options.Length, "The search options popup did not expose all matching options.");
            CheckBox matchCase = options.Single(option => option.Content.ToString()!.Contains("case", StringComparison.OrdinalIgnoreCase));
            CheckBox wholeWords = options.Single(option => option.Content.ToString()!.Contains("whole", StringComparison.OrdinalIgnoreCase));
            CheckBox regex = options.Single(option => option.Content.ToString()!.Contains("regular", StringComparison.OrdinalIgnoreCase));
            matchCase.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            view.Flush();
            Check(view.Search.MatchCase && view.Code.GetSearchReference()!.MatchCase, "The match-case checkbox did not update search and copied options.");
            Counter(view, view.Code.CurrentSearchMatchNumber, 1);

            SetSearchText(view, "Client_Ref");
            Counter(view, view.Code.CurrentSearchMatchNumber, 2);
            wholeWords.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            view.Flush();
            Check(view.Search.WholeWords && view.Code.GetSearchReference()!.WholeWords, "The whole-word checkbox did not update search and copied options.");
            Counter(view, view.Code.CurrentSearchMatchNumber, 1);

            SetSearchText(view, @"A\d+");
            Equal(0, view.Code.SearchMatchCount, "A literal query was treated as a regex before the regex option was enabled.");
            regex.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            view.Flush();
            Check(view.Search.UseRegex && view.Code.GetSearchReference()!.UseRegex, "The regex checkbox did not update search and copied options.");
            Counter(view, view.Code.CurrentSearchMatchNumber, 2);
            view.Search.FindNext();
            view.Flush();
            Check(view.Editor.SelectedText is "A12" or "A34", "A manually entered regex did not navigate using its visible options.");

            SetSearchText(view, "[");
            Equal("[", SearchPart<TextBox>(view, "PART_searchTextBox").Text, "An invalid typed regex disappeared from the textbox.");
            Check(Descendants<TextBlock>(view.Search).Any(block => block.Text.Contains("Invalid", StringComparison.OrdinalIgnoreCase)), "An invalid typed regex has no explanation.");
            Check(!SearchPart<Button>(view, "CopySearchReferenceButton").IsEnabled, "An invalid typed regex can be copied as a search reference.");
            SetSearchText(view, @"A\d+");
            Counter(view, view.Code.CurrentSearchMatchNumber, 2);
        }
        finally
        {
            popup.IsOpen = false;
        }
    }

    private static void DockedScrollbarThumbs()
    {
        string text = string.Join("\n", Enumerable.Range(1, 200).Select(line => $"needle at line {line}: {new string('x', 220)}"));
        using var view = new SearchView(text);
        Thumb[] editorThumbs = Descendants<Thumb>(view.Editor).ToArray();
        Thumb[] resizeGrips = Descendants<Thumb>(view.Code).Except(editorThumbs).ToArray();
        Check(resizeGrips.Length > 0, "The floating code window has no resize grips to exercise.");
        ScrollBar vertical = Descendants<ScrollBar>(view.Editor).Single(bar => bar.Orientation == Orientation.Vertical);
        ScrollBar horizontal = Descendants<ScrollBar>(view.Editor).Single(bar => bar.Orientation == Orientation.Horizontal);

        AssertThumbs("floating before docking");
        view.Code.SetDockedMode(true);
        view.Flush();
        AssertThumbs("docked after layout");
        Check(resizeGrips.All(grip => !grip.IsVisible), "Docking left a floating window resize grip visible.");

        view.Code.ApplySearchReference(new CodeSearchReference(DocumentPath, "needle"));
        view.Flush();
        AssertThumbs("docked with search open");
        SearchMatchOverview overview = (SearchMatchOverview)view.Code.FindName("SearchOverview");
        Check(overview.RenderedMarkerCount > 0, "The scrollbar drag check did not exercise visible search markers.");
        DragScrollbar(vertical);
        DragScrollbar(horizontal);

        view.Search.Close();
        view.Flush();
        AssertThumbs("docked after closing search");
        view.Code.SetDockedMode(false);
        view.Flush();
        AssertThumbs("floating after undocking");
        Check(resizeGrips.All(grip => grip.IsVisible), "Undocking did not restore the floating window resize grips.");
        view.Code.SetDockedMode(true);
        view.Flush();
        AssertThumbs("docked again");
        view.Search.Open();
        view.Flush();
        AssertThumbs("docked after reopening search");

        void AssertThumbs(string state)
        {
            foreach (ScrollBar bar in new[] { vertical, horizontal })
            {
                Check(bar.IsVisible && bar.Maximum > bar.Minimum, $"The {bar.Orientation} scrollbar is not scrollable while {state}.");
                Thumb thumb = Descendants<Track>(bar).Single(track => track.Orientation == bar.Orientation && track.IsVisible).Thumb;
                Check(thumb.IsVisible && thumb.ActualWidth > 0 && thumb.ActualHeight > 0,
                    $"The {bar.Orientation} scrollbar thumb disappeared while {state}; visibility: {thumb.Visibility}, size: {thumb.ActualWidth} x {thumb.ActualHeight}.");
            }
        }

        void DragScrollbar(ScrollBar bar)
        {
            bool isVertical = bar.Orientation == Orientation.Vertical;
            Thumb thumb = Descendants<Track>(bar).Single(track => track.Orientation == bar.Orientation && track.IsVisible).Thumb;
            double oldValue = bar.Value;
            double oldOffset = isVertical ? view.Editor.VerticalOffset : view.Editor.HorizontalOffset;
            double horizontalChange = isVertical ? 0 : 35;
            double verticalChange = isVertical ? 35 : 0;
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            thumb.RaiseEvent(new DragDeltaEventArgs(horizontalChange, verticalChange) { RoutedEvent = Thumb.DragDeltaEvent });
            thumb.RaiseEvent(new DragCompletedEventArgs(horizontalChange, verticalChange, false) { RoutedEvent = Thumb.DragCompletedEvent });
            view.Flush();
            double offset = isVertical ? view.Editor.VerticalOffset : view.Editor.HorizontalOffset;
            Check(bar.Value > oldValue && offset > oldOffset,
                $"Dragging the {bar.Orientation} scrollbar thumb did not move the code viewport.");
            Check(Math.Abs(bar.Value - offset) < 1,
                $"The {bar.Orientation} scrollbar value and editor offset disagree after dragging.");
            AssertThumbs($"docked after dragging the {bar.Orientation} thumb");
        }
    }

    private static void SearchLifecycle()
    {
        string text = string.Join("\n", Enumerable.Range(1, 150).Select(line => line % 20 == 0 ? $"needle at line {line}" : $"Line {line}"));
        using var view = new SearchView(text);
        view.Code.ApplySearchReference(new CodeSearchReference(DocumentPath, "needle"));
        view.Flush();
        SearchMatchOverview overview = (SearchMatchOverview)view.Code.FindName("SearchOverview");
        Equal(7, overview.MatchCount, "An open search does not expose every scrollbar match.");
        Check(overview.RenderedMarkerCount > 0, "A scrollable document has no rendered search markers.");
        view.Search.Close();
        view.Flush();
        Check(view.Search.IsClosed, "Close did not close the search panel.");
        Equal(0, overview.MatchCount, "Closing search left its scrollbar markers active.");
        Equal(0, overview.ActiveMatchNumber, "Closing search left an active scrollbar marker.");
        Equal(0, overview.RenderedMarkerCount, "Closing search left rendered scrollbar markers.");
        view.Search.Open();
        view.Flush();
        Check(!view.Search.IsClosed, "Search did not reopen.");
        Equal(7, overview.MatchCount, "Reopening search did not restore scrollbar matches.");
        Check(overview.RenderedMarkerCount > 0, "Reopening search did not redraw its markers.");
        Counter(view, view.Code.CurrentSearchMatchNumber, 7);
    }

    private static void NarrowSearchLayout()
    {
        using var view = new SearchView("Client_Ref\nclient_ref\nClient_Ref");
        view.Code.ApplySearchReference(new CodeSearchReference(DocumentPath, "Client_Ref"));
        view.Flush();
        view.Resize(320, 260);
        Counter(view, 1, 3);
        TextBlock counter = SearchPart<TextBlock>(view, "SearchMatchCountText");
        Button copy = SearchPart<Button>(view, "CopySearchReferenceButton");
        TextBox input = SearchPart<TextBox>(view, "PART_searchTextBox");
        foreach (FrameworkElement element in new FrameworkElement[] { input, counter, copy })
        {
            Rect bounds = element.TransformToVisual(view.Editor).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Left >= -1 && bounds.Right <= view.Editor.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= view.Editor.ActualHeight + 1,
                $"Search element {element.Name} is clipped in a 320 pixel code window: {bounds}; editor width {view.Editor.ActualWidth}.");
        }
        Check(counter.ActualWidth >= 35 && copy.ActualWidth >= 20 && input.ActualWidth >= 30, "Resizing collapsed a search action or its input.");
        Equal("Copy Search Reference", copy.ToolTip?.ToString(), "The icon loses its explanation in a narrow window.");
    }

    private static T SearchPart<T>(SearchView view, string name) where T : FrameworkElement =>
        (T)(view.Search.Template.FindName(name, view.Search) ?? throw new InvalidOperationException($"Search template part {name} was not rendered."));

    private static void SetSearchText(SearchView view, string text)
    {
        // SetCurrentValue preserves the live textbox binding, as text editing does.
        SearchPart<TextBox>(view, "PART_searchTextBox").SetCurrentValue(TextBox.TextProperty, text);
        view.Flush();
    }

    private static void RenderPreview(string path)
    {
        Check(Path.IsPathFullyQualified(path), "Preview path must be absolute.");
        string text = string.Join("\n", Enumerable.Range(1, 160).Select(line => line % 9 == 0
            ? $"SELECT Client_Ref, Holiday_Days FROM dbo.Payroll WHERE Client_Ref = {line};"
            : $"-- {line:000}: Review annual leave and payroll processing."));
        using var view = new SearchView(text);
        view.Code.SetDockedMode(true);
        view.Flush();
        view.Code.ApplySearchReference(new CodeSearchReference(DocumentPath, "Client_Ref"));
        view.Flush();
        for (int i = 0; i < 12; i++)
        {
            view.Search.FindNext();
        }
        view.Flush();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.Code.ActualWidth), (int)Math.Ceiling(view.Code.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view.Code);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        using FileStream output = File.Create(path);
        encoder.Save(output);
        Console.WriteLine($"Preview: {path}");
    }

    private static void Counter(SearchView view, int current, int total)
    {
        Equal(total, view.Code.SearchMatchCount, "The search result count does not match navigation.");
        Equal(current, view.Code.CurrentSearchMatchNumber, "The active search result does not match navigation.");
        string expected = $"{current}/{total}";
        string[] labels = Descendants<TextBlock>(view.Search).Select(block => block.Text).ToArray();
        Check(labels.Any(label => label.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("of", "/", StringComparison.Ordinal) == expected),
            $"Expected visible counter {current} / {total}; labels: {string.Join(" | ", labels)}");

        SearchMatchOverview overview = (SearchMatchOverview)(view.Code.FindName("SearchOverview") ?? throw new InvalidOperationException("The editor has no search overview strip."));
        Equal(total, overview.MatchCount, "The scrollbar overview does not contain every search result.");
        Equal(current, overview.ActiveMatchNumber, "The scrollbar overview marks the wrong active result.");
    }

    private static string Address(string json) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Run(string description, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {description}");
        }
        catch (Exception exception)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL {description}\n{exception}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} Expected: {expected}; actual: {actual}.");

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class SearchView : IDisposable
    {
        private readonly HwndSource _host;
        private readonly Grid _surface;

        public SearchView(string text)
        {
            Code = new FloatingCodeWindow(new OpenDocumentState { FilePath = DocumentPath, DisplayName = "Search regression.sql", Width = 1040, Height = 600 }, text, null);
            _surface = new Grid { Width = 1040, Height = 600 };
            _surface.Children.Add(Code);
            // Supply real Loaded/render behavior without creating an invisible desktop Window.
            _host = new HwndSource(new HwndSourceParameters("Surf2 search regression offscreen")
            {
                PositionX = -32000, PositionY = -32000, Width = 1040, Height = 600,
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080
            }) { RootVisual = _surface };
            Flush();
            Editor = Descendants<TextEditor>(Code).Single();
            Search = (SearchPanel)(typeof(FloatingCodeWindow).GetField("_searchPanel", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Code)
                ?? throw new InvalidOperationException("The editor search panel was not installed."));
            // These checks assert the inline search state, counters and errors.
            // Native tooltip popups cannot be positioned reliably on an offscreen host.
            foreach (var field in typeof(SearchPanel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(field => field.FieldType == typeof(ToolTip)))
                field.SetValue(Search, new OffscreenSearchToolTip());
        }

        public FloatingCodeWindow Code { get; }
        public TextEditor Editor { get; }
        public SearchPanel Search { get; }

        public void Resize(double width, double height)
        {
            Code.Width = width;
            Code.Height = height;
            _surface.Width = width;
            _surface.Height = height;
            Flush();
        }

        public void Flush()
        {
            for (int i = 0; i < 3; i++)
            {
                _surface.Measure(new Size(_surface.Width, _surface.Height));
                _surface.Arrange(new Rect(0, 0, _surface.Width, _surface.Height));
                _surface.UpdateLayout();
                var frame = new DispatcherFrame();
                bool timedOut = false;
                var dispatcher = _surface.Dispatcher;
                var queued = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                var timeout = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Send, (_, _) =>
                {
                    timedOut = true; frame.Continue = false;
                }, dispatcher);
                try { Dispatcher.PushFrame(frame); }
                finally { timeout.Stop(); queued.Abort(); }
                if (timedOut) throw new TimeoutException("Search layout did not settle within the bounded dispatcher flush.");
            }
        }

        public void Dispose()
        {
            _host.RootVisual = null;
            _surface.Children.Clear();
            _host.Dispose();
            Flush();
        }
    }

    private sealed class OffscreenSearchToolTip : ToolTip
    {
        static OffscreenSearchToolTip() => IsOpenProperty.OverrideMetadata(typeof(OffscreenSearchToolTip),
            new FrameworkPropertyMetadata(false, null, (_, _) => false));
    }
}
