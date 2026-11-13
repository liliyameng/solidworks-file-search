using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SwSearch.Contracts;

namespace SwSearch.App;

public partial class App : System.Windows.Application
{
    private const string AppMutexName = @"Local\SwSearch.App.v1";
    private const string ActivationPipeName = "swsearch.app.activation.v1";
    private readonly CancellationTokenSource _lifetime = new();
    private Mutex? _singleInstance;
    private Task? _activationListener;
    private EventWaitHandle? _installerShutdownEvent;
    private EventWaitHandle? _installerShutdownAcknowledgedEvent;
    private Task? _installerShutdownListener;
    private BackgroundSettingsLoader? _settingsLoader;
    private MainWindow? _mainWindow;
    private AppTrayController? _tray;
    private BrokerSearchClient? _brokerClient;
    private readonly AppExplicitExitCoordinator _explicitExitCoordinator;
    private Task? _explicitUserExitTask;
    private bool _failureHooksAttached;

    public App()
    {
        _explicitExitCoordinator = new AppExplicitExitCoordinator(
            ContractAppExitSessionMarker.Instance,
            ContractGracefulBrokerShutdownRequester.Instance,
            suppressBrokerRecoveryAfterMarker: SuppressBrokerRecoveryAfterExplicitExit);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AttachFailureDiagnostics();
        if (AppIsolatedOperationHost.IsInvocation(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunIsolatedOperationAndExitAsync(e.Args);
            return;
        }
        var scopeFolder = ReadScopeFolder(e.Args);
        var startHidden = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase) ||
                          e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);

        try
        {
            _singleInstance = new Mutex(true, AppMutexName, out var isOwner);
            if (!isOwner)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = ForwardActivationOrRecoverAsync(scopeFolder);
                return;
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ShowMainWindow(scopeFolder, listenForActivations: true, startHidden);
        }
        catch (Exception exception) when (AppBootstrapFailurePolicy.IsRecoverable(exception))
        {
            AppStartupDiagnostics.Record("bootstrap", exception);
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            var failure = AppBootstrapFailureWindow.Create();
            MainWindow = failure;
            failure.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifetime.Cancel();
        _tray?.Dispose();
        _settingsLoader?.Dispose();
        _installerShutdownEvent?.Dispose();
        _singleInstance?.Dispose();
        // This acknowledgement is deliberately emitted only after the tray,
        // WebView/window and single-instance resources have been torn down.
        // The installer still lets Restart Manager verify that no file handle
        // remains before InstallFiles starts.
        try
        {
            _installerShutdownAcknowledgedEvent?.Set();
        }
        catch (ObjectDisposedException)
        {
        }
        _installerShutdownAcknowledgedEvent?.Dispose();
        _lifetime.Dispose();
        base.OnExit(e);
    }

