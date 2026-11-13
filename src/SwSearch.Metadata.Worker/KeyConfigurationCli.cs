namespace SwSearch.Metadata.Worker;

public interface ISecretConsole
{
    char[] ReadSecret(string prompt);
}

public sealed class InteractiveSecretConsole : ISecretConsole
{
    public char[] ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("为避免许可证进入管道或命令历史，必须在交互式终端中输入。");
        }

        Console.Write(prompt);
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return characters.ToArray();
            }

            if (key.Key == ConsoleKey.Backspace && characters.Count > 0)
            {
                characters.RemoveAt(characters.Count - 1);
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }
    }
}

public static class KeyConfigurationCli
{
    public static async Task<int?> TryRunAsync(
        string[] args,
        IDocumentManagerCredentialStore store,
        ISecretConsole secretConsole,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 1 || args[0] is not ("--configure-key" or "--key-status" or "--remove-key"))
        {
            return null;
        }

        try
        {
            switch (args[0])
            {
                case "--key-status":
                    using (var key = await store.TryGetKeyAsync(cancellationToken).ConfigureAwait(false))
                    {
                        output.WriteLine(key is null
                            ? "未配置 Document Manager 许可证（SWDM_KEY_MISSING）。"
                            : "已在当前用户 Windows Credential Manager 中配置许可证；内容不会显示。");
                        return key is null ? 2 : 0;
                    }

                case "--remove-key":
                    output.WriteLine(await store.DeleteKeyAsync(cancellationToken).ConfigureAwait(false)
                        ? "已删除当前用户的 Document Manager 许可证凭据。"
                        : "未找到 Document Manager 许可证凭据，无需删除。");
                    return 0;

                case "--configure-key":
                    var first = secretConsole.ReadSecret("请输入合法的 Document Manager key（输入不回显）：");
                    var second = secretConsole.ReadSecret("请再次输入确认：");
                    try
                    {
                        if (first.Length == 0 || !first.AsSpan().SequenceEqual(second))
                        {
                            error.WriteLine("两次输入不一致或为空；未保存任何凭据。");
                            return 1;
                        }

                        using var key = new DocumentManagerKey(first);
                        await store.SaveKeyAsync(key, cancellationToken).ConfigureAwait(false);
                        output.WriteLine("许可证已保存到当前用户 Windows Credential Manager；未写入命令行、日志或仓库。");
                        return 0;
                    }
                    finally
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                            System.Runtime.InteropServices.MemoryMarshal.AsBytes(first.AsSpan()));
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                            System.Runtime.InteropServices.MemoryMarshal.AsBytes(second.AsSpan()));
                    }
            }
        }
        catch (Exception exception)
        {
            error.WriteLine($"许可证配置失败：{exception.Message}");
            return 1;
        }

        return null;
    }
}
