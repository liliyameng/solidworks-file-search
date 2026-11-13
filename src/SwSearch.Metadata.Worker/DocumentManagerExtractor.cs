using System.Collections;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal sealed record DocumentManagerExtraction(
    IReadOnlyList<DocumentProperty> Properties,
    IReadOnlyList<DocumentReference> References,
    IReadOnlyList<string>? ConfigurationNames = null);

internal interface IDocumentManagerSessionFactory
{
    bool IsClassRegistered { get; }

    IDocumentManagerSession Create(DocumentManagerKey key);
}

internal interface IDocumentManagerSession : IDisposable
{
    DocumentManagerExtraction Extract(ValidatedFile file);
}

internal sealed class LateBoundDocumentManagerSessionFactory : IDocumentManagerSessionFactory
{
    private const string ClassFactoryProgId = "SwDocumentMgr.SwDMClassFactory";

    public bool IsClassRegistered =>
        OperatingSystem.IsWindows() &&
        Type.GetTypeFromProgID(ClassFactoryProgId, throwOnError: false) is not null;

    public IDocumentManagerSession Create(DocumentManagerKey key)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new MetadataWorkerException(MetadataErrorCodes.DocumentManagerUnavailable);
        }

        var type = Type.GetTypeFromProgID(ClassFactoryProgId, throwOnError: false);
        if (type is null)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.DocumentManagerUnavailable);
        }

        object? factory = null;
        object? application = null;
        try
        {
            factory = Activator.CreateInstance(type);
            if (factory is null)
            {
                throw new MetadataWorkerException(MetadataErrorCodes.DocumentManagerUnavailable);
            }

            dynamic lateBoundFactory = factory;
            var revealedKey = key.Reveal();
            try
            {
                application = lateBoundFactory.GetApplication(revealedKey);
            }
            finally
            {
                revealedKey = string.Empty;
            }

            if (application is null)
            {
                throw new MetadataWorkerException(MetadataErrorCodes.DocumentManagerKeyInvalid);
            }

            return new LateBoundDocumentManagerSession(factory, application);
        }
        catch (MetadataWorkerException)
        {
            ComRelease(application);
            ComRelease(factory);
            throw;
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException)
        {
            ComRelease(application);
            ComRelease(factory);
            throw new MetadataWorkerException(
                MetadataErrorCodes.DocumentManagerKeyInvalid,
                innerException: exception);
        }
    }

    internal static void ComRelease(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}

internal sealed class LateBoundDocumentManagerSession : IDocumentManagerSession
{
    private const int MaximumPropertiesPerScope = 4_096;
    private const int MaximumConfigurations = 2_048;
    private object? _factory;
    private object? _application;

    public LateBoundDocumentManagerSession(object factory, object application)
    {
        _factory = factory;
        _application = application;
    }

