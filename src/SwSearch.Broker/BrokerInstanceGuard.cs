using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SwSearch.Contracts;

namespace SwSearch.Broker;

internal static class BrokerProcessExitCodes
{
    internal const int Success = 0;
    internal const int SoftwareFailure = 70;
    internal const int TemporaryInstanceFailure = 75;
}

internal enum BrokerInstanceDisposition
{
    Acquired,
    ExistingHealthy,
    RetryableUnavailable,
}

internal sealed record BrokerInstanceGuardOptions(
    string MutexName,
    string LegacySemaphoreName,
    TimeSpan ContentionTimeout,
    TimeSpan ProbeTimeout,
    TimeSpan RetryInterval)
{
    internal static BrokerInstanceGuardOptions Default { get; } = new(
        @"Local\SwSearch.Broker.Owner.v2",
        @"Local\SwSearch.Broker.v1",
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(125),
        TimeSpan.FromMilliseconds(50));

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(MutexName))
        {
            throw new ArgumentException("The Broker mutex name is required.", nameof(MutexName));
        }

        if (string.IsNullOrWhiteSpace(LegacySemaphoreName))
        {
            throw new ArgumentException("The legacy Broker semaphore name is required.", nameof(LegacySemaphoreName));
        }

        if (ContentionTimeout < TimeSpan.Zero || ContentionTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ContentionTimeout));
        }

        if (ProbeTimeout <= TimeSpan.Zero || ProbeTimeout > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ProbeTimeout));
        }

        if (RetryInterval <= TimeSpan.Zero || RetryInterval > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(RetryInterval));
        }
    }
}

internal interface IBrokerLivenessProbe
{
    bool IsHealthy(TimeSpan timeout);
}

internal sealed class NamedPipeBrokerLivenessProbe(string pipeName) : IBrokerLivenessProbe
{
    private const int MaximumResponseCharacters = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName = !string.IsNullOrWhiteSpace(pipeName)
        ? pipeName
        : throw new ArgumentException("The Broker pipe name is required.", nameof(pipeName));

    public bool IsHealthy(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            return ProbeAsync(timeout).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or TimeoutException or IOException or
                UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    private async Task<bool> ProbeAsync(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var requestId = Guid.NewGuid();
        var request = new BrokerRequest(
            ContractVersions.BrokerPipe,
            requestId,
            BrokerOperation.Ping);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            4096,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            4096,
            leaveOpen: true);

        await writer.WriteLineAsync(requestJson.AsMemory(), deadline.Token).ConfigureAwait(false);
        var responseJson = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        if (responseJson is null || responseJson.Length == 0 || responseJson.Length > MaximumResponseCharacters)
        {
            return false;
        }

        var response = JsonSerializer.Deserialize<BrokerResponse>(responseJson, JsonOptions);
        return response is not null &&
               response.ProtocolVersion == ContractVersions.BrokerPipe &&
               response.RequestId == requestId &&
               response.Success;
    }
}

internal sealed record BrokerInstanceAcquisition(
    BrokerInstanceDisposition Disposition,
    BrokerInstanceGuard? Guard,
    bool RecoveredAbandonedOwner)
{
    internal int ExitCode => Disposition == BrokerInstanceDisposition.RetryableUnavailable
        ? BrokerProcessExitCodes.TemporaryInstanceFailure
        : BrokerProcessExitCodes.Success;
}

/// <summary>
/// Holds both the recoverable RC52 mutex and the RC51 semaphore on a dedicated
/// thread. Mutex ownership is thread-affine, so keeping the owner thread alive
/// avoids ReleaseMutex failures after async continuations move Program.Main.
/// </summary>
public sealed class BrokerInstanceGuard : IDisposable
{
    private readonly AcquisitionWorker _worker;
    private int _disposed;

    private BrokerInstanceGuard(AcquisitionWorker worker)
    {
        _worker = worker;
    }

    internal static BrokerInstanceAcquisition Acquire(
        BrokerInstanceGuardOptions options,
        IBrokerLivenessProbe livenessProbe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(livenessProbe);
        options.Validate();

        var worker = new AcquisitionWorker(options, livenessProbe);
        worker.Start();
        worker.WaitForDecision();

        if (worker.Failure is { } failure)
        {
            worker.DisposeAfterFailedAcquisition();
            throw new InvalidOperationException("Unable to establish the Broker instance guard.", failure);
        }

        if (worker.Disposition == BrokerInstanceDisposition.Acquired)
        {
            return new BrokerInstanceAcquisition(
                BrokerInstanceDisposition.Acquired,
                new BrokerInstanceGuard(worker),
                worker.RecoveredAbandonedOwner);
        }

        var disposition = worker.Disposition;
        worker.DisposeAfterFailedAcquisition();
        return new BrokerInstanceAcquisition(disposition, null, false);
    }

