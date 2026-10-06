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
    private ExternalOpenPipeServer? _databaseExternalOpenPipeServer;
    private ExternalOpenPipeServer? _anyInstanceExternalOpenPipeServer;
    private ExternalOpenPipeServer? _activeScopeExternalOpenPipeServer;
    private string _activeScopePipeName = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool hasExternalOpenArgument = e.Args.Any(IsExternalOpenArgument);
        if (hasExternalOpenArgument)
        {
            InternalLogService.Initialize();
            InternalLogService.Info(
                "Surf2 started with external-open arguments.",
                ("ArgumentCount", e.Args.Length));
        }

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Window_Loaded));

        ExternalOpenRequest? externalOpenRequest = null;
        bool parsedExternalOpen = ExternalOpenCommandLine.TryParse(e.Args, out ExternalOpenRequest parsedExternalOpenRequest);
        if (hasExternalOpenArgument)
        {
            InternalLogService.Info(
                "External-open command line parse completed.",
                ("Parsed", parsedExternalOpen),
                ("HasTarget", parsedExternalOpenRequest.HasTarget),
                ("ScopeId", parsedExternalOpenRequest.ScopeId),
                ("ScopeName", parsedExternalOpenRequest.ScopeName),
                ("ResourcePath", parsedExternalOpenRequest.ResourcePath),
                ("ResourceKind", parsedExternalOpenRequest.ResourceKind));
        }

        if (parsedExternalOpen)
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
                    .TrySendAsync(externalOpenRequest, TimeSpan.FromMilliseconds(350))
                    .GetAwaiter()
                    .GetResult();
                if (response != null)
                {
                    if (response.Success || response.WasCancelled)
                    {
                        ExitForwarderProcess(response.Success ? 0 : 2);
                        return;
                    }

                    InternalLogService.Warning(
                        "Existing Surf2 instance rejected an external-open request; continuing startup locally.",
                        ("Message", response.Message),
                        ("ScopeId", externalOpenRequest.ScopeId),
                        ("ScopeName", externalOpenRequest.ScopeName),
                        ("ResourcePath", externalOpenRequest.ResourcePath),
                        ("ResourceKind", externalOpenRequest.ResourceKind));
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

    private static bool IsExternalOpenArgument(string arg)
    {
        return string.Equals(arg, "--surf2-open", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(arg, "--open-surf-resource", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(arg, "--external-open", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("--surf2-open-json", StringComparison.OrdinalIgnoreCase);
    }

    private void ExitForwarderProcess(int exitCode)
    {
        Shutdown(exitCode);
        Environment.Exit(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activeScopeExternalOpenPipeServer?.Dispose();
        _anyInstanceExternalOpenPipeServer?.Dispose();
        _databaseExternalOpenPipeServer?.Dispose();
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
        StartExternalOpenPipeServers(mainWindow);

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
            _ = OpenStartupExternalResourceAsync(mainWindow, externalOpenRequest);
        }
    }

    private static async Task OpenStartupExternalResourceAsync(
        MainWindow mainWindow,
        ExternalOpenRequest externalOpenRequest)
    {
        try
        {
            externalOpenRequest.SuppressSavePrompt = true;
            ExternalOpenResponse response = await mainWindow.OpenExternalResourceAsync(externalOpenRequest);
            if (!response.Success && !response.WasCancelled)
            {
                MessageBox.Show(
                    mainWindow,
                    response.Message,
                    "Open Surf2 resource",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "Failed to handle startup external-open request.");
            MessageBox.Show(
                mainWindow,
                $"Could not open the requested Surf2 resource: {ex.Message}",
                "Open Surf2 resource",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void StartExternalOpenPipeServers(MainWindow mainWindow)
    {
        try
        {
            string connectionString = mainWindow.EffectivePersistenceConnectionString;
            _databaseExternalOpenPipeServer = StartExternalOpenPipeServer(
                ExternalOpenPipeNames.CreateDatabase(connectionString),
                mainWindow);
            _anyInstanceExternalOpenPipeServer = StartExternalOpenPipeServer(
                ExternalOpenPipeNames.AnyInstance,
                mainWindow);

            mainWindow.ActiveScopeChanged += (_, _) =>
                RefreshActiveScopeExternalOpenPipeServer(mainWindow, mainWindow.EffectivePersistenceConnectionString);
            mainWindow.PersistenceConnectionChanged += (_, _) =>
            {
                _databaseExternalOpenPipeServer?.Dispose();
                _databaseExternalOpenPipeServer = StartExternalOpenPipeServer(
                    ExternalOpenPipeNames.CreateDatabase(mainWindow.EffectivePersistenceConnectionString), mainWindow);
                RefreshActiveScopeExternalOpenPipeServer(mainWindow, mainWindow.EffectivePersistenceConnectionString);
            };
            RefreshActiveScopeExternalOpenPipeServer(mainWindow, connectionString);
        }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "Failed to start external-open pipe servers.");
        }
    }

    private ExternalOpenPipeServer StartExternalOpenPipeServer(
        string pipeName,
        MainWindow mainWindow)
    {
        var pipeServer = new ExternalOpenPipeServer(
                pipeName,
                request => DispatchExternalOpenRequestAsync(mainWindow, request));
        pipeServer.Start();
        InternalLogService.Info(
            "External-open pipe server started.",
            ("PipeName", pipeName));
        return pipeServer;
    }

    private void RefreshActiveScopeExternalOpenPipeServer(
        MainWindow mainWindow,
        string connectionString)
    {
        try
        {
            string? pipeName = ExternalOpenPipeNames.CreateActiveScope(
                connectionString,
                mainWindow.ActiveScopeId,
                mainWindow.ActiveScopeName);
            if (string.Equals(_activeScopePipeName, pipeName, StringComparison.Ordinal))
            {
                return;
            }

            _activeScopeExternalOpenPipeServer?.Dispose();
            _activeScopeExternalOpenPipeServer = null;
            _activeScopePipeName = pipeName ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(pipeName))
            {
                _activeScopeExternalOpenPipeServer = StartExternalOpenPipeServer(pipeName, mainWindow);
            }
        }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "Failed to refresh active-scope external-open pipe server.");
        }
    }

    private static async Task<ExternalOpenResponse> DispatchExternalOpenRequestAsync(
        MainWindow mainWindow,
        ExternalOpenRequest request)
    {
        Task<ExternalOpenResponse> operation = await mainWindow.Dispatcher.InvokeAsync(() =>
            mainWindow.OpenExternalResourceAsync(request));
        ExternalOpenResponse response = await operation;
        if (!response.Success && !response.WasCancelled)
        {
            await mainWindow.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    mainWindow,
                    response.Message,
                    "Open Surf2 resource",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
        }

        return response;
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            AppThemeService.ApplyWindowChrome(window);
        }
    }
}
