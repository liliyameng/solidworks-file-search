using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class Rc50SandboxWorkerStructuralGates
{
    [Fact]
    public void RestartLatch_IsMonotonic()
    {
        var latch = new WorkerRestartLatch();

        Assert.False(latch.Required);
        latch.Require();
        latch.Require();

        Assert.True(latch.Required);
    }

    [Fact]
    public void RestartRequirement_IsLatchedBeforeResponseWrite_AndConsumedByConnectionHandler()
    {
        var processConnection = Method(
            typeof(MetadataPipeHost),
            "ProcessConnectionAsync");
        var processCalls = Calls(MoveNext(processConnection)).ToArray();
        var processor = IndexOf(
            processCalls,
            static method =>
                method.DeclaringType == typeof(MetadataRequestProcessor) &&
                method.Name == nameof(MetadataRequestProcessor.ProcessAsync));
        var restartFlag = IndexOf(
            processCalls,
            static method =>
                method.DeclaringType == typeof(RequestProcessingResult) &&
                method.Name == "get_RequiresWorkerRestart",
            start: processor + 1);
        var latch = IndexOf(
            processCalls,
            static method =>
                method.DeclaringType == typeof(WorkerRestartLatch) &&
                method.Name == nameof(WorkerRestartLatch.Require),
            start: restartFlag + 1);
        var finalResponseWrite = LastIndexOf(
            processCalls,
            static method =>
                method.DeclaringType == typeof(PipeFrame) &&
                method.Name == nameof(PipeFrame.WriteJsonAsync));

        Assert.True(processor < restartFlag);
        Assert.True(restartFlag < latch);
        Assert.True(latch < finalResponseWrite);

        var handleConnection = Method(
            typeof(MetadataPipeHost),
            "HandleConnectionAsync");
        var handlerCalls = Calls(MoveNext(handleConnection)).ToArray();
        Assert.Contains(
            handlerCalls,
            static method =>
                method.DeclaringType == typeof(WorkerRestartLatch) &&
                method.Name == "get_Required");
        Assert.Contains(
            handlerCalls,
            static method =>
                method.DeclaringType == typeof(CancellationTokenSource) &&
                method.Name == nameof(CancellationTokenSource.Cancel));
    }

    [Fact]
    public void QueuedRequestDeadline_IsEstablishedBeforeGate_AndRecheckedBeforeParser()
    {
        var processConnection = Method(
            typeof(MetadataPipeHost),
            "ProcessConnectionAsync");
        var calls = Calls(MoveNext(processConnection)).ToArray();

        var deadlineStart = IndexOf(
            calls,
            static method =>
                method.DeclaringType == typeof(MetadataRequestDeadline) &&
                method.Name == nameof(MetadataRequestDeadline.Start));
        var gateWait = IndexOf(
            calls,
            static method =>
                method.DeclaringType == typeof(SemaphoreSlim) &&
                method.Name == nameof(SemaphoreSlim.WaitAsync),
            start: deadlineStart + 1);
        var remainingAfterGate = IndexOf(
            calls,
            static method =>
                method.DeclaringType == typeof(MetadataRequestDeadline) &&
                method.Name == nameof(MetadataRequestDeadline.RemainingMilliseconds),
            start: gateWait + 1);
        var parser = IndexOf(
            calls,
            static method =>
                method.DeclaringType == typeof(MetadataRequestProcessor) &&
                method.Name == nameof(MetadataRequestProcessor.ProcessAsync),
            start: remainingAfterGate + 1);

        Assert.True(deadlineStart < gateWait);
        Assert.True(gateWait < remainingAfterGate);
        Assert.True(remainingAfterGate < parser);
        Assert.Contains(
            calls,
            static method =>
                method.DeclaringType == typeof(MetadataPipeHost) &&
                method.Name == "QueueDeadlineExceeded");
    }

    [Fact]
    public void ExpiredQueuedRequest_ReturnsCorrelatedTimeoutWithoutWorkerRestart()
    {
        var request = new MetadataRequest(
            ContractVersions.MetadataPipe,
            Guid.NewGuid(),
            @"C:\RC50Sandbox\queued.SLDPRT",
            ForceRefresh: true,
            TimeoutMilliseconds: 50,
            PipelineLeaseId: Guid.NewGuid());
        var timeoutFactory = Method(
            typeof(MetadataPipeHost),
            "QueueDeadlineExceeded");

        var processed = Assert.IsType<RequestProcessingResult>(
            timeoutFactory.Invoke(null, [request]));

        Assert.Equal(MetadataErrorCodes.Timeout, processed.Response.ErrorCode);
        Assert.Equal(request.RequestId, processed.Response.RequestId);
        Assert.Equal(request.PipelineLeaseId, processed.Response.PipelineLeaseId);
        Assert.Equal(MetadataPipelineOutcome.Failed, processed.Response.Outcome);
        Assert.False(processed.RequiresWorkerRestart);
    }

    [Fact]
    public void DeadlineArithmetic_ConsumesElapsedQueueTimeAndNeverReplenishesIt()
    {
        var frequency = Stopwatch.Frequency;
        var deadline = new MetadataRequestDeadline(checked(frequency * 2));

        Assert.Equal(1_000, deadline.RemainingMilliseconds(frequency));
        Assert.Equal(1, deadline.RemainingMilliseconds(checked(frequency * 2 - 1)));
        Assert.Equal(0, deadline.RemainingMilliseconds(checked(frequency * 2)));
        Assert.Equal(0, deadline.RemainingMilliseconds(checked(frequency * 3)));
    }

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(type.FullName, name);

    private static MethodInfo MoveNext(MethodInfo asyncMethod)
    {
        var stateMachine = asyncMethod.GetCustomAttribute<AsyncStateMachineAttribute>()
                           ?? throw new InvalidOperationException(
                               $"{asyncMethod.DeclaringType?.Name}.{asyncMethod.Name} is not async.");
        return Method(stateMachine.StateMachineType, "MoveNext");
    }

    private static int IndexOf(
        IReadOnlyList<MethodBase> methods,
        Func<MethodBase, bool> predicate,
        int start = 0)
    {
        for (var index = Math.Max(0, start); index < methods.Count; index++)
        {
            if (predicate(methods[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static int LastIndexOf(
        IReadOnlyList<MethodBase> methods,
        Func<MethodBase, bool> predicate)
    {
        for (var index = methods.Count - 1; index >= 0; index--)
        {
            if (predicate(methods[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var typeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var index = 0; index < bytes.Length;)
        {
            ushort value = bytes[index++];
            if (value == 0xfe)
            {
                value = (ushort)(0xfe00 | bytes[index++]);
            }

            var opCode = OpCodeMap[value];
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, index);
                MethodBase? called = null;
                try
                {
                    called = method.Module.ResolveMethod(token, typeArguments, methodArguments);
                }
                catch (ArgumentException)
                {
                }

                if (called is not null)
                {
                    yield return called;
                }
            }

            index += OperandSize(opCode.OperandType, bytes, index);
        }
    }

    private static int OperandSize(OperandType operandType, byte[] bytes, int index) =>
        operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or
                OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or
                OperandType.InlineI or OperandType.InlineMethod or
                OperandType.InlineSig or OperandType.InlineString or
                OperandType.InlineTok or OperandType.InlineType or
                OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(bytes, index) * 4),
            _ => throw new InvalidOperationException($"Unsupported IL operand: {operandType}"),
        };

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodeMap =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(static opCode => unchecked((ushort)opCode.Value));
}
