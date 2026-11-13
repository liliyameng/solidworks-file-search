using Microsoft.Win32.SafeHandles;
using SwSearch.InstallerCoordinator;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

[Trait("Category", "WindowsNativeMutexGate")]
public sealed class InstallerCoordinationMutexNativeGateTests
{
    private const uint ExactAccess = 0x00120001;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint DaclSecurityInformation = 0x00000004;
    private const ushort SeDaclProtected = 0x1000;
    private const int SeKernelObject = 6;
    private const int AclSizeInformationClass = 2;
    private const uint WaitObject0 = 0;

    [Fact]
    public void GlobalMutex_CreateReopenAcquireRelease_ReadsBackExactProtectedDacl()
    {
        var identity = TestIdentity();
        var upgradeCode = UniqueUpgradeCode();
        var name = InstallerCoordinationMutex.GetName(upgradeCode);
        var log = new MemoryLog();

        var first = InstallerCoordinationMutex.TryAcquire(
            upgradeCode,
            identity.UserSid,
            TimeSpan.FromMilliseconds(500),
            log);
        Assert.Equal(InstallerCoordinationLockStatus.Acquired, first.Status);
        Assert.NotNull(first.Lease);
        try
        {
            using var reopened = OpenMutex(ExactAccess, false, name);
            Assert.False(reopened.IsInvalid);
            var acl = ReadAcl(reopened);
            Assert.True(acl.Protected);
            Assert.Equal(ExactAccess, InstallerCoordinationMutex.ExactRequiredAccess);
            Assert.Equal(3, acl.Aces.Count);
            Assert.Equal(
                new[]
                {
                    identity.UserSid,
                    "S-1-5-18",
                    "S-1-5-32-544"
                }.Order(StringComparer.Ordinal),
                acl.Aces.Select(ace => ace.Sid).Order(StringComparer.Ordinal));
            Assert.All(acl.Aces, ace => Assert.Equal(ExactAccess, ace.Mask));
            Assert.DoesNotContain(acl.Aces, ace => ace.Sid is "S-1-1-0" or "S-1-5-11");

            var sameThreadReopen = InstallerCoordinationMutex.TryAcquire(
                upgradeCode,
                identity.UserSid,
                TimeSpan.FromMilliseconds(100),
                log);
            Assert.Equal(InstallerCoordinationLockStatus.Acquired, sameThreadReopen.Status);
            sameThreadReopen.Lease!.Dispose();
        }
        finally
        {
            first.Lease?.Dispose();
        }

        var afterRelease = InstallerCoordinationMutex.TryAcquire(
            upgradeCode,
            identity.UserSid,
            TimeSpan.FromMilliseconds(500),
            log);
        Assert.Equal(InstallerCoordinationLockStatus.Acquired, afterRelease.Status);
        afterRelease.Lease!.Dispose();
    }

    [Fact]
    public void GlobalMutex_RejectsWrongSidMaskExtraAceNullUnprotectedAndBroadRights()
    {
        var identity = TestIdentity();
        var syntheticSid = "S-1-5-21-987654321-123456789-246813579-1007";
        var exact = $"(A;;0x{ExactAccess:X8};;;{identity.UserSid})";
        var system = $"(A;;0x{ExactAccess:X8};;;SY)";
        var admins = $"(A;;0x{ExactAccess:X8};;;BA)";
        var fixtures = new[]
        {
            new InvalidAclFixture("wrong-install-user-sid", $"D:P{exact}{system}{admins}", syntheticSid),
            new InvalidAclFixture(
                "wrong-mask",
                $"D:P(A;;0x{(ExactAccess | WriteDac):X8};;;{identity.UserSid}){system}{admins}",
                identity.UserSid),
            new InvalidAclFixture(
                "extra-ace",
                $"D:P{exact}{system}{admins}(A;;0x{ExactAccess:X8};;;S-1-5-21-1-2-3-1009)",
                identity.UserSid),
            new InvalidAclFixture(
                "world-alias",
                $"D:P{exact}{system}{admins}(A;;0x{ExactAccess:X8};;;WD)",
                identity.UserSid),
            new InvalidAclFixture(
                "everyone-sid",
                $"D:P{exact}{system}{admins}(A;;0x{ExactAccess:X8};;;S-1-1-0)",
                identity.UserSid),
            new InvalidAclFixture(
                "write-dac",
                $"D:P(A;;0x{(ExactAccess | WriteDac):X8};;;{identity.UserSid}){system}{admins}",
                identity.UserSid),
            new InvalidAclFixture(
                "write-owner",
                $"D:P(A;;0x{(ExactAccess | WriteOwner):X8};;;{identity.UserSid}){system}{admins}",
                identity.UserSid),
            new InvalidAclFixture(
                "generic-all",
                $"D:P(A;;GA;;;{identity.UserSid}){system}{admins}",
                identity.UserSid),
            new InvalidAclFixture("unprotected", $"D:{exact}{system}{admins}", identity.UserSid)
        };

        foreach (var fixture in fixtures)
        {
            var upgradeCode = UniqueUpgradeCode();
            using var held = CreateFixtureMutex(
                InstallerCoordinationMutex.GetName(upgradeCode),
                fixture.Sddl);
            var result = InstallerCoordinationMutex.TryAcquire(
                upgradeCode,
                fixture.ExpectedInstallUserSid,
                TimeSpan.FromMilliseconds(50),
                new MemoryLog());

            Assert.NotEqual(InstallerCoordinationLockStatus.Acquired, result.Status);
            Assert.Null(result.Lease);
        }

        var nullUpgradeCode = UniqueUpgradeCode();
        using var nullDacl = CreateNullDaclMutex(InstallerCoordinationMutex.GetName(nullUpgradeCode));
        var nullResult = InstallerCoordinationMutex.TryAcquire(
            nullUpgradeCode,
            identity.UserSid,
            TimeSpan.FromMilliseconds(50),
            new MemoryLog());
        Assert.Equal(InstallerCoordinationLockStatus.SecurityDescriptorMismatch, nullResult.Status);
        Assert.Null(nullResult.Lease);
    }

