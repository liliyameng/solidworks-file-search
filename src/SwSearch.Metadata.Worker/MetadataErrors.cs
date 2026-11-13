namespace SwSearch.Metadata.Worker;

public static class MetadataErrorCodes
{
    public const string ProtocolVersionUnsupported = "METADATA_PROTOCOL_VERSION_UNSUPPORTED";
    public const string InvalidRequest = "METADATA_REQUEST_INVALID";
    public const string InvalidPath = "METADATA_PATH_INVALID";
    public const string DevicePathRejected = "METADATA_DEVICE_PATH_REJECTED";
    public const string PathTooLong = "METADATA_PATH_TOO_LONG";
    public const string UnsupportedFileType = "METADATA_FILE_TYPE_UNSUPPORTED";
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string FileGone = "FILE_GONE";
    public const string FileUnreadable = "FILE_UNREADABLE";
    public const string FileAccessDenied = "FILE_ACCESS_DENIED";
    public const string FileSharingViolation = "FILE_SHARING_VIOLATION";
    public const string FileChangedDuringRead = "FILE_CHANGED_DURING_READ";
    public const string FileCorrupt = "FILE_CORRUPT_OR_UNREADABLE";
    public const string NoApiFormatUnsupported = "NOAPI_FORMAT_UNSUPPORTED";
    public const string NoApiVersionUnsupported = "NOAPI_VERSION_UNSUPPORTED";
    public const string FutureVersion = "SWDM_FUTURE_VERSION";
    public const string DocumentManagerUnavailable = "SWDM_UNAVAILABLE";
    public const string DocumentManagerKeyMissing = "SWDM_KEY_MISSING";
    public const string DocumentManagerKeyInvalid = "SWDM_KEY_INVALID";
    public const string DocumentManagerKeyProviderFailed = "SWDM_KEY_PROVIDER_FAILED";
    public const string PropertyStoreUnavailable = "WINDOWS_PROPERTY_STORE_UNAVAILABLE";
    public const string PropertyStoreEmpty = "WINDOWS_PROPERTY_STORE_EMPTY";
    public const string Timeout = "METADATA_TIMEOUT";
    public const string Cancelled = "METADATA_CANCELLED";
    public const string WorkerUnavailable = "METADATA_WORKER_UNAVAILABLE";
    public const string WorkerCrashed = "METADATA_WORKER_CRASHED";
    public const string FrameInvalid = "METADATA_FRAME_INVALID";
    public const string BrokerHostedInternalFault = "METADATA_BROKER_INTERNAL_FAULT";

    public static string ChineseMessage(string code) => code switch
    {
        ProtocolVersionUnsupported => "元数据协议版本不受支持。",
        InvalidRequest => "元数据请求无效。",
        InvalidPath => "文件路径无效；请选择绝对本地路径或 UNC 路径。",
        DevicePathRejected => "为安全起见，不支持设备路径。",
        PathTooLong => "文件路径超过允许长度。",
        UnsupportedFileType => "仅支持 SLDPRT 和 SLDASM 文件。",
        FileNotFound => "文件不存在或已被移动。",
        FileAccessDenied => "没有权限读取该文件。",
        FileSharingViolation => "文件正被其他程序独占，暂时无法读取。",
        FileChangedDuringRead => "读取期间文件发生变化，已放弃本次结果并等待重试。",
        FileCorrupt => "文件损坏或无法读取；已尽量保留基本信息。",
        NoApiFormatUnsupported => "该 SOLIDWORKS 文件容器格式暂不受内置解析器支持。",
        NoApiVersionUnsupported => "该 SOLIDWORKS 文件版本尚未经过内置解析器验证，已安全停止解析。",
        FutureVersion => "当前 Document Manager 或许可证不支持该文件版本。",
        DocumentManagerUnavailable => "Document Manager 不可用，已使用 Windows 属性和文件信息降级。",
        DocumentManagerKeyMissing => "未配置 Document Manager 许可证，已使用 Windows 属性和文件信息降级。",
        DocumentManagerKeyInvalid => "Document Manager 许可证无效或不覆盖该文件版本，已降级读取。",
        DocumentManagerKeyProviderFailed => "无法从受保护存储读取 Document Manager 许可证，已降级读取。",
        PropertyStoreUnavailable => "Windows 属性处理器不可用，仅显示文件基本信息。",
        PropertyStoreEmpty => "Windows 属性处理器未返回可用属性；这不表示文件没有自定义属性。",
        Timeout => "读取 SOLIDWORKS 属性超时，请重试。",
        Cancelled => "属性读取已取消。",
        WorkerUnavailable => "元数据工作进程不可用。",
        WorkerCrashed => "元数据工作进程意外退出，已可重试。",
        FrameInvalid => "元数据通信帧无效。",
        BrokerHostedInternalFault => "Broker 内部属性处理本轮失败，正在自动恢复；现有缓存仍可搜索。",
        _ => "读取 SOLIDWORKS 属性失败。"
    };
}

internal sealed class MetadataWorkerException : Exception
{
    public MetadataWorkerException(string code, bool retryable = false, Exception? innerException = null)
        : base(MetadataErrorCodes.ChineseMessage(code), innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}
