using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using SwSearch.Broker;
using SwSearch.Indexer;
using SwSearch.Metadata.Worker;

namespace SwSearch.Integration.Tests;

public sealed class Rc50SandboxMetadataBoundaryCallGraphTests
{
    private static readonly Assembly BrokerAssembly = typeof(BrokerHost).Assembly;
    private static readonly Assembly IndexerAssembly = typeof(IndexingRuntime).Assembly;
    private static readonly Assembly WorkerAssembly = typeof(MetadataPipeClient).Assembly;

    [Fact]
    public void ProductionBroker_ComposesDisposableWorkerClient_NotInProcessParser()
    {
        var entryPoint = Assert.IsAssignableFrom<MethodBase>(BrokerAssembly.EntryPoint);
        var calls = ReachableMethods(
                [entryPoint],
                static method => method.DeclaringType?.Assembly == BrokerAssembly)
            .SelectMany(CalledMethods)
            .ToArray();

        Assert.Contains(
            calls,
            static method =>
                method.IsConstructor &&
                method.DeclaringType == typeof(MetadataWorkerClient));
        Assert.DoesNotContain(
            calls,
            static method =>
                method.IsConstructor &&
                method.DeclaringType == typeof(BrokerHostedMetadataClient));
        Assert.DoesNotContain(calls, IsParserOrEvidenceImplementation);
    }

