using System.Text.Json;
using Surf2.Models;
using Surf2.Services;

namespace Surf2.Storage.Relational.Snapshots;

// Tiny graph fixtures ONLY: the production projection never constructs these
// graphs. The existing service, not a duplicated algorithm, is the oracle.
internal static class SnapshotHistoryFixtures
{
    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();
        DateTimeOffset stamp = new(2023, 2, 3, 4, 5, 6, TimeSpan.FromHours(5.5));
        stamp = stamp.AddTicks(7654321);
        SqlTable Table(string name, bool full=false, long count=0) => new() { SchemaName="dbo", TableName=name,
            HasFullData=full, FullDataRowCount=count, FullDataImportedAtUtc=full ? stamp : null };
        SqlColumn Column(string table,string name,int ordinal) => new() { SchemaName="dbo",TableName=table,ColumnName=name,
            DataType="nvarchar(max)",MaxLength=-1,NumericPrecision=7,NumericScale=-2,IsNullable=true,IsIdentity=false,Ordinal=ordinal };
        SqlPrimaryKeyColumn Key(string table,string name) => new() { SchemaName="dbo",TableName=table,ColumnName=name,ConstraintName="PK "+table,KeyOrdinal=-3 };
        SqlTableDataSet Data(string name,long reported,int actual) => new() { SchemaName="dbo",TableName=name,RowCount=reported,ImportedAtUtc=stamp,
            Rows=Enumerable.Range(0,actual).Select(i=>JsonSerializer.SerializeToElement(new { Value=i })).ToList() };
        SqlDatabaseObject Obj(string name) => new() { SchemaName="dbo",ObjectName=name,Kind=SqlDatabaseObjectKind.StoredProcedure,
            Definition="select '"+name+"'\r\n",TypeDescription="procedure",ParentSchemaName="p",ParentObjectName="parent" };