    [Fact]
    public void GlobalMutex_ConcurrentOwnerTimesOutFailSafeThenReacquires()
    {
        var identity = TestIdentity();
        var upgradeCode = UniqueUpgradeCode();
        using var ownerAcquired = new ManualResetEventSlim(false);
        using var competitorFinished = new ManualResetEventSlim(false);
        Exception? ownerFailure = null;
        InstallerCoordinationLockAcquisition? blocked = null;
        var firstAcquireThread = 0;
        var firstDisposeThread = 0;
        var retryAcquireThread = 0;
        var retryDisposeThread = 0;

        var owner = new Thread(() =>
        {
            try
            {
                firstAcquireThread = Environment.CurrentManagedThreadId;
                var first = InstallerCoordinationMutex.TryAcquire(
                    upgradeCode,
                    identity.UserSid,
                    TimeSpan.FromMilliseconds(500),
                    new MemoryLog());
                if (first.Status != InstallerCoordinationLockStatus.Acquired || first.Lease is null)
                {
                    throw new InvalidOperationException($"Owner acquire failed: {first.Status}.");
                }

                ownerAcquired.Set();
                if (!competitorFinished.Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new TimeoutException("Competitor did not finish its bounded wait.");
                }

                firstDisposeThread = Environment.CurrentManagedThreadId;
                first.Lease.ReleaseOnOwnerThread();

                retryAcquireThread = Environment.CurrentManagedThreadId;
                var retried = InstallerCoordinationMutex.TryAcquire(
                    upgradeCode,
                    identity.UserSid,
                    TimeSpan.FromMilliseconds(500),
                    new MemoryLog());
                if (retried.Status != InstallerCoordinationLockStatus.Acquired || retried.Lease is null)
                {
                    throw new InvalidOperationException($"Owner retry failed: {retried.Status}.");
                }

                retryDisposeThread = Environment.CurrentManagedThreadId;
                retried.Lease.ReleaseOnOwnerThread();
            }
            catch (Exception exception)
            {
                ownerFailure = exception;
                ownerAcquired.Set();
            }
        });
        owner.Start();
        Assert.True(ownerAcquired.Wait(TimeSpan.FromSeconds(2)));

        var competitor = new Thread(() =>
        {
            try
            {
                blocked = InstallerCoordinationMutex.TryAcquire(
                    upgradeCode,
                    identity.UserSid,
                    TimeSpan.FromMilliseconds(40),
                    new MemoryLog());
            }
            finally
            {
                competitorFinished.Set();
            }
        });
        competitor.Start();

        Assert.True(competitor.Join(2_000));
        Assert.True(owner.Join(2_000));
        Assert.Null(ownerFailure);
        Assert.NotNull(blocked);
        Assert.Equal(InstallerCoordinationLockStatus.Timeout, blocked!.Status);
        Assert.Null(blocked.Lease);
        Assert.NotEqual(0, firstAcquireThread);
        Assert.Equal(firstAcquireThread, firstDisposeThread);
        Assert.Equal(firstAcquireThread, retryAcquireThread);
        Assert.Equal(firstAcquireThread, retryDisposeThread);
    }

