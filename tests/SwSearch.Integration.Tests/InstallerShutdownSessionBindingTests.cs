using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using SwSearch.Broker;
using SwSearch.Contracts;
using Xunit;

namespace SwSearch.Integration.Tests;

public sealed class InstallerShutdownSessionBindingTests
{
    [Fact]
    public void BrokerAndWorkerPipeNamesAreSessionSpecificAndNeverFallBackToFixedGlobalName()
    {
        var current = Process.GetCurrentProcess().SessionId;
        Assert.Equal($"swsearch.broker.v1.session.{current}", ContractVersions.BrokerPipeName);
        Assert.Equal($"swsearch.metadata.v1.session.{current}", ContractVersions.MetadataPipeName);
        Assert.NotEqual(
            ContractVersions.BrokerPipeNameForSession(current),
            ContractVersions.BrokerPipeNameForSession(OtherSession(current)));
        Assert.NotEqual("swsearch.broker.v1", ContractVersions.BrokerPipeName);
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractVersions.BrokerPipeNameForSession(-1));
    }

    [Fact]
    public void ProductionChallengeRejectsWrongSessionArbitraryNonceAndCrossComponentBinding()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = Assert.IsType<string>(identity.User?.Value);
        var session = Process.GetCurrentProcess().SessionId;
        var nonce = Guid.NewGuid();
        var challenge = ShutdownForUpdateIdentity.CreateChallenge(
            new ShutdownForUpdateChallengeRequest(sid, session, nonce));
        Assert.NotNull(challenge);

        var version = Assembly.GetEntryAssembly()!
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        var root = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var valid = new ShutdownForUpdateRequest(
            root,
            version,
            sid,
            session,
            nonce,
            challenge!.ChallengeId);
        Assert.True(ShutdownForUpdateIdentity.MatchesCurrentProcess(valid, challenge));
        Assert.False(ShutdownForUpdateIdentity.MatchesCurrentProcess(
            valid with { ExpectedSessionId = OtherSession(session) }, challenge));
        Assert.False(ShutdownForUpdateIdentity.MatchesCurrentProcess(
            valid with { Nonce = Guid.NewGuid() }, challenge));
        Assert.False(ShutdownForUpdateIdentity.MatchesCurrentProcess(
            valid with { ChallengeId = Guid.NewGuid() }, challenge));
        Assert.Null(ShutdownForUpdateIdentity.CreateChallenge(
            new ShutdownForUpdateChallengeRequest(sid, OtherSession(session), nonce)));
        Assert.Null(ShutdownForUpdateIdentity.CreateChallenge(
            new ShutdownForUpdateChallengeRequest(sid, session, Guid.Empty)));
    }

    [Fact]
    public void UnsafeNonceOnlyMatcherIsCaughtByTheProductionNegativeMatrix()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User!.Value;
        var session = Process.GetCurrentProcess().SessionId;
        var arbitrary = new ShutdownForUpdateRequest(
            AppContext.BaseDirectory,
            "1.2.0",
            sid,
            OtherSession(session),
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.True(UnsafeNonceOnlyMatcher(arbitrary));
        Assert.False(ShutdownForUpdateIdentity.MatchesCurrentProcess(arbitrary, null));
    }

    private static bool UnsafeNonceOnlyMatcher(ShutdownForUpdateRequest request) =>
        request.Nonce != Guid.Empty;

    private static int OtherSession(int session) => session == int.MaxValue ? session - 1 : session + 1;

    [Theory]
    [InlineData("{")]
    [InlineData("{\"protocolVersion\":2")]
    public async Task BrokerProductionReaderRejectsMalformedOrTruncatedJson(string payload)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await BrokerHost.ReadRequestAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task BrokerProductionReaderRejectsOversizedControlJson()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1)));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await BrokerHost.ReadRequestAsync(reader, CancellationToken.None));
    }
}
