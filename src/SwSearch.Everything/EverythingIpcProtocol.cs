using System.Buffers.Binary;
using System.Text;
using SwSearch.Contracts;

namespace SwSearch.Everything;

internal static class EverythingIpcProtocol
{
    internal const nuint CopyDataQuery2W = 18;
    internal const uint RequestFullPathAndName = 0x00000004;
    internal const uint RequestSize = 0x00000010;
    internal const uint RequestDateModified = 0x00000040;
    internal const uint RequestFlags = RequestFullPathAndName | RequestSize | RequestDateModified;
    internal const uint SortNameAscending = 1;
    internal const uint FolderFlag = 0x00000001;
    internal const int QueryHeaderSize = 28;
    internal const int ListHeaderSize = 20;
    internal const int ItemSize = 8;

    internal static byte[] BuildQuery(
        nint replyWindow,
        uint replyMessage,
        string search,
        uint offset,
        uint maximumResults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(search);
        var text = Encoding.Unicode.GetBytes(search + '\0');
        var buffer = new byte[checked(QueryHeaderSize + text.Length)];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, unchecked((uint)replyWindow.ToInt64()));
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], replyMessage);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0); // search flags
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], maximumResults);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], RequestFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], SortNameAscending);
        text.CopyTo(span[QueryHeaderSize..]);
        return buffer;
    }

    internal static EverythingQueryPage ParseResult(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ListHeaderSize)
        {
            throw InvalidData("Everything 返回的数据小于 LIST2 头。 ");
        }

        var total = ReadUInt32(buffer, 0);
        var count = ReadUInt32(buffer, 4);
        var offset = ReadUInt32(buffer, 8);
        var requestFlags = ReadUInt32(buffer, 12);
        if ((requestFlags & RequestFlags) != RequestFlags)
        {
            throw InvalidData($"Everything 返回字段不完整：0x{requestFlags:X8}。");
        }

        var itemBytes = checked((long)count * ItemSize);
        if (itemBytes > buffer.Length - ListHeaderSize)
        {
            throw InvalidData("Everything 返回的项目数量超出消息长度。");
        }

        var results = new List<FileCandidate>(checked((int)Math.Min(count, int.MaxValue)));
        for (uint index = 0; index < count; index++)
        {
            var itemOffset = checked(ListHeaderSize + (int)index * ItemSize);
            var flags = ReadUInt32(buffer, itemOffset);
            var dataOffset = ReadUInt32(buffer, itemOffset + 4);
            if ((flags & FolderFlag) != 0)
            {
                continue;
            }

            if (dataOffset > int.MaxValue)
            {
                throw InvalidData("Everything 返回的数据偏移超出消息边界。");
            }

            var cursor = (int)dataOffset;
            var path = ReadUnicodeString(buffer, ref cursor);
            var size = ReadInt64(buffer, ref cursor);
            var fileTime = ReadInt64(buffer, ref cursor);
            if (!SolidWorksEverythingQuery.TryGetKind(path, out var kind))
            {
                continue;
            }

            if (size < 0)
            {
                throw InvalidData("Everything returned a negative file size.");
            }

            var modified = ToDateTimeOffset(fileTime);
            if (modified == DateTimeOffset.MinValue)
            {
                throw InvalidData("Everything returned an invalid modified timestamp.");
            }

            results.Add(new FileCandidate(path, kind, size, modified));
        }

        return new EverythingQueryPage(total, offset, count, results);
    }

    private static string ReadUnicodeString(ReadOnlySpan<byte> buffer, ref int cursor)
    {
        EnsureAvailable(buffer, cursor, sizeof(uint));
        var length = ReadUInt32(buffer, cursor);
        cursor += sizeof(uint);
        if (length > 32_767)
        {
            throw InvalidData("Everything 返回的路径长度超过安全上限。");
        }

        var byteLength = checked((int)length * sizeof(char));
        EnsureAvailable(buffer, cursor, checked(byteLength + sizeof(char)));
        if (BinaryPrimitives.ReadUInt16LittleEndian(buffer[(cursor + byteLength)..]) != 0)
        {
            throw InvalidData("Everything 返回的路径没有 NUL 终止符。");
        }

        var value = Encoding.Unicode.GetString(buffer.Slice(cursor, byteLength));
        cursor += byteLength + sizeof(char);
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> buffer, ref int cursor)
    {
        EnsureAvailable(buffer, cursor, sizeof(long));
        var value = BinaryPrimitives.ReadInt64LittleEndian(buffer[cursor..]);
        cursor += sizeof(long);
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
    {
        EnsureAvailable(buffer, offset, sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
    }

    private static DateTimeOffset ToDateTimeOffset(long fileTime)
    {
        if (fileTime <= 0)
        {
            return DateTimeOffset.MinValue;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> buffer, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > buffer.Length - length)
        {
            throw InvalidData("Everything 返回的数据偏移超出消息边界。");
        }
    }

    private static EverythingQueryException InvalidData(string message) =>
        new("EVERYTHING_INVALID_REPLY", message);
}