    [Fact]
    public void BrokerRefreshAndIndexerExtraction_DoNotReachDocumentFileIoOrParsers()
    {
        var brokerDispatch = Assert.IsAssignableFrom<MethodBase>(
            typeof(BrokerHost).GetMethod(
                "DispatchForTestingAsync",
                BindingFlags.Instance | BindingFlags.NonPublic));
        var interactiveRefresh = Assert.IsAssignableFrom<MethodBase>(
            typeof(IndexingRuntime).GetMethod(
                nameof(IndexingRuntime.RefreshMetadataNowWithStatusAsync),
                BindingFlags.Instance | BindingFlags.Public));
        var backgroundExtraction = Assert.IsAssignableFrom<MethodBase>(
            typeof(IndexingRuntime).GetMethod(
                "ExtractLeaseAsync",
                BindingFlags.Instance | BindingFlags.NonPublic));

        var violations = new List<string>();
        foreach (var owner in ReachableMethods(
                     [brokerDispatch, interactiveRefresh, backgroundExtraction],
                     ShouldTraverseMetadataControlFlow))
        {
            foreach (var called in CalledMethods(owner))
            {
                if (IsDocumentFileIo(called) || IsParserOrEvidenceImplementation(called))
                {
                    violations.Add(
                        $"{Describe(owner)} -> {Describe(called)}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Broker/Indexer metadata control flow reached document file I/O or parsing:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations.Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    public void EvidenceAndParserImplementations_AreOwnedByMetadataWorkerAssembly()
    {
        var requiredWorkerTypes = new[]
        {
            "SwSearch.Metadata.Worker.MetadataFilePipeline",
            "SwSearch.Metadata.Worker.MetadataRequestProcessor",
            "SwSearch.Metadata.Worker.NoApiMetadataExtractor",
            "SwSearch.Metadata.Worker.SolidWorksXmlMetadataParser",
            "SwSearch.Metadata.Worker.CompoundFileMetadataReader",
            "SwSearch.Metadata.Worker.ModernSolidWorksMetadataReader",
        };

        foreach (var typeName in requiredWorkerTypes)
        {
            var type = WorkerAssembly.GetType(typeName, throwOnError: false);
            Assert.NotNull(type);
            Assert.Same(WorkerAssembly, type.Assembly);
            Assert.Null(BrokerAssembly.GetType(typeName, throwOnError: false));
            Assert.Null(IndexerAssembly.GetType(typeName, throwOnError: false));
        }
    }

    private static bool ShouldTraverseMetadataControlFlow(MethodBase method)
    {
        var type = method.DeclaringType;
        if (type is null)
        {
            return false;
        }

        if (type.Assembly == BrokerAssembly)
        {
            return type == typeof(BrokerHost) || IsNestedWithin(type, typeof(BrokerHost));
        }

        if (type.Assembly != IndexerAssembly)
        {
            return false;
        }

        return type == typeof(IndexingRuntime) ||
               IsNestedWithin(type, typeof(IndexingRuntime)) ||
               type.FullName == "SwSearch.Indexer.StableFileEvidenceReader";
    }

    private static bool IsNestedWithin(Type candidate, Type owner)
    {
        for (var current = candidate.DeclaringType; current is not null; current = current.DeclaringType)
        {
            if (current == owner)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDocumentFileIo(MethodBase method)
    {
        if (method.DeclaringType == typeof(File))
        {
            return method.Name is
                nameof(File.GetAttributes) or
                nameof(File.Exists) or
                nameof(File.Open) or
                nameof(File.OpenRead) or
                nameof(File.ReadAllBytes) or
                nameof(File.ReadAllText);
        }

        return method.DeclaringType == typeof(FileInfo);
    }

    private static bool IsParserOrEvidenceImplementation(MethodBase method)
    {
        var typeName = method.DeclaringType?.FullName ?? string.Empty;
        return typeName is
                   "SwSearch.Indexer.StableFileEvidenceReader" or
                   "SwSearch.Metadata.Worker.MetadataFilePipeline" or
                   "SwSearch.Metadata.Worker.NoApiMetadataExtractor" or
                   "SwSearch.Metadata.Worker.SolidWorksXmlMetadataParser" or
                   "SwSearch.Metadata.Worker.CompoundFileMetadataReader" or
                   "SwSearch.Metadata.Worker.ModernSolidWorksMetadataReader";
    }

    private static IEnumerable<MethodBase> ReachableMethods(
        IEnumerable<MethodBase> roots,
        Func<MethodBase, bool> shouldTraverse)
    {
        var pending = new Stack<MethodBase>(roots);
        var visited = new HashSet<MethodBase>();
        while (pending.TryPop(out var method))
        {
            if (!visited.Add(method))
            {
                continue;
            }

            yield return method;
            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } asyncStateMachine)
            {
                var moveNext = asyncStateMachine.StateMachineType.GetMethod(
                    "MoveNext",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (moveNext is not null)
                {
                    pending.Push(moveNext);
                }
            }

            foreach (var called in CalledMethods(method))
            {
                if (shouldTraverse(called))
                {
                    pending.Push(called);
                }
            }
        }
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method) =>
        DecodeInstructions(method)
            .Select(static instruction => instruction.CalledMethod)
            .OfType<MethodBase>();

    private static IEnumerable<IlInstruction> DecodeInstructions(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var typeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var index = 0; index < bytes.Length;)
        {
            var offset = index;
            ushort value = bytes[index++];
            if (value == 0xfe)
            {
                value = (ushort)(0xfe00 | bytes[index++]);
            }

            var opCode = OpCodeMap[value];
            MethodBase? calledMethod = null;
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, index);
                try
                {
                    calledMethod = method.Module.ResolveMethod(token, typeArguments, methodArguments);
                }
                catch (ArgumentException)
                {
                    // An unresolved constructed generic is not a direct file or
                    // parser call; all concrete calls remain visible.
                }
            }

            index += OperandSize(opCode.OperandType, bytes, index);
            yield return new IlInstruction(offset, opCode, calledMethod);
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

    private static string Describe(MethodBase method) =>
        $"{method.DeclaringType?.FullName ?? "<global>"}.{method.Name}";

    private readonly record struct IlInstruction(
        int Offset,
        OpCode OpCode,
        MethodBase? CalledMethod);

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodeMap =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(static opCode => unchecked((ushort)opCode.Value));
}
