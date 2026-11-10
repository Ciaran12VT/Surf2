using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage;
using Surf2.Storage.Relational;

public static partial class StorageRegressionSuite
{
    public static async Task RunV2CaptureChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync(new Dictionary<string, string>
        {
            ["database-snapshots"] = "{\"Snapshots\":[],\"Histories\":[]}",
            ["diagram-library"] = "{\"Diagrams\":[]}",
            ["scope-library"] = "{\"Scopes\":[]}"
        });
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        await fixture.MarkTestFixtureReadyAsync();
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(fixture.DestinationConnectionString));
        var scope = new Scope { ScopeId = "v2-filtered-capture", Name = "Filtered capture" };
        var token = await runtime.StateStore.CreateScopeAsync(scope, 0, Guid.NewGuid());
        var service = new RelationalDatabaseCaptureService(runtime);
        var table = (await service.ListSourceTablesAsync(fixture.SourceConnectionString)).Items.Single();
        foreach (var (selection, expectedRows) in new[] { ("TOP 1", 1L), ("WHERE DocumentKey=N'diagram-library'", 1L), ("WHERE 1=0", 0L) })
        {
            var result = await service.CaptureAsync(new(fixture.SourceConnectionString, "Capture " + selection,
                [table with { RowSelection = selection }], scope, token, AllowSerializableFallback: true));
            check(result.CapturedRows == expectedRows, "V2 captures exactly the selected rows for " + selection);
            scope = result.UpdatedScope;
            token = result.UpdatedScopeToken;
        }
        await fixture.VerifySourceUnchangedAsync(check);
    }
}
