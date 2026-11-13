using SwSearch.InstallerCoordinator;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class InstallerMsiContextContractTests
{
    private const string UpgradeCode = "{D9158356-29B3-44D6-A15A-ACDCE8328703}";
    private const string UserSid = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string Nonce = "87D8EC95-3AD7-4806-885B-264101E6D332";
    private const int SessionId = 7;

    [Theory]
    [InlineData("install", 0)]
    [InlineData("upgrade", 1)]
    [InlineData("repair", 2)]
    [InlineData("uninstall", 3)]
    public void CustomActionData_ExactSchemaRoundTripsEachInstallOperation(
        string operation,
        int expected)
    {
        var parsed = InstallerCustomActionData.TryParse(
            ValidCustomActionData(operation), out var command, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(command);
        Assert.Equal(InstallerCoordinatorMode.PrivilegedHandle, command.Mode);
        Assert.Equal(UpgradeCode, command.UpgradeCode);
        Assert.Equal(UserSid, command.InstallUserSid);
        Assert.Equal(SessionId, command.InstallSessionId);
        Assert.Equal(Guid.Parse(Nonce), command.InstanceNonce);
        Assert.Equal(InstallerContextContract.RegisteredRootPolicy, command.CurrentInstallRoot);
        Assert.Equal((InstallerOperation)expected, command.Operation);
        Assert.Equal(30000L, command.DeadlineTickCount64);
    }

    [Theory]
    [MemberData(nameof(InvalidCustomActionData))]
    public void CustomActionData_MalformedUntrustedOrAmbiguousInputFailsClosed(string value)
    {
        Assert.False(InstallerCustomActionData.TryParse(value, out var command, out _));
        Assert.Null(command);
    }

    [Fact]
    public void PrivilegedCli_AcceptsOnlyOneOpaqueCustomActionDataArgument()
    {
        Assert.True(InstallerCoordinatorCommandLine.TryParse(
            ["--privileged-handle", "--custom-action-data", ValidCustomActionData("repair")],
            out var command,
            out var error), error);
        Assert.Equal(InstallerCoordinatorMode.PrivilegedHandle, command!.Mode);

        foreach (var unsafeArguments in new[]
        {
            new[] { "--privileged-handle", "--custom-action-data", ValidCustomActionData("repair"), "--user-ipc" },
            new[] { "--privileged-handle", "--upgrade-code", UpgradeCode },
            new[] { "--privileged-handle", "--custom-action-data", ValidCustomActionData("repair"), "--install-user-sid", UserSid },
            new[] { "--custom-action-data", ValidCustomActionData("repair") }
        })
        {
            Assert.False(InstallerCoordinatorCommandLine.TryParse(unsafeArguments, out _, out _));
        }
    }

    [Fact]
    public void UserCli_RequiresExactBridgeSidNoncePolicyOperationAndDeadline()
    {
        var valid = ValidUserArguments();
        Assert.True(InstallerCoordinatorCommandLine.TryParse(valid, out var command, out var error), error);
        Assert.Equal(InstallerCoordinatorMode.UserIpc, command!.Mode);
        Assert.Equal(UserSid, command.InstallUserSid);
        Assert.Equal(SessionId, command.InstallSessionId);
        Assert.Equal(Guid.Parse(Nonce), command.InstanceNonce);

        foreach (var replacement in new[]
        {
            ("--install-user-sid", "S-1-5-18"),
            ("--install-session-id", "-1"),
            ("--instance-nonce", Guid.Empty.ToString("D")),
            ("--instance-nonce", "11111111-1111-1111-1111-111111111111"),
            ("--current-root", @"C:\Program Files\SwSearch"),
            ("--install-action", "replace"),
            ("--deadline-tick", "0")
        })
        {
            Assert.False(InstallerCoordinatorCommandLine.TryParse(
                Replace(valid, replacement.Item1, replacement.Item2), out _, out _));
        }
    }

    [Fact]
    public async Task CurrentUserIpc_PassesOneBridgeContextToBrokerAndWorkerWithoutRegeneration()
    {
        var context = new ShutdownRequestContext(UserSid, SessionId, Guid.Parse(Nonce));
        var transport = new RecordingTransport();
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            new OneProductCatalog(), transport, new NoDelay(), new NullLog());

        var outcome = await coordinator.ShutdownAsync(
            InstallerCoordinatorOptions.CreateDefault(UpgradeCode, InstallerContextContract.RegisteredRootPolicy),
            context,
            CancellationToken.None);

        Assert.Equal(ShutdownRequestResult.Accepted, outcome.Broker);
        Assert.Equal(ShutdownRequestResult.Unsupported, outcome.Worker);
        Assert.Equal(2, transport.Contexts.Count);
        Assert.All(transport.Contexts, actual => Assert.Equal(context, actual));
    }

    [Fact]
    public void NativeBridgeSource_AllowsOnlyNoThreadTokenProcessFallbackAndKeepsDiagnosticsNonSensitive()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SwSearch.InstallerMsiBridge", "InstallerMsiBridge.cpp"));

        Assert.Contains("OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE", source, StringComparison.Ordinal);
        Assert.Contains("ShouldAttemptProcessToken(threadError)", source, StringComparison.Ordinal);
        Assert.Contains("return threadTokenError == ERROR_NO_TOKEN", source, StringComparison.Ordinal);
        Assert.Contains("OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY", source, StringComparison.Ordinal);
        Assert.Contains("TokenImpersonation", source, StringComparison.Ordinal);
        Assert.Contains("TokenPrimary", source, StringComparison.Ordinal);
        Assert.Contains("TokenIsAppContainer", source, StringComparison.Ordinal);
        Assert.Contains("ProcessIdToSessionId(GetCurrentProcessId()", source, StringComparison.Ordinal);
        Assert.Contains("SECURITY_SERVICE_ID_BASE_RID", source, StringComparison.Ordinal);
        Assert.Contains("BCryptGenRandom", source, StringComparison.Ordinal);
        Assert.Contains("bytes[6] = static_cast<unsigned char>((bytes[6] & 0x0F) | 0x40)", source, StringComparison.Ordinal);
        Assert.Contains("bytes[8] = static_cast<unsigned char>((bytes[8] & 0x3F) | 0x80)", source, StringComparison.Ordinal);
        Assert.Contains("MsiSetPropertyW", source, StringComparison.Ordinal);
        Assert.Contains("SecureZeroMemory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MsiGetPropertyW(session, kSidProperty", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MsiGetPropertyW(session, kNonceProperty", source, StringComparison.Ordinal);
        Assert.Contains("TokenSessionId", source, StringComparison.Ordinal);
        Assert.Contains("returnedSession > INT_MAX", source, StringComparison.Ordinal);
        Assert.Contains("sessionId = returnedSession", source, StringComparison.Ordinal);
        Assert.Contains("MsiProcessMessage", source, StringComparison.Ordinal);
        Assert.Contains("SwSearch installer context diagnostic:", source, StringComparison.Ordinal);
        var logging = ExtractLogReasonSource(source);
        foreach (var sensitiveValue in new[]
                 {
                     "sid.c_str", "sessionText", "nonce.c_str", "kSidProperty",
                     "kSessionProperty", "kNonceProperty", "CustomActionData", "kRootPolicyProperty"
                 })
        {
            Assert.DoesNotContain(sensitiveValue, logging, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("FailureCode(reason)", logging, StringComparison.Ordinal);
        Assert.DoesNotContain("%s", logging, StringComparison.Ordinal);
        Assert.DoesNotContain("%d", logging, StringComparison.Ordinal);
        foreach (var fixedReason in new[]
                 {
                     "THREAD_TOKEN_ABSENT", "THREAD_TOKEN_TYPE_INVALID",
                     "THREAD_TOKEN_IMPERSONATION_LEVEL_INVALID", "TOKEN_USER_QUERY_FAILED",
                     "TOKEN_SESSION_QUERY_FAILED", "TOKEN_SID_INVALID",
                     "PROCESS_TOKEN_VALIDATION_FAILED", "NONCE_GENERATION_FAILED",
                     "INSTALL_ACTION_INVALID", "PRIVATE_PROPERTY_WRITE_FAILED", "UNEXPECTED_FAILURE"
                 })
        {
            Assert.Contains(fixedReason, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProductionShutdownGraphRequiresSessionPipeAndSameConnectionChallengeOnBothServers()
    {
        var root = FindRepositoryRoot();
        var transport = File.ReadAllText(Path.Combine(
            root, "src", "SwSearch.InstallerCoordinator", "ProductShutdownTransport.cs"));
        var contracts = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Contracts", "Contracts.cs"));
        var broker = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Broker", "BrokerHost.cs"));
        var worker = File.ReadAllText(Path.Combine(root, "src", "SwSearch.Metadata.Worker", "MetadataPipeHost.cs"));

        Assert.Empty(SessionBindingErrors(transport, contracts, broker, worker));

        var unsafeNonceOnly = contracts.Replace(
            "!FixedTimeGuidEquals(request.ChallengeId, challenge.ChallengeId)",
            "false",
            StringComparison.Ordinal);
        Assert.NotEmpty(SessionBindingErrors(transport, unsafeNonceOnly, broker, worker));
        var unsafeNonEmptyNonce = contracts.Replace(
            "!IsCryptographicSessionNonce(request.Nonce)",
            "request.Nonce == Guid.Empty",
            StringComparison.Ordinal);
        Assert.NotEmpty(SessionBindingErrors(transport, unsafeNonEmptyNonce, broker, worker));
        var unsafeFixedPipe = transport.Replace(
            "BrokerPipeNamePrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)",
            "\"swsearch.broker.v1\"",
            StringComparison.Ordinal);
        Assert.NotEmpty(SessionBindingErrors(unsafeFixedPipe, contracts, broker, worker));
        var unsafeBrokerNoChallenge = broker.Replace(
            "ShutdownForUpdateIdentity.CreateChallenge(request.ShutdownChallengeRequest)",
            "null",
            StringComparison.Ordinal);
        Assert.NotEmpty(SessionBindingErrors(transport, contracts, unsafeBrokerNoChallenge, worker));
    }

    public static IEnumerable<object[]> InvalidCustomActionData()
    {
        var valid = ValidCustomActionData("repair");
        yield return [string.Empty];
        yield return [new string('x', InstallerContextContract.MaximumCustomActionDataLength + 1)];
        yield return [valid + ";sid=" + UserSid];
        yield return [valid + ";unknown=value"];
        yield return [valid.Replace("sid=" + UserSid, "sid=S-1-5-18", StringComparison.Ordinal)];
        yield return [valid.Replace("session=" + SessionId, "session=-1", StringComparison.Ordinal)];
        yield return [valid.Replace("session=" + SessionId, "session=2147483648", StringComparison.Ordinal)];
        yield return [valid + ";session=" + SessionId];
        yield return [valid.Replace("nonce=" + Nonce, "nonce=00000000-0000-0000-0000-000000000000", StringComparison.Ordinal)];
        yield return [valid.Replace("nonce=" + Nonce, "nonce=11111111-1111-1111-1111-111111111111", StringComparison.Ordinal)];
        yield return [valid.Replace("root=" + InstallerContextContract.RegisteredRootPolicy, "root=C%3A%5CSwSearch", StringComparison.Ordinal)];
        yield return [valid.Replace("action=repair", "action=REPAIR", StringComparison.Ordinal)];
        yield return [valid.Replace("deadline=30000", "deadline=-1", StringComparison.Ordinal)];
        yield return [valid.Replace("nonce=" + Nonce, "nonce=%Q0", StringComparison.Ordinal)];
        yield return [valid.Replace("nonce=" + Nonce, "nonce=%3B", StringComparison.Ordinal)];
        yield return [valid.Replace("nonce=" + Nonce, "nonce=%3D", StringComparison.Ordinal)];
        yield return [valid.Replace("v=2", "v=1", StringComparison.Ordinal)];
        yield return [valid.Replace("user=1", "user=2", StringComparison.Ordinal)];
        yield return [valid.Replace(";deadline=30000", string.Empty, StringComparison.Ordinal)];
        yield return [valid.Replace($";session={SessionId}", string.Empty, StringComparison.Ordinal)];
    }

    private static string ValidCustomActionData(string operation) =>
        $"v=2;user=1;sid={UserSid};session={SessionId};upgrade={UpgradeCode};nonce={Nonce};" +
        $"root={InstallerContextContract.RegisteredRootPolicy};action={operation};deadline=30000";

    private static string[] ValidUserArguments() =>
    [
        "--user-ipc",
        "--upgrade-code", UpgradeCode,
        "--current-root", InstallerContextContract.RegisteredRootPolicy,
        "--log", @"C:\Temp\SwSearch-Installer.log",
        "--install-user-sid", UserSid,
        "--install-session-id", SessionId.ToString(),
        "--instance-nonce", Nonce,
        "--install-action", "repair",
        "--deadline-tick", "30000"
    ];

    private static string[] Replace(string[] source, string name, string value)
    {
        var result = source.ToArray();
        result[Array.IndexOf(result, name) + 1] = value;
        return result;
    }

    private static string ExtractLogReasonSource(string source)
    {
        var start = source.IndexOf("void LogReason", StringComparison.Ordinal);
        var end = source.IndexOf("bool SetPrivateProperty", start, StringComparison.Ordinal);
        return source[start..end];
    }

    private static IReadOnlyList<string> SessionBindingErrors(
        string transport,
        string contracts,
        string broker,
        string worker)
    {
        var required = new (string Source, string Token)[]
        {
            (transport, "BrokerPipeNamePrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)"),
            (transport, "WorkerPipeNamePrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)"),
            (transport, "BrokerBeginShutdownOperation"),
            (transport, "beginShutdownForUpdate"),
            (transport, "PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly"),
            (contracts, "!FixedTimeGuidEquals(request.ChallengeId, challenge.ChallengeId)"),
            (contracts, "!IsCryptographicSessionNonce(request.Nonce)"),
            (contracts, "using var process = Process.GetCurrentProcess();"),
            (contracts, "process.SessionId == expectedSessionId"),
            (broker, "ShutdownForUpdateIdentity.CreateChallenge(request.ShutdownChallengeRequest)"),
            (broker, "ShutdownForUpdateIdentity.MatchesCurrentProcess(request.ShutdownForUpdate, shutdownChallenge)"),
            (worker, "ShutdownForUpdateIdentity.CreateChallenge(request.ShutdownChallengeRequest)"),
            (worker, "shutdownChallenge"),
            (worker, "PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly")
        };
        return required.Where(item => !item.Source.Contains(item.Token, StringComparison.Ordinal))
            .Select(item => item.Token)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "installer", "wix", "Package.wxs")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class OneProductCatalog : IInstalledProductCatalog
    {
        public IReadOnlyList<InstalledProductIdentity> GetRelatedProducts(
            string upgradeCode,
            string currentInstallRoot) =>
        [
            new InstalledProductIdentity("{OLD}", "1.2.0", @"C:\Program Files\SwSearch")
        ];
    }

    private sealed class RecordingTransport : IProductShutdownTransport
    {
        internal List<ShutdownRequestContext> Contexts { get; } = [];

        public ValueTask<ShutdownRequestResult> ShutdownBrokerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return ValueTask.FromResult(ShutdownRequestResult.Accepted);
        }

        public ValueTask<ShutdownRequestResult> ShutdownWorkerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return ValueTask.FromResult(ShutdownRequestResult.Unsupported);
        }
    }

    private sealed class NoDelay : ICoordinatorDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class NullLog : ICoordinatorLog
    {
        public void Write(string message)
        {
        }
    }
}
