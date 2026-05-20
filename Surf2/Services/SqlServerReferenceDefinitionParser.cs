using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Surf2.Models;

namespace Surf2.Services;

public sealed class SqlServerReferenceDefinitionParser : IReferenceDefinitionParser
{
    public bool CanParse(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".sql", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<ReferenceEntity> Parse(string filePath, string content)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(content);
        TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> _);
        var visitor = new SqlDefinitionVisitor(filePath);
        fragment.Accept(visitor);
        return visitor.Entities;
    }

    private sealed class SqlDefinitionVisitor : TSqlFragmentVisitor
    {
        private readonly string _filePath;

        public SqlDefinitionVisitor(string filePath)
        {
            _filePath = filePath;
        }

        public List<ReferenceEntity> Entities { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            AddSchemaObject(node.ProcedureReference.Name, ReferenceEntityKind.StoredProcedure, node);
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            AddSchemaObject(node.ProcedureReference.Name, ReferenceEntityKind.StoredProcedure, node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            AddSchemaObject(node.Name, ReferenceEntityKind.Function, node);
        }

        public override void ExplicitVisit(AlterFunctionStatement node)
        {
            AddSchemaObject(node.Name, ReferenceEntityKind.Function, node);
        }

        public override void ExplicitVisit(CreateViewStatement node)
        {
            AddSchemaObject(node.SchemaObjectName, ReferenceEntityKind.View, node);
        }

        public override void ExplicitVisit(AlterViewStatement node)
        {
            AddSchemaObject(node.SchemaObjectName, ReferenceEntityKind.View, node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            AddSchemaObject(node.Name, ReferenceEntityKind.Trigger, node);
        }

        public override void ExplicitVisit(AlterTriggerStatement node)
        {
            AddSchemaObject(node.Name, ReferenceEntityKind.Trigger, node);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            string tableName = GetSimpleName(node.SchemaObjectName);
            string tableQualifiedName = GetQualifiedName(node.SchemaObjectName);

            AddSchemaObject(node.SchemaObjectName, ReferenceEntityKind.Table, node);

            foreach (ColumnDefinition column in node.Definition?.ColumnDefinitions ?? [])
            {
                string columnName = column.ColumnIdentifier?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(columnName))
                {
                    continue;
                }

                Entities.Add(new ReferenceEntity
                {
                    Name = columnName,
                    QualifiedName = string.IsNullOrWhiteSpace(tableQualifiedName) ? columnName : $"{tableQualifiedName}.{columnName}",
                    Kind = ReferenceEntityKind.Field,
                    FilePath = _filePath,
                    LineNumber = Math.Max(1, column.StartLine),
                    ColumnNumber = Math.Max(1, column.StartColumn),
                    EndLineNumber = Math.Max(1, column.StartLine),
                    EndColumnNumber = Math.Max(1, column.StartColumn),
                    Language = "SQL Server",
                    ContainerName = string.IsNullOrWhiteSpace(tableQualifiedName) ? tableName : tableQualifiedName
                });
            }
        }

        private void AddSchemaObject(SchemaObjectName? name, ReferenceEntityKind kind, TSqlFragment fragment)
        {
            string simpleName = GetSimpleName(name);
            if (string.IsNullOrWhiteSpace(simpleName))
            {
                return;
            }

            Entities.Add(new ReferenceEntity
            {
                Name = simpleName,
                QualifiedName = GetQualifiedName(name),
                Kind = kind,
                FilePath = _filePath,
                LineNumber = Math.Max(1, fragment.StartLine),
                ColumnNumber = Math.Max(1, fragment.StartColumn),
                EndLineNumber = Math.Max(1, fragment.StartLine),
                EndColumnNumber = Math.Max(1, fragment.StartColumn),
                Language = "SQL Server"
            });
        }

        private static string GetSimpleName(SchemaObjectName? name)
        {
            return name?.BaseIdentifier?.Value ?? string.Empty;
        }

        private static string GetQualifiedName(SchemaObjectName? name)
        {
            if (name == null)
            {
                return string.Empty;
            }

            return string.Join(".", name.Identifiers.Select(identifier => identifier.Value));
        }
    }
}
