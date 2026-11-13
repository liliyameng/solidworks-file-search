using System;
using System.Threading;
using System.Threading.Tasks;
using SwSearch.Pathing;

namespace SwSearch.SolidWorksAddin
{
    /// <summary>
    /// CLR4-compatible Everything-style input scheduler used directly by the
    /// taskpane and by the compiled headless behavior harness.
    /// </summary>
    internal sealed class LiveSearchScheduler : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<bool> _submit;
        private readonly Timer _timer;
        private readonly int _delayMilliseconds;
        private bool _compositionActive;
        private bool _compositionChanged;
        private bool _pendingIsInteractive;
        private bool _disposed;

        internal LiveSearchScheduler(Action submit, int delayMilliseconds)
            : this(WrapSubmit(submit), delayMilliseconds)
        {
        }

        private static Action<bool> WrapSubmit(Action submit)
        {
            if (submit == null) throw new ArgumentNullException("submit");
            return delegate(bool ignored) { submit(); };
        }

        internal LiveSearchScheduler(Action<bool> submit, int delayMilliseconds)
        {
            if (submit == null) throw new ArgumentNullException("submit");
            if (delayMilliseconds < 1 || delayMilliseconds > 60000)
                throw new ArgumentOutOfRangeException("delayMilliseconds");
            _submit = submit;
            _delayMilliseconds = delayMilliseconds;
            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        internal bool IsCompositionActive
        {
            get { lock (_gate) { return _compositionActive; } }
        }

        internal int DelayMilliseconds
        {
            get { return _delayMilliseconds; }
        }

        internal void InputChanged()
        {
            InputChanged(true);
        }

        internal void InputChanged(bool isInteractive)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _pendingIsInteractive = _pendingIsInteractive || isInteractive;
                if (_compositionActive)
                {
                    _compositionChanged = true;
                    return;
                }
                _timer.Change(_delayMilliseconds, Timeout.Infinite);
            }
        }

        internal void CompositionStarted()
        {
            lock (_gate)
            {
                if (_disposed || _compositionActive) return;
                _compositionActive = true;
                _compositionChanged = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        internal void CompositionCompleted()
        {
            lock (_gate)
            {
                if (_disposed || !_compositionActive) return;
                _compositionActive = false;
                if (_compositionChanged)
                {
                    _compositionChanged = false;
                    _timer.Change(_delayMilliseconds, Timeout.Infinite);
                }
            }
        }

        internal void SubmitImmediately()
        {
            SubmitImmediately(true);
        }

        internal void SubmitImmediately(bool isInteractive)
        {
            bool pendingIsInteractive;
            lock (_gate)
            {
                if (_disposed) return;
                _pendingIsInteractive = _pendingIsInteractive || isInteractive;
                if (_compositionActive)
                {
                    _compositionChanged = true;
                    return;
                }
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                pendingIsInteractive = _pendingIsInteractive;
                _pendingIsInteractive = false;
            }
            _submit(pendingIsInteractive);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            _timer.Dispose();
        }

        private void OnTimer(object ignored)
        {
            bool pendingIsInteractive;
            lock (_gate)
            {
                if (_disposed || _compositionActive) return;
                pendingIsInteractive = _pendingIsInteractive;
                _pendingIsInteractive = false;
            }
            _submit(pendingIsInteractive);
        }
    }

    /// <summary>
    /// Tracks the Broker's committed search-content version. Queue scheduling
    /// state is intentionally absent, so observing the same revision can never
    /// create a client-side refresh feedback loop.
    /// </summary>
    internal sealed class BrokerRevisionTracker
    {
        private string _epoch;
        private long _revision = -1;
        private int _phase = -1;

        internal bool Observe(string epoch, long revision, int phase)
        {
            bool changed = _epoch != null &&
                (!String.Equals(_epoch, epoch, StringComparison.Ordinal) || _revision != revision);
            bool becameQueryable = _phase == 0 && phase != 0;
            _epoch = epoch;
            _revision = revision;
            _phase = phase;
            return changed || becameQueryable;
        }
    }

    internal static class TaskpaneSearchScope
    {
        internal const int AllMachine = 0;
        internal const int Folder = 1;

        internal static bool TryNormalizeFolder(string path, out string normalized, out string error)
        {
            string detail;
            if (WindowsScopePath.TryNormalize(path, out normalized, out detail))
            {
                error = null;
                return true;
            }
            error = "搜索文件夹路径无效；不会自动改为全电脑搜索。" +
                (String.IsNullOrEmpty(detail) ? String.Empty : " " + detail);
            return false;
        }

        internal static bool ContainsPath(string normalizedRoot, string candidatePath)
        {
            return WindowsScopePath.Contains(normalizedRoot, candidatePath);
        }
    }

    internal sealed class TaskpaneSearchRequestFactory
    {
        private static readonly int[] SupportedKinds = new[] { 0, 1 };

        internal bool TryCreate(
            string text,
            int mode,
            int[] kinds,
            int scope,
            string rootPath,
            bool includeSubdirectories,
            out LiveSearchRequest request,
            out string error)
        {
            return TryCreate(text, mode, kinds, scope, rootPath, includeSubdirectories,
                null, out request, out error);
        }

        internal bool TryCreate(
            string text,
            int mode,
            int[] kinds,
            int scope,
            string rootPath,
            bool includeSubdirectories,
            string[] libraryRoots,
            out LiveSearchRequest request,
            out string error)
        {
            request = null;
            error = null;
            if (mode != 3 || scope != TaskpaneSearchScope.AllMachine ||
                !String.IsNullOrWhiteSpace(rootPath) || !includeSubdirectories)
            {
                error = "SOLIDWORKS 插件仅允许搜索已配置的设计库。";
                return false;
            }
            if (libraryRoots == null || libraryRoots.Length == 0)
            {
                error = "未配置 SOLIDWORKS 设计库路径。";
                return false;
            }
            request = new LiveSearchRequest(
                text, 3, (int[])SupportedKinds.Clone(), TaskpaneSearchScope.AllMachine, null, true,
                0, 200, null, null, libraryRoots);
            return true;
        }
    }

    internal sealed class LiveSearchRequest
    {
        internal LiveSearchRequest(string text, int mode, int[] kinds)
            : this(text, mode, kinds, TaskpaneSearchScope.AllMachine, null, true, 0, 200, null, null, null)
        {
        }

        internal LiveSearchRequest(
            string text,
            int mode,
            int[] kinds,
            int scope,
            string rootPath,
            bool includeSubdirectories)
            : this(text, mode, kinds, scope, rootPath, includeSubdirectories, 0, 200, null, null, null)
        {
        }

        internal LiveSearchRequest(
            string text,
            int mode,
            int[] kinds,
            int scope,
            string rootPath,
            bool includeSubdirectories,
            int offset,
            int limit,
            long? expectedRevision = null,
            string pageFingerprint = null,
            string[] libraryRoots = null,
            bool? isInteractive = null)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException("offset");
            if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException("limit");
            Text = text ?? String.Empty;
            Mode = mode;
            Kinds = kinds ?? new int[0];
            Scope = scope;
            RootPath = rootPath;
            IncludeSubdirectories = includeSubdirectories;
            Offset = offset;
            Limit = limit;
            ExpectedRevision = expectedRevision;
            PageFingerprint = pageFingerprint;
            LibraryRoots = libraryRoots ?? new string[0];
            IsInteractive = isInteractive;
        }

        internal string Text { get; private set; }
        internal int Mode { get; private set; }
        internal int[] Kinds { get; private set; }
        internal int Scope { get; private set; }
        internal string RootPath { get; private set; }
        internal bool IncludeSubdirectories { get; private set; }
        internal int Offset { get; private set; }
        internal int Limit { get; private set; }
        internal long? ExpectedRevision { get; private set; }
        internal string PageFingerprint { get; private set; }
        internal string[] LibraryRoots { get; private set; }
        internal bool? IsInteractive { get; private set; }

        internal LiveSearchRequest WithPage(int offset, int limit)
        {
            return WithPage(offset, limit, IsInteractive);
        }

        internal LiveSearchRequest WithPage(int offset, int limit, bool? isInteractive)
        {
            return new LiveSearchRequest(
                Text, Mode, Kinds, Scope, RootPath, IncludeSubdirectories, offset, limit,
                ExpectedRevision, PageFingerprint, LibraryRoots, isInteractive);
        }

        internal LiveSearchRequest WithPagingSnapshot(long? revision, string fingerprint)
        {
            return new LiveSearchRequest(
                Text, Mode, Kinds, Scope, RootPath, IncludeSubdirectories, Offset, Limit,
                revision, fingerprint, LibraryRoots, IsInteractive);
        }
    }

    internal sealed class LiveSearchCompletion
    {
        internal LiveSearchCompletion(int generation, LiveSearchRequest request, BrokerResponse response, Exception error)
        {
            Generation = generation;
            Request = request;
            Response = response;
            Error = error;
        }

        internal int Generation { get; private set; }
        internal LiveSearchRequest Request { get; private set; }
        internal BrokerResponse Response { get; private set; }
        internal Exception Error { get; private set; }
    }

    /// <summary>
    /// Non-UI CLR4 coordinator used directly by the taskpane. It owns request
    /// generations and cancellation so stale or out-of-order completions can
    /// never be published to the control.
    /// </summary>
    internal sealed class LiveSearchCoordinator : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Func<LiveSearchRequest, CancellationToken, BrokerResponse> _search;
        private CancellationTokenSource _currentCancellation;
        private int _generation;
        private int _activeRequests;
        private bool _disposed;

        internal LiveSearchCoordinator(Func<LiveSearchRequest, CancellationToken, BrokerResponse> search)
        {
            if (search == null) throw new ArgumentNullException("search");
            _search = search;
        }

        internal event Action<LiveSearchCompletion> Completed;

        internal int CurrentGeneration
        {
            get { lock (_gate) { return _generation; } }
        }

        internal int ActiveRequestCount
        {
            get { lock (_gate) { return _activeRequests; } }
        }

        internal int Submit(LiveSearchRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            CancellationTokenSource previous;
            CancellationTokenSource current = new CancellationTokenSource();
            int generation;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("LiveSearchCoordinator");
                previous = _currentCancellation;
                if (previous != null)
                {
                    previous.Cancel();
                }
                _currentCancellation = current;
                generation = ++_generation;
                _activeRequests++;
            }

            Task.Factory.StartNew(
                delegate { Execute(generation, request, current); },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            return generation;
        }

        internal void CancelCurrent()
        {
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (_disposed) return;
                _generation++;
                cancellation = _currentCancellation;
                if (cancellation != null)
                {
                    cancellation.Cancel();
                }
                _currentCancellation = null;
            }
        }

        internal bool IsCurrent(int generation)
        {
            lock (_gate)
            {
                return !_disposed && generation == _generation;
            }
        }

        internal bool WaitForIdle(TimeSpan timeout)
        {
            StopwatchBudget budget = new StopwatchBudget(timeout);
            lock (_gate)
            {
                while (_activeRequests != 0)
                {
                    TimeSpan remaining = budget.Remaining;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(_gate, remaining))
                    {
                        return _activeRequests == 0;
                    }
                }

                return true;
            }
        }

        public void Dispose()
        {
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _generation++;
                cancellation = _currentCancellation;
                if (cancellation != null)
                {
                    cancellation.Cancel();
                }
                _currentCancellation = null;
            }
        }

        private void Execute(int generation, LiveSearchRequest request, CancellationTokenSource cancellation)
        {
            BrokerResponse response = null;
            Exception error = null;
            try
            {
                response = _search(request, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                error = exception;
            }

            lock (_gate)
            {
                if (!_disposed && generation == _generation && !cancellation.IsCancellationRequested)
                {
                    Action<LiveSearchCompletion> handler = Completed;
                    if (handler != null)
                    {
                        try
                        {
                            handler(new LiveSearchCompletion(generation, request, response, error));
                        }
                        catch
                        {
                            // A UI callback failure must not strand coordinator state.
                        }
                    }
                }
                if (Object.ReferenceEquals(_currentCancellation, cancellation))
                {
                    _currentCancellation = null;
                }
                _activeRequests--;
                Monitor.PulseAll(_gate);
            }
            cancellation.Dispose();
        }

        private sealed class StopwatchBudget
        {
            private readonly DateTime _deadline;

            internal StopwatchBudget(TimeSpan timeout)
            {
                if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException("timeout");
                _deadline = DateTime.UtcNow.Add(timeout);
            }

            internal TimeSpan Remaining
            {
                get
                {
                    TimeSpan remaining = _deadline - DateTime.UtcNow;
                    return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                }
            }
        }
    }
}
