using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SwSearch.InstallerCoordinator;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

[CollectionDefinition("Windows native coordinator gate", DisableParallelization = true)]
public sealed class WindowsNativeCoordinatorGateCollection;

[Collection("Windows native coordinator gate")]
public sealed class WindowsNativeCoordinatorGateTests
{
    [Fact]
    public async Task ProductionLease_UsesHeldHandleAndTerminatesOnlyOwnedNonceHelper()
    {
        await using var sandbox = NativeHelperSandbox.Create();
        var helper = await sandbox.StartAsync("--native-gate-helper");
        var catalog = new WindowsProductProcessCatalog();

        var acquisition = await catalog.AcquireTerminationLeaseAsync(helper.ProcessId, CancellationToken.None);

        Assert.Equal(ProcessLeaseStatus.Acquired, acquisition.Status);
        using var lease = Assert.IsAssignableFrom<IProcessTerminationLease>(acquisition.Lease);
        Assert.Equal(helper.ProcessId, lease.Snapshot.ProcessId);
        Assert.Equal("SwSearch.Broker.exe", lease.Snapshot.FileName);
        Assert.False(string.IsNullOrWhiteSpace(lease.Snapshot.ImageObjectId));
        Assert.Equal(SingleProcessTerminationResult.Terminated, lease.TerminateSingleProcess());
        Assert.True(helper.WaitForExit(3_000));
        sandbox.MarkStopped(helper.Ownership);
    }

    [Fact]
    public async Task ProductionTerminateProcess_DoesNotRecursivelyTerminateUnvalidatedChild()
    {
        await using var sandbox = NativeHelperSandbox.Create();
        var parent = await sandbox.StartParentWithChildAsync();
        var catalog = new WindowsProductProcessCatalog();
        var parentAcquisition = await catalog.AcquireTerminationLeaseAsync(parent.ProcessId, CancellationToken.None);
        using var parentLease = Assert.IsAssignableFrom<IProcessTerminationLease>(parentAcquisition.Lease);

        Assert.Equal(SingleProcessTerminationResult.Terminated, parentLease.TerminateSingleProcess());
        Assert.True(parent.WaitForExit(3_000));
        sandbox.MarkStopped(parent.Ownership);

        var childAcquisition = await catalog.AcquireTerminationLeaseAsync(parent.ChildProcessId, CancellationToken.None);
        Assert.Equal(ProcessLeaseStatus.Acquired, childAcquisition.Status);
        using var childLease = Assert.IsAssignableFrom<IProcessTerminationLease>(childAcquisition.Lease);
        Assert.Equal(parent.ChildProcessId, childLease.Snapshot.ProcessId);
        Assert.Equal(SingleProcessTerminationResult.Terminated, childLease.TerminateSingleProcess());
        sandbox.MarkStopped(parent.ChildOwnership);
    }

