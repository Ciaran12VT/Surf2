using System.Text.Json;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;

namespace Surf2.RegressionTests;

internal static class JsonCodeRenderingTests
{
    public static void Run()
    {
        var settings = new CodeWindowSettings();
        settings.EnsureDefaultBackcolorEntries();
        Require(settings.GetLanguageForFile("sample.JSON") == CodeWindowSettings.JsonLanguage,
            "JSON files should default to JSON rendering regardless of extension case.");
        Require(settings.BackcolorsByExtension.Single(entry => entry.Extension == ".json").Language == CodeWindowSettings.JsonLanguage,
            "Existing settings should gain the JSON extension default.");
        Require(!settings.EnsureDefaultBackcolorEntries(), "Adding default extensions should be idempotent.");
        Require(CodeWindowSettings.GetLanguageTypeLabel("json") == "JSON", "JSON tabs should have a JSON type label.");

        var syntax = new SyntaxHighlightingService();
        IHighlightingDefinition? definition = syntax.GetDefinition("sample.JSON");
        Require(definition != null, "JSON should have a syntax definition.");
        using (var highlighter = new DocumentHighlighter(new TextDocument("{\"name\":\"Surf\",\"count\":42,\"enabled\":true,\"value\":null}"), definition!))
        {
            Require(highlighter.HighlightLine(1).Sections.Count > 0, "JSON rendering should produce highlighted tokens.");
        }

        const string source = """{"items":[{"label":"a \"quote\", \\ slash, \u0041 and { }","n":123456789012345678901234567890,"exp":1.2300e+04,"flag":true},null,false],"empty":{},"same":1,"same":2}""";
        var window = Open("sample.json", source, definition);
        Require(window.Text.Contains('\n') && window.Text.Contains("  \"items\""), "Compact JSON should open with indentation and line breaks.");
        Require(((TextEditor)window.FindName("Editor")).IsReadOnly, "Formatting should remain a read-only presentation change.");
        using (JsonDocument formatted = JsonDocument.Parse(window.Text))
        using (JsonDocument original = JsonDocument.Parse(source))
        {
            JsonElement formattedItem = formatted.RootElement.GetProperty("items")[0];
            JsonElement originalItem = original.RootElement.GetProperty("items")[0];
            foreach (string property in new[] { "label", "n", "exp", "flag" })
            {
                Require(formattedItem.GetProperty(property).GetRawText() == originalItem.GetProperty(property).GetRawText(),
                    $"Formatting must preserve the original JSON token spelling for {property}.");
            }

            Require(formatted.RootElement.EnumerateObject().Count(property => property.Name == "same") == 2,
                "Formatting must preserve duplicate properties.");
            Require(formatted.RootElement.GetProperty("items")[1].ValueKind == JsonValueKind.Null &&
                    formatted.RootElement.GetProperty("items")[2].ValueKind == JsonValueKind.False,
                "Array literals should retain their values and order.");
        }

        string pretty = window.Text;
        window.ApplySyntaxHighlighting(definition);
        Require(window.Text == pretty, "Reapplying JSON highlighting must not change formatted text.");
        window.ApplySyntaxHighlighting(null);
        Require(window.Text == source, "Switching to plain text should restore the original source text.");
        window.ApplySyntaxHighlighting(definition);
        Require(window.Text == pretty, "Switching back to JSON should reproduce the same formatted text.");
        Require(Open("restored.json", pretty, definition).Text == pretty, "Formatting already formatted JSON should be stable.");

        const string escapedSearch = """\"quote\", \\ slash, \u0041""";
        Require(window.TryShowSearchAndScrollToFirstMatch(escapedSearch, false, out int matchLine, out int matchColumn),
            "A source search containing escaped JSON strings should still match after formatting.");
        var displayedDocument = new TextDocument(window.Text);
        Require(matchLine > 1 && displayedDocument.GetText(displayedDocument.GetOffset(matchLine, matchColumn), escapedSearch.Length) == escapedSearch,
            "Search navigation should report the match position in the formatted document.");

        string nested = new string('[', 70) + "0" + new string(']', 70);
        string formattedNested = Open("nested.json", nested, definition).Text;
        Require(formattedNested.Contains('\n'), "Valid JSON deeper than the parser's default 64 levels should still be formatted.");
        using (JsonDocument parsedNested = JsonDocument.Parse(formattedNested, new JsonDocumentOptions { MaxDepth = 70 }))
        {
            JsonElement nestedValue = parsedNested.RootElement;
            for (int depth = 0; depth < 70; depth++) nestedValue = nestedValue[0];
            Require(nestedValue.GetInt32() == 0, "Formatting nested arrays must preserve their structure and leaf value.");
        }

        string extremelyNested = new string('[', 1200) + "0" + new string(']', 1200);
        Require(Open("extremely-nested.json", extremelyNested, definition).Text == extremelyNested,
            "JSON whose indentation would exceed the display expansion budget should retain its source layout.");

        foreach (string valid in new[] { "{}", "[]", "true", "false", "null", "123456789012345678901234567890", "1.2300e+04", "\"a string\"" })
        {
            using JsonDocument formatted = JsonDocument.Parse(Open("value.json", " \t" + valid + "\r\n", definition).Text);
            Require(formatted.RootElement.GetRawText() == valid, "Empty containers and top-level scalar JSON must remain valid and unchanged in value.");
        }

        foreach (string invalid in new[] { "", " \t\r\n", "{\"a\":1,}", "{\"a\":1", "{a:1}", "// comment\n{}", "{} {}", "not JSON" })
        {
            Require(Open("invalid.json", invalid, definition).Text == invalid, "Invalid JSON must remain exactly as supplied.");
        }

        var overriddenSettings = new CodeWindowSettings
        {
            BackcolorsByExtension =
            [
                new() { Extension = ".json", Language = CodeWindowSettings.PlainTextLanguage },
                new() { Extension = ".data", Language = CodeWindowSettings.JsonLanguage }
            ]
        };
        overriddenSettings.EnsureDefaultBackcolorEntries();
        IHighlightingDefinition? plainText = syntax.GetDefinition("sample.json", overriddenSettings.GetLanguageForFile("sample.json"));
        Require(plainText == null && Open("sample.json", source, plainText).Text == source,
            "An explicit Plain Text override must suppress JSON formatting.");
        IHighlightingDefinition? customJson = syntax.GetDefinition("sample.data", overriddenSettings.GetLanguageForFile("sample.data"));
        Require(customJson != null && Open("sample.data", source, customJson).Text == pretty,
            "Choosing JSON for another extension should enable highlighting and formatting.");
        Require(Open("sample.js", source, syntax.GetDefinition("sample.js")).Text == source,
            "Other code languages should retain their original text even when it is valid JSON.");
    }

    private static FloatingCodeWindow Open(string path, string text, IHighlightingDefinition? definition) =>
        new(new OpenDocumentState { FilePath = path }, text, definition);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
