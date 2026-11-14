using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Surf2.Services.LiveSql;

public static class LiveSqlQueryPolicy
{
    public static void Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 128 * 1024) throw new ArgumentException("Enter a SELECT query of at most 128 KiB.");
        var fragment = new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        if (errors.Count != 0) throw new ArgumentException("Invalid SQL: " + errors[0].Message);
        if (fragment is not TSqlScript script || script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1 ||
            script.Batches[0].Statements[0] is not SelectStatement select)
            throw new ArgumentException("Only one SELECT statement, optionally with CTEs, is supported.");
        var guard = new ReadGuard(); select.Accept(guard);
        if (guard.Error != null) throw new ArgumentException(guard.Error);
    }

    private sealed class ReadGuard : TSqlFragmentVisitor
    {
        public string? Error { get; private set; }
        public override void Visit(TSqlFragment node)
        {
            if (node.GetType().Name is "NextValueForExpression" or "OpenQueryTableReference" or
                "OpenRowsetTableReference" or "OpenRowsetCosmos" or "AdHocTableReference" or
                "BulkOpenRowset" or "SelectSetVariable" or "VariableTableReference")
                Error = "This SELECT contains an unsupported external, state-changing or variable-table operation.";
            base.Visit(node);
        }
        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into != null) Error = "SELECT INTO is not allowed.";
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(SchemaObjectName node)
        {
            if (node.DatabaseIdentifier != null || node.ServerIdentifier != null)
                Error = "Queries must stay within the selected database.";
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget target && target.MultiPartIdentifier.Identifiers.Count > 1)
                Error = "Function calls must stay within the selected database.";
            base.ExplicitVisit(node);
        }
    }
}
