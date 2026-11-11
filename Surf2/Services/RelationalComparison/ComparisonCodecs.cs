using System.IO;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Services.RelationalComparison;

internal static class ComparisonCodecs
{
    internal static readonly SpoolCodec<ComparisonResultRow> Result = new((w, x) =>
    {
        S(w, x.Key); S(w, x.Status); S(w, x.ChangedColumns); S(w, x.LeftPreview); S(w, x.RightPreview);
        w.Write(x.IsCollection); WriteTarget(w, x.Left); WriteTarget(w, x.Right);
        w.Write(x.TableCells?.Count ?? -1);
        if (x.TableCells != null)
            foreach (var cell in x.TableCells)
            {
                S(w, cell.Left);
                S(w, cell.Right);
                w.Write((byte)cell.Status);
            }
    }, r => new(S(r), S(r), S(r), S(r), S(r), r.ReadBoolean(), ReadTarget(r), ReadTarget(r), ReadCells(r)));

    private static IReadOnlyList<ComparisonTableCell>? ReadCells(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == -1) return null;
        if (count < 0 || count > CaptureLimits.MaximumColumns * 2)
            throw new InvalidDataException("Invalid comparison cell count.");
        var cells = new ComparisonTableCell[count];
        for (int i = 0; i < count; i++)
        {
            string left = S(reader), right = S(reader);
            var status = (ComparisonCellStatus)reader.ReadByte();
            if (status > ComparisonCellStatus.Added) throw new InvalidDataException("Invalid comparison cell status.");
            cells[i] = new(left, right, status);
        }
        return cells;
    }

    internal static void WriteTarget(BinaryWriter w, RelationalComparisonTarget? t)
    {
        w.Write(t != null); if (t == null) return;
        w.Write(t.Epoch.ToByteArray()); K(w, t.SnapshotKey); K(w, t.ResourceKey); K(w, t.RevisionKey);
        K(w, t.VersionKey); K(w, t.HistoricalEntryKey); K(w, (long?)t.Category);
        var x = t.Resource;
        S(w, x.DisplayName); S(w, x.TypeDisplay); S(w, x.Path); w.Write((int)x.Kind); S(w, x.ComparisonTypeKey);
        w.Write(x.IsCollection); w.Write(x.IsText); w.Write(x.IsTableData); S(w, x.IdentityKey);
        S(w, x.SnapshotId); S(w, x.DatabaseFolderName); K(w, (long?)x.DatabaseObjectKind);
        S(w, x.DatabaseObjectName); S(w, x.TableSchemaName); S(w, x.TableName); S(w, x.SyntaxPath);
        w.Write(t.ExpectedTextDigest != null); if (t.ExpectedTextDigest != null) S(w, t.ExpectedTextDigest);
    }
    internal static RelationalComparisonTarget? ReadTarget(BinaryReader r)
    {
        if (!r.ReadBoolean()) return null;
        var epoch = new Guid(r.ReadBytes(16));
        long? snapshot = K(r), resource = K(r), revision = K(r), version = K(r), entry = K(r), category = K(r);
        var value = new ComparisonResource(S(r), S(r), S(r), (ComparisonResourceKind)r.ReadInt32(), S(r),
            r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), S(r), SnapshotId: S(r), DatabaseFolderName: S(r),
            DatabaseObjectKind: (SqlDatabaseObjectKind?)K(r), DatabaseObjectName: S(r), TableSchemaName: S(r), TableName: S(r), SyntaxPath: S(r));
        string? digest = r.ReadBoolean() ? S(r) : null;
        return new(value, epoch, snapshot, resource, revision, version, entry, (ExplorerCategory?)category, ExpectedTextDigest: digest);
    }
    private static void S(BinaryWriter w, string x) => SpoolBinary.WriteString(w, x);
    private static string S(BinaryReader r) => SpoolBinary.ReadString(r);
    private static void K(BinaryWriter w, long? x) { w.Write(x.HasValue); if (x.HasValue) w.Write(x.Value); }
    private static long? K(BinaryReader r) => r.ReadBoolean() ? r.ReadInt64() : null;
}
