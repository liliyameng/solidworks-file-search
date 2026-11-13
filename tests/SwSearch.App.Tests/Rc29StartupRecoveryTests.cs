using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc29StartupRecoveryTests
{
    [TestMethod]
    public void ProductionStartup_OwnerShowsWindow_ListenerPrecedesShow_AndFailureIsVisible()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SwSearch.App", "App.xaml.cs"));
        StringAssert.Contains(source, "if (!isOwner)");
        StringAssert.Contains(source, "ShowMainWindow(scopeFolder, listenForActivations: true, startHidden)");
        StringAssert.Contains(source, "AppSingleInstanceRecovery.SignalAndTakeOwnershipAsync");
        StringAssert.Contains(source, "ShowActivationFailureWindow");
        Assert.IsTrue(
            source.IndexOf("_activationListener = ListenForActivationsAsync", StringComparison.Ordinal) <
            source.IndexOf("window.Show();", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NonResponsivePrimary_ShutdownHandshakeTransfersOwnership()
    {
        var mutexName = @"Local\SwSearch.Rc29.Test." + Guid.NewGuid().ToString("N");
        var eventName = @"Local\SwSearch.Rc29.Shutdown." + Guid.NewGuid().ToString("N");
        using var ready = new ManualResetEventSlim();
        Exception? primaryFailure = null;
        var primary = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(true, mutexName, out var owner);
                using var shutdown = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
                Assert.IsTrue(owner);
                ready.Set();
                Assert.IsTrue(shutdown.WaitOne(TimeSpan.FromSeconds(2)));
                mutex.ReleaseMutex();
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
                ready.Set();
            }
        });
        primary.IsBackground = true;
        primary.Start();
        Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(2)));

        using var secondary = new Mutex(true, mutexName, out var initiallyOwned);
        Assert.IsFalse(initiallyOwned);
        var recovered = await AppSingleInstanceRecovery.SignalAndTakeOwnershipAsync(
            secondary, eventName, TimeSpan.FromSeconds(1));

        Assert.IsTrue(recovered);
        Assert.IsTrue(primary.Join(TimeSpan.FromSeconds(2)));
        Assert.IsNull(primaryFailure);
    }

    [TestMethod]
    public async Task BusyPrimaryWithoutShutdownEndpoint_FailsBoundedInsteadOfSilentHang()
    {
        var mutexName = @"Local\SwSearch.Rc29.Test." + Guid.NewGuid().ToString("N");
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var primary = new Thread(() =>
        {
            using var mutex = new Mutex(true, mutexName, out _);
            ready.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            mutex.ReleaseMutex();
        });
        primary.IsBackground = true;
        primary.Start();
        Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(2)));
        using var secondary = new Mutex(true, mutexName, out var secondaryOwner);
        Assert.IsFalse(secondaryOwner);

        var clock = Stopwatch.StartNew();
        var recovered = await AppSingleInstanceRecovery.SignalAndTakeOwnershipAsync(
            secondary,
            @"Local\SwSearch.Rc29.Missing." + Guid.NewGuid().ToString("N"),
            TimeSpan.FromMilliseconds(150));

        Assert.IsFalse(recovered);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(1), clock.Elapsed.ToString());
        release.Set();
        Assert.IsTrue(primary.Join(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task StartupDiagnostics_PersistsStageTypeHResultAndMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "swsearch-rc29-" + Guid.NewGuid().ToString("N"));
        var log = Path.Combine(root, "startup.log");
        try
        {
            AppStartupDiagnostics.Record("rc29-bootstrap", new InvalidOperationException("fixture failure"), log);
            var text = await ReadDiagnosticAsync(log, "rc29-bootstrap", TimeSpan.FromSeconds(2));
            StringAssert.Contains(text, "rc29-bootstrap");
            StringAssert.Contains(text, "System.InvalidOperationException");
            StringAssert.Contains(text, "0x80131509");
            StringAssert.Contains(text, "fixture failure");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task<string> ReadDiagnosticAsync(string path, string requiredText, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (text.Contains(requiredText, StringComparison.Ordinal)) return text;
                }
                catch (IOException)
                {
                }
            }
            await Task.Delay(20);
        }
        Assert.Fail("Startup diagnostic was not written within " + timeout + ".");
        return string.Empty;
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SolidWorksSearch.sln"))) return current.FullName;
        }
        throw new DirectoryNotFoundException();
    }
}
