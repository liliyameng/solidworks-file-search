using SwSearch.Metadata.Worker;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class KeyConfigurationCliTests
{
    [Fact]
    public async Task ConfigureKey_RequiresMatchingInteractiveSecretsAndNeverTakesKeyArgument()
    {
        var store = new FakeStore();
        var output = new StringWriter();
        var error = new StringWriter();
        var result = await KeyConfigurationCli.TryRunAsync(
            ["--configure-key"],
            store,
            new FakeSecretConsole("legal-key", "legal-key"),
            output,
            error);

        Assert.Equal(0, result);
        Assert.True(store.Saved);
        Assert.DoesNotContain("legal-key", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("legal-key", error.ToString(), StringComparison.Ordinal);
        Assert.Null(await KeyConfigurationCli.TryRunAsync(
            ["--configure-key", "must-not-be-accepted"],
            store,
            new FakeSecretConsole(),
            output,
            error));
    }

    [Fact]
    public async Task ConfigureKey_MismatchDoesNotWriteCredential()
    {
        var store = new FakeStore();
        var result = await KeyConfigurationCli.TryRunAsync(
            ["--configure-key"],
            store,
            new FakeSecretConsole("one", "two"),
            new StringWriter(),
            new StringWriter());

        Assert.Equal(1, result);
        Assert.False(store.Saved);
    }

    [Fact]
    public async Task StatusAndRemove_UseCredentialStoreWithoutDisplayingSecret()
    {
        var store = new FakeStore { Exists = true };
        var output = new StringWriter();
        Assert.Equal(0, await KeyConfigurationCli.TryRunAsync(
            ["--key-status"], store, new FakeSecretConsole(), output, new StringWriter()));
        Assert.DoesNotContain("configured-secret", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await KeyConfigurationCli.TryRunAsync(
            ["--remove-key"], store, new FakeSecretConsole(), output, new StringWriter()));
        Assert.False(store.Exists);
    }

    private sealed class FakeSecretConsole(params string[] values) : ISecretConsole
    {
        private int _index;
        public char[] ReadSecret(string prompt) => values[_index++].ToCharArray();
    }

    private sealed class FakeStore : IDocumentManagerCredentialStore
    {
        public bool Exists { get; set; }
        public bool Saved { get; private set; }

        public ValueTask<DocumentManagerKey?> TryGetKeyAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<DocumentManagerKey?>(
                Exists ? new DocumentManagerKey("configured-secret") : null);

        public ValueTask SaveKeyAsync(DocumentManagerKey key, CancellationToken cancellationToken)
        {
            Saved = true;
            Exists = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteKeyAsync(CancellationToken cancellationToken)
        {
            var existed = Exists;
            Exists = false;
            return ValueTask.FromResult(existed);
        }
    }
}