        var current = new DatabaseMetadataSnapshot { SnapshotId="legacy/not-a-guid",DisplayName="Current alias",DatabaseName="DB",
            ImportedAtUtc=stamp.AddDays(100), Tables=[Table("A",true,99),Table("B"),Table("A",true,71),Table("C")],
            Columns=[Column("A","a0",20),Column("B","b",1),Column("Orphan","o",-1),Column("A","a1",2),Column("C","c-original",3),Column("Z","z-orphan",7)],
            PrimaryKeys=[Key("A","a0"),Key("Orphan","o"),Key("B","b"),Key("A","a1"),Key("C","c-original"),Key("Z","z-orphan")],
            TableDataSets=[Data("A",3,1),Data("B",0,2)], FullDataTableNames=["dbo.A","DBO.a","dbo.B","orphan","trailing "],
            Objects=[Obj("P"),Obj("Other"),Obj("p")] };
        var unknown=Obj("P"); unknown.Kind=SqlDatabaseObjectKind.Unknown; current.Objects.Insert(1,unknown);
        var metaA = new DatabaseSnapshotResourcePayload { Kind=DatabaseVersionedResourceKind.TableMetadata,Table=Table("A",false,-12),
            Columns=[Column("A","older1",5),Column("A","older0",1)],PrimaryKeys=[Key("A","older1"),Key("A","older0")] };
        var dataA = new DatabaseSnapshotResourcePayload { Kind=DatabaseVersionedResourceKind.TableData,Table=Table("A",false,17),TableDataSet=Data("A",-5,3) };
        var metaB = new DatabaseSnapshotResourcePayload { Kind=DatabaseVersionedResourceKind.TableMetadata,Table=Table("B",true,999),
            Columns=[Column("Other","payload-orphan",-3),Column("B","b-old",99)], PrimaryKeys=[Key("Other","payload-orphan"),Key("B","b-old")] };
        var dataC = new DatabaseSnapshotResourcePayload { Kind=DatabaseVersionedResourceKind.TableData,Table=Table("C",false,0),TableDataSet=Data("C",10,1) };
        var dataZ = new DatabaseSnapshotResourcePayload { Kind=DatabaseVersionedResourceKind.TableData,TableDataSet=Data("Z",-1,0) };
        var oldObject = new DatabaseSnapshotResourcePayload { Kind=DatabaseVersionedResourceKind.StoredProcedure,DatabaseObject=Obj("P") };
        DatabaseSnapshotResourceChange Change(DatabaseVersionedResourceKind kind,string name,DatabaseSnapshotResourceChangeKind changeKind,
            DatabaseSnapshotResourcePayload? previous=null,string? original=null) => new() { Kind=kind,ChangeKind=changeKind,
                ResourceKey=original ?? SnapshotIdentity.LegacyResourceKey(kind,"dbo",name),PreviousPayload=previous };
        DatabaseSnapshotVersion Version(int number, params DatabaseSnapshotResourceChange[] changes) => new() {
            VersionId="version/"+number,VersionName="V"+number,VersionNumber=number,CreatedAtUtc=stamp.AddDays(number),IsInitial=number==1,Changes=changes.ToList() };
        var v9=Version(9,Change(DatabaseVersionedResourceKind.TableData,"A",DatabaseSnapshotResourceChangeKind.Modified,dataA),
            Change(DatabaseVersionedResourceKind.TableMetadata,"B",DatabaseSnapshotResourceChangeKind.Modified,metaB),
            Change(DatabaseVersionedResourceKind.TableData,"B",DatabaseSnapshotResourceChangeKind.Added));
        var v4=Version(4,Change(DatabaseVersionedResourceKind.TableMetadata,"A",DatabaseSnapshotResourceChangeKind.Modified,metaA),
            Change(DatabaseVersionedResourceKind.TableData,"A",DatabaseSnapshotResourceChangeKind.Modified,dataA),
            Change(DatabaseVersionedResourceKind.TableMetadata,"C",DatabaseSnapshotResourceChangeKind.Added),
            Change(DatabaseVersionedResourceKind.TableData,"C",DatabaseSnapshotResourceChangeKind.Deleted,dataC),
            Change(DatabaseVersionedResourceKind.TableData,"Z",DatabaseSnapshotResourceChangeKind.Deleted,dataZ),
            Change(DatabaseVersionedResourceKind.StoredProcedure,"P",DatabaseSnapshotResourceChangeKind.Modified,oldObject));
        await Verify(current,[v9,Version(1),v4],"Mixed metadata/data interleavings, version gaps, duplicate names and root order",passed);
        await Verify(current,[Version(1),Version(2,
            Change(DatabaseVersionedResourceKind.TableData,"A",DatabaseSnapshotResourceChangeKind.Modified,dataA),
            Change(DatabaseVersionedResourceKind.TableMetadata,"A",DatabaseSnapshotResourceChangeKind.Modified,metaA))],
            "Data then metadata removes restored dataset/selection and retains metadata scalars",passed);
        await Verify(current,[Version(1),Version(2,
            Change(DatabaseVersionedResourceKind.TableMetadata,"A",DatabaseSnapshotResourceChangeKind.Modified,metaA),
            Change(DatabaseVersionedResourceKind.TableData,"A",DatabaseSnapshotResourceChangeKind.Modified,dataA))],
            "Metadata then data overlays flags/count/offset timestamp without moving table",passed);
        await Verify(current,[Version(1),Version(2,Change(DatabaseVersionedResourceKind.TableData,"A",DatabaseSnapshotResourceChangeKind.Added))],
            "Added data rollback clears only first duplicate table and all duplicate selections",passed);
        await Verify(current,[Version(1),Version(2,Change(DatabaseVersionedResourceKind.TableMetadata,"A",DatabaseSnapshotResourceChangeKind.Added))],
            "Added metadata rollback removes all matching tables/children/data/selections",passed);
        await Verify(current,[Version(1),Version(2,
            Change(DatabaseVersionedResourceKind.TableMetadata,"C",DatabaseSnapshotResourceChangeKind.Added),
            Change(DatabaseVersionedResourceKind.TableData,"C",DatabaseSnapshotResourceChangeKind.Deleted,dataC))],
            "Data companion resurrects table at end without resurrecting columns/keys",passed);
        await Verify(current,[Version(1),Version(2,Change(DatabaseVersionedResourceKind.TableData,"Z",DatabaseSnapshotResourceChangeKind.Deleted,dataZ))],
            "Null companion restores dataset and selection without synthesizing a table",passed);
        var differentlyNamed=new DatabaseSnapshotResourcePayload { Kind=DatabaseVersionedResourceKind.TableData,
            Table=Table("CompanionName"),TableDataSet=Data("MissingDataName",0,2) };
        await Verify(current,[Version(1),Version(2,Change(DatabaseVersionedResourceKind.TableData,"MissingDataName",DatabaseSnapshotResourceChangeKind.Deleted,differentlyNamed))],
            "Companion spelling may differ from dataset; newly appended companion is still updated",passed);
        await Verify(current,[Version(1),Version(2,
            Change(DatabaseVersionedResourceKind.TableData,"A",DatabaseSnapshotResourceChangeKind.Added,original:"not-a-resource-key"),
            Change(DatabaseVersionedResourceKind.TableMetadata,"A",DatabaseSnapshotResourceChangeKind.Modified))],
            "Malformed Added locator and null Modified payload remain no-ops",passed);
        var whitespace=current.Clone();
        whitespace.Tables.Add(new() { SchemaName="dbo",TableName="A ",HasFullData=true,FullDataRowCount=33 });
        whitespace.FullDataTableNames.Add("dbo.A ");
        await Verify(whitespace,[Version(1),Version(2,Change(DatabaseVersionedResourceKind.TableMetadata,"A ",DatabaseSnapshotResourceChangeKind.Added),
            Change(DatabaseVersionedResourceKind.TableData,"a",DatabaseSnapshotResourceChangeKind.Modified,dataA))],
            "OrdinalIgnoreCase matching does not trim trailing spaces or depend on SQL collation",passed);

