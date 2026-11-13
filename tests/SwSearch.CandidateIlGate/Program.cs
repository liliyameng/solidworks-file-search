using System.Collections;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SwSearch.Testing;

namespace SwSearch.CandidateIlGate;

internal static class Program
{
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => opcode.Value);

    private static int Main(string[] args)
    {
        if (!RunGateSelfTests())
        {
            return 1;
        }

        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: SwSearch.CandidateIlGate <CAB SwSearch.App.dll> <CAB SwSearch.Contracts.dll> <payload dependency directory>");
            return 2;
        }

        var appPath = Path.GetFullPath(args[0]);
        var contractsPath = Path.GetFullPath(args[1]);
        var dependencyDirectory = Path.GetFullPath(args[2]);
        if (!File.Exists(appPath) || !File.Exists(contractsPath) || !Directory.Exists(dependencyDirectory))
        {
            Console.Error.WriteLine("Candidate App/Contracts/dependency input is missing.");
            return 2;
        }

        var context = new PayloadLoadContext(dependencyDirectory);
        try
        {
            var assembly = context.LoadFromAssemblyPath(appPath);
            var contracts = context.LoadFromAssemblyPath(contractsPath);
            var viewModel = RequireType(assembly, "SwSearch.App.MainViewModel");
            var client = RequireType(assembly, "SwSearch.App.BrokerSearchClient");
            var app = RequireType(assembly, "SwSearch.App.App");
            var window = RequireType(assembly, "SwSearch.App.MainWindow");
            var bridge = RequireType(assembly, "SwSearch.App.DesktopWebBridge");
            if (assembly.GetType("SwSearch.App.SettingsWindow", throwOnError: false) is not null ||
                assembly.GetType("SwSearch.App.SettingsDialog", throwOnError: false) is not null)
            {
                Console.Error.WriteLine("Candidate restored a retired standalone Settings window/dialog.");
                return 1;
            }
            var xamlCommandNames = GetXamlCommandNames(assembly);
            var xamlCommandRoots = xamlCommandNames
                .Select(name => ResolveCommandTarget(viewModel, name))
                .ToArray();
            var startup = RequireMethod(
                app,
                "OnStartup",
                BindingFlags.Instance | BindingFlags.NonPublic,
                typeof(System.Windows.StartupEventArgs));
            // The App's initial SearchAsync and RetryConnection paths both enter
            // EnsureRecoveryRound. Its production recovery worker calls RecoverBrokerAsync,
            // which in turn calls this minimal Broker-launch boundary. This deliberately
            // excludes StartRecoveryForTestingAsync and cache/diagnostic-only work.
            var brokerLaunchRoot = RequireMethod(
                client,
                "LaunchBrokerAsync",
                BindingFlags.Instance | BindingFlags.NonPublic,
                typeof(CancellationToken));
            var roots = new MethodBase[] { startup }
                .Concat(RequireConstructors(window, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Cast<MethodBase>())
                .Concat(RequireConstructors(viewModel, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                .Cast<MethodBase>()
                .Append(RequireMethod(
                    viewModel,
                    "ApplyFolderScope",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    typeof(string)))
                .Append(brokerLaunchRoot)
                .Append(RequireMethod(
                    window,
                    "MainWindow_OnClosed",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    typeof(object),
                    typeof(EventArgs)))
                .Append(RequireMethod(bridge, "OpenPreferences", BindingFlags.Instance | BindingFlags.NonPublic))
                .Concat(xamlCommandRoots);
            var calls = ApplicationCallGraph(roots, assembly, contracts).ToArray();
            var forbidden = calls.Where(method => IsForbiddenSynchronousIo(method) || IsForbiddenBlockingWait(method)).ToArray();
            if (forbidden.Length != 0)
            {
                Console.Error.WriteLine("Candidate App.OnStartup/window construction call graph contains synchronous I/O or waits: " +
                                        string.Join("; ", FindForbiddenCallPaths(
                                            roots,
                                            method => IsForbiddenSynchronousIo(method) || IsForbiddenBlockingWait(method),
                                            assembly,
                                            contracts)));
                return 1;
            }
            var commandCalls = ApplicationCallGraph(xamlCommandRoots, assembly, contracts).ToArray();
            var forbiddenCommandCalls = commandCalls.Where(method =>
                IsForbiddenSynchronousIo(method) ||
                IsForbiddenBlockingWait(method) ||
                method.DeclaringType?.FullName == "System.Diagnostics.Process").ToArray();
            if (forbiddenCommandCalls.Length != 0)
            {
                Console.Error.WriteLine("Candidate XAML Command call graph contains synchronous I/O, Process, or waits: " +
                                        string.Join(", ", forbiddenCommandCalls.Select(Format)));
                return 1;
            }
            if (!calls.Any(method => Format(method) == "SwSearch.App.BackgroundSettingsLoader.Start"))
            {
                Console.Error.WriteLine("Candidate App.OnStartup does not start the production background settings loader.");
                return 1;
            }
            var searchPath = RequireType(contracts, "SwSearch.Contracts.SearchPath");
            var normalize = RequireMethod(
                searchPath,
                "NormalizeFolder",
                BindingFlags.Static | BindingFlags.Public,
                typeof(string));
            var contains = RequireMethod(
                searchPath,
                "Contains",
                BindingFlags.Static | BindingFlags.Public,
                typeof(string),
                typeof(string));
            var lexicalCalls = ApplicationCallGraph(new MethodBase[] { normalize, contains }, assembly, contracts).ToArray();
            var forbiddenPathApis = lexicalCalls.Where(method =>
                (method.DeclaringType?.FullName == "System.IO.Path" &&
                 (method.Name == "GetFullPath" || method.Name == "IsPathRooted" || method.Name == "IsPathFullyQualified")) ||
                (method.DeclaringType?.FullName == "System.String" && method.Name == "Normalize"))
                .ToArray();
            if (forbiddenPathApis.Length != 0)
            {
                Console.Error.WriteLine("Candidate Contracts lexical scope still calls platform path resolution: " +
                                        string.Join(", ", forbiddenPathApis.Select(Format)));
                return 1;
            }

            var goldenCount = 0;
            foreach (var item in WindowsScopePathGolden.Cases())
            {
                try
                {
                    var actual = (string)normalize.Invoke(null, new object[] { item.Input })!;
                    if (!item.IsValid || actual != item.Expected)
                    {
                        Console.Error.WriteLine($"Candidate Contracts golden mismatch: {item.Name} / {actual}");
                        return 1;
                    }
                }
                catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException)
                {
                    if (item.IsValid)
                    {
                        Console.Error.WriteLine($"Candidate Contracts rejected valid golden path: {item.Name}");
                        return 1;
                    }
                }
                goldenCount++;
            }

            var startupElapsed = ProbeHeadlessSettingsBootstrap(assembly);

            Console.WriteLine($"PASS candidate App.OnStartup/window synchronous I/O and waits: 0; " +
                              $"recursive calls={calls.Distinct().Count()}; " +
                              $"XAML commands={xamlCommandNames.Count}/{xamlCommandNames.Count} " +
                              $"({string.Join(",", xamlCommandNames)}), command sync I/O/Process/waits=0; " +
                              $"retired standalone SettingsWindow/SettingsDialog absent; in-page preferences bridge checked; " +
                              $"headless background-settings Start={startupElapsed.TotalMilliseconds:F3}ms; " +
                              $"Contracts lexical golden: {goldenCount}/{goldenCount}");
            Console.WriteLine("Candidate CAB App SHA256=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(appPath))));
            Console.WriteLine("Candidate CAB Contracts SHA256=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(contractsPath))));
            return 0;
        }
        catch (MissingMethodException exception)
        {
            Console.Error.WriteLine("Candidate required reflection root is missing: " + exception.Message);
            return 1;
        }
        finally
        {
            if (context.IsCollectible)
            {
                context.Unload();
            }
        }
    }

    private static Type RequireType(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: false)
        ?? throw new TypeLoadException($"Required candidate type is missing: {fullName} in {assembly.FullName}.");

    private static MethodInfo RequireMethod(
        Type declaringType,
        string name,
        BindingFlags bindingFlags,
        params Type[] parameterTypes)
    {
        var method = declaringType.GetMethod(
            name,
            bindingFlags,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        return method ?? throw new MissingMethodException(
            declaringType.FullName,
            $"{name}({string.Join(", ", parameterTypes.Select(type => type.FullName ?? type.Name))})");
    }

    private static IReadOnlyList<ConstructorInfo> RequireConstructors(Type declaringType, BindingFlags bindingFlags)
    {
        var constructors = declaringType.GetConstructors(bindingFlags);
        return constructors.Length != 0
            ? constructors
            : throw new MissingMethodException(declaringType.FullName, ".ctor");
    }

    private static IEnumerable<MethodBase> ApplicationCallGraph(
        IEnumerable<MethodBase> roots,
        params Assembly[] ownedAssemblies)
    {
        var owned = ownedAssemblies.ToHashSet();
        var pending = new Stack<MethodBase>(roots);
        var visited = new HashSet<MethodBase>();
        while (pending.Count != 0)
        {
            var method = pending.Pop();
            if (!visited.Add(method)) continue;
            var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            var moveNext = stateMachine?.GetMethod(
                "MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext is not null) pending.Push(moveNext);
            foreach (var called in CalledMethods(method))
            {
                if (IsAsyncAwaiterContinuation(method, called)) continue;
                yield return called;
                if (called.DeclaringType?.FullName == "SwSearch.App.AppIsolatedOperationHost")
                {
                    // This entry point runs only in a separately-created, job-owned helper process.
                    // Its file/registry I/O is deliberately outside every WPF/UI call graph.
                    continue;
                }
                var assembly = called.DeclaringType?.Assembly;
                if (assembly is not null && owned.Contains(assembly)) pending.Push(called);
            }
        }
    }

    private static IEnumerable<string> FindForbiddenCallPaths(
        IEnumerable<MethodBase> roots,
        Func<MethodBase, bool> isForbidden,
        params Assembly[] ownedAssemblies)
    {
        var owned = ownedAssemblies.ToHashSet();
        var pending = new Stack<(MethodBase Method, IReadOnlyList<MethodBase> Path)>(
            roots.Select(root => (root, (IReadOnlyList<MethodBase>)new[] { root })));
        var visited = new HashSet<MethodBase>();
        while (pending.Count != 0)
        {
            var (method, path) = pending.Pop();
            if (!visited.Add(method)) continue;
            var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            var moveNext = stateMachine?.GetMethod(
                "MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext is not null)
            {
                pending.Push((moveNext, path.Append(moveNext).ToArray()));
            }
            foreach (var called in CalledMethods(method))
            {
                if (IsAsyncAwaiterContinuation(method, called)) continue;
                var calledPath = path.Append(called).ToArray();
                if (isForbidden(called))
                {
                    yield return string.Join(" -> ", calledPath.Select(Format));
                }
                if (called.DeclaringType?.FullName == "SwSearch.App.AppIsolatedOperationHost")
                {
                    continue;
                }
                var assembly = called.DeclaringType?.Assembly;
                if (assembly is not null && owned.Contains(assembly))
                {
                    pending.Push((called, calledPath));
                }
            }
        }
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        var module = method.Module;
        var typeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var offset = 0; offset < bytes.Length;)
        {
            var first = bytes[offset++];
            var key = first == 0xFE ? (short)(0xFE00 | bytes[offset++]) : (short)first;
            if (!OpCodesByValue.TryGetValue(key, out var opcode))
                throw new InvalidOperationException($"Unknown IL opcode 0x{(ushort)key:X4} in {Format(method)}.");
            if (opcode.OperandType == OperandType.InlineMethod &&
                (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj))
            {
                var token = BitConverter.ToInt32(bytes, offset);
                MethodBase? called = null;
                try { called = module.ResolveMethod(token, typeArguments, methodArguments); }
                catch (ArgumentException) { }
                catch (FileNotFoundException) { }
                catch (FileLoadException) { }
                if (called is not null) yield return called;
            }
            offset += OperandSize(opcode.OperandType, bytes, offset);
        }
    }

    private static IReadOnlyList<string> GetXamlCommandNames(Assembly assembly)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.EndsWith(".g.resources", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException("Candidate WPF resource stream is missing: " + resourceName);
            using var reader = new ResourceReader(stream);
            var entries = reader.GetEnumerator();
            while (entries.MoveNext())
            {
                if (entries.Key is not string key ||
                    !key.EndsWith(".baml", StringComparison.OrdinalIgnoreCase) ||
                    entries.Value is not Stream value)
                {
                    continue;
                }

                using var copy = new MemoryStream();
                value.CopyTo(copy);
                foreach (var name in ExtractCommandNames(copy.ToArray())) names.Add(name);
            }
        }
        return names.ToArray();
    }

    private static IReadOnlyList<string> ExtractCommandNames(byte[] baml)
    {
        var text = Encoding.UTF8.GetString(baml);
        return Regex.Matches(text, @"\b[A-Za-z][A-Za-z0-9_]*Command\b", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static MethodBase ResolveCommandTarget(Type viewModel, string commandName)
    {
        var property = viewModel.GetProperty(commandName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(viewModel.FullName, commandName);
        if (property.PropertyType.FullName != "SwSearch.App.RelayCommand" && property.PropertyType != typeof(Action))
        {
            throw new InvalidDataException($"XAML Command property has an unsupported type: {viewModel.FullName}.{commandName}");
        }
        var field = viewModel.GetField($"<{commandName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(viewModel.FullName, $"<{commandName}>k__BackingField");
        foreach (var constructor in RequireConstructors(viewModel, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var instructions = ReadMemberInstructions(constructor);
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].OpCode != OpCodes.Stfld ||
                    instructions[index].Member is not FieldInfo assigned ||
                    assigned.Module != field.Module || assigned.MetadataToken != field.MetadataToken)
                {
                    continue;
                }

                for (var previous = index - 1; previous >= Math.Max(0, index - 16); previous--)
                {
                    var instruction = instructions[previous];
                    if (instruction.OpCode == OpCodes.Stfld) break;
                    if ((instruction.OpCode == OpCodes.Ldftn || instruction.OpCode == OpCodes.Ldvirtftn) &&
                        instruction.Member is MethodBase target)
                    {
                        return target;
                    }
                }
            }
        }
        throw new InvalidDataException($"XAML Command target could not be resolved from production IL: {viewModel.FullName}.{commandName}");
    }

    private readonly record struct MemberInstruction(OpCode OpCode, MemberInfo? Member);

    private static IReadOnlyList<MemberInstruction> ReadMemberInstructions(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var module = method.Module;
        var typeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var result = new List<MemberInstruction>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var first = bytes[offset++];
            var key = first == 0xFE ? (short)(0xFE00 | bytes[offset++]) : (short)first;
            if (!OpCodesByValue.TryGetValue(key, out var opcode))
                throw new InvalidOperationException($"Unknown IL opcode 0x{(ushort)key:X4} in {Format(method)}.");
            MemberInfo? member = null;
            if (opcode.OperandType == OperandType.InlineMethod || opcode.OperandType == OperandType.InlineField)
            {
                var token = BitConverter.ToInt32(bytes, offset);
                try
                {
                    member = opcode.OperandType == OperandType.InlineMethod
                        ? module.ResolveMethod(token, typeArguments, methodArguments)
                        : module.ResolveField(token, typeArguments, methodArguments);
                }
                catch (ArgumentException) { }
                catch (FileNotFoundException) { }
                catch (FileLoadException) { }
            }
            result.Add(new MemberInstruction(opcode, member));
            offset += OperandSize(opcode.OperandType, bytes, offset);
        }
        return result;
    }

    private static int OperandSize(OperandType type, byte[] bytes, int offset) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
            OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
            OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(bytes, offset) * 4),
        _ => throw new InvalidOperationException("Unknown IL operand: " + type),
    };

    private static string Format(MethodBase method) => method.DeclaringType?.FullName + "." + method.Name;

    private static bool IsAsyncAwaiterContinuation(MethodBase caller, MethodBase called) =>
        caller.Name == "MoveNext" &&
        caller.DeclaringType is not null &&
        typeof(IAsyncStateMachine).IsAssignableFrom(caller.DeclaringType) &&
        called.DeclaringType?.FullName?.Contains("Awaiter", StringComparison.Ordinal) == true &&
        called.Name == "GetResult";

    private static bool IsForbiddenSynchronousIo(MethodBase method)
    {
        var type = method.DeclaringType?.FullName ?? string.Empty;
        return type is "System.IO.DirectoryInfo" or "System.IO.DriveInfo" or "System.IO.FileInfo" or "System.IO.FileStream" ||
            type is "System.IO.Directory" or "System.IO.File" ||
            type.StartsWith("System.Windows.Shell", StringComparison.Ordinal) ||
            type.StartsWith("Microsoft.Win32.Registry", StringComparison.Ordinal) ||
            type == "System.Windows.Forms.FolderBrowserDialog" ||
            (type is "SwSearch.App.IAppSettingsStore" or "SwSearch.App.JsonAppSettingsStore" &&
             method.Name is "Load" or "Save") ||
            method.Name.StartsWith("WNet", StringComparison.Ordinal);
    }

    private static bool IsForbiddenBlockingWait(MethodBase method)
    {
        var type = method.DeclaringType?.FullName ?? string.Empty;
        return (type.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) &&
                method.Name is "Wait" or "get_Result") ||
            (type.Contains("Awaiter", StringComparison.Ordinal) && method.Name == "GetResult") ||
            (type == "System.Windows.Threading.Dispatcher" && method.Name == "Invoke") ||
            (type == "System.Diagnostics.Process" && method.Name == "WaitForExit");
    }

    private static bool RunGateSelfTests()
    {
        var fixture = typeof(ForbiddenSynchronousIoFixture);
        var assembly = fixture.Assembly;
        var direct = RequireMethod(
            fixture,
            nameof(ForbiddenSynchronousIoFixture.Direct),
            BindingFlags.Static | BindingFlags.NonPublic,
            typeof(string));
        var indirect = RequireMethod(
            fixture,
            nameof(ForbiddenSynchronousIoFixture.Indirect),
            BindingFlags.Static | BindingFlags.NonPublic,
            typeof(string));
        var offloaded = RequireMethod(
            fixture,
            nameof(ForbiddenSynchronousIoFixture.Offloaded),
            BindingFlags.Static | BindingFlags.NonPublic,
            typeof(string));
        var blocking = RequireMethod(
            fixture,
            nameof(ForbiddenSynchronousIoFixture.BlockingProcessWait),
            BindingFlags.Static | BindingFlags.NonPublic,
            typeof(Process));
        var asyncDirect = RequireMethod(
            fixture,
            nameof(ForbiddenSynchronousIoFixture.AsyncDirect),
            BindingFlags.Static | BindingFlags.NonPublic,
            typeof(string));
        var asyncOffloaded = RequireMethod(
            fixture,
            nameof(ForbiddenSynchronousIoFixture.AsyncOffloaded),
            BindingFlags.Static | BindingFlags.NonPublic,
            typeof(string));
        var missingRootRejected = false;
        try
        {
            _ = RequireMethod(fixture, "RemovedProductionRoot", BindingFlags.Static | BindingFlags.NonPublic);
        }
        catch (MissingMethodException)
        {
            missingRootRejected = true;
        }
        var directForbidden = ApplicationCallGraph(new[] { direct }, assembly).Where(IsForbiddenSynchronousIo).Select(Format).ToArray();
        var indirectForbidden = ApplicationCallGraph(new[] { indirect }, assembly).Where(IsForbiddenSynchronousIo).Select(Format).ToArray();
        var offloadedForbidden = ApplicationCallGraph(new[] { offloaded }, assembly).Where(IsForbiddenSynchronousIo).ToArray();
        var blockingForbidden = ApplicationCallGraph(new[] { blocking }, assembly).Where(IsForbiddenBlockingWait).Select(Format).ToArray();
        var asyncDirectForbidden = ApplicationCallGraph(new[] { asyncDirect }, assembly).Where(IsForbiddenSynchronousIo).Select(Format).ToArray();
        var asyncOffloadedForbidden = ApplicationCallGraph(new[] { asyncOffloaded }, assembly).Where(IsForbiddenSynchronousIo).ToArray();
        var commandNames = ExtractCommandNames(Encoding.UTF8.GetBytes("TogglePauseCommand RetryConnectionCommand"));
        var unsafeCommandTarget = ResolveCommandTarget(typeof(CommandBindingResolverFixture), nameof(CommandBindingResolverFixture.UnsafeCommand));
        var unsafeCommandForbidden = ApplicationCallGraph(new[] { unsafeCommandTarget }, assembly)
            .Where(IsForbiddenSynchronousIo).Select(Format).ToArray();
        if (!directForbidden.Any(item => item == "System.IO.File.Exists") ||
            !directForbidden.Any(item => item == "System.IO.File.ReadAllText") ||
            !directForbidden.Any(item => item == "System.IO.File.Move") ||
            indirectForbidden.Length < 3 || offloadedForbidden.Length != 0 ||
            !blockingForbidden.Contains("System.Diagnostics.Process.WaitForExit", StringComparer.Ordinal) ||
            !asyncDirectForbidden.Contains("System.IO.File.WriteAllText", StringComparer.Ordinal) ||
            !missingRootRejected ||
            asyncOffloadedForbidden.Length != 0 ||
            !commandNames.SequenceEqual(new[] { "RetryConnectionCommand", "TogglePauseCommand" }, StringComparer.Ordinal) ||
            !unsafeCommandForbidden.Contains("System.IO.File.WriteAllText", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("Candidate IL gate direct/indirect/background-boundary or required-root self-test failed.");
            return false;
        }
        Console.WriteLine("PASS candidate IL gate self-test: direct/indirect/async and resolved XAML-command I/O rejected; Task.Run boundary accepted; missing roots fail closed");
        return true;
    }

    private static TimeSpan ProbeHeadlessSettingsBootstrap(Assembly appAssembly)
    {
        var loaderType = appAssembly.GetType("SwSearch.App.BackgroundSettingsLoader", throwOnError: true)!;
        var settingsType = appAssembly.GetType("SwSearch.App.AppSettings", throwOnError: true)!;
        var loadType = typeof(Func<>).MakeGenericType(settingsType);
        var loadMethod = new DynamicMethod("LoadCandidateSettingsSlowly", settingsType, Type.EmptyTypes, typeof(Program).Module, true);
        var loadIl = loadMethod.GetILGenerator();
        loadIl.Emit(OpCodes.Ldc_I4, 500);
        loadIl.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.Sleep), new[] { typeof(int) })!);
        loadIl.Emit(OpCodes.Call, settingsType.GetProperty("Default", BindingFlags.Static | BindingFlags.Public)!.GetMethod!);
        loadIl.Emit(OpCodes.Ret);
        var load = loadMethod.CreateDelegate(loadType);

        var applyType = typeof(Action<>).MakeGenericType(settingsType);
        var applyMethod = new DynamicMethod("IgnoreCandidateSettings", typeof(void), new[] { settingsType }, typeof(Program).Module, true);
        applyMethod.GetILGenerator().Emit(OpCodes.Ret);
        var apply = applyMethod.CreateDelegate(applyType);

        var constructor = loaderType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var candidateDispatcherType = constructor.GetParameters()[1].ParameterType;
        var candidateDispatcher = candidateDispatcherType
            .GetProperty("CurrentDispatcher", BindingFlags.Static | BindingFlags.Public)!
            .GetValue(null)!;
        var loader = constructor.Invoke(new object?[]
        {
            load,
            candidateDispatcher,
            apply,
            new Func<bool>(() => false),
            TimeSpan.FromSeconds(1),
        });
        var start = loaderType.GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var clock = Stopwatch.StartNew();
        start.Invoke(loader, null);
        clock.Stop();
        ((IDisposable)loader).Dispose();
        if (clock.Elapsed >= TimeSpan.FromMilliseconds(50))
        {
            throw new InvalidOperationException("Candidate background settings bootstrap blocked: " + clock.Elapsed);
        }
        return clock.Elapsed;
    }

    private static TimeSpan ProbeHeadlessSettingsWindowFactory(Assembly appAssembly)
    {
        var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? viewModel = null;
            object? settingsWindow = null;
            var previousDataRoot = Environment.GetEnvironmentVariable("SWSEARCH_DATA_ROOT");
            var isolatedDataRoot = Path.Combine(
                Path.GetTempPath(),
                "swsearch-candidate-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(isolatedDataRoot);
                Environment.SetEnvironmentVariable("SWSEARCH_DATA_ROOT", isolatedDataRoot);
                var applicationType = appAssembly.GetType("SwSearch.App.App", throwOnError: true)!;
                var candidateApplication = Activator.CreateInstance(applicationType)!;
                applicationType.GetMethod(
                    "InitializeComponent",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .Invoke(candidateApplication, null);
                var searchClient = DispatchProxy.Create(
                    appAssembly.GetType("SwSearch.App.ISearchClient", throwOnError: true)!,
                    typeof(CandidateNoOpProxy));
                var fileActions = DispatchProxy.Create(
                    appAssembly.GetType("SwSearch.App.IFileActions", throwOnError: true)!,
                    typeof(CandidateNoOpProxy));
                var viewModelType = appAssembly.GetType("SwSearch.App.MainViewModel", throwOnError: true)!;
                var constructor = viewModelType.GetConstructors().Single();
                viewModel = constructor.Invoke(new[] { searchClient, fileActions, null, null, null, null });
                var windowType = appAssembly.GetType("SwSearch.App.MainWindow", throwOnError: true)!;
                var window = windowType.GetConstructor(new[] { viewModelType })!.Invoke(new[] { viewModel });
                var factory = windowType.GetMethod(
                    "CreateSettingsWindow",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var slowest = TimeSpan.Zero;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var clock = Stopwatch.StartNew();
                    settingsWindow = factory.Invoke(window, null);
                    clock.Stop();
                    if (clock.Elapsed > slowest) slowest = clock.Elapsed;

                    var candidateWindow = settingsWindow as System.Windows.Window ??
                        throw new InvalidOperationException("Candidate SettingsWindow factory returned null or the wrong type.");
                    candidateWindow.ShowInTaskbar = false;
                    candidateWindow.ShowActivated = false;
                    candidateWindow.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                    candidateWindow.Left = -32000;
                    candidateWindow.Top = -32000;
                    // Do not create a browser process in the MSI static gate. The
                    // entry path's immediate non-modal Show call is proven by IL;
                    // this probe bounds the native host/window factory itself.
                    candidateWindow.Close();
                }
                completion.TrySetResult(slowest);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                if (viewModel is IDisposable disposable) disposable.Dispose();
                Environment.SetEnvironmentVariable("SWSEARCH_DATA_ROOT", previousDataRoot);
                try { Directory.Delete(isolatedDataRoot, recursive: true); }
                catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var elapsed = completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            throw new InvalidOperationException("Candidate SettingsWindow STA probe did not terminate.");
        }
        if (elapsed >= TimeSpan.FromMilliseconds(250))
        {
            throw new InvalidOperationException("Candidate SettingsWindow production factory blocked: " + elapsed);
        }
        return elapsed;
    }
}

