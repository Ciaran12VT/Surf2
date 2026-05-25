using System.Windows;
using System.Windows.Threading;
using Surf2.Services;

namespace Surf2;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        InternalLogService.Info(
            "Surf2 exit.",
            ("ExitCode", e.ApplicationExitCode));

        base.OnExit(e);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        InternalLogService.Fatal(
            e.Exception,
            "Unhandled WPF dispatcher exception.",
            ("Handled", e.Handled));

        e.Handled = false;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            InternalLogService.Fatal(
                exception,
                "Unhandled AppDomain exception.",
                ("IsTerminating", e.IsTerminating));
            return;
        }

        InternalLogService.Fatal(
            "Unhandled AppDomain exception object.",
            ("ExceptionObject", e.ExceptionObject),
            ("IsTerminating", e.IsTerminating));
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        InternalLogService.Error(
            e.Exception,
            "Unobserved task exception.",
            ("Observed", e.Observed));
    }

    private static void CurrentDomain_ProcessExit(object? sender, EventArgs e)
    {
        InternalLogService.Info("Process exit event.");
    }
}
