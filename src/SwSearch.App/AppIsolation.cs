using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using SwSearch.Integration;

namespace SwSearch.App;

internal sealed record IsolatedOperationRequest(
    string Nonce,
    string Operation,
    string? SettingsPath = null,
    string? TemporaryPath = null,
    string? ContentBase64 = null,
    string? Target = null,
    bool? Enabled = null);

internal sealed record IsolatedOperationResponse(
    string Nonce,
    bool Success,
    string? ErrorType = null,
    IntegrationStatus? Status = null,
    bool CleanupComplete = true);

internal interface IAppIsolationClient
{
    ValueTask SaveSettingsAsync(
        string settingsPath,
        string temporaryPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
    ValueTask<bool> CleanupSettingsTemporaryAsync(string temporaryPath, CancellationToken cancellationToken);
    ValueTask<IntegrationStatus> ProbeIntegrationAsync(SettingsIntegrationTarget target, CancellationToken cancellationToken);
    ValueTask SetIntegrationEnabledAsync(SettingsIntegrationTarget target, bool enabled, CancellationToken cancellationToken);
}

internal sealed class AppIsolationClient : IAppIsolationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _helperPath;
    private readonly OwnedIsolatedProcessRunner _runner;

    internal AppIsolationClient(
        string? helperPath = null,
        OwnedIsolatedProcessRunner? runner = null)
    {
        _helperPath = Path.GetFullPath(helperPath ?? Path.Combine(AppContext.BaseDirectory, "SwSearch.App.exe"));
        _runner = runner ?? new OwnedIsolatedProcessRunner();
    }

    internal int ActiveCount => _runner.ActiveCount;

