using Microsoft.Win32.SafeHandles;
using SwSearch.InstallerCoordinator;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class InstallationCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("SwSearch.App.exe", "SwSearch.App", false)]
    [InlineData("SwSearch.App.exe", "SwSearch.App", true)]
    [InlineData("SwSearch.PropertyViewer.exe", "SwSearch.PropertyViewer", false)]
    [InlineData("SwSearch.PropertyViewer.exe", "SwSearch.PropertyViewer", true)]
    public async Task UiProcesses_AreNeverForceTerminated(
        string fileName,
        string assemblyStem,
        bool visibleWindow)
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(10, fileName, assemblyStem) with { HasInteractiveWindow = visibleWindow });

        var outcome = await fixture.RunAsync();

        Assert.Empty(outcome.Terminated);
        Assert.Empty(fixture.Processes.TerminationAttempts);
        Assert.Single(outcome.RemainingProductProcesses);
    }

    [Fact]
    public void UiTerminationTraceGate_FailsDeliberatelyUnsafeFixture()
    {
        var unsafeTrace = new[] { Owned(11, "SwSearch.App.exe", "SwSearch.App") };

        Assert.Throws<InvalidOperationException>(() => AssertNoUiTerminationTrace(unsafeTrace));
    }

    [Fact]
    public async Task Rc12UnsupportedShutdown_StopsVerifiedBrokerThenWorker()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(20, "SwSearch.Broker.exe", "SwSearch.Broker"));
        fixture.Processes.Add(Owned(21, "SwSearch.Metadata.Worker.exe", "SwSearch.Metadata.Worker"));

        var outcome = await fixture.RunAsync();

        Assert.Equal(
            new[] { "SwSearch.Broker.exe", "SwSearch.Metadata.Worker.exe" },
            outcome.Terminated.Select(process => process.FileName));
    }

    [Fact]
    public async Task SameNameOutsideInstalledRoot_IsIgnored()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(30, "SwSearch.Broker.exe", "SwSearch.Broker") with
        {
            ImagePath = @"D:\Unrelated\SwSearch.Broker.exe"
        });

        var outcome = await fixture.RunAsync();

        Assert.Empty(outcome.Terminated);
        Assert.Single(outcome.IgnoredUnverifiedProcesses);
    }

    [Fact]
    public async Task OldInstallRootDifferentFromNewRoot_IsAuthorizedByInstalledProduct()
    {
        var fixture = Fixture.Create(@"D:\旧版 SwSearch", @"C:\Program Files\SwSearch");
        fixture.Processes.Add(Owned(40, "SwSearch.Broker.exe", "SwSearch.Broker", @"D:\旧版 SwSearch"));

        var outcome = await fixture.RunAsync();

        Assert.Single(outcome.Terminated);
    }

    [Fact]
    public async Task ExitedProcessAndResidualPipe_DoNotBlockOrTerminate()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(50, "SwSearch.Broker.exe", "SwSearch.Broker"));
        fixture.Processes.ExitOnAcquire.Add(50);

        var outcome = await fixture.RunAsync();

        Assert.Empty(outcome.Terminated);
        Assert.Empty(outcome.RemainingProductProcesses);
    }

    [Fact]
    public async Task ProcessReplacementBetweenEvidenceAndTermination_IsNotKilled()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(60, "SwSearch.Metadata.Worker.exe", "SwSearch.Metadata.Worker"));
        fixture.Processes.ReplacementOnAcquire[60] = Owned(
            60,
            "SwSearch.Metadata.Worker.exe",
            "SwSearch.Metadata.Worker") with
        {
            StartTimeUtc = Start.AddHours(1),
            ProductName = "Unverified.Replacement"
        };

        var outcome = await fixture.RunAsync();

        Assert.Empty(outcome.Terminated);
        Assert.Empty(fixture.Processes.TerminationAttempts);
        Assert.Single(outcome.IgnoredUnverifiedProcesses);
    }

    [Fact]
    public async Task CurrentUserIpcOnly_SendsAllFourComponentsWithoutProcessCatalog()
    {
        var products = new FakeProducts(
            new InstalledProductIdentity("{OLD-PRODUCT}", "1.2.0", @"C:\Program Files\SwSearch"));
        var transport = new FakeTransport
        {
            BrokerResult = ShutdownRequestResult.Accepted,
            WorkerResult = ShutdownRequestResult.Unsupported
        };
        var coordinator = new CurrentUserIpcShutdownCoordinator(
            products,
            transport,
            new NoDelay(),
            new NullLog());

        var outcome = await coordinator.ShutdownAsync(
            new InstallerCoordinatorOptions(
                "{D9158356-29B3-44D6-A15A-ACDCE8328703}",
                @"C:\Program Files\SwSearch",
                TimeSpan.Zero),
            new ShutdownRequestContext(
                "S-1-5-21-111111111-222222222-333333333-1001",
                1,
                Guid.Parse("87D8EC95-3AD7-4806-885B-264101E6D332")),
            CancellationToken.None);

        Assert.Equal(1, outcome.TrustedProductCount);
        Assert.Equal(ShutdownRequestResult.Unavailable, outcome.App);
        Assert.Equal(ShutdownRequestResult.Unavailable, outcome.PropertyViewer);
        Assert.Equal(ShutdownRequestResult.Accepted, outcome.Broker);
        Assert.Equal(ShutdownRequestResult.Unsupported, outcome.Worker);
        Assert.Equal(new[] { "App", "PropertyViewer", "Broker", "Worker" }, transport.Calls);
    }

    [Fact]
    public async Task RepeatedRespawn_IsBoundedAndLeavesActualSurvivorForRestartManager()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(80, "SwSearch.Broker.exe", "SwSearch.Broker"));
        var next = 81;
        fixture.Processes.OnTerminate = process =>
        {
            if (next <= 83)
            {
                fixture.Processes.Add(Owned(next++, process.FileName, "SwSearch.Broker"));
            }
        };

        var outcome = await fixture.RunAsync();

        Assert.Equal(3, outcome.Terminated.Count);
        Assert.Single(outcome.RemainingProductProcesses);
    }

    [Fact]
    public async Task StableLease_AlreadyExited_IsSuccessWithoutTerminationRecord()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(90, "SwSearch.Broker.exe", "SwSearch.Broker"));
        fixture.Processes.TerminationResults[90] = SingleProcessTerminationResult.AlreadyExited;

        var outcome = await fixture.RunAsync();

        Assert.Empty(outcome.Terminated);
        Assert.Empty(outcome.RemainingProductProcesses);
        Assert.Single(fixture.Processes.TerminationAttempts);
    }

    [Fact]
    public async Task StableLease_FinalObjectChanged_IsNotTerminated()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(91, "SwSearch.Metadata.Worker.exe", "SwSearch.Metadata.Worker"));
        fixture.Processes.LeaseSnapshotOverrides[91] = Owned(
            91,
            "SwSearch.Metadata.Worker.exe",
            "SwSearch.Metadata.Worker") with { ImageObjectId = "DIFFERENT:FILE:OBJECT" };

        var outcome = await fixture.RunAsync();

        Assert.Empty(outcome.Terminated);
        Assert.Empty(fixture.Processes.TerminationAttempts);
    }

    [Fact]
    public async Task TerminatingBroker_DoesNotTerminateUnverifiedChild()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(92, "SwSearch.Broker.exe", "SwSearch.Broker"));
        fixture.Processes.Add(Owned(93, "Unrelated.Child.exe", "Unrelated.Child"));

        var outcome = await fixture.RunAsync();

        Assert.Single(outcome.Terminated);
        Assert.Equal(92, outcome.Terminated[0].ProcessId);
        Assert.True(fixture.Processes.Contains(93));
    }

    [Fact]
    public async Task MissingOrForgedRegisteredRoot_NeverAuthorizesTermination()
    {
        var processes = new FakeProcesses();
        processes.Add(Owned(94, "SwSearch.Broker.exe", "SwSearch.Broker", @"C:\Fake\SwSearch"));
        var coordinator = new InstallationCoordinator(
            new FakeProducts(new InstalledProductIdentity("{FORGED}", "1.2.0", @"C:\Fake\SwSearch", false)),
            processes,
            new NoDelay(),
            new NullLog());

        var outcome = await coordinator.CoordinateAsync(new InstallerCoordinatorOptions(
            "{D9158356-29B3-44D6-A15A-ACDCE8328703}",
            @"C:\Fake\SwSearch",
            TimeSpan.Zero,
            1));

        Assert.Empty(outcome.Terminated);
        Assert.Empty(processes.TerminationAttempts);
    }

    [Theory]
    [InlineData(@"C:\Program Files\SwSearch2\SwSearch.Broker.exe", false)]
    [InlineData(@"C:\PROGRAM FILES\SwSearch\子目录\SwSearch.Broker.exe", true)]
    [InlineData(@"C:\Program Files\SwSearch\组合é\SwSearch.Broker.exe", true)]
    public void FinalPathBoundary_IsSegmentAwareCaseInsensitiveAndUnicodePreserving(
        string processFinalPath,
        bool expected)
    {
        Assert.Equal(
            expected,
            FinalPathTrustPolicy.IsProcessInsideRegisteredRoot(
                processFinalPath,
                @"C:\Program Files\SwSearch"));
    }

    [Theory]
    [InlineData(null, @"C:\Program Files", false)]
    [InlineData(@"C:\Outside\SwSearch", @"C:\Program Files", false)]
    [InlineData(@"C:\Program Files2\SwSearch", @"C:\Program Files", false)]
    [InlineData(@"C:\Program Files\SWSEAR~1", @"C:\Program Files", false)]
    public void RegisteredRootPolicy_UsesResolvedFinalPathsOnly(
        string? registeredFinalRoot,
        string programFilesFinalRoot,
        bool expected)
    {
        Assert.Equal(
            expected,
            FinalPathTrustPolicy.IsTrustedRegisteredRoot(registeredFinalRoot, programFilesFinalRoot));
    }

    [Fact]
    public void GlobalCoordinationMutex_NameIsStableAndNonceIndependent()
    {
        const string upgradeCode = "{D9158356-29B3-44D6-A15A-ACDCE8328703}";
        var first = InstallerCoordinationMutex.GetName(upgradeCode);
        var second = InstallerCoordinationMutex.GetName(upgradeCode.ToLowerInvariant());
        var compact = InstallerCoordinationMutex.GetName("d915835629b344d6a15aacdce8328703");
        const string expected =
            @"Global\SwSearch.InstallCoordinator.D915835629B344D6A15AACDCE8328703.v2";
        var invalidUppercaseNamespace = expected.Replace(@"Global\", @"GLOBAL\", StringComparison.Ordinal);

        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.Equal(expected, compact);
        Assert.StartsWith(@"Global\", first, StringComparison.Ordinal);
        Assert.True(InstallerCoordinationMutex.IsCanonicalName(first, upgradeCode));
        Assert.NotEqual(first, invalidUppercaseNamespace);
        Assert.False(InstallerCoordinationMutex.IsCanonicalName(invalidUppercaseNamespace, upgradeCode));
        Assert.Equal(0x00120001u, InstallerCoordinationMutex.ExactRequiredAccess);
    }

    [Fact]
    public void FinalPathAnchor_ResolvesFileSymlinkAndRejectsTrustedBoundaryEscape()
    {
        AssertFileSymlinkOrRealJunction(
            () =>
            {
                var root = CreateTempRoot();
                var trusted = Directory.CreateDirectory(Path.Combine(root, "trusted")).FullName;
                var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
                var target = Path.Combine(outside, "SwSearch.Broker.exe");
                var link = Path.Combine(trusted, "SwSearch.Broker.exe");
                File.WriteAllText(target, "fixture");
                try
                {
                    File.CreateSymbolicLink(link, target);
                    using var anchor = WindowsFinalPath.TryOpenFileAnchor(link, denyReplacement: false);
                    Assert.NotNull(anchor);
                    Assert.Equal(Path.GetFullPath(target), anchor!.FinalPath, ignoreCase: true);
                    Assert.False(FinalPathTrustPolicy.IsProcessInsideRegisteredRoot(anchor.FinalPath, trusted));
                }
                finally
                {
                    if (File.Exists(link))
                    {
                        File.Delete(link);
                    }

                    Directory.Delete(root, recursive: true);
                }
            },
            AssertRealJunctionTargetOutsideTrustedRoot);
    }

    [Fact]
    public void FinalPathAnchor_ResolvesRealJunctionTargetOutsideTrustedRoot() =>
        AssertRealJunctionTargetOutsideTrustedRoot();

    [Theory]
    [InlineData(unchecked((int)0x80070522), true)]
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(unchecked((int)0x80070002), false)]
    [InlineData(unchecked((int)0x80070057), false)]
    public void FileSymlinkFallback_OnlyHandlesPrivilegeNotHeldIOException(int hresult, bool expectFallback)
    {
        var fallbackCalls = 0;
        Action assertion = () => AssertFileSymlinkOrRealJunction(
            () => throw new TestIOException(hresult),
            () => fallbackCalls++);

        if (expectFallback)
        {
            assertion();
            Assert.Equal(1, fallbackCalls);
        }
        else
        {
            var exception = Assert.Throws<TestIOException>(assertion);
            Assert.Equal(hresult, exception.HResult);
            Assert.Equal(0, fallbackCalls);
        }
    }

    [Fact]
    public void FileSymlinkFallback_DoesNotHandleUnauthorizedAccessException()
    {
        var fallbackCalls = 0;

        Assert.Throws<UnauthorizedAccessException>(() => AssertFileSymlinkOrRealJunction(
            () => throw new UnauthorizedAccessException("fixture"),
            () => fallbackCalls++));
        Assert.Equal(0, fallbackCalls);
    }

    private static void AssertRealJunctionTargetOutsideTrustedRoot()
    {
        var root = CreateTempRoot();
        var trusted = Directory.CreateDirectory(Path.Combine(root, "trusted")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var target = Path.Combine(outside, "SwSearch.Metadata.Worker.exe");
        var linkedDirectory = Path.Combine(trusted, "real-junction");
        File.WriteAllText(target, "fixture");
        try
        {
            CreateMountPointJunction(linkedDirectory, outside);
            Assert.Equal(IoReparseTagMountPoint, GetReparseTag(linkedDirectory));
            Assert.True(UnsafeLexicalPrefixAuthorizes(
                Path.Combine(linkedDirectory, "SwSearch.Metadata.Worker.exe"),
                trusted));
            Assert.Equal(
                Path.GetFullPath(outside),
                WindowsFinalPath.TryResolveDirectory(linkedDirectory),
                ignoreCase: true);
            using var anchor = WindowsFinalPath.TryOpenFileAnchor(
                Path.Combine(linkedDirectory, "SwSearch.Metadata.Worker.exe"),
                denyReplacement: false);
            Assert.NotNull(anchor);
            Assert.Equal(Path.GetFullPath(target), anchor!.FinalPath, ignoreCase: true);
            Assert.False(FinalPathTrustPolicy.IsProcessInsideRegisteredRoot(anchor.FinalPath, trusted));
        }
        finally
        {
            if (Directory.Exists(linkedDirectory))
            {
                Directory.Delete(linkedDirectory, recursive: false);
            }

            if (Directory.Exists(linkedDirectory))
            {
                throw new InvalidOperationException("Junction cleanup refused; root was preserved for inspection.");
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileAnchor_DeniesPathObjectReplacementWhileLeaseIsHeld()
    {
        var root = CreateTempRoot();
        var file = Path.Combine(root, "SwSearch.Broker.exe");
        var moved = Path.Combine(root, "moved.exe");
        File.WriteAllText(file, "fixture");
        try
        {
            using var anchor = WindowsFinalPath.TryOpenFileAnchor(file, denyReplacement: true);
            Assert.NotNull(anchor);
            Assert.ThrowsAny<IOException>(() => File.Move(file, moved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FixedProductionDll_ReadOnlyEightDotThreeAnchorIdentityGate()
    {
        var productionDll = Path.Combine(AppContext.BaseDirectory, "SwSearch.InstallerCoordinator.dll");
        var comparisonDll = typeof(InstallationCoordinatorTests).Assembly.Location;
        var productionHashBefore = ComputeSha256(productionDll);
        var comparisonHashBefore = ComputeSha256(comparisonDll);
        var shortInput = GetRequiredRealShortInput(productionDll);

        Assert.Throws<InvalidOperationException>(() =>
            RequireDistinctTildeShortInput(productionDll, productionDll));

        using var longAnchor = WindowsFinalPath.TryOpenFileAnchor(productionDll, denyReplacement: true);
        using var shortAnchor = WindowsFinalPath.TryOpenFileAnchor(shortInput, denyReplacement: true);
        using var comparisonAnchor = WindowsFinalPath.TryOpenFileAnchor(comparisonDll, denyReplacement: true);
        Assert.NotNull(longAnchor);
        Assert.NotNull(shortAnchor);
        Assert.NotNull(comparisonAnchor);

        Assert.Equal(longAnchor!.FinalPath, shortAnchor!.FinalPath);
        Assert.DoesNotContain('~', longAnchor.FinalPath);
        Assert.DoesNotContain('~', shortAnchor.FinalPath);
        Assert.Equal(longAnchor.ObjectId, shortAnchor.ObjectId);

        var longNativeIdentity = ReadNativeIdentity(longAnchor.Handle);
        var shortNativeIdentity = ReadNativeIdentity(shortAnchor.Handle);
        Assert.Equal(longNativeIdentity, shortNativeIdentity);
        Assert.Equal(longAnchor.ObjectId, longNativeIdentity.ObjectId);
        Assert.Equal(shortAnchor.ObjectId, shortNativeIdentity.ObjectId);
        Assert.True(WindowsFinalPath.ReportedPathMatchesFinal(shortInput, longAnchor.FinalPath));

        var shortDirectory = Path.GetDirectoryName(shortInput)!;
        var finalDirectory = Path.GetDirectoryName(longAnchor.FinalPath)!;
        RequireDistinctTildeShortDirectory(finalDirectory, shortDirectory);
        Assert.Equal(finalDirectory, WindowsFinalPath.TryResolveDirectory(shortDirectory));

        var missingNoncePath = Path.Combine(
            shortDirectory,
            "MISSING~" + Guid.NewGuid().ToString("N") + ".DLL");
        Assert.False(File.Exists(missingNoncePath));
        using var missingAnchor = WindowsFinalPath.TryOpenFileAnchor(missingNoncePath, denyReplacement: true);
        Assert.Null(missingAnchor);

        var wrongAlias = CreateWrongAliasFromRealShortInput(shortInput);
        Assert.False(WindowsFinalPath.ReportedPathMatchesFinal(wrongAlias, longAnchor.FinalPath));
        using var wrongAliasAnchor = WindowsFinalPath.TryOpenFileAnchor(wrongAlias, denyReplacement: true);
        if (wrongAliasAnchor is not null)
        {
            var wrongNativeIdentity = ReadNativeIdentity(wrongAliasAnchor.Handle);
            Assert.NotEqual(longAnchor.FinalPath, wrongAliasAnchor.FinalPath);
            Assert.NotEqual(longAnchor.ObjectId, wrongAliasAnchor.ObjectId);
            Assert.NotEqual(longNativeIdentity, wrongNativeIdentity);
            Assert.False(StrongAnchorComparator(
                new AnchorEvidence(longAnchor.FinalPath, longAnchor.ObjectId),
                new AnchorEvidence(wrongAliasAnchor.FinalPath, wrongAliasAnchor.ObjectId)));
        }

        Assert.NotEqual(longAnchor.ObjectId, comparisonAnchor!.ObjectId);
        Assert.False(WindowsFinalPath.ReportedPathMatchesFinal(shortInput, comparisonAnchor.FinalPath));

        var expected = new AnchorEvidence(longAnchor.FinalPath, longAnchor.ObjectId);
        var forgedPathOnlyMatch = new AnchorEvidence(longAnchor.FinalPath, comparisonAnchor.ObjectId);
        Assert.True(UnsafePathOnlyComparator(expected, forgedPathOnlyMatch));
        Assert.False(StrongAnchorComparator(expected, forgedPathOnlyMatch));

        Assert.Equal(productionHashBefore, ComputeSha256(productionDll));
        Assert.Equal(comparisonHashBefore, ComputeSha256(comparisonDll));
    }

    [Fact]
    public async Task OverallCancellation_StopsCoordinationWithoutUnboundedWork()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(95, "SwSearch.Broker.exe", "SwSearch.Broker"));
        var coordinator = new InstallationCoordinator(
            fixture.Products,
            fixture.Processes,
            new BlockingDelay(),
            new NullLog());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.CoordinateAsync(
                new InstallerCoordinatorOptions(
                    "{D9158356-29B3-44D6-A15A-ACDCE8328703}",
                    fixture.CurrentRoot,
                    TimeSpan.FromSeconds(10),
                    2),
                timeout.Token));
    }

    [Fact]
    public async Task AccessDeniedLease_RetainsVerifiedBackgroundForRestartManager()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(96, "SwSearch.Metadata.Worker.exe", "SwSearch.Metadata.Worker"));
        fixture.Processes.AcquisitionStatuses[96] = ProcessLeaseStatus.AccessDenied;

        var outcome = await fixture.RunAsync();

        Assert.Empty(outcome.Terminated);
        Assert.Single(outcome.RemainingProductProcesses);
        Assert.Empty(fixture.Processes.TerminationAttempts);
    }

    [Fact]
    public async Task ForegroundAppRespawnsBroker_ReconciliationIsBoundedAndUiIsNeverKilled()
    {
        var fixture = Fixture.Create();
        fixture.Processes.Add(Owned(97, "SwSearch.App.exe", "SwSearch.App") with { HasInteractiveWindow = false });
        fixture.Processes.Add(Owned(98, "SwSearch.Broker.exe", "SwSearch.Broker"));
        var next = 99;
        fixture.Processes.OnTerminate = process =>
        {
            if (process.FileName == "SwSearch.Broker.exe" && next <= 100)
            {
                fixture.Processes.Add(Owned(next++, "SwSearch.Broker.exe", "SwSearch.Broker"));
            }
        };

        var outcome = await fixture.RunAsync();

        Assert.DoesNotContain(fixture.Processes.TerminationAttempts, item => item.FileName == "SwSearch.App.exe");
        Assert.Contains(outcome.RemainingProductProcesses, item => item.FileName == "SwSearch.App.exe");
    }

    private static void AssertNoUiTerminationTrace(IEnumerable<ProcessSnapshot> terminationTrace)
    {
        var invalid = terminationTrace.FirstOrDefault(process =>
            process.FileName.Equals("SwSearch.App.exe", StringComparison.OrdinalIgnoreCase) ||
            process.FileName.Equals("SwSearch.PropertyViewer.exe", StringComparison.OrdinalIgnoreCase));
        if (invalid is not null)
        {
            throw new InvalidOperationException("UI process appeared in the termination trace: " + invalid.FileName);
        }
    }

    private static void AssertFileSymlinkOrRealJunction(Action assertFileSymlink, Action assertRealJunction)
    {
        try
        {
            assertFileSymlink();
        }
        catch (IOException exception) when (exception.HResult == unchecked((int)0x80070522))
        {
            assertRealJunction();
        }
    }

    private sealed class TestIOException : IOException
    {
        internal TestIOException(int hresult)
            : base("fixture")
        {
            HResult = hresult;
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "swsearch-installer-path-" + Guid.NewGuid().ToString("N"));
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsafe test root.");
        }

        Directory.CreateDirectory(full);
        return full;
    }

    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const int FileAttributeTagInfoClass = 9;

    private static bool UnsafeLexicalPrefixAuthorizes(string candidatePath, string trustedRoot) =>
        candidatePath.StartsWith(trustedRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static void CreateMountPointJunction(string junctionPath, string targetPath)
    {
        var fullJunction = Path.GetFullPath(junctionPath);
        var fullTarget = Path.GetFullPath(targetPath).TrimEnd('\\');
        Directory.CreateDirectory(fullJunction);

        using var handle = OpenReparsePoint(
            fullJunction,
            GenericWrite,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        if (handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        var substituteName = Encoding.Unicode.GetBytes(@"\??\" + fullTarget);
        var printName = Encoding.Unicode.GetBytes(fullTarget);
        var pathBufferLength = substituteName.Length + sizeof(char) + printName.Length + sizeof(char);
        var buffer = new byte[16 + pathBufferLength];
        WriteUInt32(buffer, 0, IoReparseTagMountPoint);
        WriteUInt16(buffer, 4, checked((ushort)(8 + pathBufferLength)));
        WriteUInt16(buffer, 8, 0);
        WriteUInt16(buffer, 10, checked((ushort)substituteName.Length));
        WriteUInt16(buffer, 12, checked((ushort)(substituteName.Length + sizeof(char))));
        WriteUInt16(buffer, 14, checked((ushort)printName.Length));
        Buffer.BlockCopy(substituteName, 0, buffer, 16, substituteName.Length);
        Buffer.BlockCopy(printName, 0, buffer, 16 + substituteName.Length + sizeof(char), printName.Length);

        if (!DeviceIoControl(
                handle,
                FsctlSetReparsePoint,
                buffer,
                buffer.Length,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static uint GetReparseTag(string path)
    {
        using var handle = OpenReparsePoint(
            path,
            FileReadAttributes,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        if (handle.IsInvalid ||
            !GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out var information,
                Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        return information.ReparseTag;
    }

    private static SafeFileHandle OpenReparsePoint(string path, uint desiredAccess, uint flags) =>
        CreateFile(
            path,
            desiredAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);

    private static void WriteUInt16(byte[] buffer, int offset, ushort value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, sizeof(ushort)), value);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, sizeof(uint)), value);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] input,
        int inputSize,
        IntPtr output,
        int outputSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileAttributeTagInformation information,
        int bufferSize);

    private static string GetRequiredRealShortInput(string longPath)
    {
        var parent = Path.GetDirectoryName(longPath) ??
            throw new InvalidOperationException("P1_3_ENVIRONMENT_BLOCKED: fixed DLL has no parent directory.");
        var direct = TryGetShortPath(longPath);
        var directParent = direct is null ? null : Path.GetDirectoryName(direct);
        if (IsDistinctTildeShortInput(longPath, direct) &&
            IsDistinctTildeShortDirectory(parent, directParent))
        {
            return direct!;
        }

        var shortParent = TryGetShortPath(parent);
        var parentBased = shortParent is null
            ? null
            : Path.Combine(shortParent, Path.GetFileName(longPath));
        if (IsDistinctTildeShortDirectory(parent, shortParent))
        {
            return RequireDistinctTildeShortInput(longPath, parentBased);
        }

        throw new InvalidOperationException(
            "P1_3_ENVIRONMENT_BLOCKED: fixed DLL parent did not expose a raw, distinct 8.3 directory containing '~'.");
    }

    private static string RequireDistinctTildeShortInput(string longPath, string? shortInput)
    {
        if (!IsDistinctTildeShortInput(longPath, shortInput))
        {
            throw new InvalidOperationException(
                "P1_3_ENVIRONMENT_BLOCKED: fixed DLL did not expose a raw, distinct 8.3 input containing '~'.");
        }

        return shortInput!;
    }

    private static bool IsDistinctTildeShortInput(string longPath, string? shortInput) =>
        !string.IsNullOrWhiteSpace(shortInput) &&
        !string.Equals(longPath, shortInput, StringComparison.OrdinalIgnoreCase) &&
        shortInput.Split('\\').Any(segment => segment.Contains('~'));

    private static void RequireDistinctTildeShortDirectory(string finalDirectory, string? shortDirectory)
    {
        if (!IsDistinctTildeShortDirectory(finalDirectory, shortDirectory))
        {
            throw new InvalidOperationException(
                "P1_3_ENVIRONMENT_BLOCKED: short directory chain was unchanged or did not contain a real '~' segment.");
        }
    }

    private static bool IsDistinctTildeShortDirectory(string finalDirectory, string? shortDirectory) =>
        !string.IsNullOrWhiteSpace(shortDirectory) &&
        !string.Equals(finalDirectory, shortDirectory, StringComparison.OrdinalIgnoreCase) &&
        shortDirectory.Split('\\').Any(segment => segment.Contains('~'));

    private static string CreateWrongAliasFromRealShortInput(string shortInput)
    {
        var segments = shortInput.Split('\\');
        for (var index = 0; index < segments.Length; index++)
        {
            var tilde = segments[index].IndexOf('~');
            if (tilde < 0)
            {
                continue;
            }

            var characters = segments[index].ToCharArray();
            if (tilde + 1 < characters.Length)
            {
                characters[tilde + 1] = characters[tilde + 1] == '9' ? '8' : '9';
                segments[index] = new string(characters);
            }
            else
            {
                segments[index] += "9";
            }

            var wrongAlias = string.Join("\\", segments);
            if (string.Equals(shortInput, wrongAlias, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Wrong-alias fixture did not change the real short input.");
            }

            return wrongAlias;
        }

        throw new InvalidOperationException(
            "P1_3_ENVIRONMENT_BLOCKED: real short input did not contain a '~' component for the wrong-alias fixture.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static NativeFileIdentity ReadNativeIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new NativeFileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow);
    }

    private static bool UnsafePathOnlyComparator(AnchorEvidence expected, AnchorEvidence candidate) =>
        string.Equals(expected.FinalPath, candidate.FinalPath, StringComparison.OrdinalIgnoreCase);

    private static bool StrongAnchorComparator(AnchorEvidence expected, AnchorEvidence candidate) =>
        UnsafePathOnlyComparator(expected, candidate) &&
        string.Equals(expected.ObjectId, candidate.ObjectId, StringComparison.Ordinal);

    private sealed record AnchorEvidence(string FinalPath, string ObjectId);

    private sealed record NativeFileIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow)
    {
        internal string ObjectId =>
            $"{VolumeSerialNumber:X8}:{FileIndexHigh:X8}{FileIndexLow:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    private static string? TryGetShortPath(string path)
    {
        var builder = new StringBuilder(32_768);
        var length = GetShortPathName(path, builder, (uint)builder.Capacity);
        return length == 0 || length >= builder.Capacity ? null : builder.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);

    private static ProcessSnapshot Owned(
        int processId,
        string fileName,
        string assemblyStem,
        string root = @"C:\Program Files\SwSearch") =>
        new(
            processId,
            Start.AddSeconds(processId),
            Path.Combine(root, fileName),
            fileName,
            assemblyStem,
            assemblyStem,
            "1.2.0",
            assemblyStem + ".dll",
            false,
            $"VOL:FILE:{processId}");

    private sealed class Fixture
    {
        private Fixture(
            FakeProducts products,
            FakeProcesses processes,
            string currentRoot)
        {
            Products = products;
            Processes = processes;
            CurrentRoot = currentRoot;
        }

        internal FakeProducts Products { get; }
        internal FakeProcesses Processes { get; }
        internal string CurrentRoot { get; }

        internal static Fixture Create(
            string installedRoot = @"C:\Program Files\SwSearch",
            string currentRoot = @"C:\Program Files\SwSearch") =>
            new(
                new FakeProducts(new InstalledProductIdentity("{OLD-PRODUCT}", "1.2.0", installedRoot)),
                new FakeProcesses(),
                currentRoot);

        internal async Task<CoordinationOutcome> RunAsync()
        {
            var coordinator = new InstallationCoordinator(
                Products,
                Processes,
                new NoDelay(),
                new NullLog());
            return await coordinator.CoordinateAsync(new InstallerCoordinatorOptions(
                "{D9158356-29B3-44D6-A15A-ACDCE8328703}",
                CurrentRoot,
                TimeSpan.Zero,
                2));
        }
    }

    private sealed class FakeProducts(params InstalledProductIdentity[] products) : IInstalledProductCatalog
    {
        public IReadOnlyList<InstalledProductIdentity> GetRelatedProducts(string upgradeCode, string currentInstallRoot) => products;
    }

    private sealed class FakeProcesses : IProductProcessCatalog
    {
        private readonly Dictionary<int, ProcessSnapshot> _processes = [];
        internal HashSet<int> ExitOnAcquire { get; } = [];
        internal Dictionary<int, ProcessSnapshot> ReplacementOnAcquire { get; } = [];
        internal Dictionary<int, ProcessSnapshot> LeaseSnapshotOverrides { get; } = [];
        internal List<ProcessSnapshot> TerminationAttempts { get; } = [];
        internal Action<ProcessSnapshot>? OnTerminate { get; set; }
        internal Dictionary<int, SingleProcessTerminationResult> TerminationResults { get; } = [];
        internal Dictionary<int, ProcessLeaseStatus> AcquisitionStatuses { get; } = [];

        internal void Add(ProcessSnapshot process) => _processes[process.ProcessId] = process;
        internal void Remove(int processId) => _processes.Remove(processId);
        internal bool Contains(int processId) => _processes.ContainsKey(processId);

        public ValueTask<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ProcessSnapshot>>(_processes.Values.OrderBy(item => item.ProcessId).ToArray());

        public ValueTask<ProcessLeaseAcquisition> AcquireTerminationLeaseAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            if (AcquisitionStatuses.TryGetValue(processId, out var status) && status != ProcessLeaseStatus.Acquired)
            {
                return ValueTask.FromResult(new ProcessLeaseAcquisition(status));
            }

            if (ExitOnAcquire.Remove(processId))
            {
                _processes.Remove(processId);
                return ValueTask.FromResult(new ProcessLeaseAcquisition(ProcessLeaseStatus.Exited));
            }

            if (ReplacementOnAcquire.Remove(processId, out var replacement))
            {
                _processes[processId] = replacement;
            }

            if (!_processes.TryGetValue(processId, out var process))
            {
                return ValueTask.FromResult(new ProcessLeaseAcquisition(ProcessLeaseStatus.Exited));
            }

            return ValueTask.FromResult(new ProcessLeaseAcquisition(
                ProcessLeaseStatus.Acquired,
                new FakeLease(this, LeaseSnapshotOverrides.GetValueOrDefault(processId, process))));
        }

        private sealed class FakeLease(FakeProcesses owner, ProcessSnapshot snapshot) : IProcessTerminationLease
        {
            public ProcessSnapshot Snapshot { get; } = snapshot;

            public SingleProcessTerminationResult TerminateSingleProcess()
            {
                owner.TerminationAttempts.Add(Snapshot);
                var result = owner.TerminationResults.GetValueOrDefault(
                    Snapshot.ProcessId,
                    SingleProcessTerminationResult.Terminated);
                if (result is SingleProcessTerminationResult.Terminated or SingleProcessTerminationResult.AlreadyExited)
                {
                    owner._processes.Remove(Snapshot.ProcessId);
                }

                if (result == SingleProcessTerminationResult.Terminated)
                {
                    owner.OnTerminate?.Invoke(Snapshot);
                }

                return result;
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeTransport : IProductShutdownTransport
    {
        internal ShutdownRequestResult AppResult { get; set; } = ShutdownRequestResult.Unavailable;
        internal ShutdownRequestResult PropertyViewerResult { get; set; } = ShutdownRequestResult.Unavailable;
        internal ShutdownRequestResult BrokerResult { get; set; } = ShutdownRequestResult.Unavailable;
        internal ShutdownRequestResult WorkerResult { get; set; } = ShutdownRequestResult.Unavailable;
        internal List<string> Calls { get; } = [];

        public ValueTask<ShutdownRequestResult> ShutdownAppAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add("App");
            return ValueTask.FromResult(AppResult);
        }

        public ValueTask<ShutdownRequestResult> ShutdownPropertyViewerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add("PropertyViewer");
            return ValueTask.FromResult(PropertyViewerResult);
        }

        public ValueTask<ShutdownRequestResult> ShutdownBrokerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add("Broker");
            return ValueTask.FromResult(BrokerResult);
        }

        public ValueTask<ShutdownRequestResult> ShutdownWorkerAsync(
            IReadOnlyList<InstalledProductIdentity> products,
            ShutdownRequestContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add("Worker");
            return ValueTask.FromResult(WorkerResult);
        }
    }

    private sealed class NoDelay : ICoordinatorDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class BlockingDelay : ICoordinatorDelay
    {
        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class NullLog : ICoordinatorLog
    {
        public void Write(string message)
        {
        }
    }
}
