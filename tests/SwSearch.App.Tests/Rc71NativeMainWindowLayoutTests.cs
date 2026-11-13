using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc71NativeMainWindowLayoutTests
{
    [TestMethod]
    public void MainWindow_KeepsToolbarResultsAndStatusInThreeIndependentRows()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SwSearch.App", "MainWindow.xaml"));

        StringAssert.Contains(xaml, "<RowDefinition Height=\"48\" />");
        StringAssert.Contains(xaml, "<RowDefinition Height=\"*\" />");
        StringAssert.Contains(xaml, "<RowDefinition Height=\"28\" />");
        StringAssert.Contains(xaml, "x:Name=\"WebToolbarContainer\" Grid.Row=\"0\"");
        StringAssert.Contains(xaml, "x:Name=\"NativeResultsContainer\" Grid.Row=\"1\"");
        StringAssert.Contains(xaml, "x:Name=\"NativeStatusContainer\" Grid.Row=\"2\"");
    }

    [TestMethod]
    public void ExplorerBrowser_IsAttachedOnlyAfterLoadedYieldsToIdleLayout()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SwSearch.App", "MainWindow.xaml.cs"));
        var constructorStart = source.IndexOf("public MainWindow(MainViewModel viewModel)", StringComparison.Ordinal);
        var constructorEnd = source.IndexOf("private NativeShellResultsHost CreateNativeShellResultsHost", StringComparison.Ordinal);
        Assert.IsTrue(constructorStart >= 0 && constructorEnd > constructorStart);
        var constructor = source[constructorStart..constructorEnd];
        Assert.IsFalse(
            constructor.Contains("NativeResultsContainer.Child", StringComparison.Ordinal),
            "ExplorerBrowser must not create its HWND while WPF is constructing the window.");

        var loadedStart = source.IndexOf("private async void MainWindow_OnLoaded", StringComparison.Ordinal);
        var loadedEnd = source.IndexOf("private void BridgeOnStateReady", loadedStart, StringComparison.Ordinal);
        Assert.IsTrue(loadedStart >= 0 && loadedEnd > loadedStart);
        var loaded = source[loadedStart..loadedEnd];
        var idleYield = loaded.IndexOf("Dispatcher.Yield(DispatcherPriority.ContextIdle)", StringComparison.Ordinal);
        var attach = loaded.IndexOf("AttachNativeResultsHostAfterLayout()", StringComparison.Ordinal);
        Assert.IsTrue(idleYield >= 0 && attach > idleYield,
            "The native Shell HWND must be attached only after one idle layout turn.");
        StringAssert.Contains(loaded, "Interlocked.Exchange(ref _loadedInitializationStarted, 1)");
    }

    [TestMethod]
    public void NativeResultsHost_ShowsInitializationSurfaceAndProvidesRealRecreateRetry()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SwSearch.App", "MainWindow.xaml.cs"));

        StringAssert.Contains(source, "Text = \"正在初始化原生文件视图…\"");
        StringAssert.Contains(source, "previous.BeginShutdown(result =>");
        StringAssert.Contains(source, "var replacement = CreateNativeShellResultsHost();");
        StringAssert.Contains(source, "_nativeShellResults = replacement;");
        StringAssert.Contains(source, "_nativeStatusText.Text = \"原生文件视图初始化失败。\"");
        StringAssert.Contains(source, "_nativeShellStatusRetryButton.Visible = true");
        StringAssert.Contains(source, "RetryNativeResultsHost()");
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
