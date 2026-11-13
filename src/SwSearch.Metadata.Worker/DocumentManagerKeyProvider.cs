using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SwSearch.Metadata.Worker;

public interface IDocumentManagerKeyProvider
{
    ValueTask<DocumentManagerKey?> TryGetKeyAsync(CancellationToken cancellationToken);
}

public sealed class DocumentManagerKey : IDisposable
{
    private char[]? _characters;

    public DocumentManagerKey(ReadOnlySpan<char> characters)
    {
        if (characters.IsEmpty)
        {
            throw new ArgumentException("Key cannot be empty.", nameof(characters));
        }

        _characters = characters.ToArray();
    }

    internal string Reveal()
    {
        ObjectDisposedException.ThrowIf(_characters is null, this);
        return new string(_characters);
    }

    public void Dispose()
    {
        var characters = Interlocked.Exchange(ref _characters, null);
        if (characters is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }
}

public interface IDocumentManagerCredentialStore : IDocumentManagerKeyProvider
{
    ValueTask SaveKeyAsync(DocumentManagerKey key, CancellationToken cancellationToken);
    ValueTask<bool> DeleteKeyAsync(CancellationToken cancellationToken);
}

public sealed class WindowsCredentialDocumentManagerKeyProvider : IDocumentManagerCredentialStore
{
    public const string DefaultCredentialTarget = "SwSearch/DocumentManagerLicenseKey";
    private const int ErrorNotFound = 1168;
    private readonly string _targetName;

    public WindowsCredentialDocumentManagerKeyProvider(string targetName = DefaultCredentialTarget)
    {
        _targetName = string.IsNullOrWhiteSpace(targetName)
            ? throw new ArgumentException("Credential target is required.", nameof(targetName))
            : targetName;
    }

    public ValueTask<DocumentManagerKey?> TryGetKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<DocumentManagerKey?>(null);
        }

        if (!CredRead(_targetName, CredentialType.Generic, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return ValueTask.FromResult<DocumentManagerKey?>(null);
            }

            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return ValueTask.FromResult<DocumentManagerKey?>(null);
            }

            var byteCount = checked((int)credential.CredentialBlobSize);
            var bytes = new byte[byteCount];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, byteCount);
            try
            {
                var characterCount = byteCount / sizeof(char);
                var characters = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, characterCount * sizeof(char)));
                var end = characters.IndexOf('\0');
                if (end >= 0)
                {
                    characters = characters[..end];
                }

                return ValueTask.FromResult<DocumentManagerKey?>(
                    characters.IsEmpty ? null : new DocumentManagerKey(characters));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public ValueTask SaveKeyAsync(DocumentManagerKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager 仅支持 Windows。");
        }

        var revealed = key.Reveal();
        var blob = Marshal.StringToCoTaskMemUni(revealed);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialType.Generic,
                TargetName = Marshal.StringToCoTaskMemUni(_targetName),
                Comment = Marshal.StringToCoTaskMemUni("SwSearch SOLIDWORKS Document Manager license"),
                CredentialBlobSize = checked((uint)(revealed.Length * sizeof(char))),
                CredentialBlob = blob,
                Persist = CredentialPersistence.LocalMachine,
                UserName = Marshal.StringToCoTaskMemUni(Environment.UserName)
            };
            try
            {
                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(credential.TargetName);
                Marshal.FreeCoTaskMem(credential.Comment);
                Marshal.FreeCoTaskMem(credential.UserName);
            }
        }
        finally
        {
            unsafe
            {
                CryptographicOperations.ZeroMemory(
                    new Span<byte>((void*)blob, revealed.Length * sizeof(char)));
            }

            Marshal.FreeCoTaskMem(blob);
            revealed = string.Empty;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(false);
        }

        if (CredDelete(_targetName, CredentialType.Generic, 0))
        {
            return ValueTask.FromResult(true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotFound)
        {
            return ValueTask.FromResult(false);
        }

        throw new Win32Exception(error);
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        CredentialType type,
        int reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, CredentialType type, uint flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);

    private enum CredentialType : uint
    {
        Generic = 1
    }

    private enum CredentialPersistence : uint
    {
        Session = 1,
        LocalMachine = 2,
        Enterprise = 3
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersistence Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