    public async ValueTask SaveSettingsAsync(
        string settingsPath,
        string temporaryPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var nonce = ExtractNonce(settingsPath, temporaryPath);
        var response = await SendAsync(
            new IsolatedOperationRequest(
                nonce,
                "settings-save",
                Path.GetFullPath(settingsPath),
                Path.GetFullPath(temporaryPath),
                Convert.ToBase64String(content.Span)),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async ValueTask<bool> CleanupSettingsTemporaryAsync(
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(temporaryPath);
        var response = await SendAsync(
            new IsolatedOperationRequest(ExtractNonceFromTemporary(fullPath), "settings-cleanup", TemporaryPath: fullPath),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.CleanupComplete;
    }

    public async ValueTask<IntegrationStatus> ProbeIntegrationAsync(
        SettingsIntegrationTarget target,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            new IsolatedOperationRequest(Guid.NewGuid().ToString("N"), "integration-probe", Target: target.ToString()),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Status ?? throw new InvalidDataException("Isolated integration status is missing.");
    }

    public async ValueTask SetIntegrationEnabledAsync(
        SettingsIntegrationTarget target,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            new IsolatedOperationRequest(Guid.NewGuid().ToString("N"), "integration-set", Target: target.ToString(), Enabled: enabled),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    private async ValueTask<IsolatedOperationResponse> SendAsync(
        IsolatedOperationRequest request,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(request, JsonOptions);
        var result = await _runner.RunAsync(
            _helperPath,
            ["--isolated-operation", request.Nonce],
            input,
            TimeSpan.FromSeconds(6),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.StandardOutput.Length is 0 or > 256 * 1024)
        {
            throw new InvalidOperationException("Isolated operation host failed.");
        }
        var response = JsonSerializer.Deserialize<IsolatedOperationResponse>(result.StandardOutput, JsonOptions)
            ?? throw new InvalidDataException("Isolated operation response is missing.");
        if (!string.Equals(response.Nonce, request.Nonce, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Isolated operation nonce mismatch.");
        }
        return response;
    }

    private static void EnsureSuccess(IsolatedOperationResponse response)
    {
        if (!response.Success)
        {
            throw new InvalidOperationException("Isolated operation failed: " + (response.ErrorType ?? "Unknown"));
        }
    }

    private static string ExtractNonce(string settingsPath, string temporaryPath)
    {
        var prefix = Path.GetFullPath(settingsPath) + ".";
        var fullTemporary = Path.GetFullPath(temporaryPath);
        if (!fullTemporary.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fullTemporary.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Temporary settings path is not bound to the settings file.", nameof(temporaryPath));
        }
        return ExtractNonceFromTemporary(fullTemporary);
    }

    private static string ExtractNonceFromTemporary(string temporaryPath)
    {
        var fileName = Path.GetFileName(temporaryPath);
        var parts = fileName.Split('.');
        if (parts.Length < 3 || !Guid.TryParseExact(parts[^2], "N", out _))
        {
            throw new ArgumentException("Temporary settings nonce is invalid.", nameof(temporaryPath));
        }
        return parts[^2];
    }
}

internal sealed record OwnedIsolatedProcessResult(int ExitCode, string StandardOutput);

internal sealed class OwnedIsolatedProcessRunner
{
    private int _activeCount;
    internal int ActiveCount => Volatile.Read(ref _activeCount);

    internal async Task<OwnedIsolatedProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var job = new OwnedProcessJob();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start isolated operation host.");
        try
        {
            job.Attach(process);
        }
        catch
        {
            OwnedProcessJob.TerminateSingle(process);
            throw;
        }

        Interlocked.Increment(ref _activeCount);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            return new OwnedIsolatedProcessResult(process.ExitCode, output);
        }
        catch (OperationCanceledException)
        {
            job.Terminate();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Isolated operation exceeded its total deadline.");
        }
        finally
        {
            Interlocked.Decrement(ref _activeCount);
        }
    }
}

internal sealed class OwnedProcessJob : IDisposable
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle _job;

    internal OwnedProcessJob()
    {
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(
                _job,
                JobObjectExtendedLimitInformationClass,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new Win32Exception(error);
        }
    }

    internal void Attach(Process process)
    {
        if (!AssignProcessToJobObject(_job, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal void Terminate()
    {
        if (!TerminateJobObject(_job, 1223) && Marshal.GetLastWin32Error() != 5)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static void TerminateSingle(Process process)
    {
        if (!TerminateProcess(process.SafeHandle, 1223) && Marshal.GetLastWin32Error() != 5)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose() => _job.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        uint informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

internal static class AppIsolatedOperationHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static bool IsInvocation(IReadOnlyList<string> args) =>
        args.Count == 2 &&
        string.Equals(args[0], "--isolated-operation", StringComparison.Ordinal) &&
        Guid.TryParseExact(args[1], "N", out _);

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!IsInvocation(args)) return 64;
        var argumentNonce = args[1];
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 4096, leaveOpen: false);
            var input = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (input.Length is 0 or > 4 * 1024 * 1024) return 65;
            var request = JsonSerializer.Deserialize<IsolatedOperationRequest>(input, JsonOptions);
            if (request is null || !string.Equals(request.Nonce, argumentNonce, StringComparison.Ordinal)) return 66;
            var response = await ExecuteAsync(request, new WindowsRegistryAccess(), AppContext.BaseDirectory, cancellationToken)
                .ConfigureAwait(false);
            await using var output = Console.OpenStandardOutput();
            await JsonSerializer.SerializeAsync(output, response, JsonOptions, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                await using var output = Console.OpenStandardOutput();
                await JsonSerializer.SerializeAsync(
                    output,
                    new IsolatedOperationResponse(argumentNonce, false, exception.GetType().Name),
                    JsonOptions,
                    CancellationToken.None).ConfigureAwait(false);
                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                return 0;
            }
            catch
            {
                return 70;
            }
        }
    }

    internal static async ValueTask<IsolatedOperationResponse> ExecuteAsync(
        IsolatedOperationRequest request,
        IRegistryAccess registry,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(request.Nonce, "N", out _)) throw new InvalidDataException("Invalid operation nonce.");
        switch (request.Operation)
        {
            case "settings-save":
                await SaveSettingsAsync(request, cancellationToken).ConfigureAwait(false);
                return new IsolatedOperationResponse(request.Nonce, true);
            case "settings-cleanup":
                return new IsolatedOperationResponse(
                    request.Nonce,
                    true,
                    CleanupComplete: await CleanupSettingsAsync(request, cancellationToken).ConfigureAwait(false));
            case "integration-probe":
                return new IsolatedOperationResponse(
                    request.Nonce,
                    true,
                    Status: await ProbeIntegrationAsync(request, registry, baseDirectory, cancellationToken).ConfigureAwait(false));
            case "integration-set":
                await SetIntegrationAsync(request, registry, baseDirectory, cancellationToken).ConfigureAwait(false);
                return new IsolatedOperationResponse(request.Nonce, true);
            default:
                throw new InvalidDataException("Unknown isolated operation.");
        }
    }

    private static async Task SaveSettingsAsync(IsolatedOperationRequest request, CancellationToken cancellationToken)
    {
        var settingsPath = ValidateSettingsPath(request.SettingsPath);
        var temporaryPath = ValidateTemporaryPath(settingsPath, request.TemporaryPath, request.Nonce);
        var content = Convert.FromBase64String(request.ContentBase64 ?? string.Empty);
        if (content.Length is 0 or > 1024 * 1024) throw new InvalidDataException("Settings payload length is invalid.");
        await SweepSettingsTemporaryFilesAsync(settingsPath, temporaryPath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(settingsPath))
            {
                File.Replace(temporaryPath, settingsPath, settingsPath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, settingsPath, overwrite: false);
            }
        }
        catch
        {
            await RetriableTemporaryFileCleanup.TryDeleteAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> CleanupSettingsAsync(
        IsolatedOperationRequest request,
        CancellationToken cancellationToken)
    {
        var settingsPath = ValidateSettingsPath(Path.Combine(SwSearchDataPaths.DataDirectory, "settings.json"));
        var temporaryPath = ValidateTemporaryPath(settingsPath, request.TemporaryPath, request.Nonce);
        return await RetriableTemporaryFileCleanup.TryDeleteAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SweepSettingsTemporaryFilesAsync(
        string settingsPath,
        string currentTemporary,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(settingsPath)!;
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, Path.GetFileName(settingsPath) + ".*.tmp"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(path, currentTemporary, StringComparison.OrdinalIgnoreCase)) continue;
            await RetriableTemporaryFileCleanup.TryDeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<IntegrationStatus> ProbeIntegrationAsync(
        IsolatedOperationRequest request,
        IRegistryAccess registry,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        var target = ParseTarget(request.Target);
        return target switch
        {
            SettingsIntegrationTarget.Taskpane =>
                await new SolidWorksTaskpaneRegistrationService(registry).ProbeAsync(cancellationToken).ConfigureAwait(false),
            SettingsIntegrationTarget.FolderMenu =>
                await new FolderContextMenuService(registry, useModern: false, installDirectory: baseDirectory)
                    .ProbeAsync(Path.Combine(baseDirectory, "SwSearch.App.exe"), cancellationToken).ConfigureAwait(false),
            SettingsIntegrationTarget.Startup =>
                await new StartupRegistrationService(registry)
                    .ProbeAsync(Path.Combine(baseDirectory, "SwSearch.App.exe"), cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static async ValueTask SetIntegrationAsync(
        IsolatedOperationRequest request,
        IRegistryAccess registry,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        if (request.Enabled is null) throw new InvalidDataException("Integration state is missing.");
        var target = ParseTarget(request.Target);
        switch (target)
        {
            case SettingsIntegrationTarget.Taskpane:
                await new SolidWorksTaskpaneRegistrationService(registry)
                    .SetEnabledAsync(request.Enabled.Value, cancellationToken).ConfigureAwait(false);
                break;
            case SettingsIntegrationTarget.FolderMenu:
                await new FolderContextMenuService(registry, useModern: false, installDirectory: baseDirectory)
                    .SetEnabledAsync(
                        request.Enabled.Value,
                        Path.Combine(baseDirectory, "SwSearch.App.exe"),
                        cancellationToken).ConfigureAwait(false);
                break;
            case SettingsIntegrationTarget.Startup:
                await new StartupRegistrationService(registry)
                    .SetEnabledAsync(
                        request.Enabled.Value,
                        Path.Combine(baseDirectory, "SwSearch.App.exe"),
                        cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static SettingsIntegrationTarget ParseTarget(string? value) =>
        Enum.TryParse<SettingsIntegrationTarget>(value, ignoreCase: false, out var target) &&
        Enum.IsDefined(target)
            ? target
            : throw new InvalidDataException("Integration target is invalid.");

    private static string ValidateSettingsPath(string? path)
    {
        var expected = Path.GetFullPath(Path.Combine(SwSearchDataPaths.DataDirectory, "settings.json"));
        var actual = Path.GetFullPath(path ?? string.Empty);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Settings path is outside the product data root.");
        }
        return actual;
    }

    private static string ValidateTemporaryPath(string settingsPath, string? path, string nonce)
    {
        var expected = settingsPath + "." + nonce + ".tmp";
        var actual = Path.GetFullPath(path ?? string.Empty);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Temporary settings path is not bound to the request nonce.");
        }
        return actual;
    }
}

internal static class RetriableTemporaryFileCleanup
{
    internal static async Task<bool> TryDeleteAsync(
        string path,
        CancellationToken cancellationToken,
        int attempts = 4)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt + 1 == attempts) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
        return false;
    }
}
