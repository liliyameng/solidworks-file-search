using SwSearch.InstallerCoordinator;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Xunit;

namespace SwSearch.InstallerCoordinator.Tests;

public sealed class InstallerCoordinatorCallGraphTests
{
    [Fact]
    public void ProductionUserIpcGraph_HasShutdownOnlyAndNoProcessOrTerminationReachability()
    {
        var root = typeof(ProductionInstallerCoordinatorPhaseExecutor).GetMethod(
            nameof(IInstallerCoordinatorPhaseExecutor.ExecuteUserIpcAsync),
            BindingFlags.Instance | BindingFlags.Public)!;
        var calls = EnumerateReachableCalls(root);

        Assert.Contains(calls, method => method.Name == nameof(IProductShutdownTransport.ShutdownBrokerAsync));
        Assert.Contains(calls, method => method.Name == nameof(IProductShutdownTransport.ShutdownWorkerAsync));
        Assert.DoesNotContain(calls, IsForbiddenFromUserIpcGraph);
    }

    [Fact]
    public void ProductionPrivilegedGraph_HasStrictHandlesAndNoPipeOrShutdownTransportReachability()
    {
        var root = typeof(ProductionInstallerCoordinatorPhaseExecutor).GetMethod(
            nameof(IInstallerCoordinatorPhaseExecutor.ExecutePrivilegedHandleAsync),
            BindingFlags.Instance | BindingFlags.Public)!;
        var calls = EnumerateReachableCalls(root);

        Assert.Contains(calls, method => method.Name == nameof(IProductProcessCatalog.SnapshotAsync));
        Assert.Contains(calls, method => method.Name == nameof(IProductProcessCatalog.AcquireTerminationLeaseAsync));
        Assert.Contains(calls, method => method.Name == nameof(IProcessTerminationLease.TerminateSingleProcess));
        Assert.DoesNotContain(calls, IsForbiddenFromPrivilegedGraph);
    }

