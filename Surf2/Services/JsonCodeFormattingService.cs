using System.Text;
using System.Text.Json;

namespace Surf2.Services;

internal static class JsonCodeFormattingService
{
    public static string FormatForDisplay(string content)
    {
        try
        {
            // Validate before changing anything, including incomplete JSON, comments and trailing commas.
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = int.MaxValue });
        }
        catch (JsonException)
        {
            return content;
        }

        // Change whitespace only: serializing values could change escapes or number representations
        // that users search for. Property order, duplicate keys and every string/number stay intact.
        var result = new StringBuilder(content.Length);
        // Deeply nested JSON can expand quadratically with indentation. Keep the viewer responsive
        // by retaining the source layout if formatting would grow beyond this display budget.
        long maximumLength = Math.Min(int.MaxValue, Math.Max(1_048_576L, content.Length * 4L));
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = 0; index < content.Length; index++)
        {
            char character = content[index];
            if (inString)
            {
                result.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    result.Append(character);
                    break;
                case '{':
                case '[':
                    result.Append(character);
                    while (index + 1 < content.Length && IsJsonWhitespace(content[index + 1])) index++;
                    char closing = character == '{' ? '}' : ']';
                    if (index + 1 < content.Length && content[index + 1] == closing)
                    {
                        result.Append(closing);
                        index++;
                    }
                    else
                    {
                        depth++;
                        if (!TryAppendIndentedLine(result, depth, maximumLength)) return content;
                    }
                    break;
                case '}':
                case ']':
                    depth--;
                    if (!TryAppendIndentedLine(result, depth, maximumLength)) return content;
                    result.Append(character);
                    break;
                case ',':
                    result.Append(character);
                    if (!TryAppendIndentedLine(result, depth, maximumLength)) return content;
                    break;
                case ':':
                    result.Append(": ");
                    break;
                default:
                    if (!IsJsonWhitespace(character)) result.Append(character);
                    break;
            }

            if (result.Length > maximumLength) return content;
        }

        return result.ToString();
    }

    private static bool IsJsonWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n';

    private static bool TryAppendIndentedLine(StringBuilder result, int depth, long maximumLength)
    {
        long indentationLength = depth * 2L;
        if (result.Length + Environment.NewLine.Length + indentationLength > maximumLength) return false;
        result.AppendLine();
        result.Append(' ', (int)indentationLength);
        return true;
    }
}