        var tieFirst=Version(4,Change(DatabaseVersionedResourceKind.TableMetadata,"A",DatabaseSnapshotResourceChangeKind.Modified,metaA),
            Change(DatabaseVersionedResourceKind.TableData,"A",DatabaseSnapshotResourceChangeKind.Modified,dataA));
        tieFirst.VersionId="tie/first";
        var tieSecond=Version(4,Change(DatabaseVersionedResourceKind.TableMetadata,"A",DatabaseSnapshotResourceChangeKind.Modified,metaA));
        tieSecond.VersionId="tie/second"; tieSecond.CreatedAtUtc=stamp.AddDays(40);
        await Verify(current,[tieFirst,Version(1),tieSecond],
            "Duplicate version numbers keep complete versions in source order, not interleaved change order",passed);

        var boundary=Version(4,Enumerable.Range(0,129).Select(i=>Change(i%2==0 ? DatabaseVersionedResourceKind.TableMetadata : DatabaseVersionedResourceKind.TableData,
            "A",DatabaseSnapshotResourceChangeKind.Modified,i%2==0 ? metaA : dataA)).ToArray());
        boundary.VersionId="tie/boundary";
        await Verify(current,[boundary,Version(1),tieSecond],
            "Duplicate-number keyset continuation crosses the 128-change boundary without skipping a tied version",passed);

        var firstHistory=new DatabaseSnapshotHistory { SnapshotId=current.SnapshotId.ToUpperInvariant(),NextVersionNumber=100,
            Versions=[tieFirst,Version(1),tieSecond] };
        var secondHistory=new DatabaseSnapshotHistory { SnapshotId=current.SnapshotId,NextVersionNumber=200,
            Versions=[Version(1),Version(99,Change(DatabaseVersionedResourceKind.TableMetadata,"B",DatabaseSnapshotResourceChangeKind.Added))] };
        await VerifyHistories(current,[firstHistory,secondHistory],firstHistory.Versions.Select(v=>v.VersionId),
            "Duplicate histories use the first matching source history; later histories cannot leak rollback changes",passed);
        await VerifyHistories(current,[firstHistory,secondHistory],secondHistory.Versions.Select(v=>v.VersionId),
            "Explicit secondary history replays only its own changes against the legacy isolated-history oracle",passed,1);

