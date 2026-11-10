using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Surf2.Services;

internal enum StartupStage
{
    DatabaseFormat, DatabaseReadiness, Preferences, Workspace, ScopeSelection, WorkbenchList,
    Appearance, StartupWorkbench, WorkbenchState, WorkbenchScope, ScopeLookup, ScopeState,
    ScopeMetadata, ScopeRoots, Diagram, Documents, Viewport, LegacyDocuments
}

// A last-failure report is available even when preference loading failed before logging was enabled.
// Deliberately omit exception messages, file paths, SQL text/parameters and connection properties.
internal sealed class StartupFailureDiagnostics(string? directory = null)
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private TimeSpan _stageStarted;
    public StartupStage Stage { get; private set; } = StartupStage.DatabaseFormat;
    public string StageDescription => Describe(Stage);

    public void Enter(StartupStage stage)
    {
        Stage = stage;
        _stageStarted = _elapsed.Elapsed;
    }

    public string? WriteFailure(Exception error)
    {
        string? pending = null;
        try
        {
            string folder = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Surf2", "diagnostics");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "startup-failure.log");
            pending = Path.Combine(folder, ".startup-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(pending, FormatFailure(error), new UTF8Encoding(false));
            File.Move(pending, path, overwrite: true);
            return path;
        }
        catch { return null; }
        finally
        {
            if (pending != null)
                try { File.Delete(pending); } catch { }
        }
    }

    internal string FormatFailure(Exception error)
    {
        var report = new StringBuilder();
        report.AppendLine("Surf2 startup failure (latest failure only)");
        report.AppendLine("UTC: " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        report.AppendLine("Build: " + typeof(StartupFailureDiagnostics).Assembly.GetName().Version);
        report.AppendLine("Stage: " + StageDescription);
        report.AppendLine("Elapsed: " + _elapsed.Elapsed.ToString("c", CultureInfo.InvariantCulture));
        report.AppendLine("Stage elapsed: " + (_elapsed.Elapsed - _stageStarted).ToString("c", CultureInfo.InvariantCulture));
        int depth = 0;
        for (Exception? current = error; current != null && depth++ < 16; current = current.InnerException)
        {
            report.AppendLine("Exception type: " + current.GetType().FullName);
            report.AppendLine("HResult: " + current.HResult.ToString("X8", CultureInfo.InvariantCulture));
            if (current is SqlException sql)
            {
                foreach (SqlError item in sql.Errors.Cast<SqlError>().Take(16))
                    report.AppendLine($"SQL number: {item.Number}; state: {item.State}; class: {item.Class}");
            }
            foreach (var frame in (new StackTrace(current, false).GetFrames() ?? []).Take(64))
            {
                var method = frame.GetMethod();
                report.AppendLine("  at " + method?.DeclaringType?.FullName + "." + method?.Name);
            }
        }
        return report.ToString();
    }

    private static string Describe(StartupStage stage) => stage switch
    {
        StartupStage.DatabaseFormat => "checking database format",
        StartupStage.DatabaseReadiness => "checking database readiness",
        StartupStage.Preferences => "loading startup preferences",
        StartupStage.Workspace => "loading saved workspace",
        StartupStage.ScopeSelection => "loading scope selection",
        StartupStage.WorkbenchList => "listing saved workbenches",
        StartupStage.Appearance => "loading appearance settings",
        StartupStage.StartupWorkbench => "choosing the startup workbench",
        StartupStage.WorkbenchState => "loading the saved workbench",
        StartupStage.WorkbenchScope => "resolving the saved workbench scope",
        StartupStage.ScopeLookup => "finding the selected scope",
        StartupStage.ScopeState => "loading selected scope state",
        StartupStage.ScopeMetadata => "loading scope resource metadata",
        StartupStage.ScopeRoots => "preparing Object Explorer roots",
        StartupStage.Diagram => "preparing the saved diagram",
        StartupStage.Documents => "restoring open documents",
        StartupStage.Viewport => "restoring workspace layout",
        StartupStage.LegacyDocuments => "loading legacy persistence documents",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
}
