// Container framing is independently implemented from observations documented by
// openswx (MIT, Copyright (c) 2026 openswx contributors). See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SwSearch.Metadata.Worker;

internal sealed class ModernSolidWorksMetadataReader
{
    internal const int MinimumSupportedInternalVersion = 8_000;
    internal const int MaximumSupportedInternalVersion = 19_000;

    private readonly NoApiExtractionLimits _limits;

    public ModernSolidWorksMetadataReader(NoApiExtractionLimits limits)
    {
        _limits = limits;
    }

    public NoApiExtraction Extract(
        FileStream stream,
        ValidatedFile file,
        CancellationToken cancellationToken)
    {
        var container = new ModernSolidWorksContainer(stream, _limits);
        var inventory = container.Inventory(cancellationToken);
        if (inventory.InternalVersion is null ||
            inventory.InternalVersion < MinimumSupportedInternalVersion ||
            inventory.InternalVersion > MaximumSupportedInternalVersion)
        {
            throw new MetadataWorkerException(MetadataErrorCodes.NoApiVersionUnsupported);
        }

        var streams = container.ReadSelectedStreams(inventory, cancellationToken);
        var parsed = SolidWorksXmlMetadataParser.Parse(
            streams,
            file,
            inventory.InternalVersion.Value,
            _limits,
            cancellationToken);

        return new NoApiExtraction(
            parsed.Properties,
            parsed.References,
            parsed.ConfigurationNames,
            inventory.InternalVersion,
            NoApiContainerKind.ModernChunk);
    }
}

internal sealed record ModernContainerInventory(
    int? InternalVersion,
    IReadOnlyDictionary<string, ModernSelectedChunk> SelectedStreams,
    int ValidStreamCount);

internal sealed record ModernChunkDescriptor(
    string Name,
    long DataOffset,
    int CompressedSize,
    int UncompressedSize);

internal sealed class ModernSelectedChunk
{
    public ModernSelectedChunk(
        ModernChunkDescriptor descriptor,
        byte[]? capturedCompressedBytes)
    {
        Descriptor = descriptor;
        CapturedCompressedBytes = capturedCompressedBytes;
    }

    public ModernChunkDescriptor Descriptor { get; }

    public byte[]? CapturedCompressedBytes { get; private set; }

    public byte[]? TakeCapturedCompressedBytes()
    {
        var captured = CapturedCompressedBytes;
        CapturedCompressedBytes = null;
        return captured;
    }
}

internal sealed class ModernSolidWorksContainer
{
    private static readonly byte[] Marker = [0x14, 0x00, 0x06, 0x00, 0x08, 0x00];
    private const uint InlineThreshold = 65_536;
    private const int ChunkHeaderBytes = 0x1E;
    internal const int ScanBufferBytes = 64 * 1024;
    internal const int MaximumCapturedCompressedBytes = 16 * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly NoApiExtractionLimits _limits;
    private readonly int _rotateLeftKey;
    private readonly byte[] _scanBuffer = new byte[ScanBufferBytes + Marker.Length - 1];

    public ModernSolidWorksContainer(FileStream stream, NoApiExtractionLimits limits)
    {
        _stream = stream;
        _limits = limits;
        _stream.Position = 7;
        var key = _stream.ReadByte();
        if (key < 0)
        {
            throw new InvalidDataException("Missing modern container header.");
        }

        _rotateLeftKey = key & 7;
    }

    internal static bool HasMarker(ReadOnlySpan<byte> header)
    {
        if (header.Length < 22)
        {
            return false;
        }

        var limit = Math.Min(header.Length, 64);
        return header[..limit].IndexOf(Marker) >= 0;
    }

