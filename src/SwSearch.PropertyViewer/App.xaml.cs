using System.Windows;
using SwSearch.Contracts;

namespace SwSearch.PropertyViewer;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loadTask;
    private EventWaitHandle? _installerShutdownEvent;
    private Task? _installerShutdownListener;

    protected override void OnStartup(StartupEventArgs e)
    {
        var startupClock = System.Diagnostics.Stopwatch.StartNew();
        PropertyViewerPerformanceDiagnostics.Record("process-start", 0);
        base.OnStartup(e);
        try
        {
            var fileSystem = new PhysicalViewerFileSystem();
            var validation = e.Args.Length == 1
                ? PropertyPathValidator.ValidateSyntax(e.Args[0])
                : PathValidationResult.Invalid("必须且只能传入一个 SOLIDWORKS 文件路径。", "INVALID_ARGUMENT_COUNT");
            var viewModel = new NativePropertyViewModel(
                validation,
                fileSystem,
                new BrokerPropertyDataClient(),
                propertyDisplayOrderLoader: static () => PropertyDisplayOrderReader.Read());
            var window = new NativePropertyWindow(viewModel);
            MainWindow = window;
            StartInstallerShutdownListener();
            window.Show();
            PropertyViewerPerformanceDiagnostics.Record("window-shown", startupClock.Elapsed.TotalMilliseconds);
            _loadTask = ObserveLoadAsync(viewModel, _lifetime.Token);
        }
        catch (Exception exception)
        {
            PropertyViewerPerformanceDiagnostics.Record(
                "startup-failure", startupClock.Elapsed.TotalMilliseconds,
                exception.GetType().Name + ":" + exception.Message);
            MessageBox.Show(
                "SOLIDWORKS 属性查看器无法启动。错误已写入本地诊断日志。\r\n\r\n" +
                exception.GetType().Name,
                "查看 SOLIDWORKS 属性",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    private static async Task ObserveLoadAsync(
        NativePropertyViewModel viewModel,
        CancellationToken cancellationToken)
    {
        try
        {
            await viewModel.LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PropertyViewerPerformanceDiagnostics.Record(
                "load-unhandled", 0, exception.GetType().Name + ":" + exception.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifetime.Cancel();
        _installerShutdownEvent?.Dispose();
        _lifetime.Dispose();
        base.OnExit(e);
    }

    private void StartInstallerShutdownListener()
    {
        _installerShutdownEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            ContractVersions.PropertyViewerInstallerShutdownEventName);
        _installerShutdownListener = Task.Run(() =>
        {
            var signaled = WaitHandle.WaitAny(
                new WaitHandle[] { _installerShutdownEvent, _lifetime.Token.WaitHandle });
            if (signaled == 0 && !_lifetime.IsCancellationRequested)
            {
                _ = Dispatcher.BeginInvoke((Action)(() => Shutdown()));
            }
        });
    }
}
