using System.Globalization;
using System.IO;

namespace Surf2.Storage.Relational.Packages;

internal sealed record PackageTableSpec(string Schema, string Name, Func<string, string> Predicate,
    string Selection, long? CaptureLayoutKey = null, int? CaptureColumnCount = null)
{
    internal string Qualified => PackageTableCatalogue.Quote(Schema) + "." + PackageTableCatalogue.Quote(Name);
    internal string Key => Schema + "." + Name;
    internal string Entry => "database/" + Schema + "/" + Name + ".rows.ndjson";
}

internal static class PackageTableCatalogue
{
    internal static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    internal static string Snapshot(string key) => $"EXISTS (SELECT 1 FROM surf.DatabaseSnapshot ps WHERE ps.SnapshotKey={key} AND ps.IsPublished=1)";
    internal static string Revision(string key) => $"EXISTS (SELECT 1 FROM surf.SnapshotResourceRevision pr WHERE pr.RevisionKey={key} AND pr.IsSealed=1 AND {Snapshot("pr.SnapshotKey")})";
    internal static string DataSet(string alias) => $"{alias}.State='Ready' AND {Revision(alias + ".RevisionKey")}";

    private static PackageTableSpec All(string name) => new("surf", name, _ => "1=1", "AllAuthoritativeRows-v1");
    private static PackageTableSpec SnapshotOwned(string name) => new("surf", name, a => Snapshot(a + ".SnapshotKey"), "PublishedSnapshot-v1");
    private static PackageTableSpec RevisionOwned(string name) => new("surf", name, a => Revision(a + ".RevisionKey"), "SealedPublishedRevision-v1");

    internal static readonly PackageTableSpec[] Authoritative = Build();

    private static PackageTableSpec[] Build()
    {
        var result = new List<PackageTableSpec>
        {
            All("UserProfile"),
            new("surf", "DatabaseSnapshot", a => a + ".IsPublished=1", "PublishedSnapshot-v1"),
            SnapshotOwned("SnapshotResource"),
            new("surf", "SnapshotResourceRevision", a => a + ".IsSealed=1 AND " + Snapshot(a + ".SnapshotKey"), "SealedPublishedRevision-v1"),
            RevisionOwned("DatabaseObjectRevision"), RevisionOwned("TableMetadataRevision"),
            RevisionOwned("TableDataRevision"), RevisionOwned("TableDataRevisionMetadata"),
            new("surf", "TableColumnRevision", a => Snapshot(a + ".SnapshotKey") + " AND (" + a + ".TableMetadataRevisionKey IS NULL OR " + Revision(a + ".TableMetadataRevisionKey") + ")", "PublishedSnapshotAndCompleteOwner-v1"),
            new("surf", "PrimaryKeyConstraint", a => Snapshot(a + ".SnapshotKey") + " AND (" + a + ".TableMetadataRevisionKey IS NULL OR " + Revision(a + ".TableMetadataRevisionKey") + ")", "PublishedSnapshotAndCompleteOwner-v1"),
            new("surf", "PrimaryKeyColumn", a => Snapshot(a + ".SnapshotKey") + " AND (" + a + ".TableMetadataRevisionKey IS NULL OR " + Revision(a + ".TableMetadataRevisionKey") + ")", "PublishedSnapshotAndCompleteOwner-v1"),
            SnapshotOwned("SnapshotCurrentColumn"), SnapshotOwned("SnapshotCurrentPrimaryKey"),
            SnapshotOwned("FullDataTableSelection"), SnapshotOwned("SnapshotHistory"),
            SnapshotOwned("SnapshotVersion"), SnapshotOwned("SnapshotChange"),
            new("surf", "DataSet", DataSet, "ReadySealedPublishedDataSet-v1"),
            new("surf", "DataLayout", a => $"EXISTS (SELECT 1 FROM surf.DataSet pd WHERE pd.LayoutKey={a}.LayoutKey AND {DataSet("pd")})", "ReferencedReadyLayout-v1"),
            new("surf", "DataColumn", a => $"EXISTS (SELECT 1 FROM surf.DataSet pd WHERE pd.LayoutKey={a}.LayoutKey AND {DataSet("pd")})", "ReferencedReadyLayout-v1"),
            new("surf", "DataValueException", a => $"EXISTS (SELECT 1 FROM surf.DataSet pd WHERE pd.DataSetKey={a}.DataSetKey AND {DataSet("pd")})", "ReadyDataSetExceptions-v1")
        };
        string[] state = ["ScopeCatalogueState", "Scope", "ScopeResource", "VirtualFolder", "VirtualFolderMember",
            "Diagram", "Workbench", "DiagramRevision", "DiagramObject", "Workflow", "WorkflowItem",
            "DiagramWorkflowBinding", "WorkflowItemMarker", "DiagramPortalTarget", "QueryItem", "WorkspaceSession",
            "DocumentWindowState", "DocumentWindowFilter", "WorkspaceUnloadedResource", "ReferenceConnectionLine",
            "ApplicationPreference", "ExtensionAppearance", "ReferenceStyle", "DiagramImageDefinition"];
        result.AddRange(state.Select(All));
        result.Add(new("surf", "TextContent", a => Referenced(result, a + ".ContentKey", TextReferences), "AuthoritativelyReferencedContent-v1"));
        result.Add(new("surf", "Asset", a => Referenced(result, a + ".AssetKey", AssetReferences), "AuthoritativelyReferencedAsset-v1"));
        return result.ToArray();
    }