    public ModernContainerInventory Inventory(CancellationToken cancellationToken)
    {
        var selected = new Dictionary<string, ModernSelectedChunk>(StringComparer.Ordinal);
        long searchPosition = 0;
        long capturedCompressedBytes = 0;
        var captureBudget = Math.Min(
            MaximumCapturedCompressedBytes,
            _limits.MaximumTotalDecompressedBytes);
        var validStreamCount = 0;
        int? internalVersion = null;

        while (searchPosition <= _stream.Length - Marker.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markerPosition = FindNextMarker(searchPosition, cancellationToken);
            if (markerPosition < 0)
            {
                break;
            }

            searchPosition = markerPosition + Marker.Length;
            if (markerPosition < 4)
            {
                continue;
            }

            var chunkStart = markerPosition - 4;
            if (!TryReadDescriptor(chunkStart, out var descriptor, out var inline, cancellationToken))
            {
                continue;
            }

            validStreamCount++;
            if (validStreamCount > _limits.MaximumStreamCount)
            {
                throw new InvalidDataException("Too many streams in modern container.");
            }

            if (TryReadInternalVersion(descriptor.Name, out var candidateVersion) &&
                (internalVersion is null || candidateVersion > internalVersion))
            {
                internalVersion = candidateVersion;
            }

            if (inline)
            {
                var dataEnd = checked(descriptor.DataOffset + descriptor.CompressedSize);
                if (dataEnd > _stream.Length)
                {
                    throw new InvalidDataException("Modern stream exceeds file bounds.");
                }

                if (IsSelectedMetadataStream(descriptor.Name) &&
                    !selected.ContainsKey(descriptor.Name))
                {
                    byte[]? captured = null;
                    // Capture the usual case while the scan head is already at this
                    // chunk.  Keep a bounded fallback to descriptor-only reads so a
                    // hostile file cannot make the inventory retain unbounded data.
                    if (capturedCompressedBytes + descriptor.CompressedSize <=
                        captureBudget)
                    {
                        captured = GC.AllocateUninitializedArray<byte>(descriptor.CompressedSize);
                        _stream.Position = descriptor.DataOffset;
                        NoApiMetadataExtractor.ReadExactly(_stream, captured, cancellationToken);
                        capturedCompressedBytes += captured.Length;
                    }

                    selected.Add(
                        descriptor.Name,
                        new ModernSelectedChunk(descriptor, captured));
                }

                searchPosition = Math.Max(searchPosition, dataEnd);
            }
        }

        if (validStreamCount == 0)
        {
            throw new InvalidDataException("No valid modern streams were found.");
        }

        return new ModernContainerInventory(internalVersion, selected, validStreamCount);
    }

    public IReadOnlyDictionary<string, byte[]> ReadSelectedStreams(
        ModernContainerInventory inventory,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long totalDecompressed = 0;
        Span<byte> trailing = stackalloc byte[1];

        foreach (var selected in inventory.SelectedStreams.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = selected.Descriptor;
            if (descriptor.CompressedSize > _limits.MaximumCompressedStreamBytes ||
                descriptor.UncompressedSize > _limits.MaximumDecompressedStreamBytes)
            {
                throw new InvalidDataException("Metadata stream exceeds configured limits.");
            }

            if (totalDecompressed + descriptor.UncompressedSize >
                _limits.MaximumTotalDecompressedBytes)
            {
                throw new InvalidDataException("Decompressed metadata exceeds configured limits.");
            }

            var compressed = selected.TakeCapturedCompressedBytes();
            if (compressed is null)
            {
                compressed = GC.AllocateUninitializedArray<byte>(descriptor.CompressedSize);
                _stream.Position = descriptor.DataOffset;
                NoApiMetadataExtractor.ReadExactly(_stream, compressed, cancellationToken);
            }

            using var input = new MemoryStream(compressed, writable: false);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
            var output = GC.AllocateUninitializedArray<byte>(descriptor.UncompressedSize);
            var totalRead = 0;
            while (totalRead < output.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = inflater.Read(output.AsSpan(totalRead));
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead != output.Length || inflater.Read(trailing) != 0)
            {
                throw new InvalidDataException("Metadata stream length does not match its declaration.");
            }

            totalDecompressed += totalRead;
            result.Add(descriptor.Name, output);
        }

        return result;
    }

