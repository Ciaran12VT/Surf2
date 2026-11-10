using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Surf2.Services.RelationalGrid;

// Deliberately preserves CsvGridParser's permissive quoting and blank-record
// removal. A physical line is not a CSV record.
internal sealed class CsvRecordReader(TextReader reader, GridLimits limits)
{
    private readonly char[] _buffer = new char[4096];
    private int _position;
    private int _length;

    private async ValueTask<int> PeekAsync(CancellationToken ct)
    {
        if (_position >= _length)
        {
            _length = await reader.ReadAsync(_buffer, ct).ConfigureAwait(false);
            _position = 0;
        }
        return _length == 0 ? -1 : _buffer[_position];
    }

    private async ValueTask<int> ReadAsync(CancellationToken ct)
    {
        int value = await PeekAsync(ct).ConfigureAwait(false);
        if (value >= 0) _position++;
        return value;
    }

    internal async IAsyncEnumerable<string[]> RecordsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        bool quotes = false;
        bool sawAny = false;
        int last = -1;
        long recordCharacters = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int next = await ReadAsync(ct).ConfigureAwait(false);
            if (next < 0) break;
            sawAny = true;
            last = next;
            char value = (char)next;
            if (quotes)
            {
                if (value == '"')
                {
                    if (await PeekAsync(ct).ConfigureAwait(false) == '"')
                    {
                        _ = await ReadAsync(ct).ConfigureAwait(false);
                        Append('"');
                    }
                    else quotes = false;
                }
                else Append(value);
                continue;
            }
            switch (value)
            {
                case '"': quotes = true; break;
                case ',': AddField(); break;
                case '\r':
                    if (await PeekAsync(ct).ConfigureAwait(false) == '\n') _ = await ReadAsync(ct).ConfigureAwait(false);
                    goto case '\n';
                case '\n':
                    var complete = FinishRecord();
                    if (complete.Any(s => !string.IsNullOrWhiteSpace(s))) yield return complete;
                    break;
                default: Append(value); break;
            }
        }
        if (sawAny && (field.Length > 0 || record.Count > 0 || last == ','))
        {
            var complete = FinishRecord();
            if (complete.Any(s => !string.IsNullOrWhiteSpace(s))) yield return complete;
        }

        void Append(char c)
        {
            if (field.Length >= limits.MaxCellCharacters || GridValues.RowOverhead + (record.Count + 1) * GridValues.CellOverhead + (++recordCharacters) * 4 > limits.MaxRowBytes)
                throw new GridLimitException("CSV record exceeds the streaming cell/row budget, including blank records.");
            field.Append(c);
        }
        void AddField()
        {
            if (record.Count >= limits.MaxColumns) throw new GridLimitException("CSV record exceeds the column budget.");
            record.Add(field.ToString());
            field.Clear();
            if (GridValues.RowOverhead + record.Count * GridValues.CellOverhead + recordCharacters * 4 > limits.MaxRowBytes) throw new GridLimitException("CSV record exceeds the row budget.");
        }
        string[] FinishRecord()
        {
            AddField();
            string[] cells = record.ToArray();
            record.Clear();
            recordCharacters = 0;
            return cells;
        }
    }
}

internal static class CsvHeaderPolicy
{
    internal static (string[] Headers, bool HasHeader) Infer(IReadOnlyList<string>? first, int maximumColumns)
    {
        int count = Math.Max(1, maximumColumns);
        string[] values = Enumerable.Range(0, count).Select(i => first != null && i < first.Count ? first[i].Trim() : string.Empty).ToArray();
        bool hasHeader = values.Any(v => !string.IsNullOrWhiteSpace(v)) &&
            values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Count(v => !string.IsNullOrWhiteSpace(v)) &&
            values.Count(v => !string.IsNullOrWhiteSpace(v) && v.Any(char.IsLetter) && !decimal.TryParse(v, out _) && !DateTime.TryParse(v, out _)) >= Math.Max(1, count / 2);
        if (!hasHeader) return (Enumerable.Range(1, count).Select(i => $"Column {i}").ToArray(), false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string seed = string.IsNullOrWhiteSpace(values[i]) ? $"Column {i + 1}" : values[i];
            string value = seed;
            int suffix = 2;
            while (!seen.Add(value)) value = $"{seed} {suffix++}";
            values[i] = value;
        }
        return (values, true);
    }
}