    [Fact]
    public void DeliberatelyCrossWiredFixturesAreRejectedByTheProductionRules()
    {
        var unsafeUser = typeof(UnsafeCrossWiredFixtures).GetMethod(
            nameof(UnsafeCrossWiredFixtures.UserProductionEntryCallsStrictHandleGraph),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var unsafePrivileged = typeof(UnsafeCrossWiredFixtures).GetMethod(
            nameof(UnsafeCrossWiredFixtures.PrivilegedProductionEntryCallsCurrentUserTransport),
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var unsafeUserCalls = EnumerateReachableCalls(unsafeUser);
        Assert.Contains(unsafeUserCalls, method => method.DeclaringType == typeof(WindowsProductProcessCatalog));
        Assert.Contains(unsafeUserCalls, method => method.Name == nameof(IProductProcessCatalog.SnapshotAsync));
        Assert.Contains(unsafeUserCalls, method => method.Name == nameof(IProductProcessCatalog.AcquireTerminationLeaseAsync));
        Assert.Contains(unsafeUserCalls, method => method.Name == nameof(IProcessTerminationLease.TerminateSingleProcess));
        Assert.Contains(unsafeUserCalls, IsForbiddenFromUserIpcGraph);

        var unsafePrivilegedCalls = EnumerateReachableCalls(unsafePrivileged);
        Assert.Contains(unsafePrivilegedCalls, method => method.DeclaringType == typeof(ProductShutdownTransport));
        Assert.Contains(unsafePrivilegedCalls, method => method.Name == nameof(IProductShutdownTransport.ShutdownBrokerAsync));
        Assert.Contains(unsafePrivilegedCalls, IsForbiddenFromPrivilegedGraph);
    }

    [Fact]
    public void MutexOwnershipGraph_IsConfinedToOneNonAsyncOwnerScopeAndRejectsOldAsyncPattern()
    {
        var ownerScope = typeof(InstallerCoordinationOwnerRunner).GetMethod(
            "RunOwnerScope",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var hostRun = typeof(InstallerCoordinatorHost).GetMethod(
            "RunAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var productionMethods = AllDeclaredMethods(typeof(InstallerCoordinatorHost).Assembly).ToArray();

        Assert.Null(ownerScope.GetCustomAttribute<AsyncStateMachineAttribute>());
        var ownerCalls = ReadDirectCalls(ownerScope).ToArray();
        Assert.Contains(ownerCalls, IsMutexFactoryAcquire);
        Assert.Contains(ownerCalls, IsOwnerThreadRelease);

        var acquireCallers = productionMethods
            .Where(method => ReadDirectCalls(method).Any(IsMutexFactoryAcquire))
            .ToArray();
        var releaseCallers = productionMethods
            .Where(method => ReadDirectCalls(method).Any(IsOwnerThreadRelease))
            .ToArray();
        Assert.Equal([ownerScope], acquireCallers);
        Assert.Equal([ownerScope], releaseCallers);

        var hostCalls = ReadExecutableDirectCalls(hostRun).ToArray();
        Assert.Contains(hostCalls, method =>
            method.DeclaringType == typeof(IInstallerCoordinationOwnerRunner) &&
            method.Name == nameof(IInstallerCoordinationOwnerRunner.RunExclusiveAsync));
        Assert.DoesNotContain(hostCalls, IsMutexFactoryAcquire);
        Assert.DoesNotContain(hostCalls, IsOwnerThreadRelease);

        var productionAsyncOwners = productionMethods
            .Where(method => method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null)
            .Where(HasDirectAcquireAndReleaseAcrossAwait)
            .ToArray();
        Assert.Empty(productionAsyncOwners);

        var unsafeOldPattern = typeof(UnsafeAsyncOwnerFixture).GetMethod(
            nameof(UnsafeAsyncOwnerFixture.AcquireAwaitThenRelease),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(HasDirectAcquireAndReleaseAcrossAwait(unsafeOldPattern));
    }

    [Fact]
    public void HardDeadlineFiringGraph_TerminatesFirstWithoutManagedIoOrWaits_AndUnsafeFixtureIsRejected()
    {
        var watchdogType = typeof(SelfProcessHardDeadlineWatchdogFactory).GetNestedType(
            "SelfProcessHardDeadlineWatchdog",
            BindingFlags.NonPublic)!;
        var firingEntry = watchdogType.GetMethod(
            "TryFireHardDeadline",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var calls = EnumerateReachableCalls(firingEntry);
        var directCalls = ReadDirectCalls(firingEntry).ToArray();

        Assert.Contains(calls, method => method.Name == "GetCurrentProcess");
        Assert.Contains(calls, method => method.Name == "TerminateProcess");
        Assert.Contains(calls, method =>
            method.DeclaringType == typeof(Interlocked) &&
            method.Name == nameof(Interlocked.CompareExchange));
        Assert.DoesNotContain(calls, IsForbiddenFromHardDeadlineFiringGraph);
        Assert.True(
            Array.FindIndex(directCalls, method => method.Name == "TerminateProcess") <
            Array.FindIndex(directCalls, method =>
                method.DeclaringType == typeof(Environment) && method.Name == nameof(Environment.FailFast)));

        var unsafeFiring = typeof(UnsafeWatchdogFiringFixture).GetMethod(
            nameof(UnsafeWatchdogFiringFixture.LogAndWaitBeforeTermination),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var unsafeCalls = EnumerateReachableCalls(unsafeFiring);
        Assert.Contains(unsafeCalls, IsForbiddenFromHardDeadlineFiringGraph);
    }

    [Fact]
    public void HardDeadlineDGate_HoldsOpenedExistingMutexAcrossExitAndRejectsExitThenCreateFallback()
    {
        var gateRoot = typeof(InstallerCoordinationHardDeadlineNativeGateTests).GetMethod(
            "RunHelperAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var exactAccess = typeof(InstallerCoordinationHardDeadlineNativeGateTests).GetField(
            "ExactMutexAccess",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(0x00120001u, (uint)exactAccess.GetRawConstantValue()!);
        var gateCalls = ReadExecutableDirectCalls(gateRoot).ToArray();
        Assert.True(HasAnchoredDGateOrder(gateCalls));

        var gateReachability = EnumerateReachableCalls(gateRoot);
        Assert.Contains(gateReachability, method => method.Name == "OpenMutex");
        Assert.Contains(gateReachability, method =>
            method.DeclaringType == typeof(InstallerCoordinationMutex) &&
            method.Name == nameof(InstallerCoordinationMutex.TryAcquire));
        Assert.Contains(gateReachability, method =>
            method.DeclaringType == typeof(IInstallerCoordinationLease) &&
            method.Name == nameof(IInstallerCoordinationLease.ReleaseOnOwnerThread));

        var anchorType = typeof(InstallerCoordinationHardDeadlineNativeGateTests).GetNestedType(
            "MutexObjectAnchor",
            BindingFlags.NonPublic)!;
        var openAnchor = anchorType.GetMethod("OpenAfterReady", BindingFlags.Static | BindingFlags.NonPublic)!;
        var validateAnchor = anchorType.GetMethod(
            "ValidateAndReleaseOnOwnerThread",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var openCalls = ReadDirectCalls(openAnchor).ToArray();
        var validateCalls = ReadDirectCalls(validateAnchor).ToArray();
        Assert.Contains(openCalls, method => method.Name == "OpenMutex");
        Assert.DoesNotContain(openCalls, method =>
            method.DeclaringType == typeof(InstallerCoordinationMutex) &&
            method.Name == nameof(InstallerCoordinationMutex.TryAcquire));
        Assert.True(
            Array.FindIndex(validateCalls, method =>
                method.DeclaringType == typeof(InstallerCoordinationMutex) &&
                method.Name == nameof(InstallerCoordinationMutex.TryAcquire)) <
            Array.FindIndex(validateCalls, method =>
                method.DeclaringType == typeof(IInstallerCoordinationLease) &&
                method.Name == nameof(IInstallerCoordinationLease.ReleaseOnOwnerThread)));

        var unsafeRoot = typeof(UnsafeDGateFixture).GetMethod(
            nameof(UnsafeDGateFixture.WaitForExitThenCreateNewMutex),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.False(HasAnchoredDGateOrder(ReadExecutableDirectCalls(unsafeRoot).ToArray()));
    }

    private static bool IsForbiddenFromUserIpcGraph(MethodBase method)
    {
        var type = method.DeclaringType;
        return type == typeof(WindowsProductProcessCatalog) ||
               type == typeof(InstallationCoordinator) ||
               type == typeof(IProductProcessCatalog) ||
               type == typeof(IProcessTerminationLease) ||
               method.Name is nameof(IProductProcessCatalog.SnapshotAsync) or
                   nameof(IProductProcessCatalog.AcquireTerminationLeaseAsync) or
                   nameof(IProcessTerminationLease.TerminateSingleProcess) or
                   "ReconcileComponentAsync";
    }

    private static bool IsForbiddenFromPrivilegedGraph(MethodBase method)
    {
        var type = method.DeclaringType;
        return type == typeof(ProductShutdownTransport) ||
               type == typeof(CurrentUserIpcShutdownCoordinator) ||
               type == typeof(IProductShutdownTransport) ||
               type == typeof(NamedPipeClientStream) ||
               (type?.Namespace?.StartsWith("System.IO.Pipes", StringComparison.Ordinal) ?? false) ||
               method.Name is nameof(IProductShutdownTransport.ShutdownBrokerAsync) or
                   nameof(IProductShutdownTransport.ShutdownWorkerAsync);
    }

    private static bool IsForbiddenFromHardDeadlineFiringGraph(MethodBase method)
    {
        var type = method.DeclaringType;
        return type == typeof(Console) ||
               type == typeof(Monitor) ||
               type == typeof(ICoordinatorLog) ||
               (type?.Namespace?.StartsWith("System.IO", StringComparison.Ordinal) ?? false) ||
               method.Name is "Wait" or "WaitOne" or "WaitAny" or "WaitAll" or
                   "Join" or "Sleep" or "Enter" or "TryEnter";
    }

    private static bool HasAnchoredDGateOrder(IReadOnlyList<MethodBase> calls)
    {
        var ready = FindCall(calls, "WaitForExactReadyAsync");
        var openExisting = FindCall(calls, "OpenAfterReady");
        var acknowledge = FindCall(calls, "PublishAnchorAcknowledgement");
        var waitExit = FindCall(calls, nameof(Process.WaitForExitAsync));
        var markExited = FindCall(calls, "MarkHelperExited");
        var validate = FindCall(calls, "ValidateAndReleaseOnOwnerThread");
        var disposeAnchor = FindCall(calls, nameof(IDisposable.Dispose), "MutexObjectAnchor");
        return ready >= 0 &&
               ready < openExisting &&
               openExisting < acknowledge &&
               acknowledge < waitExit &&
               waitExit < markExited &&
               markExited < validate &&
               validate < disposeAnchor;
    }

    private static int FindCall(
        IReadOnlyList<MethodBase> calls,
        string methodName,
        string? declaringTypeName = null)
    {
        for (var index = 0; index < calls.Count; index++)
        {
            var method = calls[index];
            if (method.Name == methodName &&
                (declaringTypeName is null || method.DeclaringType?.Name == declaringTypeName))
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<MethodBase> EnumerateReachableCalls(MethodInfo root)
    {
        var result = new List<MethodBase>();
        var queue = new Queue<MethodBase>();
        var visited = new HashSet<(Module Module, int Token)>();
        var rootAssembly = root.DeclaringType!.Assembly;
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
                if (called.DeclaringType?.Assembly == rootAssembly)
                {
                    EnqueueExecutable(called, queue);
                }
            }
        }

        return result;
    }

    private static bool IsMutexFactoryAcquire(MethodBase method) =>
        method.DeclaringType == typeof(IInstallerCoordinationMutexFactory) &&
        method.Name == nameof(IInstallerCoordinationMutexFactory.TryAcquire);

    private static bool IsOwnerThreadRelease(MethodBase method) =>
        method.DeclaringType == typeof(IInstallerCoordinationLease) &&
        method.Name == nameof(IInstallerCoordinationLease.ReleaseOnOwnerThread);

    private static bool HasDirectAcquireAndReleaseAcrossAwait(MethodInfo method)
    {
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is null)
        {
            return false;
        }

        var calls = ReadExecutableDirectCalls(method).ToArray();
        return calls.Any(IsMutexFactoryAcquire) && calls.Any(IsOwnerThreadRelease);
    }

    private static IEnumerable<MethodBase> ReadExecutableDirectCalls(MethodInfo method)
    {
        foreach (var called in ReadDirectCalls(method))
        {
            yield return called;
        }

        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var moveNext = stateMachine?.GetMethod(
            nameof(IAsyncStateMachine.MoveNext),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (moveNext is null)
        {
            yield break;
        }

        foreach (var called in ReadDirectCalls(moveNext))
        {
            yield return called;
        }
    }

    private static IEnumerable<MethodInfo> AllDeclaredMethods(Assembly assembly) =>
        assembly.GetTypes().SelectMany(type => type.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly));

    private static void EnqueueExecutable(MethodBase method, Queue<MethodBase> queue)
    {
        queue.Enqueue(method);
        if (method is not MethodInfo methodInfo)
        {
            return;
        }

        var stateMachine = methodInfo.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var moveNext = stateMachine?.GetMethod(
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
        var il = method.GetMethodBody()?.GetILAsByteArray();
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
        return first == 0xFE ? TwoByteOpCodes[il[position++]] : OneByteOpCodes[first];
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

    private static class UnsafeCrossWiredFixtures
    {
        internal static async ValueTask UserProductionEntryCallsStrictHandleGraph(
            IProductProcessCatalog catalog,
            IProcessTerminationLease lease,
            CancellationToken cancellationToken)
        {
            _ = new WindowsProductProcessCatalog();
            _ = await catalog.SnapshotAsync(cancellationToken);
            _ = await catalog.AcquireTerminationLeaseAsync(1234, cancellationToken);
            _ = lease.TerminateSingleProcess();
        }

        internal static async ValueTask<ShutdownRequestResult> PrivilegedProductionEntryCallsCurrentUserTransport(
            IReadOnlyList<InstalledProductIdentity> products,
            CancellationToken cancellationToken)
        {
            _ = new NamedPipeClientStream(
                ".",
                "unsafe.fixture",
                PipeDirection.InOut,
                PipeOptions.CurrentUserOnly);
            var transport = new ProductShutdownTransport(TimeSpan.FromMilliseconds(1));
            return await transport.ShutdownBrokerAsync(
                products,
                new ShutdownRequestContext(
                    "S-1-5-21-111111111-222222222-333333333-1001",
                    1,
                    Guid.Parse("87D8EC95-3AD7-4806-885B-264101E6D332")),
                cancellationToken);
        }
    }

    private static class UnsafeAsyncOwnerFixture
    {
        internal static async Task AcquireAwaitThenRelease(
            IInstallerCoordinationMutexFactory factory,
            InstallerCoordinatorCommand command,
            Func<CancellationToken, Task> phase,
            ICoordinatorLog log,
            CancellationToken cancellationToken)
        {
            var acquisition = factory.TryAcquire(
                command.UpgradeCode,
                command.InstallUserSid,
                TimeSpan.FromSeconds(1),
                log);
            if (acquisition.Lease is null)
            {
                return;
            }

            try
            {
                await phase(cancellationToken);
            }
            finally
            {
                acquisition.Lease.ReleaseOnOwnerThread();
            }
        }
    }

    private static class UnsafeWatchdogFiringFixture
    {
        internal static void LogAndWaitBeforeTermination()
        {
            Console.Error.WriteLine("unsafe-firing-fixture");
            File.WriteAllText("unsafe-fixture-not-executed", "unsafe");
            Thread.CurrentThread.Join(1);
        }
    }

    private static class UnsafeDGateFixture
    {
        internal static async Task WaitForExitThenCreateNewMutex(
            Process helper,
            InstallerCoordinatorCommand command,
            ICoordinatorLog log)
        {
            await helper.WaitForExitAsync();
            var acquisition = InstallerCoordinationMutex.TryAcquire(
                command.UpgradeCode,
                command.InstallUserSid,
                TimeSpan.FromMilliseconds(100),
                log);
            acquisition.Lease?.ReleaseOnOwnerThread();
        }
    }
}
