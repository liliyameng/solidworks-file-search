using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace SwSearch.SolidWorksAddin
{
    internal static class BrokerProtocol
    {
        internal static readonly string PipeName = GetSessionPipeName();

        private static string GetSessionPipeName()
        {
            using (Process process = Process.GetCurrentProcess())
            {
                return "swsearch.broker.v1.session." +
                       process.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        internal const int ProtocolVersion = 2;
        private static readonly BrokerPipeTransport Transport = new BrokerPipeTransport(
            PipeName,
            ResolveBrokerPath,
            delegate(ProcessStartInfo startInfo) { Process.Start(startInfo); },
            500,
            650,
            2000,
            TimeSpan.FromSeconds(6),
            100,
            delegate { return BrokerAppExitSessionMarkerCompatibility.IsMarkedForCurrentSession(); },
            12000);

        internal static BrokerResponse Search(
            string text,
            int mode,
            int[] kinds,
            int scope,
            string rootPath,
            bool includeSubdirectories,
            CancellationToken cancellationToken)
        {
            return Search(
                new LiveSearchRequest(text, mode, kinds, scope, rootPath, includeSubdirectories),
                cancellationToken);
        }

        internal static BrokerResponse Search(LiveSearchRequest search, CancellationToken cancellationToken)
        {
            return Transport.SendWithStart(CreateSearchRequest(search), cancellationToken);
        }

        internal static BrokerRequest CreateSearchRequest(LiveSearchRequest search)
        {
            if (search == null) throw new ArgumentNullException("search");
            return new BrokerRequest
            {
                ProtocolVersion = ProtocolVersion,
                RequestId = Guid.NewGuid(),
                Operation = 1,
                Query = new SearchQueryDto
                {
                    Text = search.Text,
                    Mode = search.Mode,
                    Offset = search.Offset,
                    Limit = search.Limit,
                    Kinds = search.Kinds,
                    ExpectedRevision = search.ExpectedRevision,
                    PageFingerprint = search.PageFingerprint,
                    LibraryRoots = search.LibraryRoots,
                    IsInteractive = search.IsInteractive,
                    Scope = new SearchScopeDto
                    {
                        Kind = search.Scope,
                        RootPath = search.RootPath,
                        IncludeSubdirectories = search.IncludeSubdirectories
                    }
                }
            };
        }

        internal static BrokerResponse Ping()
        {
            return Transport.SendWithStart(new BrokerRequest
            {
                ProtocolVersion = ProtocolVersion,
                RequestId = Guid.NewGuid(),
                Operation = 0
            }, CancellationToken.None);
        }

        internal static BrokerResponse GetState()
        {
            return Transport.SendWithStart(new BrokerRequest
            {
                ProtocolVersion = ProtocolVersion,
                RequestId = Guid.NewGuid(),
                Operation = 8
            }, CancellationToken.None);
        }

        internal static void RetryConnection()
        {
            Transport.ResetStartRequest();
        }

        private static string ResolveBrokerPath()
        {
            return Path.Combine(
                Path.GetDirectoryName(typeof(BrokerProtocol).Assembly.Location),
                "SwSearch.Broker.exe");
        }
    }

    internal sealed class BrokerPipeTransport
    {
        private static readonly DataContractJsonSerializer RequestSerializer =
            new DataContractJsonSerializer(typeof(BrokerRequest));
        private static readonly DataContractJsonSerializer ResponseSerializer =
            new DataContractJsonSerializer(typeof(BrokerResponse));
        private readonly string _pipeName;
        private readonly Func<string> _resolveBrokerPath;
        private readonly Action<ProcessStartInfo> _startBroker;
        private readonly int _initialConnectTimeoutMilliseconds;
        private readonly int _retryConnectTimeoutMilliseconds;
        private readonly int _responseTimeoutMilliseconds;
        private readonly int _initialSearchResponseTimeoutMilliseconds;
        private readonly TimeSpan _startupTimeout;
        private readonly int _retryDelayMilliseconds;
        private readonly Func<bool> _isAppExitMarked;
        private readonly object _startGate = new object();
        private bool _startRequested;

        internal BrokerPipeTransport(
            string pipeName,
            Func<string> resolveBrokerPath,
            Action<ProcessStartInfo> startBroker,
            int initialConnectTimeoutMilliseconds,
            int retryConnectTimeoutMilliseconds,
            int responseTimeoutMilliseconds,
            TimeSpan startupTimeout,
            int retryDelayMilliseconds,
            Func<bool> isAppExitMarked = null,
            int initialSearchResponseTimeoutMilliseconds = 0)
        {
            if (String.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", "pipeName");
            if (resolveBrokerPath == null) throw new ArgumentNullException("resolveBrokerPath");
            if (startBroker == null) throw new ArgumentNullException("startBroker");
            if (initialConnectTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("initialConnectTimeoutMilliseconds");
            if (retryConnectTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("retryConnectTimeoutMilliseconds");
            if (responseTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("responseTimeoutMilliseconds");
            if (initialSearchResponseTimeoutMilliseconds < 0)
                throw new ArgumentOutOfRangeException("initialSearchResponseTimeoutMilliseconds");
            if (startupTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("startupTimeout");
            if (retryDelayMilliseconds < 1) throw new ArgumentOutOfRangeException("retryDelayMilliseconds");
            _pipeName = pipeName;
            _resolveBrokerPath = resolveBrokerPath;
            _startBroker = startBroker;
            _initialConnectTimeoutMilliseconds = initialConnectTimeoutMilliseconds;
            _retryConnectTimeoutMilliseconds = retryConnectTimeoutMilliseconds;
            _responseTimeoutMilliseconds = responseTimeoutMilliseconds;
            _initialSearchResponseTimeoutMilliseconds = initialSearchResponseTimeoutMilliseconds == 0
                ? responseTimeoutMilliseconds
                : initialSearchResponseTimeoutMilliseconds;
            _startupTimeout = startupTimeout;
            _retryDelayMilliseconds = retryDelayMilliseconds;
            _isAppExitMarked = isAppExitMarked;
        }

        internal BrokerResponse SendWithStart(BrokerRequest request, CancellationToken cancellationToken)
        {
            ThrowIfExplicitAppExitRequested();
            try
            {
                BrokerResponse response = Send(request, _initialConnectTimeoutMilliseconds, cancellationToken);
                ResetStartRequest();
                return response;
            }
            catch (BrokerProtocolMismatchException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfExplicitAppExitRequested();
                try
                {
                    RequestBrokerStart(exception);
                    Exception last = exception;
                    Stopwatch deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed < _startupTimeout)
                    {
                        ThrowIfExplicitAppExitRequested();
                        if (cancellationToken.WaitHandle.WaitOne(_retryDelayMilliseconds))
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        try
                        {
                            BrokerResponse response = Send(request, _retryConnectTimeoutMilliseconds, cancellationToken);
                            ResetStartRequest();
                            return response;
                        }
                        catch (BrokerProtocolMismatchException)
                        {
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception retry)
                        {
                            last = retry;
                            ThrowIfExplicitAppExitRequested();
                        }
                    }
                    IOException failure = new IOException(
                        "Unable to connect to the search Broker within the bounded startup interval.", last);
                    AddinDiagnostics.WriteFailure("broker-connect-timeout", failure);
                    throw failure;
                }
                finally
                {
                    // A failed first launch must not permanently suppress every
                    // later monitor/reconnect start attempt.
                    ResetStartRequest();
                }
            }
        }

        internal void ResetStartRequest()
        {
            lock (_startGate)
            {
                _startRequested = false;
            }
        }

        internal int ResponseTimeoutMillisecondsForTesting(BrokerRequest request)
        {
            return GetResponseTimeoutMilliseconds(request);
        }

        private int GetResponseTimeoutMilliseconds(BrokerRequest request)
        {
            // The first page may legitimately include an indexed property lookup
            // and has been observed above eight seconds on a large library. Keep
            // later pages and all non-search operations on the original short
            // bound so scrolling and health checks remain immediately cancelable.
            return request != null && request.Operation == 1 &&
                   request.Query != null && request.Query.Offset == 0
                ? _initialSearchResponseTimeoutMilliseconds
                : _responseTimeoutMilliseconds;
        }

        private void RequestBrokerStart(Exception connectionFailure)
        {
            ThrowIfExplicitAppExitRequested();
            string broker = _resolveBrokerPath();
            bool shouldStart;
            lock (_startGate)
            {
                shouldStart = !_startRequested;
                _startRequested = true;
            }

            if (!shouldStart)
            {
                return;
            }

            // Recheck after claiming the launch turn: the desktop App may have
            // written its exit marker while this taskpane worker was waiting on
            // the transport/start gate.
            ThrowIfExplicitAppExitRequested();
            try
            {
                _startBroker(new ProcessStartInfo
                {
                    FileName = broker,
                    Arguments = "--background",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(broker)
                });
            }
            catch (Exception startFailure)
            {
                throw new IOException("The sibling SwSearch.Broker.exe could not be started.", startFailure);
            }
        }

        private void ThrowIfExplicitAppExitRequested()
        {
            try
            {
                if (_isAppExitMarked != null && _isAppExitMarked())
                {
                    throw new BrokerAppExitMarkedException();
                }
            }
            catch (BrokerAppExitMarkedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Marker probes are advisory. A read failure must not change
                // the existing bounded pipe-start behavior or block SOLIDWORKS.
                AddinDiagnostics.WriteFailure("broker-app-exit-marker", exception);
            }
        }

        private BrokerResponse Send(BrokerRequest request, int connectTimeoutMilliseconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            using (cancellationToken.Register(delegate
            {
                try { pipe.Dispose(); }
                catch { }
            }))
            {
                try { pipe.Connect(connectTimeoutMilliseconds); }
                catch { cancellationToken.ThrowIfCancellationRequested(); throw; }
                cancellationToken.ThrowIfCancellationRequested();
                string json;
                using (MemoryStream buffer = new MemoryStream())
                {
                    RequestSerializer.WriteObject(buffer, request);
                    json = Encoding.UTF8.GetString(buffer.ToArray());
                }

                using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                using (StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine(json);
                    System.Threading.Tasks.Task<string> read = reader.ReadLineAsync();
                    int completed = WaitHandle.WaitAny(
                        new WaitHandle[] { ((IAsyncResult)read).AsyncWaitHandle, cancellationToken.WaitHandle },
                        GetResponseTimeoutMilliseconds(request));
                    if (completed == 1)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    if (completed == WaitHandle.WaitTimeout)
                    {
                        throw new TimeoutException("The search Broker did not respond within the bounded response interval.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    string responseJson = read.GetAwaiter().GetResult();
                    if (String.IsNullOrEmpty(responseJson)) throw new IOException("The search Broker disconnected.");
                    using (MemoryStream responseBuffer = new MemoryStream(Encoding.UTF8.GetBytes(responseJson)))
                    {
                        BrokerResponse response = (BrokerResponse)ResponseSerializer.ReadObject(responseBuffer);
                        if (response == null)
                            throw new InvalidDataException("The search Broker response was invalid.");
                        if (response.ProtocolVersion != BrokerProtocol.ProtocolVersion)
                            throw new BrokerProtocolMismatchException(
                                "搜索服务版本不匹配（Taskpane " + BrokerProtocol.ProtocolVersion +
                                "，服务端 " + response.ProtocolVersion + "）。请完成升级后重试。");
                        if (response.RequestId != request.RequestId)
                            throw new InvalidDataException("The search Broker response was invalid.");
                        if (!response.Success)
                        {
                            if (String.Equals(response.ErrorCode, "BROKER_PROTOCOL_MISMATCH", StringComparison.Ordinal))
                                throw new BrokerProtocolMismatchException("搜索服务版本不匹配。请完成升级后重试。");
                            throw new IOException(response.ErrorCode + ": " + response.ErrorMessage);
                        }
                        return response;
                    }
                }
            }
        }
    }

    internal sealed class BrokerProtocolMismatchException : IOException
    {
        internal BrokerProtocolMismatchException(string message) : base(message) { }
    }

    internal sealed class BrokerAppExitMarkedException : IOException
    {
        internal BrokerAppExitMarkedException()
            : base("The desktop search application was explicitly exited from the tray. Start it again to resume the SOLIDWORKS taskpane search service.")
        {
        }
    }

    [DataContract]
    internal sealed class BrokerRequest
    {
        [DataMember(Name = "protocolVersion")] public int ProtocolVersion { get; set; }
        [DataMember(Name = "requestId")] public Guid RequestId { get; set; }
        [DataMember(Name = "operation")] public int Operation { get; set; }
        [DataMember(Name = "query", EmitDefaultValue = false)] public SearchQueryDto Query { get; set; }
    }

    [DataContract]
    internal sealed class SearchQueryDto
    {
        [DataMember(Name = "text")] public string Text { get; set; }
        [DataMember(Name = "mode")] public int Mode { get; set; }
        [DataMember(Name = "offset")] public int Offset { get; set; }
        [DataMember(Name = "limit")] public int Limit { get; set; }
        [DataMember(Name = "kinds")] public int[] Kinds { get; set; }
        [DataMember(Name = "expectedRevision", EmitDefaultValue = false)] public long? ExpectedRevision { get; set; }
        [DataMember(Name = "pageFingerprint", EmitDefaultValue = false)] public string PageFingerprint { get; set; }
        [DataMember(Name = "libraryRoots", EmitDefaultValue = false)] public string[] LibraryRoots { get; set; }
        [DataMember(Name = "isInteractive", EmitDefaultValue = false)] public bool? IsInteractive { get; set; }
        [DataMember(Name = "scope")] public SearchScopeDto Scope { get; set; }
        [IgnoreDataMember] public string RootPath
        {
            get { return Scope == null ? null : Scope.RootPath; }
            set
            {
                if (Scope == null) Scope = new SearchScopeDto();
                Scope.RootPath = value;
            }
        }
        [IgnoreDataMember] public bool IncludeSubdirectories
        {
            get { return Scope != null && Scope.IncludeSubdirectories; }
            set
            {
                if (Scope == null) Scope = new SearchScopeDto();
                Scope.IncludeSubdirectories = value;
            }
        }
    }

    [DataContract]
    internal sealed class SearchScopeDto
    {
        [DataMember(Name = "kind")] public int Kind { get; set; }
        [DataMember(Name = "rootPath", EmitDefaultValue = false)] public string RootPath { get; set; }
        [DataMember(Name = "includeSubdirectories")] public bool IncludeSubdirectories { get; set; }
    }

    [DataContract]
    internal sealed class BrokerResponse
    {
        [DataMember(Name = "protocolVersion")] public int ProtocolVersion { get; set; }
        [DataMember(Name = "requestId")] public Guid RequestId { get; set; }
        [DataMember(Name = "success")] public bool Success { get; set; }
        [DataMember(Name = "errorCode")] public string ErrorCode { get; set; }
        [DataMember(Name = "errorMessage")] public string ErrorMessage { get; set; }
        [DataMember(Name = "hits")] public List<SearchHitDto> Hits { get; set; }
        [DataMember(Name = "state")] public BrokerStateDto State { get; set; }
        [DataMember(Name = "hasMore", EmitDefaultValue = false)] public bool? HasMore { get; set; }
        [DataMember(Name = "indexRevision", EmitDefaultValue = false)] public long? IndexRevision { get; set; }
        [DataMember(Name = "pageFingerprint", EmitDefaultValue = false)] public string PageFingerprint { get; set; }
        [DataMember(Name = "pageReset", EmitDefaultValue = false)] public bool PageReset { get; set; }
        [DataMember(Name = "totalCount", EmitDefaultValue = false)] public long? TotalCount { get; set; }
    }

    [DataContract]
    internal sealed class BrokerStateDto
    {
        [DataMember(Name = "epoch")] public string Epoch { get; set; }
        [DataMember(Name = "revision")] public long Revision { get; set; }
        [DataMember(Name = "phase")] public int Phase { get; set; }
        [DataMember(Name = "cacheAvailable")] public bool CacheAvailable { get; set; }
        [DataMember(Name = "backgroundUpdating")] public bool BackgroundUpdating { get; set; }
        [DataMember(Name = "fileCount")] public long FileCount { get; set; }
    }

    [DataContract]
    internal sealed class SearchHitDto
    {
        [DataMember(Name = "fullPath")] public string FullPath { get; set; }
        [DataMember(Name = "kind")] public int Kind { get; set; }
        [DataMember(Name = "fileName")] public string FileName { get; set; }
        [DataMember(Name = "matchedProperty")] public string MatchedProperty { get; set; }
        [DataMember(Name = "matchedValue")] public string MatchedValue { get; set; }
        [DataMember(Name = "originModelPath")] public string OriginModelPath { get; set; }
        [DataMember(Name = "originConfiguration", EmitDefaultValue = false)] public string OriginConfiguration { get; set; }
        [DataMember(Name = "cacheMayBeStale")] public bool CacheMayBeStale { get; set; }
    }
}