    public DocumentManagerExtraction Extract(ValidatedFile file)
    {
        ObjectDisposedException.ThrowIf(_application is null, this);
        if (!SupportedSolidWorksDocumentKinds.IsSupported(file.Candidate.Kind))
        {
            throw new MetadataWorkerException(MetadataErrorCodes.UnsupportedFileType);
        }

        dynamic application = _application;
        dynamic? document = null;
        try
        {
            var documentType = file.Candidate.Kind switch
            {
                SolidWorksDocumentKind.Part => 1,
                SolidWorksDocumentKind.Assembly => 2,
                _ => throw new MetadataWorkerException(MetadataErrorCodes.UnsupportedFileType)
            };

            object openError = 0;
            document = application.GetDocument(file.FullPath, documentType, true, out openError);
            var openErrorValue = Convert.ToInt32(openError, System.Globalization.CultureInfo.InvariantCulture);
            if (document is null || openErrorValue != 0)
            {
                throw MapOpenError(openErrorValue);
            }

            var properties = new List<DocumentProperty>();
            properties.AddRange(ReadProperties(document, PropertyScope.Document, configurationName: null));

            var configurationNames = ReadConfigurations(document, properties);
            return new DocumentManagerExtraction(
                properties,
                Array.Empty<DocumentReference>(),
                configurationNames);
        }
        catch (MetadataWorkerException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.FileAccessDenied, innerException: exception);
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or InvalidCastException)
        {
            throw new MetadataWorkerException(
                MetadataErrorCodes.FileCorrupt,
                retryable: true,
                innerException: exception);
        }
        finally
        {
            if (document is not null)
            {
                try
                {
                    document.CloseDoc();
                }
                catch (Exception exception) when (exception is COMException or RuntimeBinderException)
                {
                    // The document was opened read-only; a close failure must not trigger a save attempt.
                }

                LateBoundDocumentManagerSessionFactory.ComRelease(document);
            }
        }
    }

    public void Dispose()
    {
        LateBoundDocumentManagerSessionFactory.ComRelease(
            Interlocked.Exchange(ref _application, null));
        LateBoundDocumentManagerSessionFactory.ComRelease(
            Interlocked.Exchange(ref _factory, null));
    }

    private static IReadOnlyList<DocumentProperty> ReadProperties(
        dynamic container,
        PropertyScope scope,
        string? configurationName)
    {
        var result = new List<DocumentProperty>();
        object? namesObject = container.GetCustomPropertyNames();
        foreach (var propertyName in ToStrings(namesObject).Take(MaximumPropertiesPerScope))
        {
            object propertyType = 0;
            string linkedExpression = string.Empty;
            string evaluatedValue;
            try
            {
                evaluatedValue = Convert.ToString(
                    container.GetCustomPropertyValues(propertyName, out propertyType, out linkedExpression),
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch (Exception exception) when (exception is COMException or RuntimeBinderException)
            {
                evaluatedValue = Convert.ToString(
                    container.GetCustomProperty(propertyName, out propertyType),
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                linkedExpression = string.Empty;
            }

            var rawValue = string.IsNullOrEmpty(linkedExpression)
                ? evaluatedValue
                : linkedExpression;

            result.Add(new DocumentProperty(
                propertyName,
                rawValue,
                evaluatedValue,
                scope,
                configurationName,
                MetadataSource.DocumentManager,
                DisplayValue: string.IsNullOrWhiteSpace(evaluatedValue) ? null : evaluatedValue,
                CachedValue: string.IsNullOrWhiteSpace(evaluatedValue) ? null : evaluatedValue,
                IsLinked: !string.IsNullOrEmpty(linkedExpression)));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadConfigurations(
        dynamic document,
        ICollection<DocumentProperty> properties)
    {
        var configurationNames = new List<string>();
        dynamic? manager = null;
        try
        {
            manager = document.ConfigurationManager;
            if (manager is null)
            {
                return configurationNames;
            }

            object? namesObject = manager.GetConfigurationNames();
            foreach (var configurationName in ToStrings(namesObject).Take(MaximumConfigurations))
            {
                configurationNames.Add(configurationName);
                dynamic? configuration = null;
                try
                {
                    configuration = manager.GetConfigurationByName(configurationName);
                    if (configuration is not null)
                    {
                        foreach (var property in ReadProperties(
                                     configuration,
                                     PropertyScope.Configuration,
                                     configurationName))
                        {
                            properties.Add(property);
                        }
                    }
                }
                finally
                {
                    LateBoundDocumentManagerSessionFactory.ComRelease(configuration);
                }
            }
        }
        finally
        {
            LateBoundDocumentManagerSessionFactory.ComRelease(manager);
        }

        return configurationNames;
    }

    private static IReadOnlyList<string> ToStrings(object? value) =>
        ToObjects(value)
            .Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();

    private static IReadOnlyList<object> ToObjects(object? value)
    {
        if (value is null)
        {
            return Array.Empty<object>();
        }

        if (value is string)
        {
            return new[] { value };
        }

        if (value is IEnumerable enumerable)
        {
            return enumerable.Cast<object>().ToArray();
        }

        return new[] { value };
    }

    private static MetadataWorkerException MapOpenError(int openError) => openError switch
    {
        3 => new MetadataWorkerException(MetadataErrorCodes.FileNotFound),
        4 => new MetadataWorkerException(MetadataErrorCodes.FileAccessDenied),
        5 => new MetadataWorkerException(MetadataErrorCodes.DocumentManagerKeyInvalid),
        6 => new MetadataWorkerException(MetadataErrorCodes.FutureVersion),
        _ => new MetadataWorkerException(MetadataErrorCodes.FileCorrupt, retryable: true)
    };
}

internal sealed class DocumentManagerCapabilityProbe : IMetadataCapabilityProbe
{
    private readonly IDocumentManagerKeyProvider _keyProvider;
    private readonly IDocumentManagerSessionFactory _sessionFactory;

    public DocumentManagerCapabilityProbe(
        IDocumentManagerKeyProvider keyProvider,
        IDocumentManagerSessionFactory sessionFactory)
    {
        _keyProvider = keyProvider;
        _sessionFactory = sessionFactory;
    }

    public async ValueTask<RuntimeCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!_sessionFactory.IsClassRegistered)
        {
            return Degraded(
                MetadataErrorCodes.DocumentManagerUnavailable,
                "Document Manager COM 未注册；Windows 属性/文件系统降级仍可用。");
        }

        DocumentManagerKey? key;
        try
        {
            key = await _keyProvider.TryGetKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Degraded(
                MetadataErrorCodes.DocumentManagerKeyProviderFailed,
                "Document Manager COM 已注册，但受保护许可证存储不可用；已降级。");
        }

        if (key is null)
        {
            return Degraded(
                MetadataErrorCodes.DocumentManagerKeyMissing,
                "Document Manager COM 已注册，但未配置受保护许可证；已降级。");
        }

        using (key)
        {
            try
            {
                using var session = _sessionFactory.Create(key);
                return new RuntimeCapability(
                    CapabilityState.Available,
                    "SWDM_AVAILABLE",
                    "Document Manager 已通过运行时许可证探测。",
                    SafeDetails());
            }
            catch (MetadataWorkerException exception)
            {
                return Degraded(
                    exception.Code,
                    MetadataErrorCodes.ChineseMessage(exception.Code));
            }
        }
    }

    private static RuntimeCapability Degraded(string code, string message) =>
        new(CapabilityState.Degraded, code, message, SafeDetails());

    private static IReadOnlyDictionary<string, string> SafeDetails() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["documentManager"] = "late-binding",
            ["fallback"] = "windows-property-store,file-system",
            ["solidWorksApplication"] = "forbidden"
        };
}
