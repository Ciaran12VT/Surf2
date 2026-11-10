using System.Reflection;
using ICSharpCode.AvalonEdit.Document;
using Surf2.Controls;
using Surf2.Models;

namespace Surf2.RegressionTests;

internal static class V2ReferencePresentationTests
{
    public static void Run()
    {
        var text = new TextDocument("Recalculate(); invoice.total; KnownProcedure(); if (ready) { } // Missing();\n\"QuotedCall()\";");
        var styles = new Dictionary<string, ReferenceHighlightStyleSetting>(StringComparer.OrdinalIgnoreCase)
        {
            ["KnownProcedure"] = new() { Foreground = "#CC8800" }
        };
        var loading = new ReferenceHighlightColorizer(styles, referencesPending: true);
        Require(loading.IsPendingAt(text, 1), "An unresolved method call is pending during file indexing.");
        Require(loading.IsPendingAt(text, text.Text.IndexOf("total", StringComparison.Ordinal)), "An unresolved member is pending.");
        Require(!loading.IsPendingAt(text, text.Text.IndexOf("KnownProcedure", StringComparison.Ordinal)), "Known name-based references stay available during indexing.");
        Require(!loading.IsPendingAt(text, text.Text.IndexOf("if", StringComparison.Ordinal)), "Language keywords remain normal syntax.");
        Require(!loading.IsPendingAt(text, text.Text.IndexOf("Missing", StringComparison.Ordinal)), "Comments do not create pending references.");
        Require(!loading.IsPendingAt(text, text.Text.IndexOf("QuotedCall", StringComparison.Ordinal)), "Quoted strings do not create pending references.");
        Require(!new ReferenceHighlightColorizer(styles).IsPendingAt(text, 1), "Unresolved calls return to normal syntax after indexing.");
        Require(!new ReferenceHighlightColorizer(styles, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Recalculate" }).IsPendingAt(text, 1),
            "Available file names retain normal styling and navigation before file contents are parsed.");

        var window = new FloatingCodeWindow(new OpenDocumentState { FilePath = "example.cs" }, text.Text, null);
        window.ApplyReferenceHighlights(new Dictionary<string, ReferenceHighlightStyleSetting>(), referencesPending: true);
        var field = typeof(FloatingCodeWindow).GetField("_referenceHighlightColorizer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Require(field.GetValue(window) is ReferenceHighlightColorizer, "Editors display pending references before the first catalogue arrives.");
        window.ApplyReferenceHighlights(new Dictionary<string, ReferenceHighlightStyleSetting>());
        Require(field.GetValue(window) == null, "Finishing an empty index removes the temporary grey highlighting.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