    /// <summary>
    /// Compatibility helper retained for existing in-process callers and tests.
    /// Production startup uses <see cref="Acquire"/> so it can distinguish a
    /// healthy owner from a retryable unhealthy owner.
    /// </summary>
    public static BrokerInstanceGuard? TryAcquire(string name = @"Local\SwSearch.Broker.v1")
    {
        var options = new BrokerInstanceGuardOptions(
            name + ".Owner.v2",
            name,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));
        return Acquire(options, NeverHealthyProbe.Instance).Guard;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _worker.ReleaseAndDispose();
    }

    private sealed class AcquisitionWorker
    {
        private readonly BrokerInstanceGuardOptions _options;
        private readonly IBrokerLivenessProbe _livenessProbe;
        private readonly ManualResetEventSlim _decision = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private Exception? _failure;
        private BrokerInstanceDisposition _disposition = BrokerInstanceDisposition.RetryableUnavailable;
        private bool _recoveredAbandonedOwner;

        internal AcquisitionWorker(
            BrokerInstanceGuardOptions options,
            IBrokerLivenessProbe livenessProbe)
        {
            _options = options;
            _livenessProbe = livenessProbe;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SwSearch Broker instance owner",
            };
        }

        internal Exception? Failure => _failure;

        internal BrokerInstanceDisposition Disposition => _disposition;

        internal bool RecoveredAbandonedOwner => _recoveredAbandonedOwner;

        internal void Start() => _thread.Start();

        internal void WaitForDecision()
        {
            var maximumWait = _options.ContentionTimeout + _options.ProbeTimeout + TimeSpan.FromSeconds(2);
            if (_decision.Wait(maximumWait))
            {
                return;
            }

            _release.Set();
            throw new TimeoutException("The Broker instance guard did not make a bounded acquisition decision.");
        }

        internal void DisposeAfterFailedAcquisition()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(1));
            _decision.Dispose();
            _release.Dispose();
        }

        internal void ReleaseAndDispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
            _decision.Dispose();
            _release.Dispose();
        }

        private void Run()
        {
            Mutex? mutex = null;
            Semaphore? legacySemaphore = null;
            var ownsMutex = false;
            var ownsLegacySemaphore = false;
            var decisionPublished = false;
            try
            {
                var clock = Stopwatch.StartNew();
                mutex = new Mutex(false, _options.MutexName);
                var mutexDecision = WaitForMutex(mutex, clock);
                if (mutexDecision != BrokerInstanceDisposition.Acquired)
                {
                    _disposition = mutexDecision;
                    _decision.Set();
                    decisionPublished = true;
                    return;
                }

                ownsMutex = true;
                var legacyDecision = WaitForLegacySemaphore(clock, out legacySemaphore);
                if (legacyDecision != BrokerInstanceDisposition.Acquired)
                {
                    _disposition = legacyDecision;
                    mutex.ReleaseMutex();
                    ownsMutex = false;
                    _decision.Set();
                    decisionPublished = true;
                    return;
                }

                ownsLegacySemaphore = true;
                _disposition = BrokerInstanceDisposition.Acquired;
                _decision.Set();
                decisionPublished = true;
                _release.Wait();
            }
            catch (Exception exception)
            {
                _failure = exception;
                if (!decisionPublished)
                {
                    _decision.Set();
                    decisionPublished = true;
                }
            }
            finally
            {
                if (ownsLegacySemaphore)
                {
                    try
                    {
                        legacySemaphore!.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                }

                legacySemaphore?.Dispose();
                if (ownsMutex)
                {
                    try
                    {
                        mutex!.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                }

                mutex?.Dispose();
                if (!decisionPublished)
                {
                    _decision.Set();
                }
            }
        }

        private BrokerInstanceDisposition WaitForMutex(Mutex mutex, Stopwatch clock)
        {
            while (true)
            {
                try
                {
                    if (mutex.WaitOne(0))
                    {
                        return BrokerInstanceDisposition.Acquired;
                    }
                }
                catch (AbandonedMutexException)
                {
                    _recoveredAbandonedOwner = true;
                    return BrokerInstanceDisposition.Acquired;
                }

                if (ProbeHealthy(clock))
                {
                    return BrokerInstanceDisposition.ExistingHealthy;
                }

                var remaining = Remaining(clock);
                if (remaining <= TimeSpan.Zero)
                {
                    return BrokerInstanceDisposition.RetryableUnavailable;
                }

                var wait = Min(remaining, _options.RetryInterval);
                try
                {
                    if (mutex.WaitOne(wait))
                    {
                        return BrokerInstanceDisposition.Acquired;
                    }
                }
                catch (AbandonedMutexException)
                {
                    _recoveredAbandonedOwner = true;
                    return BrokerInstanceDisposition.Acquired;
                }
            }
        }

        private BrokerInstanceDisposition WaitForLegacySemaphore(
            Stopwatch clock,
            out Semaphore? acquiredSemaphore)
        {
            acquiredSemaphore = null;
            while (true)
            {
                // A failed waiter must close its handle before retrying. If an
                // RC51 process died while holding the non-owning semaphore,
                // closing the last handle lets Windows destroy that stale
                // kernel object; the next create then starts at count one.
                var candidate = new Semaphore(1, 1, _options.LegacySemaphoreName);
                if (candidate.WaitOne(0))
                {
                    acquiredSemaphore = candidate;
                    return BrokerInstanceDisposition.Acquired;
                }

                candidate.Dispose();
                if (ProbeHealthy(clock))
                {
                    return BrokerInstanceDisposition.ExistingHealthy;
                }

                var remaining = Remaining(clock);
                if (remaining <= TimeSpan.Zero)
                {
                    return BrokerInstanceDisposition.RetryableUnavailable;
                }

                _release.Wait(Min(remaining, _options.RetryInterval));
            }
        }

        private bool ProbeHealthy(Stopwatch clock)
        {
            var remaining = Remaining(clock);
            if (remaining <= TimeSpan.Zero && _options.ContentionTimeout > TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                return _livenessProbe.IsHealthy(Min(
                    remaining > TimeSpan.Zero ? remaining : _options.ProbeTimeout,
                    _options.ProbeTimeout));
            }
            catch
            {
                // A health probe must never weaken exclusivity. Treat an
                // inconclusive probe as unhealthy and keep the bounded gate.
                return false;
            }
        }

        private TimeSpan Remaining(Stopwatch clock) =>
            _options.ContentionTimeout - clock.Elapsed;

        private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
            left <= right ? left : right;
    }

    private sealed class NeverHealthyProbe : IBrokerLivenessProbe
    {
        internal static NeverHealthyProbe Instance { get; } = new();

        public bool IsHealthy(TimeSpan timeout) => false;
    }
}
