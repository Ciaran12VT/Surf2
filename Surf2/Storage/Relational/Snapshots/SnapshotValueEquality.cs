using System.Text.Json;
using Surf2.Models;

namespace Surf2.Storage.Relational.Snapshots;

/// <summary>Exact persisted values; lookup-name equality is deliberately not used for payload equality.</summary>
internal static class SnapshotValueEquality
{
    internal static bool Object(SqlDatabaseObject a, SqlDatabaseObject b) => a.Kind == b.Kind &&
        a.SchemaName == b.SchemaName && a.ObjectName == b.ObjectName && a.TypeDescription == b.TypeDescription &&
        a.ParentSchemaName == b.ParentSchemaName && a.ParentObjectName == b.ParentObjectName && a.Definition == b.Definition;
    internal static bool Table(SqlTable? a, SqlTable? b) => a == null || b == null ? a == b :
        a.SchemaName == b.SchemaName && a.TableName == b.TableName && a.HasFullData == b.HasFullData &&
        a.FullDataRowCount == b.FullDataRowCount && Date(a.FullDataImportedAtUtc, b.FullDataImportedAtUtc);
    internal static bool Column(SqlColumn a, SqlColumn b) => a.SchemaName == b.SchemaName && a.TableName == b.TableName &&
        a.ColumnName == b.ColumnName && a.DataType == b.DataType && a.MaxLength == b.MaxLength &&
        a.NumericPrecision == b.NumericPrecision && a.NumericScale == b.NumericScale && a.IsNullable == b.IsNullable &&
        a.IsIdentity == b.IsIdentity && a.Ordinal == b.Ordinal;
    internal static bool Key(SqlPrimaryKeyColumn a, SqlPrimaryKeyColumn b) => a.SchemaName == b.SchemaName &&
        a.TableName == b.TableName && a.ConstraintName == b.ConstraintName && a.ColumnName == b.ColumnName && a.KeyOrdinal == b.KeyOrdinal;
    internal static bool Date(DateTimeOffset? a, DateTimeOffset? b) => !a.HasValue || !b.HasValue ? a == b : a.Value.EqualsExact(b.Value);
    internal static bool Sequence<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T,T,bool> equal) =>
        a.Count == b.Count && a.Zip(b).All(pair => equal(pair.First, pair.Second));

    // Capture stores property order, duplicate properties and raw value tokens, but
    // normalizes the whitespace surrounding a root object's properties. Match that
    // representation exactly without constructing another full dataset or string.
    internal static bool Row(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        if (a.ValueKind != JsonValueKind.Object) return a.GetRawText() == b.GetRawText();
        var left = a.EnumerateObject(); var right = b.EnumerateObject();
        while (left.MoveNext())
            if (!right.MoveNext() || left.Current.Name != right.Current.Name ||
                left.Current.Value.GetRawText() != right.Current.Value.GetRawText()) return false;
        return !right.MoveNext();
    }
}
