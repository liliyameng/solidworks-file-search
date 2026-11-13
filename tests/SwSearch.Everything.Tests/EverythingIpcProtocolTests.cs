using System.Buffers.Binary;
using System.Text;
using SwSearch.Contracts;
using SwSearch.Everything;

namespace SwSearch.Everything.Tests;

public sealed class EverythingIpcProtocolTests
{
    [Fact]
    public void BuildQuery_UsesFixedFlagsAndUnicodeSearch()
    {
        var bytes = EverythingIpcProtocol.BuildQuery(
            new nint(0x1234),
            77,
            SolidWorksEverythingQuery.Search,
            10,
            20);

        Assert.Equal((uint)0x1234, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal((uint)77, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((uint)10, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        Assert.Equal((uint)20, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(EverythingIpcProtocol.RequestFlags, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(
            SolidWorksEverythingQuery.Search + '\0',
            Encoding.Unicode.GetString(bytes.AsSpan(EverythingIpcProtocol.QueryHeaderSize)));
        Assert.DoesNotContain('"', SolidWorksEverythingQuery.Search);
    }

    [Fact]
    public void ParseResult_ReadsPathSizeAndModifiedAndFiltersOtherExtensions()
    {
        var modified = new DateTimeOffset(2026, 7, 14, 1, 2, 3, TimeSpan.Zero);
        var bytes = BuildReply(
            (0, @"C:\设计 项目\零件&一.SLDPRT", 1234, modified.ToFileTime()),
            (0, @"C:\ignore.txt", 5, modified.ToFileTime()));

        var page = EverythingIpcProtocol.ParseResult(bytes);

        Assert.Equal((uint)2, page.TotalItems);
        Assert.Equal((uint)2, page.ReturnedItems);
        var candidate = Assert.Single(page.Items);
        Assert.Equal(@"C:\设计 项目\零件&一.SLDPRT", candidate.FullPath);
        Assert.Equal(1234, candidate.Size);
        Assert.Equal(modified, candidate.LastWriteTimeUtc);
        Assert.Equal(SolidWorksDocumentKind.Part, candidate.Kind);
    }

    [Fact]
    public void ParseResult_RejectsOutOfBoundsDataOffset()
    {
        var bytes = BuildReply((0, @"C:\a.SLDASM", 1, DateTimeOffset.UtcNow.ToFileTime()));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), uint.MaxValue);
        var error = Assert.Throws<EverythingQueryException>(() => EverythingIpcProtocol.ParseResult(bytes));
        Assert.Equal("EVERYTHING_INVALID_REPLY", error.Code);
    }

    [Fact]
    public void ParseResult_RejectsInvalidSolidWorksMetadataInsteadOfDeferringToFallback()
    {
        var bytes = BuildReply((0, @"C:\a.SLDASM", -1, 0));

        var error = Assert.Throws<EverythingQueryException>(() => EverythingIpcProtocol.ParseResult(bytes));

        Assert.Equal("EVERYTHING_INVALID_REPLY", error.Code);
    }

    private static byte[] BuildReply(params (uint Flags, string Path, long Size, long Modified)[] items)
    {
        var itemData = new List<byte[]>();
        foreach (var item in items)
        {
            var text = Encoding.Unicode.GetBytes(item.Path + '\0');
            var data = new byte[4 + text.Length + 16];
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)item.Path.Length);
            text.CopyTo(data.AsSpan(4));
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4 + text.Length), item.Size);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(12 + text.Length), item.Modified);
            itemData.Add(data);
        }

        var dataStart = EverythingIpcProtocol.ListHeaderSize + items.Length * EverythingIpcProtocol.ItemSize;
        var reply = new byte[dataStart + itemData.Sum(static data => data.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, (uint)items.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), (uint)items.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), EverythingIpcProtocol.RequestFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(16), EverythingIpcProtocol.SortNameAscending);

        var cursor = dataStart;
        for (var index = 0; index < items.Length; index++)
        {
            var itemOffset = EverythingIpcProtocol.ListHeaderSize + index * EverythingIpcProtocol.ItemSize;
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(itemOffset), items[index].Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(itemOffset + 4), (uint)cursor);
            itemData[index].CopyTo(reply.AsSpan(cursor));
            cursor += itemData[index].Length;
        }

        return reply;
    }
}