    private void StartInstallerShutdownListener()
    {
        _installerShutdownEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            ContractVersions.AppInstallerShutdownEventName);
        _installerShutdownAcknowledgedEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            ContractVersions.AppInstallerShutdownAcknowledgedEventName);
        _installerShutdownAcknowledgedEvent.Reset();
        _installerShutdownListener = Task.Run(() =>
        {
            var signaled = WaitHandle.WaitAny(
                new WaitHandle[] { _installerShutdownEvent, _lifetime.Token.WaitHandle });
            if (signaled == 0 && !_lifetime.IsCancellationRequested)
            {
                _ = Dispatcher.BeginInvoke((Action)RequestApplicationExit);
            }
        });
    }

    private async Task RunIsolatedOperationAndExitAsync(IReadOnlyList<string> args)
    {
        var exitCode = await AppIsolatedOperationHost.RunAsync(args, _lifetime.Token);
        await Dispatcher.InvokeAsync(() => Shutdown(exitCode));
    }

    private async Task ListenForActivationsAsync(MainWindow window, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var path = await reader.ReadLineAsync(cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!string.IsNullOrWhiteSpace(path)) window.ApplyFolderScope(path);
                    else
                    {
                        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                        window.RestoreFromTray();
                    }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
            }
            catch (Exception exception) when (AppBootstrapFailurePolicy.IsRecoverable(exception))
            {
                AppStartupDiagnostics.Record("activation-listener", exception);
                try
                {
                    await Task.Delay(250, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task ForwardActivationOrRecoverAsync(string? scopeFolder)
    {
        if (await AppActivationForwarder.TryForwardAsync(
                ActivationPipeName,
                scopeFolder,
                TimeSpan.FromMilliseconds(750),
                _lifetime.Token))
        {
            await Dispatcher.InvokeAsync(Shutdown);
            return;
        }

        AppStartupDiagnostics.Record(
            "single-instance-activation-timeout",
            new TimeoutException("The existing UI instance did not accept activation."));

        var recovered = await AppSingleInstanceRecovery.SignalAndTakeOwnershipAsync(
            _singleInstance!,
            ContractVersions.AppInstallerShutdownEventName,
            TimeSpan.FromSeconds(3),
            _lifetime.Token);
        if (recovered)
        {
            await Dispatcher.InvokeAsync(() => ShowMainWindow(scopeFolder, listenForActivations: true, startHidden: false));
            return;
        }

        await Dispatcher.InvokeAsync(ShowActivationFailureWindow);
    }

    private void ShowMainWindow(string? scopeFolder, bool listenForActivations, bool startHidden)
    {
        // A new App launch is the explicit way to restore Broker activity after
        // a prior tray Exit.  This is intentionally done only after this process
        // owns (or has recovered) the App single-instance lease.
        _explicitExitCoordinator.ClearForApplicationLaunch();
        var settingsStore = new JsonAppSettingsStore();
        var client = new BrokerSearchClient();
        _brokerClient = client;
        var viewModel = new MainViewModel(
            client,
            new WindowsFileActions(),
            settingsStore: settingsStore,
            initialSettings: AppSettings.Default);
        // Folder activations must establish their generation before MainWindow
        // subscribes Loaded/StartAsync.  This prevents a cold AllMachine request
        // (and its old result set) from ever being published under a folder token.
        if (scopeFolder is not null)
        {
            viewModel.ApplyFolderScope(scopeFolder);
        }
        var window = new MainWindow(viewModel);
        _mainWindow = window;
        MainWindow = window;
        StartInstallerShutdownListener();
        _tray = new AppTrayController(window, RequestExplicitUserExit);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_brokerClient, client))
            {
                _brokerClient = null;
            }
            _tray?.Dispose();
            _tray = null;
            if (!_lifetime.IsCancellationRequested) Shutdown();
        };
        // Arm the activation pipe before the first Show/Layout pass. A second
        // launch can therefore wake this instance even while WebView is still
        // completing its asynchronous initialization.
        if (listenForActivations)
        {
            _activationListener = ListenForActivationsAsync(window, _lifetime.Token);
        }
        window.Show();
        var settingsGeneration = viewModel.UserStateGeneration;
        _settingsLoader = new BackgroundSettingsLoader(
            settingsStore.Load,
            Dispatcher,
            window.ApplyLoadedSettings,
            () => window.CanApplyBackgroundSettings && viewModel.UserStateGeneration == settingsGeneration);
        _settingsLoader.Start();
        if (startHidden && scopeFolder is null)
        {
            _ = Dispatcher.BeginInvoke(window.HideToTray, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    private void RequestApplicationExit()
    {
        if (_mainWindow is { } window)
        {
            window.RequestProcessExit();
            return;
        }
        Shutdown();
    }

    private void RequestExplicitUserExit()
    {
        // The tray menu is the only ordinary UI path that should publish the
        // session marker and stop the Broker. Window close/minimize continues
        // to hide to tray, and installer shutdown remains a separate protocol.
        _explicitUserExitTask ??= CompleteExplicitUserExitAsync();
    }

    private void SuppressBrokerRecoveryAfterExplicitExit()
    {
        _brokerClient?.SuppressBrokerRecoveryForExplicitExit();
    }

    private async Task CompleteExplicitUserExitAsync()
    {
        await _explicitExitCoordinator.RequestExitAsync();
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync((Action)RequestApplicationExit);
        }
        catch (Exception exception) when (AppBootstrapFailurePolicy.IsRecoverable(exception))
        {
            // The exit request has already been persisted.  Do not spin or
            // force-terminate if WPF is concurrently tearing down.
            AppStartupDiagnostics.Record("app-explicit-exit-dispatch", exception);
        }
    }

    private void ShowActivationFailureWindow()
    {
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        var failure = AppBootstrapFailureWindow.CreateSingleInstanceFailure();
        MainWindow = failure;
        failure.Show();
    }

    private void AttachFailureDiagnostics()
    {
        if (_failureHooksAttached) return;
        _failureHooksAttached = true;
        DispatcherUnhandledException += (_, args) =>
            AppStartupDiagnostics.Record("dispatcher-unhandled", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                AppStartupDiagnostics.Record("appdomain-unhandled", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppStartupDiagnostics.Record("task-unobserved", args.Exception);
    }

    public static string? ReadScopeFolder(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--scope-folder", StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count)
            {
                return null;
            }
            return SearchPath.NormalizeFolder(args[index + 1]);
        }
        return null;
    }
}
