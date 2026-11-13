using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc23StartupRecoveryTests
{
    [TestMethod]
    public async Task ActivationForwarder_ForwardsFolderToResponsivePrimary()
    {
        var pipeName = "swsearch-rc23-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var received = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, new UTF8Encoding(false));
            return await reader.ReadLineAsync();
        });

        var forwarded = await AppActivationForwarder.TryForwardAsync(
            pipeName,
            @"C:\models",
            TimeSpan.FromSeconds(1));

        Assert.IsTrue(forwarded);
        Assert.AreEqual(@"C:\models", await received.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task ActivationForwarder_UnresponsivePrimaryReturnsFalseWithinShortDeadline()
    {
        var stopwatch = Stopwatch.StartNew();
        var forwarded = await AppActivationForwarder.TryForwardAsync(
            "swsearch-rc23-missing-" + Guid.NewGuid().ToString("N"),
            null,
            TimeSpan.FromMilliseconds(120));

        Assert.IsFalse(forwarded);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
    }

    [TestMethod]
    public void SecondaryActivationFailure_OpensRecoveryUiInsteadOfSilentShutdown()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SwSearch.App",
            "App.xaml.cs"));

        StringAssert.Contains(source, "ForwardActivationOrRecoverAsync");
        StringAssert.Contains(source, "ShowMainWindow(scopeFolder, listenForActivations: false)");
        StringAssert.Contains(source, "single-instance-activation-timeout");
        Assert.IsFalse(source.Contains("finally\r\n        {\r\n            await Dispatcher.InvokeAsync(Shutdown)", StringComparison.Ordinal));
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
        throw new DirectoryNotFoundException();
    }
}