    [Fact]
    public void GlobalMutex_AbandonedOwnerIsRecoveredOnlyAfterExactAclValidation()
    {
        var identity = TestIdentity();
        var upgradeCode = UniqueUpgradeCode();
        var name = InstallerCoordinationMutex.GetName(upgradeCode);
        using var rawHandle = CreateFixtureMutex(
            name,
            InstallerCoordinationMutex.GetExpectedSddl(identity.UserSid));
        Exception? ownerFailure = null;
        var owner = new Thread(() =>
        {
            if (WaitForSingleObject(rawHandle, 1_000) != WaitObject0)
            {
                ownerFailure = new InvalidOperationException("Native fixture owner did not acquire its mutex.");
            }
        });
        owner.Start();
        Assert.True(owner.Join(2_000));
        Assert.Null(ownerFailure);

        var log = new MemoryLog();
        var recovered = InstallerCoordinationMutex.TryAcquire(
            upgradeCode,
            identity.UserSid,
            TimeSpan.FromMilliseconds(500),
            log);
        Assert.Equal(InstallerCoordinationLockStatus.Acquired, recovered.Status);
        Assert.Contains(log.Entries, entry => entry.Contains("abandoned-recovered", StringComparison.Ordinal));
        recovered.Lease!.Dispose();
    }

    private static TestAccountIdentity TestIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User?.Value;
        if (identity.IsSystem || string.IsNullOrWhiteSpace(sid) || !identity.User!.IsAccountSid())
        {
            throw new InvalidOperationException("P1_MSI_MUTEX_ENVIRONMENT_BLOCKED: gate requires one non-SYSTEM account token.");
        }

        return new TestAccountIdentity(sid);
    }

    private static string UniqueUpgradeCode() => Guid.NewGuid().ToString("B");

    private static SafeWaitHandle CreateFixtureMutex(string name, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };
            var handle = CreateMutexEx(ref attributes, name, 0, ExactAccess);
            if (handle.IsInvalid)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            return handle;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static SafeWaitHandle CreateNullDaclMutex(string name)
    {
        var descriptor = Marshal.AllocHGlobal(64);
        try
        {
            if (!InitializeSecurityDescriptor(descriptor, 1) ||
                !SetSecurityDescriptorDacl(descriptor, true, IntPtr.Zero, false))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };
            var handle = CreateMutexEx(ref attributes, name, 0, ExactAccess);
            if (handle.IsInvalid)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(descriptor);
        }
    }

    private static AclEvidence ReadAcl(SafeWaitHandle handle)
    {
        var error = GetSecurityInfo(
            handle,
            SeKernelObject,
            DaclSecurityInformation,
            IntPtr.Zero,
            IntPtr.Zero,
            out var dacl,
            IntPtr.Zero,
            out var descriptor);
        if (error != 0 || dacl == IntPtr.Zero || descriptor == IntPtr.Zero)
        {
            if (descriptor != IntPtr.Zero)
            {
                LocalFree(descriptor);
            }

            throw new InvalidOperationException($"Independent DACL read failed: {error}.");
        }

        try
        {
            Assert.True(GetSecurityDescriptorControl(descriptor, out var control, out _));
            Assert.True(GetAclInformation(
                dacl,
                out var information,
                Marshal.SizeOf<AclSizeInformation>(),
                AclSizeInformationClass));
            var aces = new List<AceEvidence>();
            for (uint index = 0; index < information.AceCount; index++)
            {
                Assert.True(GetAce(dacl, index, out var ace));
                var mask = unchecked((uint)Marshal.ReadInt32(ace, 4));
                var sid = new SecurityIdentifier(IntPtr.Add(ace, 8));
                aces.Add(new AceEvidence(sid.Value, mask));
            }

            return new AclEvidence((control & SeDaclProtected) != 0, aces);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private sealed record TestAccountIdentity(string UserSid);
    private sealed record InvalidAclFixture(string Name, string Sddl, string ExpectedInstallUserSid);
    private sealed record AceEvidence(string Sid, uint Mask);
    private sealed record AclEvidence(bool Protected, IReadOnlyList<AceEvidence> Aces);

    private sealed class MemoryLog : ICoordinatorLog
    {
        internal List<string> Entries { get; } = [];
        public void Write(string message) => Entries.Add(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AclSizeInformation
    {
        internal uint AceCount;
        internal uint AclBytesInUse;
        internal uint AclBytesFree;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateMutexExW")]
    private static extern SafeWaitHandle CreateMutexEx(
        ref SecurityAttributes mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenMutexW")]
    private static extern SafeWaitHandle OpenMutex(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeSecurityDescriptor(IntPtr securityDescriptor, uint revision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] bool daclPresent,
        IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] bool daclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeWaitHandle handle,
        int objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        out IntPtr dacl,
        IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(
        IntPtr securityDescriptor,
        out ushort control,
        out uint revision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAclInformation(
        IntPtr acl,
        out AclSizeInformation aclInformation,
        int aclInformationLength,
        int aclInformationClass);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAce(IntPtr acl, uint aceIndex, out IntPtr ace);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
