using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckStartupResilienceAsync(SqlFixture fixture, RelationalSession session,
        RelationalStateStore state, QueryIntegrationFixture seeded, string connection, Action<bool, string> check)
    {
        check(new AppSettings().Diagnostics.EnableInternalLogging, "Fresh settings default internal logging to enabled");
        check(JsonSerializer.Deserialize<AppSettings>("{}")!.Diagnostics.EnableInternalLogging &&
            JsonSerializer.Deserialize<AppSettings>("{\"Diagnostics\":{}}")!.Diagnostics.EnableInternalLogging,
            "Legacy settings with no persisted logging value retain the enabled default");
        var disabled = JsonSerializer.Deserialize<AppSettings>("{\"Diagnostics\":{\"EnableInternalLogging\":false}}")!;
        disabled.EnsureDefaults();
        check(!disabled.Clone().Diagnostics.EnableInternalLogging,
            "A persisted logging opt-out survives deserialization, defaults and cloning");
        foreach (string json in new[] { "{}", "{\"Diagnostics\":{}}", "{\"Diagnostics\":{\"EnableInternalLogging\":false}}" })
        {
            using var legacy = JsonDocument.Parse(json);
            check(!LegacySchemaInspector.Deserialize<AppSettings>(legacy.RootElement).Diagnostics.EnableInternalLogging,
                "The frozen migration converter retains its original omitted/off preference semantics for compatible recovery");
        }
        using (var legacy = JsonDocument.Parse("{\"Diagnostics\":{\"EnableInternalLogging\":true}}"))
            check(LegacySchemaInspector.Deserialize<AppSettings>(legacy.RootElement).Diagnostics.EnableInternalLogging,
                "The migration converter still preserves a persisted logging-on value");

        var selection = Required(await state.ReadScopeSelectionAsync(), "startup resilience selection");
        await state.SaveScopeSelectionAsync(selection.Value with { LastActiveScopeId = "query-scope" }, selection.Token, Guid.NewGuid());
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var scope = await runtime.Explorer.OpenScopeAsync(seeded.ScopeToken.Key);
        var indexed = await new RelationalScopeIndexRefresher(runtime.Session, runtime.Index, runtime.Snapshots)
            .RefreshAsync(scope, ExplorerIndexLanguagePolicy.Capture(new CodeWindowSettings()));
        check(indexed.FullyPublished, "Owned startup fixture establishes durable reference checkpoints before blocking metadata");

        // A blocked derived catalogue must not be confused with failure to load authoritative saved state.
        await using (var blocker = await session.OpenAsync())
        await using (var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            await using var lockCommand = blocker.CreateCommand();
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "SELECT COUNT_BIG(*) FROM surf.SymbolDefinition WITH (TABLOCKX,HOLDLOCK);";
            await lockCommand.ExecuteScalarAsync();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection));
            using var trace = new StateAccessSqlTrace(connection, command =>
            {
                if (StateAccessSql(command.CommandText).Contains("SURF.SYMBOLDEFINITION", StringComparison.Ordinal))
                    entered.TrySetResult(true);
            });
            try
            {
                var startup = (Task<bool>)RuntimeWpfInvoke(main, "TryLoadRelationalPersistenceAsync")!;
                check(await startup.WaitAsync(TimeSpan.FromSeconds(15)) &&
                    RuntimeWpfField<object>(main, "_isPersistenceHydrated") is true && main.RootNodes.Count == 3,
                    "Ready startup displays the selected scope and hydrates saving while reference SQL remains locked");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                check(!RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask").IsCompleted &&
                    RuntimeWpfOptionalField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue") == null,
                    "Blocked catalogue assembly belongs solely to the tracked background job; no empty readiness proof is installed");
                check(!InternalLogService.IsEnabled,
                    "Relational startup applies the persisted logging opt-out before subsequent state reads");
                await transaction.RollbackAsync();
                await RuntimeWpfDrainReferenceRefreshAsync(main);
                check(RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue").Coverage.FullyPublished &&
                    RuntimeWpfField<object>(main, "_isPersistenceHydrated") is true,
                    "After releasing the index lock, background publication restores reference coverage without another startup");
            }
            finally
            {
                if (transaction.Connection != null) await transaction.RollbackAsync();
                await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync");
                RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
                main.Close();
            }
        }

        await RuntimeWpfCheckCoreStartupTimeoutAsync(fixture, session, connection, check);
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task RuntimeWpfCheckCoreStartupTimeoutAsync(SqlFixture fixture, RelationalSession session,
        string connection, Action<bool, string> check)
    {
        await using var blocker = await session.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(IsolationLevel.Serializable);
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.Transaction = transaction;
        lockCommand.CommandText = "SELECT Version FROM surf.ApplicationPreference WITH (TABLOCKX,HOLDLOCK) WHERE ProfileKey=1;";
        var before = (byte[]) (await lockCommand.ExecuteScalarAsync())!;
        var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection));
        var diagnostics = new StartupFailureDiagnostics(Path.Combine(fixture.OwnedDirectory, "diagnostics"));
        RuntimeWpfSetField(main, "_startupDiagnostics", diagnostics);
        using var trace = new StateAccessSqlTrace(connection, command =>
        {
            if (Regex.IsMatch(StateAccessSql(command.CommandText), @"\bFROM\s+SURF\.APPLICATIONPREFERENCE\b"))
                command.CommandTimeout = 1; // Fixture-only timeout: production timeout settings are unchanged.
        });
        try
        {
            Exception? failure = null;
            try { await RuntimeWpfInvokeTaskAsync(main, "TryLoadRelationalPersistenceAsync").WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (SqlException error) when (error.Number == -2) { failure = error; }
            check(failure != null && diagnostics.Stage == StartupStage.Preferences &&
                RuntimeWpfField<object>(main, "_isPersistenceHydrated") is false,
                "An actual SQL preference timeout identifies its startup stage and leaves saving disabled");
            if (failure == null) throw new InvalidOperationException("Expected an owned-fixture SQL timeout.");
            check(new StackTrace(failure, false).ToString().Contains("ReadPreferenceSummaryAsync", StringComparison.Ordinal),
                "Failed selected-state propagation preserves the original SQL read call stack");
            string message = (string)RuntimeWpfInvoke(main, "DescribeStartupFailure", failure)!;
            check(message.Contains("loading startup preferences", StringComparison.Ordinal) &&
                message.Contains("SQL request timed out", StringComparison.Ordinal),
                "Startup failure status names the operation rather than reporting only a generic workspace timeout");
            string path = diagnostics.WriteFailure(failure) ?? throw new InvalidOperationException("Owned report could not be written.");
            string report = await File.ReadAllTextAsync(path);
            check(report.Contains("SQL number: -2", StringComparison.Ordinal) && report.Contains("ReadPreferenceSummaryAsync", StringComparison.Ordinal) &&
                !report.Contains(connection, StringComparison.Ordinal) && !report.Contains(fixture.OwnedDirectory, StringComparison.Ordinal) &&
                !report.Contains(new SqlConnectionStringBuilder(connection).InitialCatalog, StringComparison.Ordinal),
                "Sanitized failure report preserves the stage, SQL error codes and method stack without database/connection/path values");
            var sensitive = new InvalidOperationException("secret connection and document text", new IOException("secret path"));
            sensitive.Data["Credentials"] = "secret password";
            check(!diagnostics.FormatFailure(sensitive).Contains("secret", StringComparison.Ordinal),
                "Failure report excludes arbitrary exception messages and Data entries");
            check(trace.Commands.All(c => !Regex.IsMatch(c.Text, @"\b(?:INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|MERGE)\b")),
                "Core startup timeout publishes no default replacement or schema mutation");
            await transaction.RollbackAsync();
            await using var read = blocker.CreateCommand();
            read.CommandText = "SELECT Version FROM surf.ApplicationPreference WHERE ProfileKey=1;";
            var after = (byte[]) (await read.ExecuteScalarAsync())!;
            check(before.AsSpan().SequenceEqual(after),
                "Failed startup leaves the authoritative saved preference token untouched");
        }
        finally
        {
            if (transaction.Connection != null) await transaction.RollbackAsync();
            RuntimeWpfSetField(main, "_startupDiagnostics", null!);
            await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync");
            RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
            main.Close();
        }
    }
}
