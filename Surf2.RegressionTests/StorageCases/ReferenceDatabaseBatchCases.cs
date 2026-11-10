using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;

public static partial class StorageRegressionSuite
{
    public static async Task RunReferenceDatabaseBatchChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_ReferenceDatabaseBatch_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var limits = new ExplorerLimits(PageSize: 128, MaximumDocumentCharacters: 8192);
        var owner = new ReferenceWarmStartOwner(connection, limits);
        var seeded = await SeedQueryIntegrationAsync(fixture, owner.Session, owner.Content, new RelationalCaptureStore(owner.Session), owner.State);
        var policy = ExplorerIndexLanguagePolicy.Capture(new CodeWindowSettings());
        var cold = await owner.Refresher.RefreshAsync(await owner.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(cold.FullyPublished && cold.Published == 3, "Database batch fixture cold registration publishes both captured resources and the independent diagram");

        var other = Required(await owner.State.ReadScopeAsync(seeded.OtherScopeKey), "batch other scope");
        other.Value.Resources.Add(new() { ResourceId = "batch-other-owner", Kind = ResourceKind.DatabaseSnapshot, Path = "query-snapshot" });
        var otherToken = await owner.State.SaveScopeAsync(other.Value, other.Token, Guid.NewGuid());
        await InTransactionAsync(owner.Session, async (c, t) =>
        {
            otherToken = await owner.State.ResolveScopeResourceTargetsAsync(c, t, otherToken, [new(0, seeded.SnapshotKey, null)], Guid.NewGuid());
        });
        long otherRoot = (await owner.Explorer.OpenScopeAsync(seeded.OtherScopeKey)).Resources.Single().ScopeResourceKey;
        await fixture.DestinationSqlAsync("""
            DECLARE @Document bigint=(SELECT DocumentKey FROM surf.Document WHERE Kind=1 AND SnapshotResourceKey=@Resource);
            DECLARE @Root bigint=(SELECT MIN(m.ScopeResourceKey) FROM surf.ResourceDocument m
              JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey WHERE m.DocumentKey=@Document AND sr.ScopeKey=@Scope);
            INSERT surf.ResourceDocument(ScopeResourceKey,DocumentKey,DisplayName,Locator,NodeKey,ParentNodeKey,SortOrdinal)
              SELECT @Other,DocumentKey,DisplayName,Locator,NodeKey,ParentNodeKey,SortOrdinal FROM surf.ResourceDocument
              WHERE DocumentKey=@Document AND ScopeResourceKey=@Root;
            INSERT surf.DocumentLocator(DocumentKey,ScopeResourceKey,Kind,OriginalLocator,LocatorHash,IsAscii)
              SELECT DocumentKey,@Other,Kind,OriginalLocator,LocatorHash,IsAscii FROM surf.DocumentLocator
              WHERE DocumentKey=@Document AND ScopeResourceKey=@Root;
            UPDATE surf.IndexCatalogueHead SET Generation=Generation+1 WHERE Singleton=1;
            """, RelationalSession.Parameter("@Resource", SqlDbType.BigInt, seeded.Procedure.ResourceKey),
            RelationalSession.Parameter("@Scope", SqlDbType.BigInt, seeded.ScopeToken.Key), RelationalSession.Parameter("@Other", SqlDbType.BigInt, otherRoot));
        long otherLocatorIdentity = Convert.ToInt64(await fixture.DestinationSqlAsync(
            "SELECT MAX(LocatorKey) FROM surf.DocumentLocator WHERE ScopeResourceKey=@Root;", RelationalSession.Parameter("@Root", SqlDbType.BigInt, otherRoot)));
        await ReferenceWarmStartSeedScaleAsync(fixture, seeded, 1000);
        const int pages = 9; // 1,001 procedures in eight pages, plus one table page.
        var initial = await ReferenceWarmStartObserveAsync(connection, async () =>
            await owner.Refresher.RefreshAsync(await owner.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy));
        ReferenceDatabaseBatchTrace(initial.Commands, pages, false, check);
        check(initial.Value.FullyPublished && initial.Value.Considered == 1002 && initial.Value.Unchanged == 1003 && initial.Value.Published == 0,
            "One-thousand-object rebuild reuses published bodies and processes every captured resource beyond page boundaries");

        // Damage only disposable derived membership metadata, not captured source revisions or text.
        await fixture.DestinationSqlAsync("""
            UPDATE m SET DisplayName=N'old-batch-fixture-name'
            FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
            JOIN surf.Document d ON d.DocumentKey=m.DocumentKey WHERE sr.ScopeKey=@Scope AND d.Kind IN(1,2);
            UPDATE surf.IndexCatalogueHead SET Generation=Generation+1 WHERE Singleton=1;
            """, RelationalSession.Parameter("@Scope", SqlDbType.BigInt, seeded.ScopeToken.Key));
        var changed = await ReferenceWarmStartObserveAsync(connection, async () =>
            await owner.Refresher.RefreshAsync(await owner.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy));
        ReferenceDatabaseBatchTrace(changed.Commands, pages, true, check);
        check(changed.Value.FullyPublished && changed.Value.Published == 0 && changed.Value.Considered == 1002,
            "Changed-only membership pages are repaired without reparsing or publishing unchanged captured source text");
        long repaired = Convert.ToInt64(await fixture.DestinationSqlAsync("""
            SELECT COUNT_BIG(*) FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
            JOIN surf.Document d ON d.DocumentKey=m.DocumentKey WHERE sr.ScopeKey=@Scope AND d.Kind IN(1,2)
            AND m.DisplayName=N'old-batch-fixture-name';
            """, RelationalSession.Parameter("@Scope", SqlDbType.BigInt, seeded.ScopeToken.Key)));
        long otherPreserved = Convert.ToInt64(await fixture.DestinationSqlAsync("""
            SELECT COUNT_BIG(*) FROM surf.ResourceDocument m WHERE m.ScopeResourceKey=@Root
            AND (SELECT COUNT_BIG(*) FROM surf.DocumentLocator l WHERE l.ScopeResourceKey=@Root)=2
            AND (SELECT MAX(LocatorKey) FROM surf.DocumentLocator l WHERE l.ScopeResourceKey=@Root)=@Identity;
            """, RelationalSession.Parameter("@Root", SqlDbType.BigInt, otherRoot),
            RelationalSession.Parameter("@Identity", SqlDbType.BigInt, otherLocatorIdentity)));
        check(repaired == 0 && otherPreserved == 1,
            "Bulk replacement repairs all selected alias metadata while another scope retains its original membership and locator identities");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static void ReferenceDatabaseBatchTrace(IReadOnlyList<StateAccessCommand> commands, int pages,
        bool changed, Action<bool, string> check)
    {
        check(commands.Count(c => ReferenceWarmStartHas(c, "SELECT d.SnapshotResourceKey,d.Kind,d.DocumentKey,d.Version,d.Freshness")) == pages &&
            commands.Count(c => ReferenceWarmStartHas(c, "SELECT d.DocumentKey,d.Version,d.Kind,d.SnapshotKey,d.SnapshotResourceKey")) == pages,
            "Captured state and exact scoped memberships are fetched once per metadata page, not once per document");
        check(!commands.Any(c => ReferenceWarmStartHas(c, "SELECT d.DocumentKey,d.Version,LEFT(f.OriginalPath") ||
            ReferenceWarmStartHas(c, "SELECT TOP(513) m.ScopeResourceKey") || ReferenceWarmStartHas(c, "c.Text") ||
            ReferenceWarmStartHas(c, "INSERT surf.DocumentRevision")),
            "Primed published database documents bypass point registration, per-document membership queries and code-body reads");
        check(commands.Count(c => ReferenceWarmStartHas(c, "CREATE TABLE #Membership")) == (changed ? pages : 0) &&
            commands.Count(c => ReferenceWarmStartHas(c, "JOIN @Changed wanted ON wanted.Id=l.DocumentKey")) == (changed ? pages : 0),
            "Temporary bulk staging and replacement run exactly once for each changed page and never for unchanged memberships");
        check(commands.Count(c => ReferenceWarmStartHas(c, "SELECT Generation FROM surf.IndexCatalogueHead WITH(UPDLOCK,HOLDLOCK)")) <= pages + 3,
            "Captured rebuild writer-lock transaction count grows with metadata pages, not document count");
    }
}