public class CandidateNoOpProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var returnType = targetMethod?.ReturnType ?? typeof(void);
        return returnType == typeof(void) ? null :
            returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

internal sealed class CommandBindingResolverFixture
{
    internal CommandBindingResolverFixture()
    {
        UnsafeCommand = UnsafeTarget;
    }

    internal Action UnsafeCommand { get; }

    private static void UnsafeTarget() => File.WriteAllText("unsafe-command.fixture", "unsafe");
}

internal static class ForbiddenSynchronousIoFixture
{
    internal static string Direct(string path)
    {
        var content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        File.Move(path, path + ".moved");
        return content;
    }

    internal static string Indirect(string path) => Direct(path);

    internal static Task<string> Offloaded(string path) => Task.Run(() => Direct(path));

    internal static bool BlockingProcessWait(Process process) => process.WaitForExit(5_000);

    internal static async Task AsyncDirect(string path)
    {
        await Task.Yield();
        File.WriteAllText(path, "unsafe");
    }

    internal static async Task AsyncOffloaded(string path)
    {
        await Task.Yield();
        await Task.Run(() => File.WriteAllText(path, "offloaded"));
    }
}

internal sealed class PayloadLoadContext(string directory) : AssemblyLoadContext(isCollectible: false)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = Path.Combine(directory, assemblyName.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}
