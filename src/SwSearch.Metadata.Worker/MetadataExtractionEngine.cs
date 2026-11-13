using System.Collections.Concurrent;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal interface IMetadataExtractionEngine
{
    DocumentMetadata Extract(string fullPath, CancellationToken cancellationToken);
}

internal interface IEvidenceAwareMetadataExtractionEngine
{
    DocumentMetadata Extract(
        MetadataFileEvidence evidence,
        CancellationToken cancellationToken);
}

internal sealed class MetadataExtractionEngine :
    IMetadataExtractionEngine,
    IEvidenceAwareMetadataExtractionEngine
{
    private readonly FileRequestValidator _validator;
    private readonly INoApiMetadataExtractor? _noApiExtractor;
    private readonly IDocumentManagerKeyProvider? _keyProvider;
    private readonly IDocumentManagerSessionFactory? _sessionFactory;
    private readonly IWindowsPropertyStoreExtractor _propertyStore;

    public MetadataExtractionEngine(
        FileRequestValidator validator,
        INoApiMetadataExtractor noApiExtractor,
        IWindowsPropertyStoreExtractor propertyStore)
    {
        _validator = validator;
        _noApiExtractor = noApiExtractor;
        _propertyStore = propertyStore;
    }

    // Kept for compatibility with isolated legacy Document Manager tests and
    // deployments. The production composition below does not use this path.
    public MetadataExtractionEngine(
        FileRequestValidator validator,
        IDocumentManagerKeyProvider keyProvider,
        IDocumentManagerSessionFactory sessionFactory,
        IWindowsPropertyStoreExtractor propertyStore)
    {
        _validator = validator;
        _keyProvider = keyProvider;
        _sessionFactory = sessionFactory;
        _propertyStore = propertyStore;
    }

    public DocumentMetadata Extract(string fullPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = _validator.Validate(fullPath);
        return Extract(file, cancellationToken);
    }

    public DocumentMetadata Extract(
        MetadataFileEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new FileCandidate(
            evidence.RequestedPath,
            evidence.Kind,
            evidence.Size,
            evidence.LastWriteTimeUtc);
        return Extract(
            new ValidatedFile(evidence.RequestedPath, candidate),
            cancellationToken);
    }

    private DocumentMetadata Extract(
        ValidatedFile file,
        CancellationToken cancellationToken)
    {
        if (!SupportedSolidWorksDocumentKinds.IsSupported(file.Candidate.Kind))
        {
            throw new MetadataWorkerException(MetadataErrorCodes.UnsupportedFileType);
        }

        string? primaryWarning;
        if (_noApiExtractor is not null)
        {
            try
            {
                var extraction = _noApiExtractor.Extract(file, cancellationToken);
                return new DocumentMetadata(
                    file.Candidate,
                    extraction.Properties,
                    Array.Empty<DocumentReference>(),
                    MetadataSource.NoApi,
                    DateTimeOffset.UtcNow,
                    WarningCode: null,
                    extraction.ConfigurationNames);
            }
            catch (MetadataWorkerException exception) when (
                exception.Code is not MetadataErrorCodes.FileAccessDenied and
                not MetadataErrorCodes.FileChangedDuringRead and
                not MetadataErrorCodes.FileSharingViolation)
            {
                primaryWarning = exception.Code;
            }
        }
        else
        {
            primaryWarning = TryExtractWithDocumentManager(
                file,
                cancellationToken,
                out var documentManagerExtraction);
            if (documentManagerExtraction is not null)
            {
                return new DocumentMetadata(
                    file.Candidate,
                    documentManagerExtraction.Properties,
                    Array.Empty<DocumentReference>(),
                    MetadataSource.DocumentManager,
                    DateTimeOffset.UtcNow,
                    WarningCode: null,
                    documentManagerExtraction.ConfigurationNames);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var propertyStore = _propertyStore.Extract(file);
        var warning = SelectWarning(primaryWarning, propertyStore.WarningCode);
        var source = propertyStore.Properties.Count > 0
            ? MetadataSource.WindowsPropertyStore
            : MetadataSource.FileSystemOnly;

        return new DocumentMetadata(
            file.Candidate,
            propertyStore.Properties,
            Array.Empty<DocumentReference>(),
            source,
            DateTimeOffset.UtcNow,
            warning,
            ConfigurationNames: Array.Empty<string>());
    }

    private string? TryExtractWithDocumentManager(
        ValidatedFile file,
        CancellationToken cancellationToken,
        out DocumentManagerExtraction? extraction)
    {
        extraction = null;
        if (_sessionFactory is null || _keyProvider is null || !_sessionFactory.IsClassRegistered)
        {
            return MetadataErrorCodes.DocumentManagerUnavailable;
        }

        DocumentManagerKey? key;
        try
        {
            key = _keyProvider.TryGetKeyAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return MetadataErrorCodes.DocumentManagerKeyProviderFailed;
        }

        if (key is null)
        {
            return MetadataErrorCodes.DocumentManagerKeyMissing;
        }

        using (key)
        {
            try
            {
                using var session = _sessionFactory.Create(key);
                extraction = session.Extract(file);
                return null;
            }
            catch (MetadataWorkerException exception)
            {
                return exception.Code;
            }
        }
    }

    private static string? SelectWarning(string? documentManagerWarning, string? propertyStoreWarning)
    {
        if (propertyStoreWarning is MetadataErrorCodes.FileAccessDenied or MetadataErrorCodes.FileCorrupt)
        {
            return propertyStoreWarning;
        }

        return documentManagerWarning ?? propertyStoreWarning;
    }
}

internal sealed class MetadataRequestProcessor
{
    public const int DefaultTimeoutMilliseconds = 5_000;
    public const int MaximumTimeoutMilliseconds = 15_000;
    private readonly IMetadataExtractionEngine _engine;

    public MetadataRequestProcessor(IMetadataExtractionEngine engine)
    {
        _engine = engine;
    }

    public async ValueTask<RequestProcessingResult> ProcessAsync(
        MetadataRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != ContractVersions.MetadataPipe)
        {
            return Error(request, MetadataErrorCodes.ProtocolVersionUnsupported, retryable: false);
        }

        if (request.RequestId == Guid.Empty)
        {
            return Error(request, MetadataErrorCodes.InvalidRequest, retryable: false);
        }

        var timeout = request.TimeoutMilliseconds <= 0
            ? DefaultTimeoutMilliseconds
            : Math.Min(request.TimeoutMilliseconds, MaximumTimeoutMilliseconds);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var extractionTask = StaTaskRunner.RunAsync(
            () => ProcessFilePipeline(request, linkedSource.Token));
        try
        {
            return await extractionTask.WaitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error(request, MetadataErrorCodes.Cancelled, retryable: true);
        }
        catch (OperationCanceledException)
        {
            return Error(
                request,
                MetadataErrorCodes.Timeout,
                retryable: true,
                requiresWorkerRestart: true);
        }
        catch (MetadataWorkerException exception)
        {
            return Error(request, exception.Code, exception.Retryable);
        }
        catch
        {
            return Error(request, MetadataErrorCodes.FileCorrupt, retryable: true);
        }
    }

    private RequestProcessingResult ProcessFilePipeline(
        MetadataRequest request,
        CancellationToken cancellationToken)
    {
        MetadataFileEvidence? before = null;
        var disposition = MetadataFileDisposition.Normal;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = MetadataFilePipeline.ValidateAndNormalizePath(request.FullPath);
            disposition = MetadataFilePipeline.ReadDisposition(fullPath);
            if (disposition == MetadataFileDisposition.CloudPlaceholder)
            {
                return PipelineResult(
                    request,
                    metadata: null,
                    MetadataPipelineOutcome.Deferred,
                    disposition,
                    before: null,
                    after: null,
                    "CLOUD_PLACEHOLDER_DEFERRED",
                    retryable: true);
            }
            if (disposition == MetadataFileDisposition.ReparsePoint)
            {
                return PipelineResult(
                    request,
                    metadata: null,
                    MetadataPipelineOutcome.Unsupported,
                    disposition,
                    before: null,
                    after: null,
                    "METADATA_SKIPPED_REPARSE_POINT",
                    retryable: false);
            }

            before = MetadataFilePipeline.Capture(fullPath);
            if (!string.Equals(
                    before.RequestedPath,
                    before.FinalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return PipelineResult(
                    request,
                    metadata: null,
                    MetadataPipelineOutcome.Unsupported,
                    MetadataFileDisposition.ReparsePoint,
                    before,
                    after: null,
                    "METADATA_REPARSE_REDIRECT_REJECTED",
                    retryable: false);
            }

            if (!string.IsNullOrEmpty(request.ExpectedFingerprint) &&
                request.ExtractorVersion > 0 &&
                !string.Equals(
                    request.ExpectedFingerprint,
                    MetadataFilePipeline.Fingerprint(before, request.ExtractorVersion),
                    StringComparison.Ordinal))
            {
                return PipelineResult(
                    request,
                    metadata: null,
                    MetadataPipelineOutcome.FileChanged,
                    disposition,
                    before,
                    before,
                    MetadataErrorCodes.FileChangedDuringRead,
                    retryable: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var metadata = _engine is IEvidenceAwareMetadataExtractionEngine evidenceAware
                ? evidenceAware.Extract(before, cancellationToken)
                : _engine.Extract(fullPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var after = MetadataFilePipeline.Capture(fullPath);
            if (!MetadataFilePipeline.SameObjectAndVersion(before, after) ||
                !MetadataFilePipeline.Matches(after, metadata.File))
            {
                return PipelineResult(
                    request,
                    metadata: null,
                    MetadataPipelineOutcome.FileChanged,
                    disposition,
                    before,
                    after,
                    MetadataErrorCodes.FileChangedDuringRead,
                    retryable: true);
            }

            return PipelineResult(
                request,
                metadata,
                string.IsNullOrEmpty(metadata.WarningCode)
                    ? MetadataPipelineOutcome.Completed
                    : MetadataPipelineOutcome.Partial,
                disposition,
                before,
                after,
                errorCode: null,
                retryable: false);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return PipelineResult(
                request,
                metadata: null,
                MetadataPipelineOutcome.FileGone,
                disposition,
                before,
                after: null,
                MetadataErrorCodes.FileGone,
                retryable: false);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return PipelineResult(
                request,
                metadata: null,
                MetadataPipelineOutcome.FileUnreadable,
                disposition,
                before,
                after: null,
                MetadataErrorCodes.FileUnreadable,
                retryable: true);
        }
        catch (MetadataWorkerException exception)
        {
            var outcome = exception.Code switch
            {
                MetadataErrorCodes.FileNotFound => MetadataPipelineOutcome.FileGone,
                MetadataErrorCodes.FileAccessDenied or MetadataErrorCodes.FileSharingViolation
                    => MetadataPipelineOutcome.FileUnreadable,
                MetadataErrorCodes.UnsupportedFileType or
                MetadataErrorCodes.NoApiFormatUnsupported or
                MetadataErrorCodes.NoApiVersionUnsupported or
                MetadataErrorCodes.FileCorrupt => MetadataPipelineOutcome.Unsupported,
                _ => MetadataPipelineOutcome.Failed,
            };
            return PipelineResult(
                request,
                metadata: null,
                outcome,
                disposition,
                before,
                after: null,
                exception.Code,
                exception.Retryable);
        }
    }

    private static RequestProcessingResult PipelineResult(
        MetadataRequest request,
        DocumentMetadata? metadata,
        MetadataPipelineOutcome outcome,
        MetadataFileDisposition disposition,
        MetadataFileEvidence? before,
        MetadataFileEvidence? after,
        string? errorCode,
        bool retryable) =>
        new(
            new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                metadata,
                errorCode,
                errorCode is null ? null : MetadataErrorCodes.ChineseMessage(errorCode),
                retryable,
                request.PipelineLeaseId,
                WorkerGenerationNonce: default,
                outcome,
                disposition,
                before,
                after),
            RequiresWorkerRestart: false);

    private static RequestProcessingResult Error(
        MetadataRequest request,
        string code,
        bool retryable,
        bool requiresWorkerRestart = false) =>
        new(
            new MetadataResponse(
                ContractVersions.MetadataPipe,
                request.RequestId,
                Metadata: null,
                code,
                MetadataErrorCodes.ChineseMessage(code),
                retryable,
                request.PipelineLeaseId,
                WorkerGenerationNonce: default,
                Outcome: code == MetadataErrorCodes.Timeout
                    ? MetadataPipelineOutcome.Failed
                    : MetadataPipelineOutcome.Unknown),
            requiresWorkerRestart);
}

internal sealed record RequestProcessingResult(
    MetadataResponse Response,
    bool RequiresWorkerRestart);

internal static class StaTaskRunner
{
    private static readonly BlockingCollection<Action> Queue = new();

    static StaTaskRunner()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable())
            {
                work();
            }
        })
        {
            IsBackground = true,
            Name = "SwSearch.Metadata.STA"
        };

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
    }

    public static Task<T> RunAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }
}

internal sealed record WorkerServices(
    MetadataRequestProcessor RequestProcessor,
    IMetadataCapabilityProbe CapabilityProbe);

internal static class WorkerComposition
{
    public static WorkerServices CreateDefault()
    {
        var validator = new FileRequestValidator(new PhysicalFileSystemAccessor());
        var engine = new MetadataExtractionEngine(
            validator,
            new NoApiMetadataExtractor(),
            new WindowsPropertyStoreExtractor());

        return new WorkerServices(
            new MetadataRequestProcessor(engine),
            new NoApiCapabilityProbe());
    }
}
