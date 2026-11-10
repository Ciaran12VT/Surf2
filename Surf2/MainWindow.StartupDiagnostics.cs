using Surf2.Services;

namespace Surf2;

public partial class MainWindow
{
    private StartupFailureDiagnostics? _startupDiagnostics;

    private void SetStartupStage(StartupStage stage)
    {
        if (_startupDiagnostics == null) return;
        _startupDiagnostics.Enter(stage);
        InternalLogService.Info("Startup phase.", ("Stage", _startupDiagnostics.StageDescription));
        StatusText = "Restoring workspace: " + _startupDiagnostics.StageDescription + "...";
    }

    private string DescribeStartupFailure(Exception error)
    {
        string stage = _startupDiagnostics?.StageDescription ?? "loading persistence state";
        string? report = InternalLogService.IsEnabled ? _startupDiagnostics?.WriteFailure(error) : null;
        string reason = error is Microsoft.Data.SqlClient.SqlException sql
            ? sql.Number == -2 ? "SQL request timed out" : "SQL error " + sql.Number
            : error.Message.TrimEnd('.', ' ');
        return $"Could not restore workspace while {stage}: {reason}. Saving is disabled until Surf2 restarts successfully." +
            (report == null ? "" : " Diagnostic report: " + report);
    }
}
