using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SwSearch.InstallerCoordinator;

internal enum InstallerCoordinationLockStatus
{
    Acquired,
    Timeout,
    AccessDenied,
    SecurityDescriptorMismatch,
    Failed
}

internal interface IInstallerCoordinationLease : IDisposable
{
    void ReleaseOnOwnerThread();
}

internal sealed record InstallerCoordinationLockAcquisition(
    InstallerCoordinationLockStatus Status,
    IInstallerCoordinationLease? Lease = null,
    string? Detail = null);

internal sealed class InstallerCoordinationMutex : IInstallerCoordinationLease
{
    private const uint Synchronize = 0x00100000;
    private const uint MutexModifyState = 0x00000001;
    private const uint ReadControl = 0x00020000;
    internal const uint ExactRequiredAccess = Synchronize | MutexModifyState | ReadControl;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorAlreadyExists = 183;
    private const uint DaclSecurityInformation = 0x00000004;
    private const ushort SeDaclProtected = 0x1000;
    private const byte AccessAllowedAceType = 0x00;
    private const int SeKernelObject = 6;
    private const int AclSizeInformationClass = 2;

    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private InstallerCoordinationMutex(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    internal static InstallerCoordinationLockAcquisition TryAcquire(
        string upgradeCode,
        string installUserSid,
        TimeSpan timeout,
        ICoordinatorLog log)
    {
        if (!TryGetAllowedSids(installUserSid, out var allowedSids, out var sidError))
        {
            return new InstallerCoordinationLockAcquisition(
                InstallerCoordinationLockStatus.SecurityDescriptorMismatch,
                Detail: sidError);
        }

        var name = GetName(upgradeCode);
        var sddl = BuildSddl(allowedSids!);
        IntPtr securityDescriptor = IntPtr.Zero;
        SafeWaitHandle? handle = null;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                    sddl,
                    1,
                    out securityDescriptor,
                    out _))
            {
                return Failed("ConvertStringSecurityDescriptorToSecurityDescriptor", Marshal.GetLastPInvokeError());
            }

            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = false
            };
            handle = CreateMutexEx(
                ref attributes,
                name,
                0,
                ExactRequiredAccess);
            if (handle.IsInvalid)
            {
                var error = (uint)Marshal.GetLastPInvokeError();
                handle.Dispose();
                return error == ErrorAccessDenied
                    ? new InstallerCoordinationLockAcquisition(
                        InstallerCoordinationLockStatus.AccessDenied,
                        Detail: $"CreateMutexEx access denied for {name}.")
                    : Failed("CreateMutexEx", unchecked((int)error));
            }

            var existed = (uint)Marshal.GetLastPInvokeError() == ErrorAlreadyExists;
            var validation = ValidateDacl(handle, allowedSids!);
            if (validation is not null)
            {
                handle.Dispose();
                return new InstallerCoordinationLockAcquisition(
                    InstallerCoordinationLockStatus.SecurityDescriptorMismatch,
                    Detail: validation);
            }

            var mutex = new Mutex();
            mutex.SafeWaitHandle = handle;
            handle = null;
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    // WaitOne grants ownership when the previous installer
                    // action exited without releasing. The protected DACL and
                    // stable name were already revalidated on this same handle.
                    ownsMutex = true;
                    log.Write("installer-coordination-mutex=abandoned-recovered");
                }

                if (!ownsMutex)
                {
                    mutex.Dispose();
                    return new InstallerCoordinationLockAcquisition(
                        InstallerCoordinationLockStatus.Timeout,
                        Detail: $"Timed out waiting for {name} after {timeout.TotalMilliseconds:0} ms.");
                }

                log.Write(
                    $"installer-coordination-mutex=acquired name={name} existed={existed} " +
                    $"acl={(allowedSids!.Count == 2 ? "SYSTEM+Administrators" : "install-user+SYSTEM+Administrators")} " +
                    "access=SYNCHRONIZE|MUTEX_MODIFY_STATE|READ_CONTROL");
                return new InstallerCoordinationLockAcquisition(
                    InstallerCoordinationLockStatus.Acquired,
                    new InstallerCoordinationMutex(mutex, ownsMutex: true));
            }
            catch
            {
                if (ownsMutex)
                {
                    mutex.ReleaseMutex();
                }

                mutex.Dispose();
                throw;
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            handle?.Dispose();
            return new InstallerCoordinationLockAcquisition(
                InstallerCoordinationLockStatus.AccessDenied,
                Detail: exception.Message);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            handle?.Dispose();
            return new InstallerCoordinationLockAcquisition(
                InstallerCoordinationLockStatus.Failed,
                Detail: exception.Message);
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                LocalFree(securityDescriptor);
            }
        }
    }

    internal static string GetName(string upgradeCode)
    {
        if (!Guid.TryParse(upgradeCode, out var parsed))
        {
            throw new ArgumentException("UpgradeCode is invalid.", nameof(upgradeCode));
        }

        var normalizedUpgradeCode = parsed.ToString("N").ToUpperInvariant();
        return $@"Global\SwSearch.InstallCoordinator.{normalizedUpgradeCode}.v2";
    }

    internal static bool IsCanonicalName(string candidate, string upgradeCode)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        try
        {
            return string.Equals(candidate, GetName(upgradeCode), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static string GetExpectedSddl(string installUserSid)
    {
        if (!TryGetAllowedSids(installUserSid, out var allowedSids, out var error))
        {
            throw new ArgumentException(error, nameof(installUserSid));
        }

        return BuildSddl(allowedSids!);
    }

    public void Dispose() => ReleaseOnOwnerThread();

    public void ReleaseOnOwnerThread()
    {
        if (_ownsMutex)
        {
            _ownsMutex = false;
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }

    private static string BuildSddl(IReadOnlyCollection<SecurityIdentifier> allowedSids)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var installUser = allowedSids.SingleOrDefault(sid => sid != system && sid != administrators);
        return "D:P" +
               (installUser is null ? string.Empty : $"(A;;0x{ExactRequiredAccess:X8};;;{installUser.Value})") +
               $"(A;;0x{ExactRequiredAccess:X8};;;SY)" +
               $"(A;;0x{ExactRequiredAccess:X8};;;BA)";
    }

    private static bool TryGetAllowedSids(
        string installUserSid,
        out IReadOnlyCollection<SecurityIdentifier>? allowedSids,
        out string error)
    {
        allowedSids = null;
        error = string.Empty;
        if (string.Equals(
                installUserSid,
                InstallerContextContract.MachineOnlyMutexSidMarker,
                StringComparison.Ordinal))
        {
            allowedSids = new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            };
            return true;
        }
        try
        {
            var user = new SecurityIdentifier(installUserSid);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (user == system || user == administrators || user.IsWellKnown(WellKnownSidType.WorldSid) ||
                user.IsWellKnown(WellKnownSidType.AnonymousSid) ||
                user.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) ||
                !user.IsAccountSid())
            {
                error = "Install user SID is not an individual Windows account SID.";
                return false;
            }

            allowedSids = new[] { user, system, administrators };
            return true;
        }
        catch (ArgumentException exception)
        {
            error = "Install user SID is invalid: " + exception.Message;
            return false;
        }
    }

    private static string? ValidateDacl(
        SafeWaitHandle handle,
        IReadOnlyCollection<SecurityIdentifier> expectedSids)
    {
        var result = GetSecurityInfo(
            handle,
            SeKernelObject,
            DaclSecurityInformation,
            IntPtr.Zero,
            IntPtr.Zero,
            out var dacl,
            IntPtr.Zero,
            out var securityDescriptor);
        if (result != 0 || dacl == IntPtr.Zero || securityDescriptor == IntPtr.Zero)
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                LocalFree(securityDescriptor);
            }

            return $"GetSecurityInfo failed or returned a null DACL: {result}.";
        }

        try
        {
            if (!GetSecurityDescriptorControl(securityDescriptor, out var control, out _) ||
                (control & SeDaclProtected) == 0)
            {
                return "Named mutex DACL is not protected from inheritance.";
            }

            if (!GetAclInformation(
                    dacl,
                    out var information,
                    Marshal.SizeOf<AclSizeInformation>(),
                    AclSizeInformationClass))
            {
                return $"GetAclInformation failed: {Marshal.GetLastPInvokeError()}.";
            }

            if (information.AceCount != expectedSids.Count)
            {
                return $"Named mutex ACE count mismatch: {information.AceCount}/{expectedSids.Count}.";
            }

            var expected = new HashSet<string>(expectedSids.Select(sid => sid.Value), StringComparer.Ordinal);
            var actual = new HashSet<string>(StringComparer.Ordinal);
            for (uint index = 0; index < information.AceCount; index++)
            {
                if (!GetAce(dacl, index, out var acePointer))
                {
                    return $"GetAce failed at {index}: {Marshal.GetLastPInvokeError()}.";
                }

                var header = Marshal.PtrToStructure<AceHeader>(acePointer);
                if (header.AceType != AccessAllowedAceType || header.AceFlags != 0)
                {
                    return $"Unexpected ACE type/flags at {index}: {header.AceType}/{header.AceFlags}.";
                }

                var mask = unchecked((uint)Marshal.ReadInt32(acePointer, 4));
                if (mask != ExactRequiredAccess)
                {
                    return $"Named mutex ACE rights mismatch at {index}: 0x{mask:X8}.";
                }

                var sid = new SecurityIdentifier(IntPtr.Add(acePointer, 8));
                if (!expected.Contains(sid.Value) || !actual.Add(sid.Value))
                {
                    return $"Unexpected or duplicate named mutex SID at {index}: {sid.Value}.";
                }
            }

            return actual.SetEquals(expected) ? null : "Named mutex SID set mismatch.";
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    private static InstallerCoordinationLockAcquisition Failed(string operation, int error) =>
        new(
            InstallerCoordinationLockStatus.Failed,
            Detail: $"{operation} failed: {error} ({new Win32Exception(error).Message}).");

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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AceHeader
    {
        internal byte AceType;
        internal byte AceFlags;
        internal ushort AceSize;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateMutexExW")]
    private static extern SafeWaitHandle CreateMutexEx(
        ref SecurityAttributes mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

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
