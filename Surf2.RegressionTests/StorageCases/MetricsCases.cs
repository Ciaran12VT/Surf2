using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access;

public static partial class StorageRegressionSuite
{
    public static async Task RunSqlMetricsChecksAsync(Action<bool, string> check)
    {
        var queries = new RelationalQueryMetrics();
        var epoch = Guid.NewGuid();
        var digest = QueryFingerprint.FromTemplate("fixture text fetch");
        for (int i = 0; i < 300; i++)
        {
            using var measurement = queries.Diagnostics.Begin(QueryOperation.DefinitionRead, digest, epoch);
            measurement.RecordRead(1, 2, queryCount: 0);
            if (i % 2 == 0) measurement.RecordCacheHit();
            measurement.Complete();
        }
        using (queries.Diagnostics.Begin(QueryOperation.DefinitionRead, digest, epoch)) { }
        var queryMetrics = queries.Snapshot();
        check(queryMetrics.Active == 0 && queryMetrics.Completed == 300 && queryMetrics.Abandoned == 1 &&
            queryMetrics.RowsRead == 300 && queryMetrics.BytesRead == 600 && queryMetrics.Queries == 0 && queryMetrics.CacheHits == 150,
            "Fetch telemetry accounts for returned rows/bytes, cache hits and incomplete operations without inventing SQL roundtrips");
        check(queryMetrics.Recent.Count == 256 && queryMetrics.Recent.All(r => r.Epoch == epoch && r.Fingerprint == digest),
            "Fetch telemetry retains only a bounded immutable fingerprint-based ring");
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        await fixture.MarkTestFixtureReadyAsync();
        var session = new RelationalSession(fixture.DestinationConnectionString);
        session.Metrics.Enable();
        await session.RequireReadyAsync();
        const string canary = "fixture-secret-parameter-do-not-log";
        await using (var connection = await session.OpenAsync())
        {
            for (int i = 0; i < 300; i++)
            {
                await using var command = connection.CreateCommand(); command.CommandText = "SELECT @Value;";
                command.Parameters.Add(RelationalSession.Parameter("@Value", SqlDbType.NVarChar, canary, 100));
                check((string)(await command.ExecuteScalarAsync())! == canary, "Instrumented SQL still returns the exact selected value");
            }
            await using var bad = connection.CreateCommand(); bad.CommandText = "SELECT FixtureMissingMetricsColumn;";
            await ThrowsAsync<SqlException>(async () => await bad.ExecuteScalarAsync(), check,
                "Instrumented SQL preserves provider failure instead of swallowing it");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await using var slow = connection.CreateCommand(); slow.CommandText = "WAITFOR DELAY '00:00:05'; SELECT 1;";
            bool cancelled = false;
            try { await slow.ExecuteScalarAsync(cancellation.Token); }
            catch (Exception error) when (error is OperationCanceledException or SqlException) { cancelled = true; }
            check(cancelled, "Instrumented SQL preserves asynchronous cancellation");
        }
        var metrics = session.Metrics.Snapshot();
        check(metrics.Started >= 303 && metrics.Completed >= 301 && metrics.Failed >= 1 &&
            metrics.Started == metrics.Completed + metrics.Failed + metrics.Cancelled && metrics.ActiveCommands == 0 &&
            metrics.DroppedMeasurements == 0, "Actual command telemetry accounts for success, failure and cancellation after readers exit");
        check(metrics.Recent.Count == 256 && metrics.TotalExecutionDuration > TimeSpan.Zero &&
            metrics.Recent.All(r => r.Epoch == session.Epoch && r.Fingerprint.Value?.Length == 64 && r.ExecutionDuration >= TimeSpan.Zero),
            "Actual SQL timings retain only a fixed-size fingerprint/epoch/outcome ring");
        string serialized = JsonSerializer.Serialize(metrics);
        check(!serialized.Contains(canary, StringComparison.Ordinal) && !serialized.Contains("WAITFOR", StringComparison.Ordinal) &&
            !serialized.Contains("SELECT", StringComparison.Ordinal) && !serialized.Contains(fixture.DestinationConnectionString, StringComparison.Ordinal),
            "SQL timing snapshots contain no SQL, parameter values, connection details or provider error text");
        long before = metrics.Started;
        await fixture.DestinationSqlAsync("SELECT 42;");
        check(session.Metrics.Snapshot().Started == before, "Telemetry excludes unrelated connections to the same database");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}