    private static readonly (string Table, string[] Columns)[] TextReferences =
    [
        ("DatabaseObjectRevision", ["DefinitionContentKey"]), ("Scope", ["DescriptionContentKey"]),
        ("ScopeResource", ["DetailsOverrideContentKey"]),
        ("DiagramObject", ["ImageDataBase64ContentKey", "LabelTextContentKey", "DocumentationXamlContentKey"]),
        ("WorkflowItem", ["ItemDescriptionContentKey", "ItemDocumentationXamlContentKey"]),
        ("QueryItem", ["QueryDescriptionContentKey"]),
        ("DiagramImageDefinition", ["RegexContentKey", "NameRegexContentKey", "ContentRegexContentKey", "ImageDataBase64ContentKey"])
    ];

    private static readonly (string Table, string[] Columns)[] AssetReferences =
    [ ("DiagramObject", ["ImageAssetKey", "PastedAssetKey", "PastedFallbackAssetKey"]), ("DiagramImageDefinition", ["AssetKey"]) ];

    private static string Referenced(List<PackageTableSpec> tables, string key, (string Table, string[] Columns)[] references) =>
        string.Join(" OR ", references.Select((reference, index) =>
        {
            string alias = "pc" + index.ToString(CultureInfo.InvariantCulture);
            var table = tables.Single(spec => spec.Name == reference.Table);
            return $"EXISTS (SELECT 1 FROM {table.Qualified} {alias} WHERE ({table.Predicate(alias)}) AND (" +
                string.Join(" OR ", reference.Columns.Select(column => alias + "." + Quote(column) + "=" + key)) + "))";
        }));

    internal static PackageTableSpec Capture(long key, int count)
    {
        if (key <= 0 || count is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(key));
        string name = "Data_" + key.ToString(CultureInfo.InvariantCulture);
        return new("capture", name, a => $"EXISTS (SELECT 1 FROM surf.DataSet pd WHERE pd.DataSetKey={a}.DataSetKey AND pd.LayoutKey={key.ToString(CultureInfo.InvariantCulture)} AND {DataSet("pd")})",
            "ReadyGeneratedCaptureRows-v1", key, count);
    }

    internal static string SelectSql(PackageTableSpec spec, RelationalPackageTable table)
    {
        var columns = table.Columns.Where(column => column.StreamOrdinal.HasValue).ToArray();
        if (columns.Length == 0 || table.PrimaryKey.Count == 0) throw new InvalidDataException("A portable package stream needs columns and a stable primary key.");
        string projection = string.Join(",", columns.Select(column =>
        {
            string name = "t." + Quote(column.Name);
            return PackageRowEncoding.IsChunked(column.Encoding!) ? $"CONVERT(bigint,DATALENGTH({name})),CONVERT(varbinary(max),{name})" : name;
        }));
        string order = string.Join(",", table.PrimaryKey.Select(column => "t." + Quote(column)));
        return $"SELECT {projection} FROM {spec.Qualified} t WHERE ({spec.Predicate("t")}) ORDER BY {order};";
    }
}
