using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Surf2.Services.RelationalGrid;

internal static class GridValues
{
    internal const long RowOverhead = 192;
    internal const long CellOverhead = 40;
    internal static long Estimate(IReadOnlyList<string> cells) => checked(RowOverhead + cells.Count * CellOverhead + cells.Sum(s => s.Length * 4L));

    internal static void Validate(IReadOnlyList<string> cells, GridLimits limits)
    {
        if (cells.Count > limits.MaxColumns || cells.Any(s => s == null || s.Length > limits.MaxCellCharacters) || Estimate(cells) > limits.MaxRowBytes)
            throw new GridLimitException("One grid record exceeds the reviewed column/cell/row budget; no rows were truncated.");
    }

    internal static void WriteString(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        foreach (char c in value) writer.Write((ushort)c);
    }

    internal static string ReadString(BinaryReader reader, GridLimits limits)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > limits.MaxCellCharacters || length * 2L > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid owned grid string record.");
        return string.Create(length, reader, static (span, input) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = (char)input.ReadUInt16();
        });
    }
}

internal sealed class FrozenGridQuery
{
    internal GridFilter[] Filters { get; }
    internal GridSort[] Sorts { get; }
    internal bool Any { get; }
    internal string Fingerprint { get; }
    private readonly CompareInfo _compare;

    internal FrozenGridQuery(GridQuery? query, GridDescriptor descriptor, long overlayGeneration)
    {
        int columns = descriptor.Columns.Count;
        if (query?.Filters?.Count > columns || query?.Sorts?.Count > columns || query?.AnyMatchColumnOrdinals?.Count > columns)
            throw new ArgumentException("Too many filter/sort columns.", nameof(query));
        var filters = query?.Filters?.ToArray() ?? [];
        Sorts = query?.Sorts?.ToArray() ?? [];
        var any = (query?.AnyMatchColumnOrdinals ?? []).ToHashSet();
        foreach (var filter in filters)
            if (filter == null || filter.Text == null || filter.Text.Length > 4096 || !Valid(filter.ColumnOrdinal))
                throw new ArgumentException("Invalid column filter.", nameof(query));
        if (filters.Select(f => f.ColumnOrdinal).Distinct().Count() != filters.Length ||
            Sorts.Any(s => s == null || !Valid(s.ColumnOrdinal)) || Sorts.Select(s => s.ColumnOrdinal).Distinct().Count() != Sorts.Length || any.Any(i => !Valid(i)))
            throw new ArgumentException("Duplicate or unknown query column.", nameof(query));
        var active = filters.Where(f => !string.IsNullOrWhiteSpace(f.Text)).OrderBy(f => f.ColumnOrdinal).ToArray();
        var activeAny = active.Where(f => any.Contains(f.ColumnOrdinal)).ToArray();
        Any = activeAny.Length > 0;
        Filters = Any ? activeAny : active;
        _compare = CultureInfo.GetCultureInfo(query?.SortCultureName ?? CultureInfo.CurrentCulture.Name).CompareInfo;
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);
        writer.Write(descriptor.SourceId.ToByteArray());
        GridValues.WriteString(writer, descriptor.Revision);
        writer.Write(overlayGeneration);
        GridValues.WriteString(writer, _compare.Name);
        writer.Write(_compare.Version.FullVersion);
        writer.Write(_compare.Version.SortId.ToByteArray());
        writer.Write(Any);
        writer.Write(Filters.Length);
        foreach (var f in Filters) { writer.Write(f.ColumnOrdinal); GridValues.WriteString(writer, f.Text); }
        writer.Write(Sorts.Length);
        foreach (var s in Sorts) { writer.Write(s.ColumnOrdinal); writer.Write(s.Descending); }
        writer.Flush();
        Fingerprint = Convert.ToHexString(SHA256.HashData(memory.GetBuffer().AsSpan(0, checked((int)memory.Length))));
        bool Valid(int i) => i >= 0 && i < columns;
    }

    internal bool Matches(IReadOnlyList<string> cells)
    {
        foreach (var filter in Filters)
        {
            bool match = cells[filter.ColumnOrdinal].IndexOf(filter.Text, StringComparison.OrdinalIgnoreCase) >= 0;
            if (Any && match) return true;
            if (!Any && !match) return false;
        }
        return !Any;
    }

    internal string[] Keys(IReadOnlyList<string> cells) => Sorts.Select(s => cells[s.ColumnOrdinal]).ToArray();
    internal int Compare(string[] a, long aOrdinal, string[] b, long bOrdinal)
    {
        for (int i = 0; i < Sorts.Length; i++)
        {
            int c = _compare.Compare(a[i], b[i], CompareOptions.None);
            if (c != 0) return Sorts[i].Descending ? -Math.Sign(c) : Math.Sign(c);
        }
        return aOrdinal.CompareTo(bOrdinal);
    }
}
