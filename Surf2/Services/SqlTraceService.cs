using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Surf2.Models;

namespace Surf2.Services;

public sealed partial class SqlTraceService
{
    private const int MaximumTraceDepth = 20;

    private static readonly Regex BlockCommentPattern = new(
        @"/\*.*?\*/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex LineCommentPattern = new(
        @"--.*?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public SqlTraceResult BuildTrace(string rootName, string rootSql, DatabaseSnapshotLibrary databaseSnapshots)
    {
        List<SqlTraceObject> storedProcedures = CreateStoredProcedureIndex(databaseSnapshots);
        var builder = new StringBuilder();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activePath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counters = new SqlTraceCounters();

        string normalizedRootName = NormalizeTraceName(rootName);
        AppendTrace(
            builder,
            rootName,
            rootSql,
            currentObjectKey: normalizedRootName,
            storedProcedures,
            visited,
            activePath,
            counters,
            depth: 0);

        return new SqlTraceResult(builder.ToString().TrimEnd(), counters.QueryCount, counters.StoredProcedureCallCount);
    }

    private static void AppendTrace(
        StringBuilder builder,
        string name,
        string sql,
        string currentObjectKey,
        IReadOnlyCollection<SqlTraceObject> storedProcedures,
        HashSet<string> visited,
        HashSet<string> activePath,
        SqlTraceCounters counters,
        int depth)
    {
        counters.QueryCount++;
        string indent = new(' ', depth * 2);
        SqlCrudSummary crud = ExtractCrud(sql);
        List<SqlTraceObject> calledProcedures = FindCalledStoredProcedures(sql, storedProcedures, currentObjectKey)
            .OrderBy(item => item.QualifiedName)
            .ThenBy(item => item.SnapshotDisplayName)
            .ToList();

        builder.AppendLine($"{indent}Query: {name}");
        AppendTraceLine(builder, indent, "Selects", crud.Selects);
        AppendTraceLine(builder, indent, "Updates", crud.Updates);
        AppendTraceLine(builder, indent, "Inserts", crud.Inserts);
        AppendTraceLine(builder, indent, "Deletes", crud.Deletes);

        if (calledProcedures.Count > 0)
        {
            counters.StoredProcedureCallCount += calledProcedures.Count;
            AppendTraceLine(builder, indent, "Calls", calledProcedures.Select(item => item.DisplayName));
        }

        if (crud.IsEmpty && calledProcedures.Count == 0)
        {
            builder.AppendLine($"{indent}No table usage or stored procedure calls found.");
        }

        builder.AppendLine();

        if (depth >= MaximumTraceDepth)
        {
            builder.AppendLine($"{indent}Trace stopped: maximum nested depth reached.");
            builder.AppendLine();
            return;
        }

        foreach (SqlTraceObject calledProcedure in calledProcedures)
        {
            if (activePath.Contains(calledProcedure.Key))
            {
                builder.AppendLine($"{indent}  Query: {calledProcedure.DisplayName} (recursive call already in progress)");
                builder.AppendLine();
                continue;
            }

            if (!visited.Add(calledProcedure.Key))
            {
                builder.AppendLine($"{indent}  Query: {calledProcedure.DisplayName} (already traced)");
                builder.AppendLine();
                continue;
            }

            activePath.Add(calledProcedure.Key);
            AppendTrace(
                builder,
                calledProcedure.DisplayName,
                calledProcedure.Definition,
                calledProcedure.Key,
                storedProcedures,
                visited,
                activePath,
                counters,
                depth + 1);
            activePath.Remove(calledProcedure.Key);
        }
    }

    private static void AppendTraceLine(StringBuilder builder, string indent, string label, IEnumerable<string> values)
    {
        string joined = string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value));
        if (!string.IsNullOrWhiteSpace(joined))
        {
            builder.AppendLine($"{indent}{label}: {joined}");
        }
    }

    private static List<SqlTraceObject> CreateStoredProcedureIndex(DatabaseSnapshotLibrary databaseSnapshots)
    {
        return databaseSnapshots.Snapshots
            .SelectMany(snapshot => snapshot.Objects
                .Where(databaseObject =>
                    databaseObject.Kind == SqlDatabaseObjectKind.StoredProcedure &&
                    !string.IsNullOrWhiteSpace(databaseObject.ObjectName) &&
                    !string.IsNullOrWhiteSpace(databaseObject.Definition))
                .Select(databaseObject =>
                {
                    string qualifiedName = SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName);
                    return new SqlTraceObject(
                        Key: $"{snapshot.SnapshotId}|{qualifiedName}",
                        SimpleName: databaseObject.ObjectName,
                        QualifiedName: qualifiedName,
                        SnapshotDisplayName: snapshot.DisplayName,
                        Definition: databaseObject.Definition);
                }))
            .ToList();
    }

    private static List<SqlTraceObject> FindCalledStoredProcedures(
        string sql,
        IReadOnlyCollection<SqlTraceObject> storedProcedures,
        string currentObjectKey,
        CancellationToken cancellationToken = default,
        bool bounded = false)
    {
        if (string.IsNullOrWhiteSpace(sql) || storedProcedures.Count == 0)
        {
            return [];
        }

        string strippedSql = StripComments(sql);
        string normalizedCurrentObjectKey = NormalizeTraceName(currentObjectKey);
        var results = new List<SqlTraceObject>();

        foreach (SqlTraceObject storedProcedure in storedProcedures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(NormalizeTraceName(storedProcedure.Key), normalizedCurrentObjectKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeTraceName(storedProcedure.SimpleName), normalizedCurrentObjectKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeTraceName(storedProcedure.QualifiedName), normalizedCurrentObjectKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string escapedName = Regex.Escape(storedProcedure.SimpleName);
            string pattern =
                $@"(?i)\b(?:exec(?:ute)?\s+)?(?:(?:\[[^\]\r\n]+\]|[A-Za-z_#][A-Za-z0-9_#$]*)\s*\.\s*){{0,2}}\[?{escapedName}\]?(?=\s*(?:;|\(|@|\b|$))";

            if (bounded
                ? Regex.IsMatch(strippedSql, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))
                : Regex.IsMatch(strippedSql, pattern, RegexOptions.CultureInvariant))
            {
                results.Add(storedProcedure);
            }
        }

        return results
            .DistinctBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string StripComments(string sql)
    {
        string withoutBlockComments = BlockCommentPattern.Replace(sql, string.Empty);
        return LineCommentPattern.Replace(withoutBlockComments, string.Empty);
    }

    private static SqlCrudSummary ExtractCrud(string sql)
    {
        var deletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inserts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(sql))
        {
            return new SqlCrudSummary(deletes, inserts, updates, selects);
        }

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql);
            TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> _);
            fragment.Accept(new CrudVisitor(
                onDelete: tableName => AddTableName(deletes, tableName),
                onInsert: tableName => AddTableName(inserts, tableName),
                onUpdate: tableName => AddTableName(updates, tableName),
                onSelect: tableName => AddTableName(selects, tableName)));
        }
        catch (ArgumentException)
        {
            return new SqlCrudSummary(deletes, inserts, updates, selects);
        }

        return new SqlCrudSummary(deletes, inserts, updates, selects);
    }

    private static void AddTableName(HashSet<string> tableNames, string? tableName)
    {
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            tableNames.Add(tableName);
        }
    }

    private static string? GetSchemaObjectName(SchemaObjectName? name)
    {
        if (name == null || name.BaseIdentifier == null)
        {
            return null;
        }

        string baseName = name.BaseIdentifier.Value;
        if (string.IsNullOrWhiteSpace(baseName) ||
            baseName.StartsWith('#') ||
            baseName.StartsWith('@') ||
            baseName.Equals("inserted", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("deleted", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] parts = name.Identifiers
            .Select(identifier => identifier.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return parts.Length == 0 ? baseName : string.Join(".", parts);
    }

    private static string NormalizeTraceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string normalized = name.Trim();
        if (normalized.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        normalized = normalized.Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        return normalized;
    }

    private sealed record SqlTraceObject(
        string Key,
        string SimpleName,
        string QualifiedName,
        string SnapshotDisplayName,
        string Definition)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(SnapshotDisplayName)
            ? QualifiedName
            : $"{QualifiedName} ({SnapshotDisplayName})";
    }

    private sealed class SqlTraceCounters
    {
        public int QueryCount { get; set; }

        public int StoredProcedureCallCount { get; set; }
    }

    private sealed record SqlCrudSummary(
        IReadOnlyCollection<string> Deletes,
        IReadOnlyCollection<string> Inserts,
        IReadOnlyCollection<string> Updates,
        IReadOnlyCollection<string> Selects)
    {
        public bool IsEmpty => Deletes.Count == 0 && Inserts.Count == 0 && Updates.Count == 0 && Selects.Count == 0;
    }

    private sealed class CrudVisitor : TSqlFragmentVisitor
    {
        private readonly Action<string> _onDelete;
        private readonly Action<string> _onInsert;
        private readonly Action<string> _onUpdate;
        private readonly Action<string> _onSelect;

        public CrudVisitor(Action<string> onDelete, Action<string> onInsert, Action<string> onUpdate, Action<string> onSelect)
        {
            _onDelete = onDelete;
            _onInsert = onInsert;
            _onUpdate = onUpdate;
            _onSelect = onSelect;
        }

        public override void Visit(DeleteSpecification node)
        {
            if (node.Target is NamedTableReference target)
            {
                string? tableName = GetSchemaObjectName(target.SchemaObject);
                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    _onDelete(tableName);
                }
            }

            node.FromClause?.Accept(new TableReferenceVisitor(_onSelect));
            base.Visit(node);
        }

        public override void Visit(InsertSpecification node)
        {
            if (node.Target is NamedTableReference target)
            {
                string? tableName = GetSchemaObjectName(target.SchemaObject);
                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    _onInsert(tableName);
                }
            }

            base.Visit(node);
        }

        public override void Visit(UpdateSpecification node)
        {
            if (node.Target is NamedTableReference target)
            {
                string? tableName = GetSchemaObjectName(target.SchemaObject);
                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    _onUpdate(tableName);
                }
            }

            node.FromClause?.Accept(new TableReferenceVisitor(_onSelect));
            base.Visit(node);
        }

        public override void Visit(MergeSpecification node)
        {
            if (node.Target is NamedTableReference target)
            {
                string? tableName = GetSchemaObjectName(target.SchemaObject);
                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    _onInsert(tableName);
                    _onUpdate(tableName);
                }
            }

            base.Visit(node);
        }

        public override void Visit(QuerySpecification node)
        {
            node.FromClause?.Accept(new TableReferenceVisitor(_onSelect));
            base.Visit(node);
        }
    }

    private sealed class TableReferenceVisitor : TSqlFragmentVisitor
    {
        private readonly Action<string> _onTableReference;

        public TableReferenceVisitor(Action<string> onTableReference)
        {
            _onTableReference = onTableReference;
        }

        public override void Visit(NamedTableReference node)
        {
            string? tableName = GetSchemaObjectName(node.SchemaObject);
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                _onTableReference(tableName);
            }

            base.Visit(node);
        }
    }
}

public sealed record SqlTraceResult(string Text, int QueryCount, int StoredProcedureCallCount);
