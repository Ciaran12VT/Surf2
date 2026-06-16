using System.Windows;
using System.Windows.Threading;
using Surf2.Services;
using Surf2.Storage;

namespace Surf2;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ExternalOpenPipeServer? _externalOpenPipeServer;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Window_Loaded));

        ExternalOpenRequest? externalOpenRequest = null;
        if (ExternalOpenCommandLine.TryParse(e.Args, out ExternalOpenRequest parsedExternalOpenRequest))
        {
            externalOpenRequest = parsedExternalOpenRequest;
            if (!string.IsNullOrWhiteSpace(externalOpenRequest.ConnectionString))
            {
                Environment.SetEnvironmentVariable(
                    SqlServerConnectionOptions.EnvironmentVariableName,
                    externalOpenRequest.ConnectionString);
            }

            try
            {
                ExternalOpenResponse? response = ExternalOpenPipeClient
                    .TrySendAsync(externalOpenRequest, TimeSpan.FromMilliseconds(1200))
                    .GetAwaiter()
                    .GetResult();
                if (response != null)
                {
                    Shutdown(response.Success ? 0 : 2);
                    return;
                }
            }
            catch (Exception ex)
            {
                InternalLogService.Warning(
                    "Could not forward external-open request to an existing Surf2 instance.",
                    ("Error", ex.Message));
            }
        }

        base.OnStartup(e);
        ShowMainWindowWithSplash(externalOpenRequest);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _externalOpenPipeServer?.Dispose();
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

    private void ShowMainWindowWithSplash(ExternalOpenRequest? externalOpenRequest)
    {
        StartupSplashWindow? splashWindow = null;
        try
        {
            splashWindow = new StartupSplashWindow();
            splashWindow.Show();
            splashWindow.CenterOnPrimaryWorkArea();
        }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "Failed to show startup splash window.");
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        StartExternalOpenPipeServer(mainWindow);

        bool splashClosed = false;
        void CloseSplash()
        {
            if (splashClosed)
            {
                return;
            }

            splashClosed = true;
            try
            {
                splashWindow?.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }

        mainWindow.ContentRendered += (_, _) => CloseSplash();
        mainWindow.Closed += (_, _) => CloseSplash();
        mainWindow.Show();

        if (externalOpenRequest != null)
        {
            _ = mainWindow.OpenExternalResourceAsync(externalOpenRequest);
        }
    }

    private void StartExternalOpenPipeServer(MainWindow mainWindow)
    {
        try
        {
            string connectionString = SqlServerConnectionOptions.CreateDefault().ConnectionString;
            _externalOpenPipeServer = new ExternalOpenPipeServer(
                connectionString,
                request => DispatchExternalOpenRequestAsync(mainWindow, request));
            _externalOpenPipeServer.Start();
        }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "Failed to start external-open pipe server.");
        }
    }

    private static async Task<ExternalOpenResponse> DispatchExternalOpenRequestAsync(
        MainWindow mainWindow,
        ExternalOpenRequest request)
    {
        Task<ExternalOpenResponse> operation = await mainWindow.Dispatcher.InvokeAsync(() =>
            mainWindow.OpenExternalResourceAsync(request));
        return await operation;
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            AppThemeService.ApplyWindowChrome(window);
        }
    }
}