    private long FindNextMarker(long startPosition, CancellationToken cancellationToken)
    {
        var carry = 0;
        var readPosition = startPosition;
        while (readPosition < _stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream.Position = readPosition;
            var toRead = (int)Math.Min(
                ScanBufferBytes,
                _stream.Length - readPosition);
            var read = _stream.Read(_scanBuffer, carry, toRead);
            if (read == 0)
            {
                return -1;
            }

            var windowLength = carry + read;
            var index = _scanBuffer.AsSpan(0, windowLength).IndexOf(Marker);
            if (index >= 0)
            {
                return readPosition - carry + index;
            }

            carry = Math.Min(Marker.Length - 1, windowLength);
            _scanBuffer.AsSpan(windowLength - carry, carry).CopyTo(_scanBuffer);
            readPosition += read;
        }

        return -1;
    }

    private bool TryReadDescriptor(
        long chunkStart,
        out ModernChunkDescriptor descriptor,
        out bool inline,
        CancellationToken cancellationToken)
    {
        descriptor = null!;
        inline = false;
        if (chunkStart < 0 || chunkStart + ChunkHeaderBytes > _stream.Length)
        {
            return false;
        }

        Span<byte> fields = stackalloc byte[16];
        _stream.Position = chunkStart + 0x0E;
        NoApiMetadataExtractor.ReadExactly(_stream, fields, cancellationToken);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(fields);
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
        var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]);
        var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(fields[12..]);

        if (nameSize == 0 || nameSize > _limits.MaximumStreamNameBytes ||
            compressedSize > _limits.MaximumCompressedStreamBytes ||
            uncompressedSize > _limits.MaximumDecompressedStreamBytes)
        {
            return false;
        }

        var nameOffset = checked(chunkStart + ChunkHeaderBytes);
        var dataOffset = checked(nameOffset + nameSize);
        if (dataOffset > _stream.Length)
        {
            return false;
        }

        var encodedName = GC.AllocateUninitializedArray<byte>((int)nameSize);
        _stream.Position = nameOffset;
        NoApiMetadataExtractor.ReadExactly(_stream, encodedName, cancellationToken);
        for (var index = 0; index < encodedName.Length; index++)
        {
            encodedName[index] = RotateLeft(encodedName[index], _rotateLeftKey);
            if (encodedName[index] is < 0x20 or >= 0x7F)
            {
                return false;
            }
        }

        var name = Encoding.ASCII.GetString(encodedName);
        inline = flags >= InlineThreshold;
        descriptor = new ModernChunkDescriptor(
            name,
            dataOffset,
            checked((int)compressedSize),
            checked((int)uncompressedSize));
        return true;
    }

    private static byte RotateLeft(byte value, int shift)
    {
        if (shift == 0)
        {
            return value;
        }

        return (byte)((value << shift) | (value >> (8 - shift)));
    }

    private static bool TryReadInternalVersion(string name, out int version)
    {
        const string prefix = "_MO_VERSION_";
        version = 0;
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var slash = name.IndexOf('/', prefix.Length);
        return slash > prefix.Length &&
               int.TryParse(
                   name.AsSpan(prefix.Length, slash - prefix.Length),
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out version);
    }

    private static bool IsSelectedMetadataStream(string name)
    {
        if (name is
            "docProps/custom.xml" or
            "docProps/core.xml" or
            "docProps/app.xml" or
            "docProps/ISolidWorksInformation.xml" or
            "Contents/CMgrHdr" or
            "Contents/CMgrHdr2" or
            "swXmlContents/CustomProperties" or
            "swXmlContents/KeyWords" or
            "swXmlContents/MATERIALTREE" or
            "swXmlContents/COMPINSTANCETREE")
        {
            return true;
        }

        return TryReadConfigurationPropertyIndex(name, out _);
    }

    internal static bool TryReadConfigurationPropertyIndex(string name, out int index)
    {
        const string prefix = "docProps/Config-";
        const string suffix = "-Properties.xml";
        index = 0;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return !digits.IsEmpty &&
               int.TryParse(
                   digits,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out index) &&
               index >= 0;
    }
}
