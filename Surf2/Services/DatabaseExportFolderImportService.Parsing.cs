using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Surf2.Models;

namespace Surf2.Services;

public sealed partial class DatabaseExportFolderImportService
{
    private const string Bracketed = @"\[(?:[^\]]|\]\])*\]";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static Identity ReadIdentity(string text, string path)
    {
        var match = Regex.Match(text, "^(?<schema>" + Bracketed + @")\.(?<name>" + Bracketed + ")$", RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success) throw Bad("Cannot read the bracketed SQL identity in " + path);
        var identity = new Identity(Unbracket(match.Groups["schema"].Value), Unbracket(match.Groups["name"].Value));
        if (string.IsNullOrEmpty(identity.Schema) || string.IsNullOrEmpty(identity.Name)) throw Bad("Empty SQL identity in " + path);
        return identity;
    }
    private static string Unbracket(string value) => value[1..^1].Replace("]]", "]", StringComparison.Ordinal);

    private static void ReadTable(Resource resource, string text, ImportState state)
    {
        string path = resource.File.Relative;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count < 4 || !lines[0].StartsWith("-- Table: ", StringComparison.Ordinal)) throw Bad("Not a Surf table document: " + path);
        Identity identity = ReadIdentity(lines[0][10..], path);
        if (resource.Identity is { } expected && expected != identity) throw Bad("Table document identity differs from its sidecar: " + path);
        var table = new SqlTable { SchemaName = identity.Schema, TableName = identity.Name };
        int next = 1;
        const string countPrefix = "-- Full data imported: ";
        if (lines[next].StartsWith(countPrefix, StringComparison.Ordinal))
        {
            string count = lines[next][countPrefix.Length..];
            if (!count.EndsWith(" row(s)", StringComparison.Ordinal) || !long.TryParse(count[..^7], NumberStyles.Integer, CultureInfo.InvariantCulture, out long rows) || rows < 0)
                throw Bad("Invalid table row count: " + path);
            table.HasFullData = true; table.FullDataRowCount = rows; next++;
        }
        if (lines[next++] != "SELECT" || !lines[^1].StartsWith("FROM ", StringComparison.Ordinal) || !lines[^1].EndsWith(';') || ReadIdentity(lines[^1][5..^1], path) != identity)
            throw Bad("Malformed Surf table document: " + path);
        if (!state.Tables.TryAdd(NameKey(identity), table)) throw Bad("Repeated table identity: " + Display(identity));
        state.Snapshot.Tables.Add(table);
        if (resource.Identity is null) state.LegacyTableFiles.Add(Path.GetFileNameWithoutExtension(resource.File.Path), identity);
        var columnLines = lines.Skip(next).Take(lines.Count - next - 1).ToList();
        if (columnLines.Count == 1 && columnLines[0] == "    *") return;
        if (columnLines.Count == 0) throw Bad("Table has no column list: " + path);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < columnLines.Count; index++)
        {
            var match = Regex.Match(columnLines[index], @"^    (?<name>" + Bracketed + @")(?<comma>,?) -- (?<type>.+?) (?<nullable>NOT NULL|NULL)(?<pk> PK)?(?<identity> IDENTITY)?$", RegexOptions.CultureInvariant, RegexTimeout);
            if (!match.Success || match.Groups["comma"].Success && (match.Groups["comma"].Value == ",") != (index < columnLines.Count - 1))
                throw Bad("Malformed exported column: " + path);
            string name = Unbracket(match.Groups["name"].Value);
            if (string.IsNullOrEmpty(name) || !names.Add(name)) throw Bad("Empty or repeated exported column: " + path);
            var column = new SqlColumn { SchemaName = identity.Schema, TableName = identity.Name, ColumnName = name,
                Ordinal = index + 1, IsNullable = match.Groups["nullable"].Value == "NULL", IsIdentity = match.Groups["identity"].Success };
            ReadDataType(column, match.Groups["type"].Value, path);
            state.Snapshot.Columns.Add(column);
            if (match.Groups["pk"].Success) state.Snapshot.PrimaryKeys.Add(new() { SchemaName = identity.Schema, TableName = identity.Name,
                ColumnName = name, ConstraintName = string.Empty, KeyOrdinal = 0 });
        }
        state.Warn("Table documents preserve displayed column order, type text, nullability, identity and primary-key membership. Original column ordinal numbers, primary-key constraint names/key order and type attributes omitted by the exporter are unavailable; unknown values remain empty or zero.");
    }

    private static void ReadDataType(SqlColumn column, string text, string path)
    {
        var sized = Regex.Match(text, @"^(?<type>varchar|char|varbinary|binary|nvarchar|nchar)\((?<length>max|\d+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        if (sized.Success)
        {
            column.DataType = sized.Groups["type"].Value;
            if (sized.Groups["length"].Value.Equals("max", StringComparison.OrdinalIgnoreCase)) column.MaxLength = -1;
            else if (!int.TryParse(sized.Groups["length"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int length) || length > int.MaxValue / 2)
                throw Bad("Invalid exported type length: " + path);
            else column.MaxLength = column.DataType.StartsWith('n') || column.DataType.StartsWith('N') ? length * 2 : length;
            return;
        }
        var numeric = Regex.Match(text, @"^(?<type>decimal|numeric)\((?<precision>\d+),(?<scale>\d+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        if (numeric.Success)
        {
            if (!byte.TryParse(numeric.Groups["precision"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out byte precision) ||
                !int.TryParse(numeric.Groups["scale"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int scale)) throw Bad("Invalid exported numeric type: " + path);
            column.DataType = numeric.Groups["type"].Value; column.NumericPrecision = precision; column.NumericScale = scale;
        }
        else column.DataType = text;
    }

    private sealed record Declaration(Identity Identity, SqlDatabaseObjectKind Kind, Identity? Parent);
    private sealed class DeclarationVisitor : TSqlFragmentVisitor
    {
        public List<Declaration> Items { get; } = [];
        private void Add(SchemaObjectName? name, SqlDatabaseObjectKind kind, SchemaObjectName? parent = null)
        {
            if (name?.BaseIdentifier is null) return;
            Items.Add(new(new(name.SchemaIdentifier?.Value ?? "", name.BaseIdentifier.Value), kind,
                parent?.BaseIdentifier is null ? null : new(parent.SchemaIdentifier?.Value ?? "", parent.BaseIdentifier.Value)));
        }
        public override void ExplicitVisit(CreateProcedureStatement node) => Add(node.ProcedureReference.Name, SqlDatabaseObjectKind.StoredProcedure);
        public override void ExplicitVisit(AlterProcedureStatement node) => Add(node.ProcedureReference.Name, SqlDatabaseObjectKind.StoredProcedure);
        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => Add(node.ProcedureReference.Name, SqlDatabaseObjectKind.StoredProcedure);
        public override void ExplicitVisit(CreateViewStatement node) => Add(node.SchemaObjectName, SqlDatabaseObjectKind.View);
        public override void ExplicitVisit(AlterViewStatement node) => Add(node.SchemaObjectName, SqlDatabaseObjectKind.View);
        public override void ExplicitVisit(CreateOrAlterViewStatement node) => Add(node.SchemaObjectName, SqlDatabaseObjectKind.View);
        public override void ExplicitVisit(CreateFunctionStatement node) => Add(node.Name, SqlDatabaseObjectKind.Function);
        public override void ExplicitVisit(AlterFunctionStatement node) => Add(node.Name, SqlDatabaseObjectKind.Function);
        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => Add(node.Name, SqlDatabaseObjectKind.Function);
        public override void ExplicitVisit(CreateTriggerStatement node) => Add(node.Name, SqlDatabaseObjectKind.Trigger, node.TriggerObject?.Name);
        public override void ExplicitVisit(AlterTriggerStatement node) => Add(node.Name, SqlDatabaseObjectKind.Trigger, node.TriggerObject?.Name);
        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => Add(node.Name, SqlDatabaseObjectKind.Trigger, node.TriggerObject?.Name);
    }
    private static void ReadObject(Resource resource, string text, ImportState state)
    {
        Identity? identity = resource.Identity; SqlDatabaseObjectKind? kind = resource.Kind; Identity? parent = null;
        const string missing = "-- No definition imported for ";
        if (text.TrimEnd().StartsWith(missing, StringComparison.Ordinal) && text.TrimEnd().EndsWith('.'))
        {
            Identity placeholder = ReadIdentity(text.TrimEnd()[missing.Length..^1], resource.File.Relative);
            identity ??= placeholder;
            state.Warn("Definitions that were absent in the source export remain unavailable; exported placeholder text is retained.");
        }
        else
        {
            using var reader = new StringReader(text);
            var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(reader, out _);
            var visitor = new DeclarationVisitor(); fragment.Accept(visitor);
            if (visitor.Items.Count == 1)
            {
                var declaration = visitor.Items[0];
                if (kind is not null && kind != declaration.Kind)
                {
                    if (resource.Identity is null) throw Bad("SQL declaration kind differs from the export category: " + resource.File.Relative);
                    state.Warn("Some SQL declaration kinds differ from their authoritative sidecars. The exported object kinds and original SQL text are both preserved.");
                }
                kind ??= declaration.Kind;
                Identity declared = declaration.Identity;
                if (identity is null && declared.Schema.Length == 0)
                {
                    string stem = Path.GetFileNameWithoutExtension(resource.File.Path); int dot = stem.IndexOf('.');
                    if (dot <= 0 || stem.IndexOf('.', dot + 1) >= 0 || stem[(dot + 1)..] != declared.Name)
                        throw Bad("The unqualified SQL declaration has no recoverable schema: " + resource.File.Relative);
                    declared = new(stem[..dot], declared.Name);
                    state.Warn("Some SQL declarations omit the schema. Their schema names are recovered from their exported filenames; review these identities before saving.");
                }
                identity ??= declared;
                if (identity.Name != declared.Name || declared.Schema.Length > 0 && identity.Schema != declared.Schema)
                    state.Warn("Some SQL declaration names differ from authoritative resource sidecars (for example after a SQL rename). Sidecar identities and original SQL text are both preserved.");
                parent = declaration.Parent;
                if (parent?.Schema.Length == 0) { parent = null; state.Warn("An unqualified trigger parent schema cannot be recovered from the exported SQL; its parent metadata is left empty."); }
            }
            else if (identity is null || kind is null)
                throw Bad("Cannot identify one SQL object declaration in " + resource.File.Relative + ". This export has no authoritative object identity/type metadata.");
            else state.Warn("Some SQL definitions could not be parsed as one object declaration. Their authoritative sidecar identities/types and original text are preserved.");
        }
        if (identity is null || kind is null) throw Bad("The export does not identify this SQL object's kind: " + resource.File.Relative);
        if (resource.Identity is null && !IsLegacyFileName(Path.GetFileName(resource.File.Path), LegacyFileName(identity, ".txt")))
            throw Bad("The SQL declaration identity does not match its exported filename: " + resource.File.Relative + ". A renamed object may retain its old declaration; use an export with authoritative resource sidecars.");
        if (!state.Objects.Add(((int)kind.Value).ToString(CultureInfo.InvariantCulture) + ":" + NameKey(identity))) throw Bad("Repeated SQL object identity: " + Display(identity));
        state.Snapshot.Objects.Add(new() { SchemaName = identity.Schema, ObjectName = identity.Name, Kind = kind.Value, Definition = text,
            ParentSchemaName = parent?.Schema ?? "", ParentObjectName = parent?.Name ?? "", TypeDescription = "" });
        state.Warn("SQL catalog type descriptions are absent from display exports. Object definitions are retained verbatim; trigger parent metadata is recovered only when its qualified name is present in a parseable declaration.");
    }

    // A strict CSV reader: unlike the grid's forgiving viewer, this preserves blank records
    // and rejects damaged quoting instead of silently importing shifted or missing cells.
    private static List<string[]> ReadCsv(string text, string path)
    {
        var records = new List<string[]>(); var cells = new List<string>(); var cell = new StringBuilder();
        bool quoted = false, closed = false, started = false; int rowCharacters = 0;
        void EndCell() { cells.Add(cell.ToString()); cell.Clear(); closed = false; started = false; if (cells.Count > 128) throw Bad("CSV exceeds 128 columns: " + path); }
        void EndRow() { EndCell(); records.Add(cells.ToArray()); cells.Clear(); rowCharacters = 0; if (records.Count > 1_000_001) throw Bad("CSV exceeds 1,000,000 rows: " + path); }
        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (++rowCharacters > 4 * 1024 * 1024) throw Bad("CSV record exceeds 4 MiB of characters: " + path);
            if (quoted)
            {
                if (ch == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"') { cell.Append('"'); index++; }
                    else { quoted = false; closed = true; }
                }
                else cell.Append(ch);
                continue;
            }
            if (ch == ',') { EndCell(); continue; }
            if (ch is '\r' or '\n') { if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++; EndRow(); continue; }
            if (closed) throw Bad("Unexpected text after a closing CSV quote: " + path);
            if (ch == '"') { if (started) throw Bad("Unexpected quote in an unquoted CSV cell: " + path); quoted = true; started = true; continue; }
            cell.Append(ch); started = true;
        }
        if (quoted) throw Bad("Unterminated quoted CSV cell: " + path);
        if (started || closed || cells.Count != 0) EndRow();
        return records;
    }
}