    [Fact]
    public async Task ProductionLease_RejectsCreationAndFileObjectReplacementEvenWhenPidPathComparatorWouldPass()
    {
        await using var sandbox = NativeHelperSandbox.Create();
        var catalog = new WindowsProductProcessCatalog();
        var first = await sandbox.StartAsync("--native-gate-helper");
        var firstAcquisition = await catalog.AcquireTerminationLeaseAsync(first.ProcessId, CancellationToken.None);
        ProcessSnapshot firstSnapshot;
        using (var firstLease = Assert.IsAssignableFrom<IProcessTerminationLease>(firstAcquisition.Lease))
        {
            firstSnapshot = firstLease.Snapshot;
            Assert.Equal(SingleProcessTerminationResult.Terminated, firstLease.TerminateSingleProcess());
        }

        Assert.True(first.WaitForExit(3_000));
        sandbox.MarkStopped(first.Ownership);
        sandbox.ReplaceExecutableWithFreshFileObject();

        var second = await sandbox.StartAsync("--native-gate-helper");
        var secondAcquisition = await catalog.AcquireTerminationLeaseAsync(second.ProcessId, CancellationToken.None);
        using var secondLease = Assert.IsAssignableFrom<IProcessTerminationLease>(secondAcquisition.Lease);
        var secondSnapshot = secondLease.Snapshot;
        Assert.NotEqual(firstSnapshot.ImageObjectId, secondSnapshot.ImageObjectId);

        var unsafeObjectReuseEvidence = firstSnapshot with
        {
            ProcessId = secondSnapshot.ProcessId,
            StartTimeUtc = secondSnapshot.StartTimeUtc,
            ImagePath = secondSnapshot.ImagePath
        };
        Assert.True(UnsafePidAndPathOnly(unsafeObjectReuseEvidence, secondSnapshot));
        Assert.False(ProcessIdentityContinuity.IsSameInstance(unsafeObjectReuseEvidence, secondSnapshot));

        var unsafePidReuseEvidence = firstSnapshot with
        {
            ProcessId = secondSnapshot.ProcessId,
            ImagePath = secondSnapshot.ImagePath,
            ImageObjectId = secondSnapshot.ImageObjectId
        };
        Assert.True(UnsafePidAndPathOnly(unsafePidReuseEvidence, secondSnapshot));
        Assert.False(ProcessIdentityContinuity.IsSameInstance(unsafePidReuseEvidence, secondSnapshot));

        Assert.Equal(SingleProcessTerminationResult.Terminated, secondLease.TerminateSingleProcess());
        Assert.True(second.WaitForExit(3_000));
        sandbox.MarkStopped(second.Ownership);
    }

    [Fact]
    public async Task NativeCleanup_StaleEvidencePointingAtNewHelperCannotRetargetOriginalHandle()
    {
        await using var sandbox = NativeHelperSandbox.Create();
        var catalog = new WindowsProductProcessCatalog();
        var oldHelper = await sandbox.StartAsync("--native-gate-helper");
        var oldAcquisition = await catalog.AcquireTerminationLeaseAsync(oldHelper.ProcessId, CancellationToken.None);
        using (var oldLease = Assert.IsAssignableFrom<IProcessTerminationLease>(oldAcquisition.Lease))
        {
            Assert.Equal(SingleProcessTerminationResult.Terminated, oldLease.TerminateSingleProcess());
        }

        Assert.True(oldHelper.WaitForExit(3_000));
        var newHelper = await sandbox.StartAsync("--native-gate-helper");
        oldHelper.Ownership.OverrideDiagnosticEvidenceForNegativeTest(newHelper.Ownership.ProductionSnapshot);

        Assert.Equal(CleanupDisposition.AlreadyExited, oldHelper.Ownership.TryCleanup(out _));
        Assert.False(newHelper.WaitForExit(0));
        Assert.Equal(0, newHelper.Ownership.OriginalHandleTerminationCalls);
        sandbox.MarkStopped(oldHelper.Ownership);
    }

    [Fact]
    public async Task NativeCleanup_ReplacedImageObjectCannotRedirectOriginalHandleToNewHelper()
    {
        await using var sandbox = NativeHelperSandbox.Create();
        var catalog = new WindowsProductProcessCatalog();
        var oldHelper = await sandbox.StartAsync("--native-gate-helper");
        var oldSnapshot = oldHelper.Ownership.ProductionSnapshot;
        var oldAcquisition = await catalog.AcquireTerminationLeaseAsync(oldHelper.ProcessId, CancellationToken.None);
        using (var oldLease = Assert.IsAssignableFrom<IProcessTerminationLease>(oldAcquisition.Lease))
        {
            Assert.Equal(SingleProcessTerminationResult.Terminated, oldLease.TerminateSingleProcess());
        }

        Assert.True(oldHelper.WaitForExit(3_000));
        oldHelper.Ownership.ReleaseProductionEvidenceAnchorForNegativeTest();
        sandbox.ReplaceExecutableWithFreshFileObject();
        var newHelper = await sandbox.StartAsync("--native-gate-helper");
        Assert.NotEqual(oldSnapshot.ImageObjectId, newHelper.Ownership.ProductionSnapshot.ImageObjectId);
        oldHelper.Ownership.OverrideDiagnosticEvidenceForNegativeTest(newHelper.Ownership.ProductionSnapshot);

        Assert.Equal(CleanupDisposition.AlreadyExited, oldHelper.Ownership.TryCleanup(out _));
        Assert.False(newHelper.WaitForExit(0));
        Assert.Equal(0, newHelper.Ownership.OriginalHandleTerminationCalls);
        sandbox.MarkStopped(oldHelper.Ownership);
    }

