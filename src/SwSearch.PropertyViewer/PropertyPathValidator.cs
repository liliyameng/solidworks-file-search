using System.IO;

namespace SwSearch.PropertyViewer;

public sealed record PathValidationResult(bool IsValid, string? FullPath, string? ErrorMessage, string? ErrorCode)
{
    public static PathValidationResult Valid(string fullPath) => new(true, fullPath, null, null);
    public static PathValidationResult Invalid(string message, string code) => new(false, null, message, code);
}

public sealed record ViewerFileSnapshot(long Length, DateTimeOffset LastWriteTimeUtc);

public interface IViewerFileSystem
{
    bool FileExists(string fullPath);
    ViewerFileSnapshot GetSnapshot(string fullPath);
}

public sealed class PhysicalViewerFileSystem : IViewerFileSystem
{
    public bool FileExists(string fullPath) => File.Exists(fullPath);

    public ViewerFileSnapshot GetSnapshot(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return new ViewerFileSnapshot(info.Length, info.LastWriteTimeUtc);
    }
}

public static class PropertyPathValidator
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sldprt", ".sldasm"
    };

    public static PathValidationResult Validate(string? argument, IViewerFileSystem fileSystem)
    {
        var syntax = ValidateSyntax(argument);
        if (!syntax.IsValid) return syntax;
        return fileSystem.FileExists(syntax.FullPath!)
            ? syntax
            : PathValidationResult.Invalid("文件不存在，可能已被移动或删除。", "FILE_NOT_FOUND");
    }

    public static PathValidationResult ValidateSyntax(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return PathValidationResult.Invalid("未提供文件路径。", "PATH_EMPTY");
        }

        if (argument.Length > 32767 || argument.IndexOf('\0') >= 0)
        {
            return PathValidationResult.Invalid("文件路径过长或包含无效字符。", "PATH_INVALID");
        }

        if (argument.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith(@"\\?\PIPE", StringComparison.OrdinalIgnoreCase))
        {
            return PathValidationResult.Invalid("不允许设备或管道路径。", "DEVICE_PATH_REJECTED");
        }

        try
        {
            if (!Path.IsPathFullyQualified(argument))
            {
                return PathValidationResult.Invalid("必须使用绝对本地路径或 UNC 路径。", "PATH_NOT_ABSOLUTE");
            }

            var fullPath = Path.GetFullPath(argument);
            var isUnc = fullPath.StartsWith(@"\\", StringComparison.Ordinal);
            var isDrivePath = fullPath.Length >= 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' &&
                              (fullPath[2] == '\\' || fullPath[2] == '/');
            var isExtendedDrive = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                                  fullPath.Length >= 7 && char.IsAsciiLetter(fullPath[4]) && fullPath[5] == ':';
            if (!isUnc && !isDrivePath && !isExtendedDrive)
            {
                return PathValidationResult.Invalid("仅允许绝对本地路径或 UNC 路径。", "PATH_KIND_REJECTED");
            }

            if (!AllowedExtensions.Contains(Path.GetExtension(fullPath)))
            {
                return PathValidationResult.Invalid("仅支持 .SLDPRT 和 .SLDASM 文件。", "EXTENSION_NOT_ALLOWED");
            }

            return PathValidationResult.Valid(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathValidationResult.Invalid("文件路径格式无效。", "PATH_INVALID");
        }
    }
}
