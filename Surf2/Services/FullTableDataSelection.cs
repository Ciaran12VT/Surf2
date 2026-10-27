using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Surf2.Models;

namespace Surf2.Services;

public sealed class FullTableDataSelection
{
    private FullTableDataSelection(string schemaName, string tableName, int? topCount = null, string? whereClause = null)
    {
        SchemaName = schemaName;
        TableName = tableName;
        TopCount = topCount;
        WhereClause = whereClause;
        VariableSuffix = new string($"{schemaName}_{tableName}"
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
    }

    public string SchemaName { get; }
    public string TableName { get; }
    public int? TopCount { get; }
    public string? WhereClause { get; }
    public string VariableSuffix { get; }

    public static FullTableDataSelection FromParts(string schemaName, string tableName)
    {
        return new FullTableDataSelection(
            string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName.Trim(),
            tableName.Trim());
    }

    public static FullTableDataSelection? TryParse(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        int selectionStart = GetSelectionStart(text);
        FullTableDataSelection? table = ParseTableName(selectionStart < 0 ? text : text[..selectionStart]);
        if (table == null || selectionStart < 0)
        {
            return table;
        }

        string selection = text[selectionStart..];
        if (selection.StartsWith('['))
        {
            if (!selection.EndsWith(']'))
            {
                return null;
            }

            selection = selection[1..^1].Trim();
        }

        Match top = Regex.Match(selection, @"\ATOP\s+([0-9]+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (top.Success && int.TryParse(top.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int topCount) && topCount > 0)
        {
            return new FullTableDataSelection(table.SchemaName, table.TableName, topCount: topCount);
        }

        if (selection.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase) &&
            selection.Length > 5 && char.IsWhiteSpace(selection[5]) &&
            !string.IsNullOrWhiteSpace(selection[5..]))
        {
            return new FullTableDataSelection(table.SchemaName, table.TableName, whereClause: selection[5..].Trim());
        }

        return null;
    }

    public static string GetTableIdentity(string value)
    {
        string text = value.Trim();
        int selectionStart = GetSelectionStart(text);
        FullTableDataSelection? table = ParseTableName(selectionStart < 0 ? text : text[..selectionStart]);
        return table == null ? text : SqlName.FormatMultipartName(table.SchemaName, table.TableName);
    }

    public static List<string> MergeTableNames(IEnumerable<string> names, IEnumerable<string> additionalNames)
    {
        return names.Concat(additionalNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .DistinctBy(GetTableIdentity, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int GetSelectionStart(string text)
    {
        bool inBrackets = false;
        bool inQuotes = false;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (inBrackets)
            {
                if (character == ']')
                {
                    if (index + 1 < text.Length && text[index + 1] == ']')
                    {
                        index++;
                    }
                    else
                    {
                        inBrackets = false;
                    }
                }

                continue;
            }

            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }

                continue;
            }

            if (index > 0 && char.IsWhiteSpace(text[index - 1]) &&
                !string.IsNullOrWhiteSpace(text[..index]) && !text[..index].TrimEnd().EndsWith('.'))
            {
                string remaining = text[index..];
                if (remaining.StartsWith('['))
                {
                    remaining = remaining[1..];
                }

                if (StartsWithKeyword(remaining, "TOP") || StartsWithKeyword(remaining, "WHERE"))
                {
                    return index;
                }
            }

            inBrackets = character == '[';
            inQuotes = character == '"';
        }

        return -1;
    }

    private static bool StartsWithKeyword(string text, string keyword)
    {
        return text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
               (text.Length == keyword.Length || char.IsWhiteSpace(text[keyword.Length]) || text[keyword.Length] == ']');
    }

    private static FullTableDataSelection? ParseTableName(string value)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        char closingQuote = '\0';
        string text = value.Trim();
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (closingQuote != '\0')
            {
                part.Append(character);
                if (character == closingQuote)
                {
                    if (index + 1 < text.Length && text[index + 1] == closingQuote)
                    {
                        part.Append(text[++index]);
                    }
                    else
                    {
                        closingQuote = '\0';
                    }
                }
            }
            else if (character is '[' or '"')
            {
                part.Append(character);
                closingQuote = character == '[' ? ']' : '"';
            }
            else if (character == '.')
            {
                parts.Add(Unquote(part.ToString().Trim()));
                part.Clear();
            }
            else
            {
                part.Append(character);
            }
        }

        if (closingQuote != '\0')
        {
            return null;
        }

        parts.Add(Unquote(part.ToString().Trim()));
        if (parts.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        return parts.Count == 1
            ? new FullTableDataSelection("dbo", parts[0])
            : new FullTableDataSelection(parts[^2], parts[^1]);
    }

    private static string Unquote(string value)
    {
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            return value[1..^1].Replace("]]", "]", StringComparison.Ordinal);
        }

        return value.StartsWith('"') && value.EndsWith('"')
            ? value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : value;
    }
}