        var firstAlias=Version(7); firstAlias.VersionId="Alias";
        var laterExact=Version(1); laterExact.VersionId="alias";
        await VerifyHistories(current,[new() { SnapshotId=current.SnapshotId,NextVersionNumber=100,Versions=[firstAlias,laterExact,Version(9)] }],
            ["alias","ALIAS","Alias"],"Duplicate/case-colliding version IDs select the first source match, not the later exact hash match",passed);
        return passed;
    }

    private static async Task Verify(DatabaseMetadataSnapshot current, DatabaseSnapshotVersion[] versions, string label, List<string> passed)
    {
        var history=new DatabaseSnapshotHistory { SnapshotId=current.SnapshotId,NextVersionNumber=100,Versions=new(versions) };
        await VerifyHistories(current,[history],versions.Select(v=>v.VersionId),label,passed);
    }

    private static async Task VerifyHistories(DatabaseMetadataSnapshot current, DatabaseSnapshotHistory[] histories,
        IEnumerable<string> versionIds, string label, List<string> passed, int? selectedHistoryOrdinal=null)
    {
        int owner=selectedHistoryOrdinal ?? Array.FindIndex(histories,h=>string.Equals(h.SnapshotId,current.SnapshotId,StringComparison.OrdinalIgnoreCase));
        var history=histories[owner];
        // The legacy public API only selects the first history. Moving an explicit
        // history to the front supplies an independent oracle for that selected owner.
        var library=new DatabaseSnapshotLibrary { Snapshots=[current],Histories=new(histories.Where((_,i)=>i==owner).Concat(histories.Where((_,i)=>i!=owner))) };
        var service=new DatabaseSnapshotHistoryService(new DatabaseDocumentService());
        var changes=histories.SelectMany((h,hi)=>h.Versions.SelectMany((v,vi)=>v.Changes.Select((change,ci)=>new {
            History=hi,Change=change,Position=new HistoricalChangePosition(v.VersionNumber,vi,10_000-vi,ci,1_000_000-ci) }))).ToArray();
        foreach(var id in versionIds)
        {
            var target=history.Versions.First(v=>string.Equals(v.VersionId,id,StringComparison.OrdinalIgnoreCase));
            var fixture=new MemoryProjection(current);
            HistoricalChangePosition? after=null;
            while(true)
            {
                var batch=changes.Where(c=>c.History==owner && c.Position.VersionNumber>target.VersionNumber &&
                    (!after.HasValue || c.Position.CompareTo(after.Value)>0)).OrderBy(c=>c.Position).Take(128).ToArray();
                if(batch.Length==0) break;
                foreach(var item in batch)
                    await HistoricalSnapshotReplay.ApplyAsync(fixture,fixture.Event(item.Change)).ConfigureAwait(false);
                after=batch[^1].Position;
            }
            var expected=service.ReconstructSnapshot(library,current.SnapshotId,id);
            var actual=fixture.Materialize(current,target.CreatedAtUtc);
            if(JsonSerializer.Serialize(expected)!=JsonSerializer.Serialize(actual))
                throw new InvalidOperationException($"Historical projection differs from legacy reconstruction: {label}, target {target.VersionNumber}.\nExpected: {JsonSerializer.Serialize(expected)}\nActual: {JsonSerializer.Serialize(actual)}");
        }
        passed.Add(label);
    }

    private sealed class MemoryProjection : IHistoricalSnapshotState
    {
        private readonly List<HistoricalSnapshotEntry> _entries=[];
        private readonly Dictionary<long,SqlDatabaseObject> _objects=[];
        private readonly Dictionary<long,SqlColumn> _columns=[];
        private readonly Dictionary<long,SqlPrimaryKeyColumn> _keys=[];
        private readonly Dictionary<long,SqlTableDataSet> _data=[];
        private readonly Dictionary<(long,HistoricalCollection),List<HistoricalSnapshotEntry>> _children=[];
        private long _identity;
        internal MemoryProjection(DatabaseMetadataSnapshot current)
        {
            foreach(var value in current.Objects) { long id=++_identity; _objects[id]=value; Add(new(0,HistoricalCollection.Objects,0,id,id,null,value.SchemaName,value.ObjectName,value.Kind)); }
            foreach(var value in current.Tables) { long id=++_identity; Add(new(0,HistoricalCollection.Tables,0,id,id,null,value.SchemaName,value.TableName,Table:value)); }
            foreach(var value in current.Columns) { long id=++_identity; _columns[id]=value; Add(new(0,HistoricalCollection.Columns,0,null,null,id,value.SchemaName,value.TableName)); }
            foreach(var value in current.PrimaryKeys) { long id=++_identity; _keys[id]=value; Add(new(0,HistoricalCollection.PrimaryKeys,0,null,null,id,value.SchemaName,value.TableName)); }
            foreach(var value in current.TableDataSets) { long id=++_identity; _data[id]=value; Add(new(0,HistoricalCollection.TableDataSets,0,id,id,null,value.SchemaName,value.TableName)); }
            foreach(var value in current.FullDataTableNames) Add(new(0,HistoricalCollection.FullDataTableNames,0,null,null,null,"",value));
        }
        internal HistoricalRollbackEvent Event(DatabaseSnapshotResourceChange change)
        {
            var p=change.PreviousPayload; HistoricalPayloadHeader? header=null;
            if(p!=null)
            {
                long revision=++_identity;
                if(p.DatabaseObject!=null) { _objects[revision]=p.DatabaseObject; header=new(revision,revision,p.Kind,p.DatabaseObject.SchemaName,p.DatabaseObject.ObjectName,p.DatabaseObject.Kind); }
                else if(p.Kind==DatabaseVersionedResourceKind.TableMetadata && p.Table!=null)
                {
                    header=new(revision,revision,p.Kind,p.Table.SchemaName,p.Table.TableName,Table:p.Table);
                    _children[(revision,HistoricalCollection.Columns)]=p.Columns.Select(value=> { long id=++_identity; _columns[id]=value;
                        return new HistoricalSnapshotEntry(0,HistoricalCollection.Columns,0,null,revision,id,value.SchemaName,value.TableName); }).ToList();
                    _children[(revision,HistoricalCollection.PrimaryKeys)]=p.PrimaryKeys.Select(value=> { long id=++_identity; _keys[id]=value;
                        return new HistoricalSnapshotEntry(0,HistoricalCollection.PrimaryKeys,0,null,revision,id,value.SchemaName,value.TableName); }).ToList();
                }
                else if(p.TableDataSet!=null) { _data[revision]=p.TableDataSet; header=new(revision,revision,p.Kind,p.TableDataSet.SchemaName,p.TableDataSet.TableName,
                    Table:p.Table,ReportedRowCount:p.TableDataSet.RowCount,ActualRowCount:p.TableDataSet.Rows.Count,DataImportedAtUtc:p.TableDataSet.ImportedAtUtc); }
            }
            return new(change.Kind,change.ChangeKind,change.ResourceKey,header);
        }
        private HistoricalSnapshotEntry Add(HistoricalSnapshotEntry entry)
        {
            long ordinal=_entries.Where(e=>e.Collection==entry.Collection).Select(e=>e.SortOrdinal).DefaultIfEmpty(-1).Max()+1;
            var result=entry with { EntryKey=++_identity,SortOrdinal=ordinal,Table=entry.Table==null ? null : HistoricalSnapshotContext.CopyTable(entry.Table) };
            _entries.Add(result); return result;
        }
        public Task RemoveAsync(HistoricalCollection collection,string schema,string name,SqlDatabaseObjectKind? kind,CancellationToken ct)
        { _entries.RemoveAll(e=>HistoricalSnapshotReplay.Matches(e,collection,schema,name,kind)); return Task.CompletedTask; }
        public Task<HistoricalSnapshotEntry> AppendAsync(HistoricalSnapshotEntry entry,CancellationToken ct) => Task.FromResult(Add(entry));
        public Task AppendChildrenAsync(long revision,HistoricalCollection collection,CancellationToken ct)
        { if(_children.TryGetValue((revision,collection),out var children)) foreach(var child in children) Add(child); return Task.CompletedTask; }
        public Task<HistoricalSnapshotEntry?> FirstTableAsync(string schema,string name,CancellationToken ct) =>
            Task.FromResult(_entries.FirstOrDefault(e=>HistoricalSnapshotReplay.Matches(e,HistoricalCollection.Tables,schema,name)));
        public Task UpdateTableAsync(long key,bool full,long count,DateTimeOffset? stamp,CancellationToken ct)
        { var entry=_entries.Single(e=>e.EntryKey==key); entry.Table!.HasFullData=full; entry.Table.FullDataRowCount=count; entry.Table.FullDataImportedAtUtc=stamp; return Task.CompletedTask; }
        public Task EnsureSelectionAsync(string name,CancellationToken ct)
        { if(!_entries.Any(e=>e.Collection==HistoricalCollection.FullDataTableNames && string.Equals(e.Name,name,StringComparison.OrdinalIgnoreCase)))
            Add(new(0,HistoricalCollection.FullDataTableNames,0,null,null,null,"",name)); return Task.CompletedTask; }
        internal DatabaseMetadataSnapshot Materialize(DatabaseMetadataSnapshot current,DateTimeOffset stamp)
        {
            var result=new DatabaseMetadataSnapshot { SnapshotId=current.SnapshotId,DisplayName=current.DisplayName,DatabaseName=current.DatabaseName,ImportedAtUtc=stamp };
            foreach(var entry in _entries)
                switch(entry.Collection)
                {
                    case HistoricalCollection.Objects: result.Objects.Add(_objects[entry.RevisionKey!.Value]); break;
                    case HistoricalCollection.Tables: result.Tables.Add(entry.Table!); break;
                    case HistoricalCollection.Columns: result.Columns.Add(_columns[entry.ChildKey!.Value]); break;
                    case HistoricalCollection.PrimaryKeys: result.PrimaryKeys.Add(_keys[entry.ChildKey!.Value]); break;
                    case HistoricalCollection.TableDataSets: result.TableDataSets.Add(_data[entry.RevisionKey!.Value]); break;
                    case HistoricalCollection.FullDataTableNames: result.FullDataTableNames.Add(entry.Name); break;
                }
            return result;
        }
    }
}
