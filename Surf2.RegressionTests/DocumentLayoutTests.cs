using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Storage;

namespace Surf2.RegressionTests;

internal static class DocumentLayoutTests
{
    private const string SourcePath = @"C:\Surf2LayoutFixture\Source.cs";
    private const string CanvasTargetPath = @"C:\Surf2LayoutFixture\CanvasTarget.cs";
    private const string TabTargetPath = @"C:\Surf2LayoutFixture\TabTarget.cs";
    private const string SpreadsheetPath = @"C:\Surf2LayoutFixture\Target.csv";

    public static void Run()
    {
        var app = Application.Current ?? throw new InvalidOperationException("Initialize WPF resources before the layout check.");
        Window? previousMainWindow = app.MainWindow;
        var main = new MainWindow(SqlServerConnectionOptions.FromConnectionString(
            "Server=127.0.0.1,1;Database=Surf2_DocumentLayout_NoSql;Integrated Security=True;Connect Timeout=1"));
        main.Loaded -= Method("Window_Loaded").CreateDelegate<RoutedEventHandler>(main);
        main.Closing -= Method("Window_Closing").CreateDelegate<CancelEventHandler>(main);
        try
        {
            var original = new OpenDocumentState { FilePath = SourcePath, Left = 240, Top = 180, Width = 760, Height = 500 };
            OpenCode(main, SourcePath, original);
            var canvas = (Canvas)main.FindName("WorkspaceCanvas");
            var source = canvas.Children.OfType<FloatingCodeWindow>().Single();

            OpenCode(main, CanvasTargetPath, source: source);
            OpenDocumentState canvasTarget = main.OpenTabs.Single(tab => tab.FilePath == CanvasTargetPath).State;
            double canvasTargetLeft = canvasTarget.Left;
            Require(canvasTargetLeft > original.Left + original.Width && canvasTarget.Top == original.Top,
                "A reference opened on the canvas should still be placed next to its source.");

            MethodInfo switchView = Method("SetCodeViewMode");
            Invoke(main, switchView, Enum.Parse(switchView.GetParameters()[0].ParameterType, "Tabs"));
            Require(double.IsNaN(source.Width) && double.IsNaN(source.Height),
                "The source must be docked with WPF Auto dimensions to reproduce the reported failure.");

            OpenCode(main, TabTargetPath, source: source);
            ((Task)Invoke(main, Method("OpenSpreadsheetContentAsync"), SpreadsheetPath, "Id,Name\r\n1,Example", "Target.csv", null, source)!)
                .GetAwaiter().GetResult();

            var captured = (List<OpenDocumentState>)Invoke(main, Method("CaptureRelationalWindows"))!;
            Require(captured.Count == 4 && captured.Any(document => document.FilePath == TabTargetPath) &&
                captured.Any(document => document.FilePath == SpreadsheetPath), "The new code and spreadsheet tabs must both be captured.");
            Require(captured.All(document => double.IsFinite(document.Left) && double.IsFinite(document.Top) &&
                double.IsFinite(document.Width) && document.Width > 0 && double.IsFinite(document.Height) && document.Height > 0),
                "Every captured document must have usable bounds for workbench persistence.");
            var capturedSource = captured.Single(document => document.FilePath == SourcePath);
            Require(capturedSource is { Left: 240, Top: 180, Width: 760, Height: 500 } &&
                captured.Single(document => document.FilePath == CanvasTargetPath).Left == canvasTargetLeft,
                "Docking and opening references must preserve the existing canvas layout.");
            Require(!main.IsLoaded && new WindowInteropHelper(main).Handle == IntPtr.Zero,
                "The fixture must stay offscreen without running startup.");
        }
        finally
        {
            main.Close();
            app.MainWindow = previousMainWindow;
        }
    }

    private static void OpenCode(MainWindow main, string path, OpenDocumentState? existing = null, FloatingCodeWindow? source = null) =>
        ((Task)Invoke(main, Method("OpenDocumentContentAsync"), path, "class Example { }", path, path, existing, null, null, null, source, true)!)
            .GetAwaiter().GetResult();

    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(MainWindow).FullName, name);

    private static object? Invoke(MainWindow main, MethodInfo method, params object?[] args) =>
        method.Invoke(main, BindingFlags.DoNotWrapExceptions, null, args, null);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
