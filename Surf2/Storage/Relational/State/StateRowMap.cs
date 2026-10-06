using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.State;

// Only the explicit, application-owned maps below can supply SQL identifiers.
internal sealed record StateField<T>(string Property, string Column, SqlDbType Type,
    Func<T, object?> Get, Action<T, object?> Set, bool Content = false, bool Nullable = false);

internal sealed class StateRowMap<T>(string table, string key, params StateField<T>[] fields) where T : new()
{
    public string Table { get; } = table;
    public string Key { get; } = key;
    public IReadOnlyList<StateField<T>> Fields { get; } = fields;
    public string Columns => string.Join(", ", fields.Select(f => $"[{f.Column}]"));

    public T Copy(T source, StateBudget budget)
    {
        budget.Row();
        var copy = new T();
        foreach (var field in fields)
        {
            object? value = field.Get(source);
            if (value == null && !field.Nullable) throw new InvalidDataException($"{Table}.{field.Property} is null.");
            if (value is string text) budget.Text(checked((long)text.Length * 2));
            if (value is double number && !double.IsFinite(number))
                throw new InvalidDataException($"{Table}.{field.Property} is not a finite SQL float.");
            field.Set(copy, value);
        }
        return copy;
    }

    public string Projection(string alias = "s") => string.Join(", ", fields.Select(f =>
    {
        string expression = f.Content
            ? $"(SELECT c.[Text] FROM surf.TextContent c WHERE c.ContentKey={alias}.[{f.Column}])"
            : $"{alias}.[{f.Column}]";
        return f.Type == SqlDbType.NVarChar ? StateText.Projection(expression) : expression;
    }));

    public async Task<T> ReadAsync(SqlDataReader reader, int start, StateBudget budget, CancellationToken ct)
    {
        budget.Row();
        var value = new T();
        int index = start;
        foreach (var field in fields)
        {
            object? scalar;
            if (field.Type == SqlDbType.NVarChar)
            {
                scalar = await StateText.ReadAsync(reader, index, budget, ct);
                index += 2;
            }
            else
            {
                scalar = await reader.IsDBNullAsync(index, ct) ? null : reader.GetValue(index);
                index++;
            }
            if (scalar == null && !field.Nullable) throw new InvalidDataException($"Missing {Table}.{field.Property}.");
            field.Set(value, scalar);
        }
        return value;
    }

    public async Task AddParametersAsync(SqlCommand command, T value, RelationalContentStore content,
        SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            object? scalar = field.Get(value);
            if (field.Content)
                scalar = await content.PutTextAsync(connection, transaction, (string)scalar!, ct);
            command.Parameters.Add(RelationalSession.Parameter($"@f{i}", field.Content ? SqlDbType.BigInt : field.Type,
                scalar, !field.Content && field.Type == SqlDbType.NVarChar ? -1 : 0));
        }
    }

    public string Values => string.Join(", ", fields.Select((_, i) => $"@f{i}"));
    public string Assignments => string.Join(", ", fields.Select((f, i) => $"[{f.Column}]=@f{i}"));
}

internal static class StateText
{
    public static string Projection(string expression) => $"CONVERT(bigint, DATALENGTH({expression})), {expression}";

    // Length is a preceding scalar column: querying a LOB's length can consume it under SequentialAccess.
    public static async Task<string?> ReadAsync(SqlDataReader reader, int lengthColumn, StateBudget budget, CancellationToken ct)
    {
        if (await reader.IsDBNullAsync(lengthColumn, ct)) return null;
        long bytes = reader.GetInt64(lengthColumn);
        budget.Text(bytes);
        using var text = reader.GetTextReader(lengthColumn + 1);
        string value = await text.ReadToEndAsync(ct);
        if (checked((long)value.Length * 2) != bytes) throw new InvalidDataException("State text length is inconsistent.");
        return value;
    }
}

internal static class StateField
{
    public static StateField<T> NullableKey<T>(string name, Func<T, long?> get, Action<T, long?> set) =>
        new(name, name, SqlDbType.BigInt, v => get(v), (v, x) => set(v, (long?)x), Nullable: true);

    public static StateField<T> Text<T>(string name, Func<T, string> get, Action<T, string> set, bool content = false,
        string? column = null) => new(name, column ?? (content ? name + "ContentKey" : name), SqlDbType.NVarChar,
        v => get(v), (v, x) => set(v, (string)x!), content);
    public static StateField<T> NullableText<T>(string name, Func<T, string?> get, Action<T, string?> set) =>
        new(name, name, SqlDbType.NVarChar, v => get(v), (v, x) => set(v, (string?)x), Nullable: true);
    public static StateField<T> Bool<T>(string name, Func<T, bool> get, Action<T, bool> set) =>
        new(name, name, SqlDbType.Bit, v => get(v), (v, x) => set(v, (bool)x!));
    public static StateField<T> Int<T>(string name, Func<T, int> get, Action<T, int> set, string? column = null) =>
        new(name, column ?? name, SqlDbType.Int, v => get(v), (v, x) => set(v, (int)x!));
    public static StateField<T> Float<T>(string name, Func<T, double> get, Action<T, double> set) =>
        new(name, name, SqlDbType.Float, v => get(v), (v, x) => set(v, (double)x!));
    public static StateField<T> Time<T>(string name, Func<T, DateTimeOffset> get, Action<T, DateTimeOffset> set) =>
        new(name, name, SqlDbType.DateTimeOffset, v => get(v), (v, x) => set(v, (DateTimeOffset)x!));
    public static StateField<T> Enum<T, E>(string name, Func<T, E> get, Action<T, E> set) where E : struct, Enum =>
        new(name, name, SqlDbType.Int, v => Checked(get(v)), (v, x) =>
        {
            E parsed = (E)System.Enum.ToObject(typeof(E), x!);
            Checked(parsed);
            set(v, parsed);
        });
    private static int Checked<E>(E value) where E : struct, Enum => System.Enum.IsDefined(value)
        ? Convert.ToInt32(value) : throw new InvalidDataException($"Unsupported {typeof(E).Name} value.");
}