    [Fact]
    public void CleanupCallGraphGate_RejectsPidReacquiringFixtureAndAcceptsHeldLeaseCleanup()
    {
        var unsafeRoot = typeof(UnsafePidReacquiringCleanupFixture).GetMethod(
            nameof(UnsafePidReacquiringCleanupFixture.CleanupAsync),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var safeRoot = typeof(NativeHelperSandbox).GetMethod(
            nameof(IAsyncDisposable.DisposeAsync),
            BindingFlags.Instance | BindingFlags.Public)!;

        Assert.Contains(
            EnumerateReachableCalls(unsafeRoot),
            method => method.Name == nameof(IProductProcessCatalog.AcquireTerminationLeaseAsync));
        Assert.DoesNotContain(
            EnumerateReachableCalls(safeRoot),
            method => method.Name == nameof(IProductProcessCatalog.AcquireTerminationLeaseAsync));
    }

    private static bool UnsafePidAndPathOnly(ProcessSnapshot evidence, ProcessSnapshot lease) =>
        evidence.ProcessId == lease.ProcessId &&
        string.Equals(evidence.ImagePath, lease.ImagePath, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<MethodBase> EnumerateReachableCalls(MethodInfo root)
    {
        var result = new List<MethodBase>();
        var queue = new Queue<MethodBase>();
        var visited = new HashSet<(Module Module, int Token)>();
        EnqueueExecutable(root, queue);

        while (queue.TryDequeue(out var method))
        {
            if (!TryGetMethodKey(method, out var key) || !visited.Add(key))
            {
                continue;
            }

            foreach (var called in ReadDirectCalls(method))
            {
                result.Add(called);
                if (called is MethodInfo calledMethod &&
                    calledMethod.DeclaringType?.Assembly == root.DeclaringType!.Assembly)
                {
                    EnqueueExecutable(calledMethod, queue);
                }
            }
        }

        return result;
    }

    private static void EnqueueExecutable(MethodInfo method, Queue<MethodBase> queue)
    {
        queue.Enqueue(method);
        var asyncStateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        var moveNext = asyncStateMachine?.StateMachineType.GetMethod(
            nameof(IAsyncStateMachine.MoveNext),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (moveNext is not null)
        {
            queue.Enqueue(moveNext);
        }
    }

    private static bool TryGetMethodKey(MethodBase method, out (Module Module, int Token) key)
    {
        try
        {
            key = (method.Module, method.MetadataToken);
            return true;
        }
        catch (InvalidOperationException)
        {
            key = default;
            return false;
        }
    }

    private static IEnumerable<MethodBase> ReadDirectCalls(MethodBase method)
    {
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var position = 0;
        while (position < il.Length)
        {
            var opCode = ReadOpCode(il, ref position);
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, position);
                position += sizeof(int);
                MethodBase? called = null;
                try
                {
                    called = method.Module.ResolveMethod(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                }

                if (called is not null)
                {
                    yield return called;
                }

                continue;
            }

            position += GetOperandSize(opCode.OperandType, il, position);
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int position)
    {
        var first = il[position++];
        if (first != 0xFE)
        {
            return OneByteOpCodes[first];
        }

        return TwoByteOpCodes[il[position++]];
    }

    private static int GetOperandSize(OperandType operandType, byte[] il, int position) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineSig or
            OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => sizeof(int) + (BitConverter.ToInt32(il, position) * sizeof(int)),
        _ => throw new InvalidOperationException($"Unsupported IL operand type: {operandType}.")
    };

    private static readonly OpCode[] OneByteOpCodes = BuildOpCodeTable(twoByte: false);
    private static readonly OpCode[] TwoByteOpCodes = BuildOpCodeTable(twoByte: true);

    private static OpCode[] BuildOpCodeTable(bool twoByte)
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            var value = unchecked((ushort)opCode.Value);
            if ((value > byte.MaxValue) == twoByte)
            {
                table[value & byte.MaxValue] = opCode;
            }
        }

        return table;
    }

