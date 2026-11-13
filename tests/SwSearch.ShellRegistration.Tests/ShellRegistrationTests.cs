using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SwSearch.ShellRegistration.Tests;

[TestClass]
public sealed class ShellRegistrationTests
{
    private const string ViewerPath = @"C:\Program Files\SwSearch\SwSearch.PropertyViewer.exe";

    [TestMethod]
    public void Plan_ContainsOnlyPartAndAssemblyVerbsAndQuotedDataArgument()
    {
        var plan = RegistrationPlanFactory.Create(ViewerPath, requireFileExists: false);

        CollectionAssert.AreEqual(new[] { ".sldprt", ".sldasm" }, RegistrationPlanFactory.Extensions);
        Assert.AreEqual(2, plan.OwnedVerbKeys.Count);
        Assert.AreEqual(10, plan.Writes.Count);
        Assert.AreEqual("\"C:\\Program Files\\SwSearch\\SwSearch.PropertyViewer.exe\" \"%1\"", plan.CommandValue);
        Assert.IsTrue(plan.OwnedVerbKeys.All(key => key.EndsWith(@"\shell\SwSearch.ViewProperties", StringComparison.Ordinal)));
        Assert.IsFalse(plan.OwnedVerbKeys.Any(key => key.Contains(".slddrw", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(plan.Writes.Any(write => write.KeyPath.Contains("CLSID", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void InstallStatusUninstall_AreSymmetricAndPreserveUnrelatedKeys()
    {
        var registry = new MemoryRegistryStore();
        const string unrelated = @"Software\Classes\SystemFileAssociations\.sldprt\shell\Another.Product";
        registry.SetValue(unrelated, string.Empty, "保留我");
        var service = new ShellRegistrationService(registry);
        var plan = RegistrationPlanFactory.Create(ViewerPath, requireFileExists: false);

        service.Install(plan);
        var installed = service.GetStatus(ViewerPath);
        service.Uninstall();
        var removed = service.GetStatus(ViewerPath);

        Assert.IsTrue(installed.IsFullyInstalled);
        Assert.IsFalse(removed.IsFullyInstalled);
        Assert.AreEqual("保留我", registry.GetValue(unrelated, string.Empty));
        Assert.IsTrue(plan.OwnedVerbKeys.All(key => !registry.KeyExists(key)));
    }

    [TestMethod]
    public void InstallAndUninstall_RemoveOnlyTheLegacyDrawingProductVerb()
    {
        var registry = new MemoryRegistryStore();
        var service = new ShellRegistrationService(registry);
        var plan = RegistrationPlanFactory.Create(ViewerPath, requireFileExists: false);
        var legacyVerb = RegistrationPlanFactory.LegacyDrawingVerbKey;
        const string unrelatedDrawingVerb =
            @"Software\Classes\SystemFileAssociations\.slddrw\shell\Another.Product";
        registry.SetValue(legacyVerb, string.Empty, "Legacy SwSearch value");
        registry.SetValue(legacyVerb + @"\command", string.Empty, "legacy-command");
        registry.SetValue(unrelatedDrawingVerb, string.Empty, "Keep me");

        service.Install(plan);

        Assert.IsFalse(registry.KeyExists(legacyVerb));
        Assert.AreEqual("Keep me", registry.GetValue(unrelatedDrawingVerb, string.Empty));
        Assert.IsFalse(service.GetStatus(ViewerPath).Extensions.Any(item =>
            string.Equals(item.Extension, ".slddrw", StringComparison.OrdinalIgnoreCase)));

        registry.SetValue(legacyVerb, string.Empty, "Legacy SwSearch value again");
        service.Uninstall();

        Assert.IsFalse(registry.KeyExists(legacyVerb));
        Assert.AreEqual("Keep me", registry.GetValue(unrelatedDrawingVerb, string.Empty));
    }

    [TestMethod]
    public void Status_RejectsTamperedOrUnquotedCommand()
    {
        var registry = new MemoryRegistryStore();
        var service = new ShellRegistrationService(registry);
        var plan = RegistrationPlanFactory.Create(ViewerPath, requireFileExists: false);
        service.Install(plan);
        var commandKey = plan.OwnedVerbKeys[0] + @"\command";
        registry.SetValue(commandKey, string.Empty, @"C:\Program Files\SwSearch\SwSearch.PropertyViewer.exe %1");

        var status = service.GetStatus();

        Assert.IsFalse(status.IsFullyInstalled);
        Assert.IsFalse(status.Extensions[0].IsInstalled);
    }

    [TestMethod]
    public void InvalidViewerPath_IsRejectedBeforeRegistryMutation()
    {
        Assert.ThrowsException<ArgumentException>(() => RegistrationPlanFactory.Create("relative.exe", requireFileExists: false));
        Assert.ThrowsException<ArgumentException>(() => RegistrationPlanFactory.Create(@"C:\Viewer.dll", requireFileExists: false));
    }

    private sealed class MemoryRegistryStore : IRegistryStore
    {
        private readonly Dictionary<(string Key, string Name), string> _values = new(new RegistryTupleComparer());

        public void SetValue(string keyPath, string valueName, string value) => _values[(keyPath, valueName)] = value;

        public string? GetValue(string keyPath, string valueName) =>
            _values.TryGetValue((keyPath, valueName), out var value) ? value : null;

        public bool KeyExists(string keyPath) => _values.Keys.Any(item =>
            string.Equals(item.Key, keyPath, StringComparison.OrdinalIgnoreCase) ||
            item.Key.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase));

        public void DeleteTree(string keyPath)
        {
            var keys = _values.Keys.Where(item =>
                string.Equals(item.Key, keyPath, StringComparison.OrdinalIgnoreCase) ||
                item.Key.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var key in keys) _values.Remove(key);
        }

        private sealed class RegistryTupleComparer : IEqualityComparer<(string Key, string Name)>
        {
            public bool Equals((string Key, string Name) x, (string Key, string Name) y) =>
                StringComparer.OrdinalIgnoreCase.Equals(x.Key, y.Key) && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
            public int GetHashCode((string Key, string Name) obj) =>
                HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
        }
    }
}
