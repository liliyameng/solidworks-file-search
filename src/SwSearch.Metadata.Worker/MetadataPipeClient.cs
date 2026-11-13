using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

public sealed class MetadataPipeClient : IMetadataExtractionClient
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMilliseconds;

    public MetadataPipeClient(
        string? pipeName = null,
        int connectTimeoutMilliseconds = 1_000)
    {
        _pipeName = pipeName is null
            ? MetadataWorkerDefaults.PipeName
            : string.IsNullOrWhiteSpace(pipeName)
                ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
                : pipeName;
        _connectTimeoutMilliseconds = connectTimeoutMilliseconds > 0
            ? connectTimeoutMilliseconds
            : throw new ArgumentOutOfRangeException(nameof(connectTimeoutMilliseconds));
    }

    public async ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var response = await SendAsync(
                new PipeRequestEnvelope(
                    ContractVersions.MetadataPipe,
                    requestId,
                    "probe"),
                cancellationToken)
            .ConfigureAwait(false);

        ValidateEnvelope(response, requestId);
        if (response.Capability is not null)
        {
            return response.Capability;
        }

        var code = response.ErrorCode ?? MetadataErrorCodes.WorkerUnavailable;
        return new RuntimeCapability(
            CapabilityState.Unavailable,
            code,
            response.ChineseErrorMessage ?? MetadataErrorCodes.ChineseMessage(code));
    }

    public async ValueTask<MetadataResponse> ExtractAsync(
        MetadataRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                new PipeRequestEnvelope(
                    request.ProtocolVersion,
                    request.RequestId,
                    "extract",
                    request),
                cancellationToken)
            .ConfigureAwait(false);

        ValidateEnvelope(response, request.RequestId);
        return response.MetadataResponse ?? new MetadataResponse(
            ContractVersions.MetadataPipe,
            request.RequestId,
            Metadata: null,
            response.ErrorCode ?? MetadataErrorCodes.InvalidRequest,
            response.ChineseErrorMessage ?? MetadataErrorCodes.ChineseMessage(
                response.ErrorCode ?? MetadataErrorCodes.InvalidRequest),
            response.Retryable);
    }

    private async ValueTask<PipeResponseEnvelope> SendAsync(
        PipeRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(_connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        await PipeFrame.WriteJsonAsync(
                pipe,
                request,
                PipeFrame.MaximumRequestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return await PipeFrame.ReadJsonAsync<PipeResponseEnvelope>(
                pipe,
                PipeFrame.MaximumResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateEnvelope(PipeResponseEnvelope response, Guid requestId)
    {
        if (response.ProtocolVersion != ContractVersions.MetadataPipe || response.RequestId != requestId)
        {
            throw new IOException(MetadataErrorCodes.ChineseMessage(MetadataErrorCodes.FrameInvalid));
        }
    }
}

public sealed record MetadataWorkerClientOptions(
    string WorkerExecutablePath,
    string? PipeName = null,
    int ConnectTimeoutMilliseconds = 1_000,
    long MaximumProcessMemoryBytes = 256L * 1024 * 1024,
    Func<MetadataWorkerDiagnostic, CancellationToken, ValueTask>? DiagnosticSink = null);

public sealed record MetadataWorkerDiagnostic(
    string Level,
    string Code,
    string Message);

internal sealed record WorkerRecoverySettings(
    int StartupAttemptLimit,
    TimeSpan StartupRetryDelay,
    TimeSpan ReadinessTimeout,
    TimeSpan ReadinessPollInterval,
    TimeSpan StartupFailureCooldown,
    TimeSpan StartupCoordinationTimeout)
{
    internal static WorkerRecoverySettings Default { get; } = new(
        StartupAttemptLimit: 3,
        StartupRetryDelay: TimeSpan.FromMilliseconds(100),
        ReadinessTimeout: TimeSpan.FromSeconds(5),
        ReadinessPollInterval: TimeSpan.FromMilliseconds(25),
        StartupFailureCooldown: TimeSpan.FromSeconds(2),
        StartupCoordinationTimeout: TimeSpan.FromSeconds(30));
}

internal sealed record WorkerLaunchRequest(
    string ExecutablePath,
    Guid ReadyNonce,
    string PipeName);

internal interface IWorkerProcessHandle : IDisposable
{
    bool HasExited { get; }

    void Terminate();

    bool WaitForExit(int milliseconds);
}

internal interface IWorkerProcessServices : IDisposable
{
    void PrepareIsolation();

    IWorkerProcessHandle CreateSuspended(WorkerLaunchRequest request);

    void Attach(IWorkerProcessHandle process);

    void Resume(IWorkerProcessHandle process);
}

internal interface IWorkerStartupLease : IAsyncDisposable
{
}

internal interface IWorkerStartupCoordinator
{
    ValueTask<IWorkerStartupLease?> AcquireAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class NamedMutexWorkerStartupCoordinator : IWorkerStartupCoordinator
{
    private readonly Action<Mutex> _releaseMutex;

    internal static NamedMutexWorkerStartupCoordinator Shared { get; } = new();

    internal NamedMutexWorkerStartupCoordinator()
        : this(static mutex => mutex.ReleaseMutex())
    {
    }

    internal NamedMutexWorkerStartupCoordinator(Action<Mutex> releaseMutex) =>
        _releaseMutex = releaseMutex ?? throw new ArgumentNullException(nameof(releaseMutex));

    public ValueTask<IWorkerStartupLease?> AcquireAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IWorkerStartupLease?>(AcquireCoreAsync(
            Name(pipeName),
            timeout,
            cancellationToken));
    }

    internal static string Name(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userScope = identity.User?.Value ??
                        $"{Environment.UserDomainName}\\{Environment.UserName}";
        var key = userScope + "\0" + WorkerPipeIdentity.Canonicalize(pipeName);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return @"Local\SwSearch.Metadata.Worker.Startup." +
               Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private Task<IWorkerStartupLease?> AcquireCoreAsync(
        string mutexName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var acquired = new TaskCompletionSource<IWorkerStartupLease?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new EventWaitHandle(false, EventResetMode.ManualReset);
        var ownerFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutMilliseconds = checked((int)Math.Clamp(
            (long)Math.Ceiling(timeout.TotalMilliseconds),
            1,
            int.MaxValue));

        var owner = new Thread(() =>
        {
            Mutex? mutex = null;
            var ownsMutex = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, mutexName);
                int waitResult;
                try
                {
                    waitResult = WaitHandle.WaitAny(
                        [mutex, cancellationToken.WaitHandle],
                        timeoutMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    // Abandonment transfers ownership to this exact thread.
                    waitResult = 0;
                }

                if (waitResult == WaitHandle.WaitTimeout)
                {
                    acquired.TrySetResult(null);
                    return;
                }
                if (waitResult == 1)
                {
                    acquired.TrySetCanceled(cancellationToken);
                    return;
                }
                if (waitResult != 0)
                {
                    acquired.TrySetException(
                        new InvalidOperationException("Unexpected startup mutex wait result."));
                    return;
                }

                ownsMutex = true;
                var lease = new OwnerThreadStartupLease(released, ownerFinished.Task);
                acquired.TrySetResult(lease);
                released.WaitOne();
            }
            catch (Exception exception)
            {
                acquired.TrySetException(exception);
            }
            finally
            {
                try
                {
                    if (ownsMutex)
                    {
                        _releaseMutex(mutex!);
                    }
                    ownerFinished.TrySetResult();
                }
                catch (Exception releaseFailure)
                {
                    ownerFinished.TrySetException(releaseFailure);
                }
                finally
                {
                    mutex?.Dispose();
                    released.Dispose();
                }
            }
        })
        {
            IsBackground = true,
            Name = "SwSearch metadata startup mutex owner",
        };

        try
        {
            owner.Start();
        }
        catch
        {
            released.Dispose();
            throw;
        }

        return acquired.Task;
    }

    private sealed class OwnerThreadStartupLease(
        EventWaitHandle released,
        Task ownerFinished) : IWorkerStartupLease
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                released.Set();
            }

            return new ValueTask(ownerFinished.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }
}

internal sealed class SystemWorkerProcessServices(long maximumProcessMemoryBytes) : IWorkerProcessServices
{
    private const uint CreateSuspendedFlag = 0x0000_0004;
    private const uint CreateNoWindowFlag = 0x0800_0000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private WorkerProcessIsolation? _isolation;

    public void PrepareIsolation() =>
        _isolation ??= new WorkerProcessIsolation(maximumProcessMemoryBytes);

    public IWorkerProcessHandle CreateSuspended(WorkerLaunchRequest request)
    {
        var startupInfo = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
        };
        var encodedPipeName = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.PipeName));
        var commandLine = new StringBuilder(
            $"\"{request.ExecutablePath}\" --ready-nonce {request.ReadyNonce:N} " +
            $"--pipe-name-base64 {encodedPipeName}");
        // Allocate the fail-closed capability before CreateProcessW.  Once the
        // native call succeeds, no subsequent managed wrapper allocation is
        // allowed to strand the creation-time process handle.
        var creationCapability = new CreatedProcessTerminationHandle();
        var creationFailure = new WorkerProcessCreationException(creationCapability);
        SafeProcessHandle? process = null;
        SafeWaitHandle? thread = null;
        if (!CreateProcess(
                request.ExecutablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                CreateSuspendedFlag | CreateNoWindowFlag,
                IntPtr.Zero,
                Path.GetDirectoryName(request.ExecutablePath),
                ref startupInfo,
                out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            creationCapability.OwnRaw(
                processInformation.Process,
                processInformation.Thread);
            process = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            creationCapability.AdoptSafeProcessHandle(process);
            thread = new SafeWaitHandle(processInformation.Thread, ownsHandle: true);
            creationCapability.AdoptSafeThreadHandle(thread);
            var suspended = new SuspendedWorkerProcessHandle(process, thread);
            creationCapability.Disarm();
            return suspended;
        }
        catch
        {
            // Both carrier and exception were allocated before CreateProcessW.
            // Do not allocate or clean up here: the caller receives the exact
            // process/thread capability and performs fail-closed termination.
            throw creationFailure;
        }
    }

    public void Attach(IWorkerProcessHandle process)
    {
        if (process is not SuspendedWorkerProcessHandle suspended)
        {
            throw new ArgumentException("A suspended worker process handle is required.", nameof(process));
        }

        (_isolation ?? throw new InvalidOperationException("Worker isolation was not prepared."))
            .Attach(suspended.ProcessHandle);
    }

    public void Resume(IWorkerProcessHandle process)
    {
        if (process is not SuspendedWorkerProcessHandle suspended)
        {
            throw new ArgumentException("A suspended worker process handle is required.", nameof(process));
        }

        suspended.Resume();
    }

    public void Dispose() => _isolation?.Dispose();

    private sealed class SuspendedWorkerProcessHandle(
        SafeProcessHandle processHandle,
        SafeWaitHandle primaryThreadHandle) : IWorkerProcessHandle
    {
        private SafeWaitHandle? _primaryThreadHandle = primaryThreadHandle;

        internal SafeProcessHandle ProcessHandle { get; } = processHandle;

        public bool HasExited
        {
            get
            {
                var result = WaitForSingleObject(ProcessHandle, 0);
                return result switch
                {
                    WaitObject0 => true,
                    WaitTimeout => false,
                    WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error()),
                    _ => throw new InvalidOperationException("Unexpected process wait result."),
                };
            }
        }

        public void Terminate()
        {
            if (!TerminateProcess(ProcessHandle, 1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            var result = WaitForSingleObject(ProcessHandle, checked((uint)milliseconds));
            return result switch
            {
                WaitObject0 => true,
                WaitTimeout => false,
                WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error()),
                _ => throw new InvalidOperationException("Unexpected process wait result."),
            };
        }

        internal void Resume()
        {
            var thread = Interlocked.Exchange(ref _primaryThreadHandle, null) ??
                         throw new InvalidOperationException("The worker primary thread was already resumed.");
            using (thread)
            {
                if (ResumeThread(thread) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _primaryThreadHandle, null)?.Dispose();
            ProcessHandle.Dispose();
        }
    }

    private sealed class CreatedProcessTerminationHandle : IWorkerProcessHandle
    {
        private SafeProcessHandle? _safeProcessHandle;
        private SafeWaitHandle? _safeThreadHandle;
        private IntPtr _rawProcessHandle;
        private IntPtr _rawThreadHandle;

        internal void OwnRaw(IntPtr processHandle, IntPtr threadHandle)
        {
            _rawProcessHandle = processHandle;
            _rawThreadHandle = threadHandle;
        }

        internal void AdoptSafeProcessHandle(SafeProcessHandle processHandle)
        {
            _safeProcessHandle = processHandle;
            _rawProcessHandle = IntPtr.Zero;
        }

        internal void AdoptSafeThreadHandle(SafeWaitHandle threadHandle)
        {
            _safeThreadHandle = threadHandle;
            _rawThreadHandle = IntPtr.Zero;
        }

        internal void Disarm()
        {
            _safeProcessHandle = null;
            _safeThreadHandle = null;
            _rawProcessHandle = IntPtr.Zero;
            _rawThreadHandle = IntPtr.Zero;
        }

        public bool HasExited
        {
            get
            {
                var result = _safeProcessHandle is { } safeProcessHandle
                    ? WaitForSingleObject(safeProcessHandle, 0)
                    : WaitForSingleObjectRaw(RawProcessHandle, 0);
                return result switch
                {
                    WaitObject0 => true,
                    WaitTimeout => false,
                    WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error()),
                    _ => throw new InvalidOperationException("Unexpected process wait result."),
                };
            }
        }

        public void Terminate()
        {
            var terminated = _safeProcessHandle is { } safeProcessHandle
                ? TerminateProcess(safeProcessHandle, 1)
                : TerminateProcessRaw(RawProcessHandle, 1);
            if (!terminated)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            var result = _safeProcessHandle is { } safeProcessHandle
                ? WaitForSingleObject(safeProcessHandle, checked((uint)milliseconds))
                : WaitForSingleObjectRaw(RawProcessHandle, checked((uint)milliseconds));
            return result switch
            {
                WaitObject0 => true,
                WaitTimeout => false,
                WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error()),
                _ => throw new InvalidOperationException("Unexpected process wait result."),
            };
        }

        public void Dispose()
        {
            var safeThreadHandle = Interlocked.Exchange(ref _safeThreadHandle, null);
            if (safeThreadHandle is not null)
            {
                safeThreadHandle.Dispose();
            }
            else
            {
                var rawThreadHandle = Interlocked.Exchange(ref _rawThreadHandle, IntPtr.Zero);
                if (rawThreadHandle != IntPtr.Zero)
                {
                    _ = CloseHandle(rawThreadHandle);
                }
            }

            var safeProcessHandle = Interlocked.Exchange(ref _safeProcessHandle, null);
            if (safeProcessHandle is not null)
            {
                safeProcessHandle.Dispose();
                return;
            }

            var rawProcessHandle = Interlocked.Exchange(ref _rawProcessHandle, IntPtr.Zero);
            if (rawProcessHandle != IntPtr.Zero)
            {
                _ = CloseHandle(rawProcessHandle);
            }
        }

        private IntPtr RawProcessHandle =>
            _rawProcessHandle != IntPtr.Zero
                ? _rawProcessHandle
                : throw new ObjectDisposedException(nameof(CreatedProcessTerminationHandle));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcessRaw(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    private static extern uint WaitForSingleObjectRaw(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Bytes;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}

internal sealed class MetadataWorkerStartupException(
    string stage,
    int? nativeErrorCode,
    Exception innerException)
    : IOException(MetadataErrorCodes.ChineseMessage(MetadataErrorCodes.WorkerUnavailable), innerException)
{
    internal string Stage { get; } = stage;

    internal int? NativeErrorCode { get; } = nativeErrorCode;
}

internal sealed class WorkerProcessCreationException : IOException
{
    internal WorkerProcessCreationException(IWorkerProcessHandle process)
        : base("CreateProcessW succeeded but managed handle construction did not complete.") =>
        Process = process;

    internal WorkerProcessCreationException(
        IWorkerProcessHandle process,
        Exception innerException)
        : base(
            "CreateProcessW succeeded but managed handle construction did not complete.",
            innerException) =>
        Process = process;

    internal IWorkerProcessHandle Process { get; }
}

internal readonly record struct WorkerGenerationToken(
    long Generation,
    Guid ReadyNonce);

internal readonly record struct WorkerGenerationHealthSnapshot(
    WorkerGenerationToken Token,
    bool IsUnhealthy,
    int ActiveRequests,
    bool RetirementScheduled);

public sealed class MetadataWorkerClient :
    IMetadataExtractionClient,
    IMetadataGenerationSupervisor,
    IBrokerHostedMetadataExtractionClient,
    IAsyncDisposable
{
    private static readonly TimeSpan CleanupOperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseReleaseTimeout = TimeSpan.FromSeconds(2);
    private const int MaximumBufferedDiagnostics = 64;
    private readonly MetadataWorkerClientOptions _options;
    private readonly MetadataPipeClient _pipeClient;
    private readonly IWorkerProcessServices _processServices;
    private readonly Func<string, bool> _workerInstanceExists;
    private readonly Func<Guid, bool> _readySignalExists;
    private readonly bool _workerExecutablePresent;
    private readonly WorkerRecoverySettings _recovery;
    private readonly IWorkerStartupCoordinator _startupCoordinator;
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly SemaphoreSlim _terminationSingleFlight = new(1, 1);
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly object _diagnosticGate = new();
    private readonly Queue<MetadataWorkerDiagnostic> _diagnostics = new();
    private readonly object _terminationCapabilityGate = new();
    private readonly Dictionary<IWorkerProcessHandle, TerminationIdentity> _terminationIdentities =
        new(ReferenceEqualityComparer.Instance);
    private OwnedWorkerGeneration? _ownedGeneration;
    private Task<OwnedWorkerGeneration?>? _startupTask;
    private CancellationTokenSource? _startupCancellationSource;
    private IWorkerProcessHandle? _terminationBlockedProcess;
    private IWorkerStartupLease? _terminationBlockedStartupLease;
    private MetadataWorkerStartupException? _terminationBlockedFailure;
    private TaskCompletionSource _terminationParticipantsDrained = CompletedParticipantsSignal();
    private int _terminationParticipantCount;
    private MetadataWorkerStartupException? _lastStartupFailure;
    private long _startupRetryAfterUtcTicks;
    private long _nextGeneration;
    private long _startingGeneration;
    private long _poisonedStartupThroughGeneration;
    private bool _disposed;
    private bool _disposeCompleted;
    private string EffectivePipeName => _options.PipeName ?? MetadataWorkerDefaults.PipeName;

    internal Func<ValueTask>? AfterTerminationReservationForTesting { get; set; }

    private readonly record struct GenerationBoundResult<T>(
        T Result,
        OwnedWorkerGeneration? Generation);

    private sealed class TerminationIdentity
    {
        internal int Participants { get; set; }

        internal bool Terminated { get; set; }
    }

    private sealed class TerminationReservation : IDisposable
    {
        private readonly MetadataWorkerClient _owner;
        private readonly IWorkerProcessHandle _process;
        private int _disposed;

        internal TerminationReservation(
            MetadataWorkerClient owner,
            IWorkerProcessHandle process,
            TerminationIdentity identity)
        {
            _owner = owner;
            _process = process;
            Identity = identity;
        }

        internal TerminationIdentity Identity { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.LeaveTermination(_process, Identity);
            }
        }
    }

    private static TaskCompletionSource CompletedParticipantsSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    public MetadataWorkerClient(MetadataWorkerClientOptions options)
        : this(options, null, null, null, null, null, null)
    {
    }

    internal MetadataWorkerClient(
        MetadataWorkerClientOptions options,
        IWorkerProcessServices? processServices,
        Func<string, bool>? workerInstanceExists,
        Func<Guid, bool>? readySignalExists,
        Func<string, bool>? fileExists,
        WorkerRecoverySettings? recovery,
        IWorkerStartupCoordinator? startupCoordinator)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.WorkerExecutablePath))
        {
            throw new ArgumentException("Worker executable path must be absolute.", nameof(options));
        }
        if (options.MaximumProcessMemoryBytes is < 64L * 1024 * 1024 or > 2L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _options = options;
        _pipeClient = new MetadataPipeClient(
            options.PipeName ?? MetadataWorkerDefaults.PipeName,
            options.ConnectTimeoutMilliseconds);
        _processServices = processServices ?? new SystemWorkerProcessServices(options.MaximumProcessMemoryBytes);
        _workerInstanceExists = workerInstanceExists ?? WorkerInstanceSignal.Exists;
        _readySignalExists = readySignalExists ?? WorkerReadySignal.Exists;
        _workerExecutablePresent = (fileExists ?? File.Exists)(
            options.WorkerExecutablePath);
        _recovery = recovery ?? WorkerRecoverySettings.Default;
        _startupCoordinator = startupCoordinator ?? NamedMutexWorkerStartupCoordinator.Shared;
        if (_recovery.StartupAttemptLimit is < 1 or > 10 ||
            _recovery.StartupRetryDelay < TimeSpan.Zero ||
            _recovery.ReadinessTimeout <= TimeSpan.Zero ||
            _recovery.ReadinessPollInterval <= TimeSpan.Zero ||
            _recovery.StartupFailureCooldown < TimeSpan.Zero ||
            _recovery.StartupCoordinationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(recovery));
        }
    }

    public ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken) =>
        ExecuteWithRecoveryAsync(
            token => _pipeClient.ProbeAsync(token),
            cancellationToken);

    public async ValueTask<MetadataResponse> ExtractAsync(
        MetadataRequest request,
        CancellationToken cancellationToken)
    {
        var timeout = request.TimeoutMilliseconds <= 0
            ? MetadataWorkerDefaults.DefaultTimeoutMilliseconds
            : Math.Min(request.TimeoutMilliseconds, MetadataWorkerDefaults.MaximumTimeoutMilliseconds);
        var boundedRequest = request with { TimeoutMilliseconds = timeout };
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            var execution = await ExecuteWithRecoveryBoundAsync(
                    token => _pipeClient.ExtractAsync(boundedRequest, token),
                    linkedSource.Token)
                .ConfigureAwait(false);
            var response = execution.Result;
            var observedGeneration = execution.Generation;
            if (response.ProtocolVersion != ContractVersions.MetadataPipe ||
                response.RequestId != boundedRequest.RequestId ||
                response.PipelineLeaseId != boundedRequest.PipelineLeaseId ||
                response.WorkerGenerationNonce == Guid.Empty ||
                observedGeneration is null ||
                response.WorkerGenerationNonce != observedGeneration.ReadyNonce)
            {
                // Validation is bound to the exact generation that serviced
                // the request. A late G1 frame can never poison a newer G2.
                RequestGenerationRetirement(observedGeneration);
                return ErrorResponse(
                    boundedRequest,
                    MetadataErrorCodes.FrameInvalid,
                    retryable: true);
            }

            if (response.ErrorCode == MetadataErrorCodes.Timeout &&
                response.WorkerGenerationNonce != Guid.Empty)
            {
                RequestGenerationRetirement(observedGeneration);
            }
            return response;
        }
        catch (StartupGenerationOperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            // User cancellation does not poison a shared startup generation.
            return ErrorResponse(boundedRequest, MetadataErrorCodes.Cancelled, retryable: true);
        }
        catch (StartupGenerationOperationCanceledException exception)
        {
            // The request's absolute deadline expired while the shared worker
            // was still starting. Poison exactly that startup generation so it
            // cannot publish late and be silently reused.
            RequestStartupGenerationPoison(exception);
            return ErrorResponse(boundedRequest, MetadataErrorCodes.Timeout, retryable: true);
        }
        catch (GenerationOperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Per-request cancellation never tears down a shared generation.
            return ErrorResponse(boundedRequest, MetadataErrorCodes.Cancelled, retryable: true);
        }
        catch (GenerationOperationCanceledException exception)
        {
            // Poison is monotonic. Retirement occurs after the exact
            // generation's final active request releases.
            RequestGenerationRetirement(exception.Generation);
            return ErrorResponse(boundedRequest, MetadataErrorCodes.Timeout, retryable: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ErrorResponse(boundedRequest, MetadataErrorCodes.Cancelled, retryable: true);
        }
        catch (OperationCanceledException)
        {
            // No owned generation was acquired (for example, path/startup
            // coordination timed out), so there is no process capability to
            // terminate here.
            return ErrorResponse(boundedRequest, MetadataErrorCodes.Timeout, retryable: true);
        }
        catch (MetadataWorkerStartupException)
        {
            return ErrorResponse(boundedRequest, MetadataErrorCodes.WorkerUnavailable, retryable: true);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            return ErrorResponse(boundedRequest, MetadataErrorCodes.WorkerCrashed, retryable: true);
        }
    }

    public async ValueTask RetireGenerationAsync(
        Guid workerGenerationNonce,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (workerGenerationNonce == Guid.Empty)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var generation = Volatile.Read(ref _ownedGeneration);
        if (generation is null || generation.ReadyNonce != workerGenerationNonce)
        {
            return;
        }

        await ReportGenerationTransportFailureAsync(generation).ConfigureAwait(false);
        await WriteDiagnosticAsync(
                "WARN",
                "METADATA_WORKER_RECOVERING",
                "generation-retire",
                reason,
                attempt: 0,
                exception: null,
                generation)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!await _disposeGate.WaitAsync(CleanupOperationTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException("Timed out waiting to dispose the metadata worker client.");
        }
        try
        {
            if (_disposeCompleted)
            {
                return;
            }

            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        Task<OwnedWorkerGeneration?>? startup;
        OwnedWorkerGeneration? generation;
        TerminationReservation? reservation = null;
        if (!await _processGate.WaitAsync(CleanupOperationTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException("Timed out acquiring metadata worker cleanup ownership.");
        }
        try
        {
            _disposed = true;
            _startingGeneration = 0;
            startup = _startupTask;
            generation = Interlocked.Exchange(ref _ownedGeneration, null);
            if (generation is not null)
            {
                reservation = RegisterTerminationCapability(
                    generation.Process,
                    startupLease: null);
            }
        }
        finally
        {
            _processGate.Release();
        }

        _lifetimeSource.Cancel();
        if (startup is not null)
        {
            try
            {
                await startup.WaitAsync(CleanupOperationTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The lifetime token is already cancelled. Preserve the owned
                // process services for a later cleanup attempt rather than
                // disposing capabilities still used by a stuck launcher.
                throw new TimeoutException(
                    "Timed out waiting for metadata worker startup cleanup.");
            }
            catch
            {
                // Startup owns and closes its suspended/original handles.
            }
        }

        if (generation is not null)
        {
            try
            {
                await TerminateGenerationAsync(
                        generation,
                        releaseRetainedLeaseOnSuccess: true,
                        reservation)
                    .ConfigureAwait(false);
            }
            finally
            {
                reservation!.Dispose();
            }
        }
        await TerminateRetainedCapabilityAsync()
            .AsTask()
            .WaitAsync(CleanupOperationTimeout)
            .ConfigureAwait(false);
        await WaitForTerminationParticipantsAsync()
            .WaitAsync(CleanupOperationTimeout)
            .ConfigureAwait(false);
        _processServices.Dispose();
        _lifetimeSource.Dispose();
        _disposeCompleted = true;
        _terminationSingleFlight.Dispose();
        _processGate.Dispose();
    }

    private async ValueTask<T> ExecuteWithRecoveryAsync<T>(
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken) =>
        (await ExecuteWithRecoveryBoundAsync(action, cancellationToken)
            .ConfigureAwait(false)).Result;

    private async ValueTask<GenerationBoundResult<T>> ExecuteWithRecoveryBoundAsync<T>(
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ThrowIfTerminationBlockedAsync(cancellationToken).ConfigureAwait(false);
        var observedGeneration = Volatile.Read(ref _ownedGeneration);
        if (observedGeneration is not null &&
            !observedGeneration.IsUnhealthy &&
            !HasExited(observedGeneration.Process))
        {
            try
            {
                var result = await RetryOwnedWorkerAsync(
                    observedGeneration,
                    action,
                    cancellationToken).ConfigureAwait(false);
                return new GenerationBoundResult<T>(result, observedGeneration);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                // The exact observed generation has already been conditionally
                // retired. Continue through the single-flight recovery path.
            }
        }

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            return new GenerationBoundResult<T>(result, Generation: null);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            if (_workerInstanceExists(EffectivePipeName))
            {
                return await RetryExistingWorkerBoundAsync(action, cancellationToken)
                    .ConfigureAwait(false);
            }

            var generation = await EnsureOwnedWorkerAsync(cancellationToken).ConfigureAwait(false);
            if (generation is null)
            {
                return await RetryExistingWorkerBoundAsync(action, cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = await RetryOwnedWorkerAsync(generation, action, cancellationToken)
                .ConfigureAwait(false);
            return new GenerationBoundResult<T>(result, generation);
        }
    }

    private async ValueTask<T> RetryOwnedWorkerAsync<T>(
        OwnedWorkerGeneration generation,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(
            MetadataWorkerDefaults.DefaultTimeoutMilliseconds,
            _options.ConnectTimeoutMilliseconds * 2L));
        Exception? lastFailure = null;
        using var lease = generation.AcquireRequest();
        try
        {
            while (!HasExited(generation.Process) && DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await action(cancellationToken).ConfigureAwait(false);
                    generation.MarkHealthy();
                    return result;
                }
                catch (Exception exception) when (IsTransportFailure(exception))
                {
                    lastFailure = exception;
                    // A transport failure is generation-scoped poison. Never
                    // let a later success revive or reuse this exact process.
                    break;
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new GenerationOperationCanceledException(generation, exception);
        }

        lease.Dispose();
        RequestGenerationRetirement(generation);
        throw new IOException(
            MetadataErrorCodes.ChineseMessage(MetadataErrorCodes.WorkerCrashed),
            lastFailure);
    }

    private async ValueTask<T> ExecuteAgainstGenerationAsync<T>(
        OwnedWorkerGeneration generation,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        OperationCanceledException? cancellation = null;
        T? result = default;
        using (generation.AcquireRequest())
        {
            try
            {
                result = await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                failure = exception;
            }
            catch (OperationCanceledException exception)
            {
                cancellation = exception;
            }
        }

        if (cancellation is not null)
        {
            throw new GenerationOperationCanceledException(generation, cancellation);
        }
        if (failure is null)
        {
            generation.MarkHealthy();
            return result!;
        }

        await ReportGenerationTransportFailureAsync(generation).ConfigureAwait(false);
        ExceptionDispatchInfo.Capture(failure).Throw();
        throw new UnreachableException();
    }

    private async ValueTask<GenerationBoundResult<T>> RetryExistingWorkerBoundAsync<T>(
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(
            MetadataWorkerDefaults.DefaultTimeoutMilliseconds,
            _options.ConnectTimeoutMilliseconds * 2L));
        Exception? lastFailure = null;
        while (_workerInstanceExists(EffectivePipeName) && DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await action(cancellationToken).ConfigureAwait(false);
                return new GenerationBoundResult<T>(result, Generation: null);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                lastFailure = exception;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_workerInstanceExists(EffectivePipeName))
        {
            throw new IOException(
                "The existing metadata worker is busy or unresponsive; a competing worker was not started.",
                lastFailure);
        }

        var generation = await EnsureOwnedWorkerAsync(cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            return new GenerationBoundResult<T>(result, Generation: null);
        }

        var ownedResult = await RetryOwnedWorkerAsync(generation, action, cancellationToken)
            .ConfigureAwait(false);
        return new GenerationBoundResult<T>(ownedResult, generation);
    }

    private async ValueTask<OwnedWorkerGeneration?> EnsureOwnedWorkerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<OwnedWorkerGeneration?>? startup = null;
            CancellationTokenSource? startupCancellation = null;
            long startupGeneration = 0;
            Task? waitForIdle = null;
            OwnedWorkerGeneration? retired = null;
            TerminationReservation? retirementReservation = null;
            string? retiredOutcome = null;
            await ThrowIfTerminationBlockedAsync(cancellationToken).ConfigureAwait(false);
            await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_startupTask is not null)
                {
                    startup = _startupTask;
                    startupCancellation = _startupCancellationSource;
                    startupGeneration = _startingGeneration;
                }
                else if (_ownedGeneration is { } current)
                {
                    if (HasExited(current.Process))
                    {
                        _ownedGeneration = null;
                        retirementReservation = RegisterTerminationCapability(
                            current.Process,
                            startupLease: null);
                        retired = current;
                        retiredOutcome = "exited";
                    }
                    else if (!current.IsUnhealthy)
                    {
                        return current;
                    }
                    else if (current.ActiveRequests > 0)
                    {
                        waitForIdle = current.WhenIdle;
                    }
                    else
                    {
                        _ownedGeneration = null;
                        retirementReservation = RegisterTerminationCapability(
                            current.Process,
                            startupLease: null);
                        retired = current;
                        retiredOutcome = "unhealthy";
                    }
                }

                if (startup is null && waitForIdle is null && retired is null && _ownedGeneration is null)
                {
                    if (_workerInstanceExists(EffectivePipeName))
                    {
                        return null;
                    }

                    var now = DateTimeOffset.UtcNow;
                    var retryAfter = Interlocked.Read(ref _startupRetryAfterUtcTicks);
                    if (_lastStartupFailure is { } cachedFailure && retryAfter > now.UtcTicks)
                    {
                        throw new MetadataWorkerStartupException(
                            cachedFailure.Stage,
                            cachedFailure.NativeErrorCode,
                            cachedFailure);
                    }

                    if (!_workerExecutablePresent)
                    {
                        var missing = new FileNotFoundException(
                            MetadataErrorCodes.ChineseMessage(MetadataErrorCodes.WorkerUnavailable));
                        _ = WriteDiagnosticAsync(
                            "ERROR",
                            "METADATA_WORKER_STARTUP_FAILED",
                            "path-check",
                            "failed",
                            attempt: 0,
                            missing);
                        throw CacheStartupFailure("path-check", missing);
                    }

                    var generation = Interlocked.Increment(ref _nextGeneration);
                    var generationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        _lifetimeSource.Token);
                    _startingGeneration = generation;
                    _startupCancellationSource = generationSource;
                    startup = StartOwnedWorkerGenerationAsync(
                        generation,
                        generationSource.Token);
                    _startupTask = startup;
                    startupCancellation = generationSource;
                    startupGeneration = generation;
                    _ = ObserveStartupCancellationSourceAsync(
                        startup,
                        generationSource);
                }
            }
            finally
            {
                _processGate.Release();
            }

            if (retired is not null)
            {
                try
                {
                    await TerminateGenerationAsync(
                            retired,
                            reservation: retirementReservation)
                        .ConfigureAwait(false);
                }
                finally
                {
                    retirementReservation!.Dispose();
                }
                await WriteDiagnosticAsync(
                    "WARN",
                    "METADATA_WORKER_RETIRED",
                    "process-observed",
                    retiredOutcome ?? "retired",
                    attempt: 0,
                    exception: null,
                    retired).ConfigureAwait(false);
                continue;
            }
            if (waitForIdle is not null)
            {
                await waitForIdle.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                return await startup!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                cancellationToken.IsCancellationRequested &&
                startupGeneration > 0 &&
                startupCancellation is not null)
            {
                throw new StartupGenerationOperationCanceledException(
                    startupGeneration,
                    startup!,
                    startupCancellation,
                    exception);
            }
        }
    }

    private async Task<OwnedWorkerGeneration?> StartOwnedWorkerGenerationAsync(
        long generationNumber,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        IWorkerStartupLease? startupLease = null;
        try
        {
            startupLease = await _startupCoordinator.AcquireAsync(
                    EffectivePipeName,
                    _recovery.StartupCoordinationTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await ClearStartupTaskAsync(generationNumber).ConfigureAwait(false);
            throw;
        }

        if (startupLease is null)
        {
            var timeout = new TimeoutException(
                "Timed out waiting for the metadata worker startup owner.");
            var failure = CacheStartupFailure("startup-coordinate", timeout);
            await WriteDiagnosticAsync(
                "ERROR",
                "METADATA_WORKER_STARTUP_FAILED",
                failure.Stage,
                "coordination-timeout",
                attempt: 0,
                timeout).ConfigureAwait(false);
            await ClearStartupTaskAsync(generationNumber).ConfigureAwait(false);
            throw failure;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_workerInstanceExists(EffectivePipeName))
            {
                await ClearStartupTaskAsync(generationNumber).ConfigureAwait(false);
                return null;
            }

            Exception? lastFailure = null;
            var lastStage = "process-create-suspended";
            for (var attempt = 1; attempt <= _recovery.StartupAttemptLimit; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IWorkerProcessHandle? process = null;
                var resumed = false;
                var readyNonce = Guid.NewGuid();
                try
                {
                lastStage = "job-prepare";
                _processServices.PrepareIsolation();
                lastStage = "process-create-suspended";
                await WriteDiagnosticAsync(
                    "INFO",
                    "METADATA_WORKER_STARTUP",
                    lastStage,
                    "attempt",
                    attempt,
                    exception: null).ConfigureAwait(false);
                process = _processServices.CreateSuspended(new WorkerLaunchRequest(
                    _options.WorkerExecutablePath,
                    readyNonce,
                    EffectivePipeName));
                lastStage = "job-attach";
                // The helper is intentionally synchronous so the production IL
                // gate can prove that every Resume path is dominated by Attach.
                lastStage = "process-resume";
                AttachAndResumeIsolatedProcess(_processServices, process);
                resumed = true;
                lastStage = "ready-nonce";
                await WaitForReadinessAsync(process, readyNonce, cancellationToken).ConfigureAwait(false);

                var generation = new OwnedWorkerGeneration(
                    generationNumber,
                    readyNonce,
                    process,
                    OnPoisonedGenerationBecameIdle);
                await PublishStartedGenerationAsync(generation, attempt).ConfigureAwait(false);
                process = null;
                return generation;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (process is not null)
                {
                    try
                    {
                        await TerminateProcessOrRetainAsync(
                                process,
                                startupLease,
                                releaseRetainedLeaseOnSuccess: false)
                            .ConfigureAwait(false);
                    }
                    catch (MetadataWorkerStartupException)
                    {
                        startupLease = null;
                        await ClearStartupTaskAsync(generationNumber).ConfigureAwait(false);
                        throw;
                    }
                }
                await ClearStartupTaskAsync(generationNumber).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                if (process is null && exception is WorkerProcessCreationException creationFailure)
                {
                    process = creationFailure.Process;
                }
                lastFailure = exception;
                lastStage = StartupStage(exception, lastStage);
                if (process is not null)
                {
                    try
                    {
                        await TerminateProcessOrRetainAsync(
                                process,
                                startupLease,
                                releaseRetainedLeaseOnSuccess: false)
                            .ConfigureAwait(false);
                        process = null;
                    }
                    catch (MetadataWorkerStartupException terminationFailure)
                    {
                        startupLease = null;
                        await WriteDiagnosticAsync(
                            "ERROR",
                            "METADATA_WORKER_STARTUP_FAILED",
                            terminationFailure.Stage,
                            "termination-unconfirmed",
                            attempt,
                            terminationFailure).ConfigureAwait(false);
                        await ClearStartupTaskAsync(generationNumber).ConfigureAwait(false);
                        throw;
                    }
                }
                await WriteDiagnosticAsync(
                    "WARN",
                    "METADATA_WORKER_STARTUP_FAILED",
                    lastStage,
                    resumed ? "failed-after-resume" : "failed-suspended",
                    attempt,
                    exception).ConfigureAwait(false);
                if (!IsRecoverableStartupFailure(exception))
                {
                    await ClearStartupTaskAsync(generationNumber).ConfigureAwait(false);
                    ExceptionDispatchInfo.Capture(exception).Throw();
                    throw new UnreachableException();
                }
                if (attempt < _recovery.StartupAttemptLimit && _recovery.StartupRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_recovery.StartupRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                }
            }

            var failure = CacheStartupFailure(
                lastStage,
                lastFailure ?? new InvalidOperationException(
                    MetadataErrorCodes.ChineseMessage(MetadataErrorCodes.WorkerUnavailable)));
            await ClearStartupTaskAsync(generationNumber).ConfigureAwait(false);
            throw failure;
        }
        finally
        {
            if (startupLease is not null)
            {
                await ReleaseStartupLeaseOrRetainAsync(startupLease).ConfigureAwait(false);
            }
        }
    }

    internal static void AttachAndResumeIsolatedProcess(
        IWorkerProcessServices processServices,
        IWorkerProcessHandle process)
    {
        processServices.Attach(process);
        processServices.Resume(process);
    }

    private async ValueTask WaitForReadinessAsync(
        IWorkerProcessHandle process,
        Guid readyNonce,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(_recovery.ReadinessTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited(process))
            {
                throw new IOException(MetadataErrorCodes.ChineseMessage(MetadataErrorCodes.WorkerCrashed));
            }
            if (_readySignalExists(readyNonce))
            {
                return;
            }

            await Task.Delay(_recovery.ReadinessPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The metadata worker did not publish its generation readiness nonce in time.");
    }

    private async ValueTask PublishStartedGenerationAsync(OwnedWorkerGeneration generation, int attempt)
    {
        if (!await _processGate.WaitAsync(CleanupOperationTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException("Timed out publishing a metadata worker generation.");
        }
        try
        {
            if (_disposed ||
                _startingGeneration != generation.Generation ||
                generation.Generation <= Volatile.Read(ref _poisonedStartupThroughGeneration))
            {
                throw new OperationCanceledException("The worker startup generation is no longer current.");
            }

            _ownedGeneration = generation;
            _startupTask = null;
            _startupCancellationSource = null;
            _startingGeneration = 0;
            _lastStartupFailure = null;
            Interlocked.Exchange(ref _startupRetryAfterUtcTicks, 0);
        }
        finally
        {
            _processGate.Release();
        }

        await WriteDiagnosticAsync(
            "INFO",
            "METADATA_WORKER_STARTED",
            "ready-nonce",
            "ready",
            attempt,
            exception: null,
            generation).ConfigureAwait(false);
    }

    private async ValueTask ClearStartupTaskAsync(long generation)
    {
        if (!await _processGate.WaitAsync(CleanupOperationTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException("Timed out clearing metadata worker startup state.");
        }
        try
        {
            if (_startingGeneration == generation)
            {
                _startupTask = null;
                _startupCancellationSource = null;
                _startingGeneration = 0;
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    private static async Task ObserveStartupCancellationSourceAsync(
        Task<OwnedWorkerGeneration?> startup,
        CancellationTokenSource cancellationSource)
    {
        try
        {
            await startup.ConfigureAwait(false);
        }
        catch
        {
            // Startup reports through its owning request/diagnostic path.
        }
        finally
        {
            cancellationSource.Dispose();
        }
    }

    private void RequestStartupGenerationPoison(
        StartupGenerationOperationCanceledException exception)
    {
        UpdateMaximum(
            ref _poisonedStartupThroughGeneration,
            exception.Generation);
        var current = Volatile.Read(ref _ownedGeneration);
        if (current is not null && current.Generation == exception.Generation)
        {
            RequestGenerationRetirement(current);
        }
        try
        {
            _ = exception.CancellationSource.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Completion raced the timeout; the poison epoch still prevents a
            // late publication and the observer retires an already-published
            // exact generation.
        }
        _ = ObservePoisonedStartupAsync(
            exception.Generation,
            exception.StartupTask);
    }

    private async Task ObservePoisonedStartupAsync(
        long generationNumber,
        Task<OwnedWorkerGeneration?> startup)
    {
        OwnedWorkerGeneration? generation = null;
        try
        {
            generation = await startup.ConfigureAwait(false);
        }
        catch
        {
            // A cancelled startup cleans its own original process handle.
        }

        generation ??= Volatile.Read(ref _ownedGeneration);
        if (generation is not null && generation.Generation == generationNumber)
        {
            RequestGenerationRetirement(generation);
        }
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }

    private async ValueTask ReportGenerationTransportFailureAsync(OwnedWorkerGeneration generation)
    {
        if (generation.PoisonAndReserveRetirementIfIdle())
        {
            await RetireReservedGenerationAsync(generation).ConfigureAwait(false);
        }
    }

    private void RequestGenerationRetirement(OwnedWorkerGeneration? generation)
    {
        if (generation is not null &&
            generation.PoisonAndReserveRetirementIfIdle())
        {
            _ = RetireReservedGenerationSafelyAsync(generation);
        }
    }

    private void OnPoisonedGenerationBecameIdle(OwnedWorkerGeneration generation) =>
        _ = RetireReservedGenerationSafelyAsync(generation);

    private async Task RetireReservedGenerationSafelyAsync(OwnedWorkerGeneration generation)
    {
        try
        {
            await RetireReservedGenerationAsync(generation).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await WriteDiagnosticAsync(
                    "ERROR",
                    "METADATA_WORKER_RETIRE_FAILED",
                    "generation-retire",
                    "failed",
                    attempt: 0,
                    exception,
                    generation)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask RetireReservedGenerationAsync(OwnedWorkerGeneration generation)
    {
        OwnedWorkerGeneration? retired = null;
        TerminationReservation? reservation = null;
        if (!await _processGate.WaitAsync(CleanupOperationTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException("Timed out reserving metadata worker retirement.");
        }
        try
        {
            if (!ReferenceEquals(_ownedGeneration, generation))
            {
                return;
            }

            if (generation.ActiveRequests == 0)
            {
                _ownedGeneration = null;
                reservation = RegisterTerminationCapability(
                    generation.Process,
                    startupLease: null);
                retired = generation;
            }
        }
        finally
        {
            _processGate.Release();
        }

        if (retired is not null)
        {
            try
            {
                if (AfterTerminationReservationForTesting is { } reservationHook)
                {
                    await reservationHook().ConfigureAwait(false);
                }
                await TerminateGenerationAsync(retired, reservation: reservation)
                    .ConfigureAwait(false);
            }
            finally
            {
                reservation!.Dispose();
            }
            await WriteDiagnosticAsync(
                "WARN",
                "METADATA_WORKER_RETIRED",
                "transport-failure",
                "unhealthy",
                attempt: 0,
                exception: null,
                retired).ConfigureAwait(false);
        }
    }

    internal async ValueTask<WorkerGenerationToken> EnsureOwnedWorkerForTestingAsync(
        CancellationToken cancellationToken = default)
    {
        var generation = await EnsureOwnedWorkerAsync(cancellationToken).ConfigureAwait(false) ??
                         throw new InvalidOperationException("An external worker satisfied the request.");
        return generation.Token;
    }

    internal async ValueTask<WorkerGenerationToken?> EnsureOwnedOrExternalWorkerForTestingAsync(
        CancellationToken cancellationToken = default)
    {
        var generation = await EnsureOwnedWorkerAsync(cancellationToken).ConfigureAwait(false);
        return generation?.Token;
    }

    internal ValueTask<T> ExecuteAgainstGenerationForTestingAsync<T>(
        WorkerGenerationToken token,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken = default)
    {
        var generation = Volatile.Read(ref _ownedGeneration);
        if (generation is null || generation.Token != token)
        {
            throw new InvalidOperationException("The requested worker generation is no longer current.");
        }

        return ExecuteAgainstGenerationAsync(generation, action, cancellationToken);
    }

    internal ValueTask RetireGenerationForTestingAsync(WorkerGenerationToken token)
    {
        var generation = Volatile.Read(ref _ownedGeneration);
        if (generation is null || generation.Token != token)
        {
            throw new InvalidOperationException("The requested worker generation is no longer current.");
        }

        return ReportGenerationTransportFailureAsync(generation);
    }

    internal int TerminationIdentityCountForTesting
    {
        get
        {
            lock (_terminationCapabilityGate)
            {
                return _terminationIdentities.Count;
            }
        }
    }

    internal WorkerGenerationHealthSnapshot? CurrentGenerationHealthForTesting
    {
        get
        {
            var generation = Volatile.Read(ref _ownedGeneration);
            return generation is null
                ? null
                : new WorkerGenerationHealthSnapshot(
                    generation.Token,
                    generation.IsUnhealthy,
                    generation.ActiveRequests,
                    generation.RetirementScheduled);
        }
    }

    private MetadataWorkerStartupException CacheStartupFailure(string stage, Exception exception)
    {
        var failure = new MetadataWorkerStartupException(stage, NativeError(exception), exception);
        _lastStartupFailure = failure;
        Interlocked.Exchange(
            ref _startupRetryAfterUtcTicks,
            DateTimeOffset.UtcNow.Add(_recovery.StartupFailureCooldown).UtcTicks);
        return failure;
    }

    private ValueTask TerminateGenerationAsync(
        OwnedWorkerGeneration generation,
        bool releaseRetainedLeaseOnSuccess = false,
        TerminationReservation? reservation = null) =>
        TerminateProcessOrRetainAsync(
            generation.Process,
            startupLease: null,
            releaseRetainedLeaseOnSuccess,
            reservation);

    private async ValueTask TerminateProcessOrRetainAsync(
        IWorkerProcessHandle process,
        IWorkerStartupLease? startupLease,
        bool releaseRetainedLeaseOnSuccess,
        TerminationReservation? reservation = null)
    {
        var participant = reservation ?? JoinTermination(process);
        var ownsParticipant = reservation is null;
        var identity = participant.Identity;
        var enteredSingleFlight = false;
        try
        {
            if (!await _terminationSingleFlight.WaitAsync(CleanupOperationTimeout)
                    .ConfigureAwait(false))
            {
                throw new TimeoutException(
                    "Timed out waiting for metadata worker termination ownership.");
            }
            enteredSingleFlight = true;
            IWorkerStartupLease? retainedLease = null;
            var terminateProcess = true;
            lock (_terminationCapabilityGate)
            {
                if (identity.Terminated)
                {
                    terminateProcess = false;
                    if (releaseRetainedLeaseOnSuccess &&
                        ReferenceEquals(_terminationBlockedProcess, process))
                    {
                        retainedLease = _terminationBlockedStartupLease;
                    }
                }
                else if (_terminationBlockedProcess is not null &&
                    !ReferenceEquals(_terminationBlockedProcess, process))
                {
                    throw _terminationBlockedFailure ?? new MetadataWorkerStartupException(
                        "process-termination-blocked",
                        nativeErrorCode: null,
                        new InvalidOperationException(
                            "A prior worker still has unconfirmed termination."));
                }

                if (terminateProcess)
                {
                    _terminationBlockedProcess = process;
                    _terminationBlockedStartupLease ??= startupLease;
                }
            }

            if (terminateProcess)
            {
                try
                {
                    TerminateProcessHandle(process);
                }
                catch (Exception exception)
                {
                    var failure = TerminationFailure(exception);
                    lock (_terminationCapabilityGate)
                    {
                        if (ReferenceEquals(_terminationBlockedProcess, process))
                        {
                            _terminationBlockedFailure = failure;
                        }
                    }
                    throw failure;
                }

                lock (_terminationCapabilityGate)
                {
                    identity.Terminated = true;
                    if (ReferenceEquals(_terminationBlockedProcess, process))
                    {
                        retainedLease = releaseRetainedLeaseOnSuccess
                            ? _terminationBlockedStartupLease
                            : null;
                    }
                }
            }

            if (retainedLease is not null)
            {
                // Keep both the terminated-process marker and the lease rooted
                // until release succeeds; a failed lease release is retryable.
                await retainedLease.DisposeAsync()
                    .AsTask()
                    .WaitAsync(LeaseReleaseTimeout)
                    .ConfigureAwait(false);
            }

            ClearTerminationState(process);
        }
        finally
        {
            if (enteredSingleFlight)
            {
                _terminationSingleFlight.Release();
            }
            if (ownsParticipant)
            {
                participant.Dispose();
            }
        }
    }

    private TerminationReservation JoinTermination(IWorkerProcessHandle process)
    {
        lock (_terminationCapabilityGate)
        {
            return JoinTerminationUnsafe(process);
        }
    }

    private TerminationReservation JoinTerminationUnsafe(IWorkerProcessHandle process)
    {
        if (!_terminationIdentities.TryGetValue(process, out var identity))
        {
            identity = new TerminationIdentity();
            _terminationIdentities.Add(process, identity);
        }
        identity.Participants++;
        if (_terminationParticipantCount++ == 0)
        {
            _terminationParticipantsDrained = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new TerminationReservation(this, process, identity);
    }

    private void LeaveTermination(
        IWorkerProcessHandle process,
        TerminationIdentity identity)
    {
        lock (_terminationCapabilityGate)
        {
            identity.Participants--;
            _terminationParticipantCount--;
            if (identity.Participants == 0 &&
                !ReferenceEquals(_terminationBlockedProcess, process))
            {
                _terminationIdentities.Remove(process);
            }
            if (_terminationParticipantCount == 0)
            {
                _terminationParticipantsDrained.TrySetResult();
            }
        }
    }

    private TerminationReservation RegisterTerminationCapability(
        IWorkerProcessHandle process,
        IWorkerStartupLease? startupLease)
    {
        lock (_terminationCapabilityGate)
        {
            if (_terminationBlockedProcess is not null &&
                !ReferenceEquals(_terminationBlockedProcess, process))
            {
                throw _terminationBlockedFailure ?? new MetadataWorkerStartupException(
                    "process-termination-blocked",
                    nativeErrorCode: null,
                    new InvalidOperationException(
                        "A prior worker still has unconfirmed termination."));
            }

            _terminationBlockedProcess = process;
            _terminationBlockedStartupLease ??= startupLease;
            return JoinTerminationUnsafe(process);
        }
    }

    private Task WaitForTerminationParticipantsAsync()
    {
        lock (_terminationCapabilityGate)
        {
            return _terminationParticipantsDrained.Task;
        }
    }

    private void ClearTerminationState(IWorkerProcessHandle process)
    {
        lock (_terminationCapabilityGate)
        {
            if (ReferenceEquals(_terminationBlockedProcess, process))
            {
                _terminationBlockedProcess = null;
                _terminationBlockedStartupLease = null;
                _terminationBlockedFailure = null;
            }
            if (_terminationIdentities.TryGetValue(process, out var identity) &&
                identity.Participants == 0 &&
                !ReferenceEquals(_terminationBlockedProcess, process))
            {
                _terminationIdentities.Remove(process);
            }
        }
    }

    private async ValueTask ReleaseStartupLeaseOrRetainAsync(IWorkerStartupLease startupLease)
    {
        try
        {
            await startupLease.DisposeAsync()
                .AsTask()
                .WaitAsync(LeaseReleaseTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failure = new MetadataWorkerStartupException(
                "startup-coordinate-release",
                NativeError(exception),
                exception);
            lock (_terminationCapabilityGate)
            {
                _terminationBlockedStartupLease ??= startupLease;
                _terminationBlockedFailure ??= failure;
            }
            throw failure;
        }
    }

    private async ValueTask ThrowIfTerminationBlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!await _terminationSingleFlight.WaitAsync(
                CleanupOperationTimeout,
                cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException(
                "Timed out waiting for prior metadata worker cleanup.");
        }
        try
        {
            IWorkerProcessHandle? process;
            IWorkerStartupLease? startupLease;
            MetadataWorkerStartupException? failure;
            lock (_terminationCapabilityGate)
            {
                process = _terminationBlockedProcess;
                startupLease = _terminationBlockedStartupLease;
                failure = _terminationBlockedFailure;
            }

            if (process is null && startupLease is null)
            {
                return;
            }

            if (process is null)
            {
                try
                {
                    await startupLease!.DisposeAsync()
                        .AsTask()
                        .WaitAsync(LeaseReleaseTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = new MetadataWorkerStartupException(
                        "startup-coordinate-release",
                        NativeError(exception),
                        exception);
                    lock (_terminationCapabilityGate)
                    {
                        if (_terminationBlockedProcess is null &&
                            ReferenceEquals(_terminationBlockedStartupLease, startupLease))
                        {
                            _terminationBlockedFailure = failure;
                        }
                    }
                    throw failure;
                }

                lock (_terminationCapabilityGate)
                {
                    if (_terminationBlockedProcess is null &&
                        ReferenceEquals(_terminationBlockedStartupLease, startupLease))
                    {
                        _terminationBlockedStartupLease = null;
                        _terminationBlockedFailure = null;
                    }
                }
                return;
            }

            var alreadyTerminated = false;
            lock (_terminationCapabilityGate)
            {
                alreadyTerminated =
                    _terminationIdentities.TryGetValue(process, out var identity) &&
                    identity.Terminated;
            }

            if (!alreadyTerminated)
            {
                bool exited;
                try
                {
                    exited = process.HasExited;
                }
                catch (Exception exception)
                {
                    failure = new MetadataWorkerStartupException(
                        "process-exit-query",
                        NativeError(exception),
                        exception);
                    lock (_terminationCapabilityGate)
                    {
                        if (ReferenceEquals(_terminationBlockedProcess, process))
                        {
                            _terminationBlockedFailure = failure;
                        }
                    }
                    throw failure;
                }

                if (!exited)
                {
                    throw failure ?? new MetadataWorkerStartupException(
                        "process-termination-blocked",
                        nativeErrorCode: null,
                        new InvalidOperationException(
                            "The prior worker termination has not been confirmed."));
                }

                process.Dispose();
                lock (_terminationCapabilityGate)
                {
                    if (!_terminationIdentities.TryGetValue(process, out var identity))
                    {
                        identity = new TerminationIdentity();
                        _terminationIdentities.Add(process, identity);
                    }
                    identity.Terminated = true;
                }
            }
            if (startupLease is not null)
            {
                await startupLease.DisposeAsync()
                    .AsTask()
                    .WaitAsync(LeaseReleaseTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            ClearTerminationState(process);
        }
        finally
        {
            _terminationSingleFlight.Release();
        }
    }

    private async ValueTask TerminateRetainedCapabilityAsync()
    {
        IWorkerProcessHandle? process;
        IWorkerStartupLease? startupLease;
        lock (_terminationCapabilityGate)
        {
            process = _terminationBlockedProcess;
            startupLease = _terminationBlockedStartupLease;
        }

        if (process is null)
        {
            if (startupLease is not null)
            {
                await ThrowIfTerminationBlockedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return;
        }

        await TerminateProcessOrRetainAsync(
                process,
                startupLease: null,
                releaseRetainedLeaseOnSuccess: true)
            .ConfigureAwait(false);
    }

    private static MetadataWorkerStartupException TerminationFailure(Exception exception) =>
        exception as MetadataWorkerStartupException ??
        new MetadataWorkerStartupException(
            exception is TimeoutException ? "process-wait-exit" : "process-terminate",
            NativeError(exception),
            exception);

    private static void TerminateProcessHandle(IWorkerProcessHandle process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Terminate();
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // A raced exit is accepted only after a positive query through
                // the same creation-time handle.  Otherwise preserve and
                // propagate the original termination error.
                var exitedAfterFailure = false;
                try
                {
                    exitedAfterFailure = process.HasExited;
                }
                catch (Exception queryFailure) when (
                    queryFailure is InvalidOperationException or Win32Exception)
                {
                    // Preserve the Terminate error; neither exception proves exit.
                }

                if (!exitedAfterFailure)
                {
                    ExceptionDispatchInfo.Capture(exception).Throw();
                }
            }

            if (!process.WaitForExit(2_000))
            {
                throw new TimeoutException(
                    "The metadata worker did not exit after termination.");
            }
        }

        process.Dispose();
    }

    private ValueTask WriteDiagnosticAsync(
        string level,
        string code,
        string stage,
        string outcome,
        int attempt,
        Exception? exception,
        OwnedWorkerGeneration? generation = null)
    {
        var nativeError = NativeError(exception)?.ToString(CultureInfo.InvariantCulture) ?? "none";
        var exceptionType = exception?.GetType().Name ?? "none";
        var generationValue = generation?.Generation.ToString(CultureInfo.InvariantCulture) ?? "none";
        var nonceValue = generation is null ? "none" : "bound";
        var message =
            $"stage={CleanDiagnosticToken(stage)};outcome={CleanDiagnosticToken(outcome)};" +
            $"attempt={attempt.ToString(CultureInfo.InvariantCulture)};native_error={nativeError};" +
            $"path={DiagnosticPath()};exception={CleanDiagnosticToken(exceptionType)};" +
            $"generation={generationValue};ready_nonce={nonceValue};isolation=required";
        var diagnostic = new MetadataWorkerDiagnostic(level, code, message);
        lock (_diagnosticGate)
        {
            if (_diagnostics.Count == MaximumBufferedDiagnostics)
            {
                _diagnostics.Dequeue();
            }
            _diagnostics.Enqueue(diagnostic);
        }

        // DiagnosticSink is retained for source compatibility only. Invoking
        // an arbitrary delegate cannot be made bounded in-process: it may
        // block before returning a ValueTask or ignore cancellation. Recovery
        // therefore records into this owned bounded ring and never creates a
        // detached diagnostic task/thread.
        return ValueTask.CompletedTask;
    }

    internal IReadOnlyList<MetadataWorkerDiagnostic> DiagnosticSnapshotForTesting
    {
        get
        {
            lock (_diagnosticGate)
            {
                return _diagnostics.ToArray();
            }
        }
    }

    private string DiagnosticPath()
    {
        var fileName = Path.GetFileName(_options.WorkerExecutablePath);
        var basePath = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(_options.WorkerExecutablePath);
        var scope = fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ? "app" : "external";
        return $"<{scope}>{Path.DirectorySeparatorChar}{CleanDiagnosticToken(fileName)}";
    }

    private static int? NativeError(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception win32)
            {
                return win32.NativeErrorCode;
            }
        }

        return null;
    }

    private static string StartupStage(Exception exception, string fallback) =>
        exception is WorkerIsolationException isolation ? isolation.Stage : fallback;

    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException or TimeoutException or EndOfStreamException or Win32Exception;

    private static bool IsRecoverableStartupFailure(Exception exception) =>
        exception is IOException or Win32Exception or InvalidOperationException or TimeoutException;

    private static string CleanDiagnosticToken(string value) =>
        value.Replace(';', '_').Replace('=', '_').Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');

    private static bool HasExited(IWorkerProcessHandle process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            // A failed handle query is unknown, never proof of exit.  Keep the
            // original capability and fail closed until a later query succeeds.
            return false;
        }
    }

    private static MetadataResponse ErrorResponse(
        MetadataRequest request,
        string code,
        bool retryable) =>
        new(
            ContractVersions.MetadataPipe,
            request.RequestId,
            Metadata: null,
            code,
            MetadataErrorCodes.ChineseMessage(code),
            retryable,
            PipelineLeaseId: request.PipelineLeaseId,
            Outcome: MetadataPipelineOutcome.Failed);

    private sealed class OwnedWorkerGeneration(
        long generation,
        Guid readyNonce,
        IWorkerProcessHandle process,
        Action<OwnedWorkerGeneration> poisonedIdleCallback)
    {
        private readonly object _gate = new();
        private readonly Action<OwnedWorkerGeneration> _poisonedIdleCallback =
            poisonedIdleCallback;
        private TaskCompletionSource _idle = CompletedIdleSource();
        private int _activeRequests;
        private bool _unhealthy;
        private bool _retirementScheduled;

        internal long Generation { get; } = generation;

        internal Guid ReadyNonce { get; } = readyNonce;

        internal IWorkerProcessHandle Process { get; } = process;

        internal WorkerGenerationToken Token => new(Generation, ReadyNonce);

        internal int ActiveRequests => Volatile.Read(ref _activeRequests);

        internal bool IsUnhealthy
        {
            get
            {
                lock (_gate)
                {
                    return _unhealthy;
                }
            }
        }

        internal bool RetirementScheduled
        {
            get
            {
                lock (_gate)
                {
                    return _retirementScheduled;
                }
            }
        }

        internal Task WhenIdle
        {
            get
            {
                lock (_gate)
                {
                    return _idle.Task;
                }
            }
        }

        internal IDisposable AcquireRequest()
        {
            lock (_gate)
            {
                if (_unhealthy)
                {
                    throw new IOException(
                        "The metadata worker generation is poisoned and cannot accept new requests.");
                }
                if (_activeRequests == 0)
                {
                    _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                _activeRequests++;
                return new RequestLease(this);
            }
        }

        internal void MarkHealthy()
        {
            // Health is monotonic within a generation. Once any request has
            // poisoned it, a concurrent success cannot make it reusable.
        }

        internal bool PoisonAndReserveRetirementIfIdle()
        {
            lock (_gate)
            {
                _unhealthy = true;
                if (_activeRequests == 0 && !_retirementScheduled)
                {
                    _retirementScheduled = true;
                    return true;
                }

                return false;
            }
        }

        private void ReleaseRequest()
        {
            var retire = false;
            lock (_gate)
            {
                if (--_activeRequests == 0)
                {
                    _idle.TrySetResult();
                    if (_unhealthy && !_retirementScheduled)
                    {
                        _retirementScheduled = true;
                        retire = true;
                    }
                }
            }

            if (retire)
            {
                _poisonedIdleCallback(this);
            }
        }

        private static TaskCompletionSource CompletedIdleSource()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }

        private sealed class RequestLease(OwnedWorkerGeneration owner) : IDisposable
        {
            private OwnedWorkerGeneration? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseRequest();
        }
    }

    private sealed class GenerationOperationCanceledException(
        OwnedWorkerGeneration generation,
        OperationCanceledException innerException)
        : OperationCanceledException(innerException.Message, innerException, innerException.CancellationToken)
    {
        internal OwnedWorkerGeneration Generation { get; } = generation;
    }

    private sealed class StartupGenerationOperationCanceledException(
        long generation,
        Task<OwnedWorkerGeneration?> startupTask,
        CancellationTokenSource cancellationSource,
        OperationCanceledException innerException)
        : OperationCanceledException(
            innerException.Message,
            innerException,
            innerException.CancellationToken)
    {
        internal long Generation { get; } = generation;

        internal Task<OwnedWorkerGeneration?> StartupTask { get; } = startupTask;

        internal CancellationTokenSource CancellationSource { get; } = cancellationSource;
    }
}
