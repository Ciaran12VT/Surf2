using System.Text;
using Surf2.Models;

namespace Surf2.Services;

public static class CsvGridParser
{
    public static CsvGridDocument Parse(string content)
    {
        List<List<string>> records = ParseRecords(content);
        if (records.Count == 0)
        {
            return new CsvGridDocument(CreateGeneratedHeaders(1), []);
        }

        int columnCount = Math.Max(1, records.Max(record => record.Count));
        bool hasHeaders = LooksLikeHeaderRow(records[0], columnCount);
        IReadOnlyList<string> headers = hasHeaders
            ? NormalizeHeaders(records[0], columnCount)
            : CreateGeneratedHeaders(columnCount);

        IEnumerable<List<string>> dataRecords = hasHeaders ? records.Skip(1) : records;
        List<CsvGridRow> rows = dataRecords
            .Select(record => new CsvGridRow(PadRecord(record, columnCount)))
            .ToList();

        return new CsvGridDocument(headers, rows);
    }

    private static List<List<string>> ParseRecords(string content)
    {
        var records = new List<List<string>>();
        var currentRecord = new List<string>();
        var currentField = new StringBuilder();
        bool inQuotes = false;
        bool sawAnyCharacter = false;

        for (int index = 0; index < content.Length; index++)
        {
            char value = content[index];
            sawAnyCharacter = true;

            if (inQuotes)
            {
                if (value == '"')
                {
                    if (index + 1 < content.Length && content[index + 1] == '"')
                    {
                        currentField.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentField.Append(value);
                }

                continue;
            }

            switch (value)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    currentRecord.Add(currentField.ToString());
                    currentField.Clear();
                    break;
                case '\r':
                    if (index + 1 < content.Length && content[index + 1] == '\n')
                    {
                        index++;
                    }

                    AddCurrentRecord(records, currentRecord, currentField);
                    break;
                case '\n':
                    AddCurrentRecord(records, currentRecord, currentField);
                    break;
                default:
                    currentField.Append(value);
                    break;
            }
        }

        if (sawAnyCharacter && (currentField.Length > 0 || currentRecord.Count > 0 || content.EndsWith(",", StringComparison.Ordinal)))
        {
            AddCurrentRecord(records, currentRecord, currentField);
        }

        return records
            .Where(record => record.Any(field => !string.IsNullOrWhiteSpace(field)))
            .ToList();
    }

    private static void AddCurrentRecord(List<List<string>> records, List<string> currentRecord, StringBuilder currentField)
    {
        currentRecord.Add(currentField.ToString());
        currentField.Clear();
        records.Add([.. currentRecord]);
        currentRecord.Clear();
    }

    private static bool LooksLikeHeaderRow(IReadOnlyList<string> row, int columnCount)
    {
        string[] normalized = PadRecord(row, columnCount)
            .Select(value => value.Trim())
            .ToArray();

        if (normalized.All(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        if (normalized.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != normalized.Count(value => !string.IsNullOrWhiteSpace(value)))
        {
            return false;
        }

        int textLikeCount = normalized.Count(IsTextLikeHeader);
        return textLikeCount >= Math.Max(1, normalized.Length / 2);
    }

    private static bool IsTextLikeHeader(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Any(char.IsLetter) &&
               !decimal.TryParse(value, out _) &&
               !DateTime.TryParse(value, out _);
    }

    private static IReadOnlyList<string> NormalizeHeaders(IReadOnlyList<string> headers, int columnCount)
    {
        string[] normalized = PadRecord(headers, columnCount);
        var seenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < normalized.Length; index++)
        {
            string header = normalized[index].Trim();
            if (string.IsNullOrWhiteSpace(header))
            {
                header = $"Column {index + 1}";
            }

            string uniqueHeader = header;
            int suffix = 2;
            while (!seenHeaders.Add(uniqueHeader))
            {
                uniqueHeader = $"{header} {suffix}";
                suffix++;
            }

            normalized[index] = uniqueHeader;
        }

        return normalized;
    }

    private static string[] CreateGeneratedHeaders(int columnCount)
    {
        return Enumerable.Range(1, Math.Max(1, columnCount))
            .Select(index => $"Column {index}")
            .ToArray();
    }

    private static string[] PadRecord(IReadOnlyList<string> record, int columnCount)
    {
        var cells = new string[Math.Max(1, columnCount)];
        for (int index = 0; index < cells.Length; index++)
        {
            cells[index] = index < record.Count ? record[index] : string.Empty;
        }

        return cells;
    }
}
