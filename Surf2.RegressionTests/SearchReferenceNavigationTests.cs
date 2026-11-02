using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Search;
using Surf2;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;

internal static class SearchReferenceNavigationTests
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Surf2-search-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, "code # references.sql");
        File.WriteAllText(filePath, "select Client_Ref;\nselect other;\nselect client_ref;\n");
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        // Exercise the real diagram link dispatcher without loading or saving the user's workspace.
        main.Loaded -= (RoutedEventHandler)Method("Window_Loaded").CreateDelegate(typeof(RoutedEventHandler), main);
        main.Closing -= (CancelEventHandler)Method("Window_Closing").CreateDelegate(typeof(CancelEventHandler), main);
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        try
        {
            main.Show();
            Flush();
            var reference = new CodeSearchReference(filePath, "Client_Ref", MatchCase: true);
            var metadata = new DiagramObjectMetadata { Link = CodeSearchReferenceService.Encode(reference) };
            metadata = JsonSerializer.Deserialize<DiagramObjectMetadata>(JsonSerializer.Serialize(metadata))!;
            Require(Open(main, metadata.Link), "A saved diagram search link should open its code document. " + main.StatusText);
            Dictionary<string, FloatingCodeWindow> windows = Windows(main);
            Require(windows.Count == 1 && windows.ContainsKey(filePath), "Search navigation should open exactly one code tab.");
            FloatingCodeWindow code = windows[filePath];
            Require(code.GetSearchReference() == reference, "Navigation should restore the search text and options.");
            TextEditor editor = (TextEditor)code.FindName("Editor");
            Require(editor.SelectedText == "Client_Ref", "Navigation should select the first matching occurrence.");

            var missing = reference with { SearchText = "removed text" };
            Require(Open(main, CodeSearchReferenceService.Encode(missing)), "A search link with no matches should still open.");
            Require(ReferenceEquals(code, Windows(main)[filePath]) && Windows(main).Count == 1,
                "A second reference to the same page should reuse its existing code tab.");
            Require(code.GetSearchReference() == missing && !Panel(code).IsClosed,
                "A missing search string should remain visible in the open search panel.");

            MethodInfo switchView = Method("SetCodeViewMode");
            switchView.Invoke(main, [Enum.Parse(switchView.GetParameters()[0].ParameterType, "Tabs")]);
            Flush();
            Require(Open(main, metadata.Link), "Search references should also work in Tabs view.");
            var tabs = (TabControl)main.FindName("CodeDocumentsTabControl");
            Require(tabs.SelectedItem is TabItem selected && ReferenceEquals(selected.Content, code),
                "Search navigation in Tabs view should select the existing destination tab.");
            Require(Panel(code).IsLoaded && code.GetSearchReference() == reference,
                "Search should remain visible when its viewer moves from Canvas to Tabs.");

            Require(Open(main, filePath + ":2"), "Existing line references should remain supported.");
            Require(editor.TextArea.Caret.Line == 2, "Existing line references should retain their line destination.");
            Require(!Open(main, "surf-search:v99:invalid"), "Unknown search-reference versions should fail cleanly.");
            Require(!Open(main, CodeSearchReferenceService.Encode(reference with { DocumentPath = filePath + ".missing" })),
                "Missing file references should fail cleanly.");

            var snapshot = new DatabaseMetadataSnapshot { SnapshotId = "search-test-snapshot", DisplayName = "Search Test", DatabaseName = "Test" };
            var procedure = new SqlDatabaseObject { SchemaName = "dbo", ObjectName = "FindClient", Kind = SqlDatabaseObjectKind.StoredProcedure, Definition = "select Client_Ref;" };
            snapshot.Objects.Add(procedure);
            snapshot.Tables.Add(new SqlTable { SchemaName = "dbo", TableName = "Clients", HasFullData = true });
            snapshot.Columns.Add(new SqlColumn { SchemaName = "dbo", TableName = "Clients", ColumnName = "Client_Ref", DataType = "int", Ordinal = 1 });
            Field<DatabaseSnapshotLibrary>(main, "_databaseSnapshots").Snapshots.Add(snapshot);
            string canonical = DatabaseDocumentService.CreateCanonicalObjectDocumentPath(snapshot, procedure);
            var databaseReference = reference with { DocumentPath = canonical };
            string address = CodeSearchReferenceService.Encode(databaseReference);
            Require(Open(main, address), "Search links should open database code pages.");
            Require(Windows(main).ContainsKey(canonical), "Database search links should use canonical document identity.");
            string readable = (string)Method("GetReadableDatabaseMetadataLink").Invoke(main, [address])!;
            string normalized = (string)Method("NormalizeDatabaseMetadataLink").Invoke(main, [readable])!;
            Require(CodeSearchReferenceService.TryDecode(normalized, out CodeSearchReference restored) && restored == databaseReference,
                "Database search links should survive readable display and canonical normalization.");
            Field<DatabaseSnapshotLibrary>(main, "_databaseSnapshots").Snapshots.Insert(0,
                new DatabaseMetadataSnapshot { SnapshotId = "different-snapshot", DisplayName = snapshot.DisplayName, DatabaseName = snapshot.DatabaseName });
            readable = (string)Method("GetReadableDatabaseMetadataLink").Invoke(main, [address])!;
            normalized = (string)Method("NormalizeDatabaseMetadataLink").Invoke(main, [readable])!;
            Require(CodeSearchReferenceService.TryDecode(normalized, out restored) && restored == databaseReference,
                "Snapshots with identical display names must not retarget a copied search reference.");
            Require((bool)Method("DiagramMetadataLinkTargetsResource").Invoke(main,
                [address, new ScopedResource { Kind = ResourceKind.DatabaseSnapshot, Path = snapshot.SnapshotId }])!,
                "Removing a database resource should recognize search links to that resource.");
            Require((bool)Method("DiagramMetadataLinkTargetsResource").Invoke(main,
                [metadata.Link, new ScopedResource { Kind = ResourceKind.File, Path = filePath }])!,
                "Removing a file resource should recognize search links to that file.");

            string tablePath = DatabaseDocumentService.CreateCanonicalTableDocumentPath(snapshot, snapshot.Tables[0]);
            Require(Open(main, CodeSearchReferenceService.Encode(reference with { DocumentPath = tablePath })),
                "Search links should open table definitions as code.");
            Require(ReferenceEquals(Field<FloatingCodeWindow>(main, "_activeCodeWindow"), Windows(main)[tablePath]),
                "Opening a table-definition search link should leave the code window active.");
            Require(Field<Dictionary<string, FloatingSpreadsheetWindow>>(main, "_openSpreadsheetWindows").Count == 0,
                "Table-definition search links should not open related spreadsheet data.");
            Require(Open(main, metadata.Link) && Panel(code).IsLoaded && editor.SelectedText == reference.SearchText,
                "Returning to an inactive code tab should restore its visible search and select the first match.");
            Require(tabs.SelectedItem is TabItem returned && ReferenceEquals(returned.Content, code),
                "Returning from another code tab should select the search reference destination.");
        }
        finally
        {
            main.Close();
            File.Delete(filePath);
            Directory.Delete(directory);
            Flush();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static bool Open(MainWindow main, string address)
    {
        var task = (Task<bool>)Method("OpenMetadataLinkAsync").Invoke(main, [address])!;
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            Flush();
            Thread.Sleep(1);
        }
        Require(task.IsCompleted, "Code-link navigation timed out.");
        bool result = task.GetAwaiter().GetResult();
        Flush();
        return result;
    }

    private static void Flush()
    {
        for (int i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, InstancePrivate)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, InstancePrivate)!.GetValue(target)!;
    private static Dictionary<string, FloatingCodeWindow> Windows(MainWindow main) => Field<Dictionary<string, FloatingCodeWindow>>(main, "_openWindows");
    private static SearchPanel Panel(FloatingCodeWindow code) => Field<SearchPanel>(code, "_searchPanel");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
