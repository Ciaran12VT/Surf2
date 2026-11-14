using System.Data;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.LiveSql;

namespace Surf2.RegressionTests;

internal static class LiveSqlFeatureTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (string sql in new[] { "SELECT 1 AS [DROP]", "WITH C AS (SELECT 1 x) SELECT x FROM C", "SELECT TOP (@limit) * FROM [dbo].[Order]]Items]", "SELECT 'EXEC; DROP' AS Text" })
        { LiveSqlQueryPolicy.Validate(sql); check(true, "live SELECT accepts structured read: " + sql); }
        foreach (string sql in new[] { "DELETE FROM dbo.T", "SELECT 1; SELECT 2", "SELECT * INTO dbo.NewTable FROM dbo.T", "SELECT NEXT VALUE FOR dbo.Seq", "SELECT * FROM OtherDb.dbo.T", "SELECT OtherDb.dbo.f()", "EXEC dbo.P", "SELECT * FROM OPENQUERY(Server,'SELECT 1')", "SELECT * FROM OPENROWSET(BULK 'C:\\x',SINGLE_CLOB) AS x" })
        {
            bool rejected = false; try { LiveSqlQueryPolicy.Validate(sql); } catch (ArgumentException) { rejected = true; }
            check(rejected, "live SELECT rejects side effects/external query: " + sql);
        }
        const string id = "19dce857-6da4-4c2b-a423-ffde506e81a6";
        var origin = DatabaseOriginQueryService.Parse(JsonSerializer.Serialize(new { Format = "Surf2.DatabaseOrigin", Version = 1,
            Engine = "SqlServer", Server = "Host", Database = "DbA", IdentifierKind = "database_guid", Identifier = id,
            Availability = "Available", ObservedAtUtc = "2026-01-01T12:00:00Z" }));
        check(origin.IsComplete && origin.Acquisition == "PastedIdentityQuery", "pasted identity stays evidence, not direct capture");
        check(!origin.AgreesWith(origin with { Database = "DbB" }), "same GUID/server cannot match another database name");
        check(!origin.AgreesWith(origin with { Endpoint = "other" }) || origin.Endpoint.Length == 0, "optional endpoint evidence does not invent a conflict");
        check(!(origin with { Endpoint = "original" }).AgreesWith(origin with { Endpoint = "different" }), "conflicting recorded connection locators require confirmation");
        check(!(origin with { Identifier = "" }).IsComplete, "partial origin remains incomplete");
        check((origin with { Identifier = "" }).ConflictsWith(origin with { Server = "OtherHost" }), "partial origin still detects conflicting server evidence");
        check(DatabaseOrigin.FromJson(origin.ToJson())!.AgreesWith(origin), "versioned origin roundtrip preserves identity");
        check(new DatabaseMetadataSnapshot { Origin = origin }.Clone().Origin == origin, "snapshot clone preserves origin");
        var running = new Surf2.Storage.Relational.Snapshots.SnapshotRefreshDisplay(10, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow, Surf2.Storage.Relational.Snapshots.SnapshotRefreshOutcome.Running, null);
        check(running.Label("DbA").Contains("Last Refreshed not recorded") && running.Label("DbA").Contains("completion not recorded"),
            "running refresh is not displayed as a completed refresh");
        check(LiveSqlCell.Compare(new("Decimal", "10000000000000000000000000000000000001"), new("Decimal", "10000000000000000000000000000000000002")) < 0, "typed sort retains SQL 38-digit decimal precision");
        check(LiveSqlCell.Compare(new("Null", null), new("Text", "")) != 0, "NULL differs from empty string");
        check(LiveSqlCell.Compare(new("DateTimeOffset", "2026-01-01T10:00:00+02:00"), new("DateTimeOffset", "2026-01-01T09:00:00+00:00")) < 0,
            "typed datetimeoffset sorts by instant, not displayed local clock");
        string query = new DatabaseMetadataQueryService().CreateImportQuery(new[] { "[dbo].[Missing] WHERE Id > 3" });
        check(query.Contains("DatabaseOrigin") && query.Contains("FullDataSelection"), "generated import query carries identity and requested capture recipe");
        var manifest = SnapshotExportMetadata.Create(origin, "DbA", DateTimeOffset.UtcNow, new[] { "[dbo].[Missing] WHERE Id > 3" });
        check(SnapshotExportMetadata.Parse(JsonSerializer.SerializeToUtf8Bytes(manifest)).RequestedTables.Count == 1, "folder manifest retains absent-table recipe");
    }
}