    private sealed class NativeHelperSandbox : IAsyncDisposable
    {
        private readonly string _sourceDirectory;
        private readonly string _root;
        private readonly List<NativeHelperOwnership> _ownedHelpers = [];
        private readonly List<string> _cleanupBlockers = [];
        private readonly WindowsProductProcessCatalog _catalog = new();

        private NativeHelperSandbox(string sourceDirectory, string root)
        {
            _sourceDirectory = sourceDirectory;
            _root = root;
            CopyDirectory(_sourceDirectory, _root);
        }

        internal string ExecutablePath => Path.Combine(_root, "SwSearch.Broker.exe");

        internal static NativeHelperSandbox Create()
        {
            var source = Path.Combine(AppContext.BaseDirectory, "NativeCoordinatorGateHelper");
            if (!File.Exists(Path.Combine(source, "SwSearch.Broker.exe")))
            {
                throw new FileNotFoundException("Native gate helper was not copied by the build.", source);
            }

            var root = Path.Combine(
                Path.GetTempPath(),
                "swsearch-native-coordinator-" + Guid.NewGuid().ToString("N"));
            AssertSafeTemp(root);
            Directory.CreateDirectory(root);
            return new NativeHelperSandbox(source, root);
        }

        internal async Task<StartedHelper> StartAsync(string mode)
        {
            var nonce = Guid.NewGuid().ToString("N");
            var ready = Path.Combine(_root, nonce + ".ready");
            var created = NativeCreatedProcess.CreateSuspended(
                ExecutablePath,
                _root,
                [mode, nonce, ready],
                parentProcess: null);
            var ownership = RegisterCreatedProcess(created, nonce);
            try
            {
                await AttachProductionEvidenceAsync(ownership);
                ownership.ResumeCreatedProcess();
                var fields = await WaitForReadyAsync(ready, nonce);
                Assert.Equal(created.ProcessId, int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
                ownership.ConfirmReadyProof(nonce, created.ProcessId);
                return new StartedHelper(created.ProcessId, ownership);
            }
            catch (Exception exception)
            {
                _cleanupBlockers.Add($"pid={created.ProcessId}: helper startup failed ({exception.Message})");
                throw;
            }
        }

        internal async Task<StartedParent> StartParentWithChildAsync()
        {
            var parentNonce = Guid.NewGuid().ToString("N");
            var parentReady = Path.Combine(_root, parentNonce + ".parent.ready");
            var parentCreated = NativeCreatedProcess.CreateSuspended(
                ExecutablePath,
                _root,
                ["--native-gate-helper", parentNonce, parentReady],
                parentProcess: null);
            var parentOwnership = RegisterCreatedProcess(parentCreated, parentNonce);
            try
            {
                await AttachProductionEvidenceAsync(parentOwnership);
                parentOwnership.ResumeCreatedProcess();
                var parentFields = await WaitForReadyAsync(parentReady, parentNonce);
                Assert.Equal(
                    parentCreated.ProcessId,
                    int.Parse(parentFields[1], System.Globalization.CultureInfo.InvariantCulture));
                parentOwnership.ConfirmReadyProof(parentNonce, parentCreated.ProcessId);

                var childNonce = Guid.NewGuid().ToString("N");
                var childReady = Path.Combine(_root, childNonce + ".child.ready");
                var childCreated = NativeCreatedProcess.CreateSuspended(
                    ExecutablePath,
                    _root,
                    ["--native-gate-child", childNonce, childReady],
                    parentOwnership.CreatedProcessHandle);
                var childOwnership = RegisterCreatedProcess(childCreated, childNonce);
                await AttachProductionEvidenceAsync(childOwnership);
                childOwnership.ResumeCreatedProcess();
                var childFields = await WaitForReadyAsync(childReady, childNonce);
                Assert.Equal(
                    childCreated.ProcessId,
                    int.Parse(childFields[1], System.Globalization.CultureInfo.InvariantCulture));
                childOwnership.ConfirmReadyProof(childNonce, childCreated.ProcessId);
                return new StartedParent(
                    parentCreated.ProcessId,
                    parentOwnership,
                    childCreated.ProcessId,
                    childOwnership);
            }
            catch (Exception exception)
            {
                _cleanupBlockers.Add($"pid={parentCreated.ProcessId}: parent/child startup failed ({exception.Message})");
                throw;
            }
        }

        internal void MarkStopped(NativeHelperOwnership ownership)
        {
            if (_ownedHelpers.Remove(ownership))
            {
                ownership.Dispose();
            }
        }

        internal void ReplaceExecutableWithFreshFileObject()
        {
            var tombstone = Path.Combine(_root, "old-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Move(ExecutablePath, tombstone);
            File.Copy(Path.Combine(_sourceDirectory, "SwSearch.Broker.exe"), ExecutablePath);
        }

        public async ValueTask DisposeAsync()
        {
            var cleanupFailures = new List<string>(_cleanupBlockers);
            foreach (var ownership in _ownedHelpers.ToArray())
            {
                try
                {
                    var disposition = ownership.TryCleanup(out var reason);
                    if (disposition is CleanupDisposition.Refused or CleanupDisposition.Failed)
                    {
                        cleanupFailures.Add($"pid={ownership.ProcessId}: {reason}");
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add($"pid={ownership.ProcessId}: cleanup threw {exception.Message}");
                }
                finally
                {
                    ownership.Dispose();
                }
            }

            _ownedHelpers.Clear();
            if (cleanupFailures.Count == 0)
            {
                for (var attempt = 0; attempt < 20 && Directory.Exists(_root); attempt++)
                {
                    try
                    {
                        Directory.Delete(_root, recursive: true);
                    }
                    catch (IOException) when (attempt < 19)
                    {
                        await Task.Delay(50);
                    }
                }
            }

            if (cleanupFailures.Count != 0)
            {
                throw new InvalidOperationException(
                    "Native helper cleanup refused; sandbox was preserved at " + _root + ". " +
                    string.Join("; ", cleanupFailures));
            }
        }

        private NativeHelperOwnership RegisterCreatedProcess(NativeCreatedProcess createdProcess, string nonce)
        {
            var ownership = new NativeHelperOwnership(nonce, _root, createdProcess);
            _ownedHelpers.Add(ownership);
            return ownership;
        }

        private async Task AttachProductionEvidenceAsync(NativeHelperOwnership ownership)
        {
            var acquisition = await _catalog.AcquireTerminationLeaseAsync(
                ownership.ProcessId,
                CancellationToken.None);
            if (acquisition.Status != ProcessLeaseStatus.Acquired || acquisition.Lease is null)
            {
                _cleanupBlockers.Add(
                    $"pid={ownership.ProcessId}: production evidence lease unavailable ({acquisition.Status})");
                throw new InvalidOperationException(
                    $"Could not acquire production evidence for pid={ownership.ProcessId}: {acquisition.Status}. " +
                    "The native gate must remain blocked; cleanup will use only the original suspended creation handle.");
            }

            try
            {
                ownership.AttachProductionEvidence(acquisition.Lease);
            }
            catch (Exception exception)
            {
                _cleanupBlockers.Add(
                    $"pid={ownership.ProcessId}: production evidence rejected ({exception.Message})");
                throw;
            }
        }

        private static async Task<string[]> WaitForReadyAsync(string path, string nonce)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? lastIncompleteState = null;
            try
            {
                while (true)
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(path, timeout.Token);
                        var fields = content.Split('|');
                        if (fields.Length == 2 &&
                            string.Equals(fields[0], nonce, StringComparison.Ordinal) &&
                            int.TryParse(
                                fields[1],
                                System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var processId) &&
                            processId > 0)
                        {
                            return fields;
                        }

                        lastIncompleteState =
                            $"incomplete payload length={content.Length} fields={fields.Length} nonceMatch=" +
                            (fields.Length > 0 && string.Equals(fields[0], nonce, StringComparison.Ordinal));
                    }
                    catch (FileNotFoundException)
                    {
                        lastIncompleteState = "atomic ready file not published yet";
                    }
                    catch (DirectoryNotFoundException)
                    {
                        lastIncompleteState = "sandbox directory unavailable";
                    }
                    catch (IOException exception)
                    {
                        lastIncompleteState = "transient ready sharing/I/O failure: " + exception.HResult.ToString("X8");
                    }

                    await Task.Delay(10, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Native helper did not atomically publish a complete nonce/PID ready record within 5 seconds. Last state: " +
                    (lastIncompleteState ?? "none"));
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        private static void AssertSafeTemp(string path)
        {
            var full = Path.GetFullPath(path);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("swsearch-native-coordinator-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe native gate path.");
            }
        }
    }

    private enum CleanupDisposition
    {
        Terminated,
        AlreadyExited,
        Refused,
        Failed
    }

    private sealed class NativeHelperOwnership : IDisposable
    {
        private readonly string _nonce;
        private readonly string _sandboxRoot;
        private readonly string _exactImagePath;
        private readonly NativeCreatedProcess _createdProcess;
        private IProcessTerminationLease? _productionEvidenceLease;
        private ProcessSnapshot? _creationSnapshot;
        private ProcessSnapshot? _diagnosticEvidence;
        private bool _readyProofValidated;
        private bool _disposed;

        internal NativeHelperOwnership(
            string nonce,
            string sandboxRoot,
            NativeCreatedProcess createdProcess)
        {
            _nonce = nonce;
            _sandboxRoot = Path.GetFullPath(sandboxRoot);
            _exactImagePath = Path.Combine(_sandboxRoot, "SwSearch.Broker.exe");
            _createdProcess = createdProcess;
        }

        internal int ProcessId => _createdProcess.ProcessId;

        internal SafeProcessHandle CreatedProcessHandle => _createdProcess.ProcessHandle;

        internal ProcessSnapshot ProductionSnapshot =>
            _creationSnapshot ?? throw new InvalidOperationException("Production process evidence was not attached.");

        internal int OriginalHandleTerminationCalls => _createdProcess.TerminationCalls;

        internal void AttachProductionEvidence(IProcessTerminationLease lease)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var snapshot = lease.Snapshot;
            if (snapshot.ProcessId != ProcessId ||
                !IsExactSafeSandboxImage(_sandboxRoot, _exactImagePath) ||
                !string.Equals(snapshot.ImagePath, _exactImagePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(snapshot.FileName, "SwSearch.Broker.exe", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(snapshot.ImageObjectId))
            {
                lease.Dispose();
                throw new InvalidOperationException(
                    "Production evidence does not identify the exact suspended helper created in this sandbox.");
            }

            _productionEvidenceLease = lease;
            _creationSnapshot = snapshot;
            _diagnosticEvidence = snapshot;
        }

        internal void ResumeCreatedProcess() => _createdProcess.Resume();

        internal bool WaitForExit(int milliseconds) => _createdProcess.WaitForExit(milliseconds);

        internal void ConfirmReadyProof(string nonce, int processId)
        {
            if (!string.Equals(_nonce, nonce, StringComparison.Ordinal) ||
                ProcessId != processId)
            {
                throw new InvalidOperationException("Native helper ready proof does not match its original creation handle.");
            }

            _readyProofValidated = true;
        }

        internal void OverrideDiagnosticEvidenceForNegativeTest(ProcessSnapshot untrustedEvidence) =>
            _diagnosticEvidence = untrustedEvidence;

        internal void ReleaseProductionEvidenceAnchorForNegativeTest()
        {
            _productionEvidenceLease?.Dispose();
            _productionEvidenceLease = null;
        }

        internal CleanupDisposition TryCleanup(out string reason)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Guid.TryParseExact(_nonce, "N", out _) ||
                !IsExactSafeSandboxImage(_sandboxRoot, _exactImagePath))
            {
                reason = "creation request is not bound to an exact nonce-owned sandbox image";
                return CleanupDisposition.Refused;
            }

            // The diagnostic snapshot may be stale or deliberately hostile. It is never a cleanup
            // capability: a Windows process handle remains bound to its original kernel object and
            // cannot retarget when a PID or executable path is reused.
            var result = _createdProcess.TerminateOriginalProcess();
            reason = $"{result}; ready={_readyProofValidated}; evidence={_diagnosticEvidence?.ProcessId.ToString() ?? "none"}; " +
                "target=original-create-process-handle";
            return result switch
            {
                SingleProcessTerminationResult.Terminated => CleanupDisposition.Terminated,
                SingleProcessTerminationResult.AlreadyExited => CleanupDisposition.AlreadyExited,
                _ => CleanupDisposition.Failed
            };
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _productionEvidenceLease?.Dispose();
            _createdProcess.Dispose();
        }

        private static bool IsExactSafeSandboxImage(string sandboxRoot, string imagePath)
        {
            var fullRoot = Path.GetFullPath(sandboxRoot);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullRoot).StartsWith("swsearch-native-coordinator-", StringComparison.Ordinal) &&
                string.Equals(
                    Path.GetFullPath(imagePath),
                    Path.Combine(fullRoot, "SwSearch.Broker.exe"),
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class NativeCreatedProcess : IDisposable
    {
        private const uint CreateSuspendedFlag = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private static readonly nuint ProcThreadAttributeParentProcess = 0x00020000;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private SafeWaitHandle? _initialThreadHandle;
        private bool _disposed;

        private NativeCreatedProcess(
            int processId,
            SafeProcessHandle processHandle,
            SafeWaitHandle initialThreadHandle)
        {
            ProcessId = processId;
            ProcessHandle = processHandle;
            _initialThreadHandle = initialThreadHandle;
        }

        internal int ProcessId { get; }

        internal SafeProcessHandle ProcessHandle { get; }

        internal int TerminationCalls { get; private set; }

        internal static NativeCreatedProcess CreateSuspended(
            string executablePath,
            string workingDirectory,
            IReadOnlyList<string> arguments,
            SafeProcessHandle? parentProcess)
        {
            var startup = new StartupInfoEx();
            startup.StartupInfo.cb = parentProcess is null
                ? Marshal.SizeOf<StartupInfo>()
                : Marshal.SizeOf<StartupInfoEx>();
            var attributeList = IntPtr.Zero;
            var parentValue = IntPtr.Zero;
            try
            {
                var creationFlags = CreateSuspendedFlag | CreateNoWindow;
                if (parentProcess is not null)
                {
                    nuint bytes = 0;
                    _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                    if (bytes == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    attributeList = Marshal.AllocHGlobal(checked((int)bytes));
                    if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref bytes))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    parentValue = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(parentValue, parentProcess.DangerousGetHandle());
                    if (!UpdateProcThreadAttribute(
                            attributeList,
                            0,
                            ProcThreadAttributeParentProcess,
                            parentValue,
                            (nuint)IntPtr.Size,
                            IntPtr.Zero,
                            IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    startup.AttributeList = attributeList;
                    creationFlags |= ExtendedStartupInfoPresent;
                }

                var commandLine = new StringBuilder(BuildCommandLine(executablePath, arguments));
                if (!CreateProcess(
                        executablePath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        creationFlags,
                        IntPtr.Zero,
                        workingDirectory,
                        ref startup,
                        out var processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return new NativeCreatedProcess(
                    checked((int)processInformation.ProcessId),
                    new SafeProcessHandle(processInformation.ProcessHandle, ownsHandle: true),
                    new SafeWaitHandle(processInformation.ThreadHandle, ownsHandle: true));
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

                if (parentValue != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(parentValue);
                }
            }
        }

        internal void Resume()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var thread = _initialThreadHandle ??
                throw new InvalidOperationException("The helper initial thread was already resumed.");
            if (ResumeThread(thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            thread.Dispose();
            _initialThreadHandle = null;
        }

        internal bool WaitForExit(int milliseconds)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var wait = WaitForSingleObject(ProcessHandle, checked((uint)milliseconds));
            return wait switch
            {
                WaitObject0 => true,
                WaitTimeout => false,
                _ => throw new Win32Exception(Marshal.GetLastPInvokeError())
            };
        }

        internal SingleProcessTerminationResult TerminateOriginalProcess()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var initialWait = WaitForSingleObject(ProcessHandle, 0);
            if (initialWait == WaitObject0)
            {
                return SingleProcessTerminationResult.AlreadyExited;
            }

            if (initialWait != WaitTimeout)
            {
                return SingleProcessTerminationResult.Failed;
            }

            TerminationCalls++;
            if (!TerminateProcess(ProcessHandle, 0x53575348))
            {
                return WaitForSingleObject(ProcessHandle, 0) == WaitObject0
                    ? SingleProcessTerminationResult.AlreadyExited
                    : SingleProcessTerminationResult.Failed;
            }

            return WaitForSingleObject(ProcessHandle, 5_000) == WaitObject0
                ? SingleProcessTerminationResult.Terminated
                : SingleProcessTerminationResult.Failed;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initialThreadHandle?.Dispose();
            ProcessHandle.Dispose();
        }

        private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
        {
            var values = new string[arguments.Count + 1];
            values[0] = executablePath;
            for (var index = 0; index < arguments.Count; index++)
            {
                values[index + 1] = arguments[index];
            }

            return string.Join(" ", values.Select(QuoteWindowsArgument));
        }

        private static string QuoteWindowsArgument(string value)
        {
            var result = new StringBuilder(value.Length + 2).Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('\\', (backslashes * 2) + 1).Append('"');
                    backslashes = 0;
                    continue;
                }

                result.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }

            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            internal int cb;
            internal IntPtr Reserved;
            internal IntPtr Desktop;
            internal IntPtr Title;
            internal int X;
            internal int Y;
            internal int XSize;
            internal int YSize;
            internal int XCountChars;
            internal int YCountChars;
            internal int FillAttribute;
            internal int Flags;
            internal short ShowWindow;
            internal short Reserved2;
            internal IntPtr Reserved2Pointer;
            internal IntPtr StandardInput;
            internal IntPtr StandardOutput;
            internal IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            internal StartupInfo StartupInfo;
            internal IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            internal IntPtr ProcessHandle;
            internal IntPtr ThreadHandle;
            internal uint ProcessId;
            internal uint ThreadId;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            nuint attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(SafeWaitHandle threadHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(SafeProcessHandle processHandle, uint exitCode);
    }

    private sealed class UnsafePidReacquiringCleanupFixture(IProductProcessCatalog processCatalog)
    {
        internal async ValueTask CleanupAsync(int processId)
        {
            var reacquired = await processCatalog.AcquireTerminationLeaseAsync(processId, CancellationToken.None);
            if (reacquired.Status == ProcessLeaseStatus.Acquired && reacquired.Lease is not null)
            {
                using (reacquired.Lease)
                {
                    _ = reacquired.Lease.TerminateSingleProcess();
                }
            }
        }
    }

    private sealed record StartedHelper(int ProcessId, NativeHelperOwnership Ownership)
    {
        internal bool WaitForExit(int milliseconds) => Ownership.WaitForExit(milliseconds);
    }

    private sealed record StartedParent(
        int ProcessId,
        NativeHelperOwnership Ownership,
        int ChildProcessId,
        NativeHelperOwnership ChildOwnership)
    {
        internal bool WaitForExit(int milliseconds) => Ownership.WaitForExit(milliseconds);
    }
}
