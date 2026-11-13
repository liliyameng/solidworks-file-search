// CFB traversal follows the public MS-CFB structure and was cross-checked with
// openswx (MIT, Copyright (c) 2026 openswx contributors). See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal sealed class CompoundFileMetadataReader
{
    private readonly NoApiExtractionLimits _limits;

    public CompoundFileMetadataReader(NoApiExtractionLimits limits)
    {
        _limits = limits;
    }

    public NoApiExtraction Extract(
        FileStream stream,
        ValidatedFile file,
        CancellationToken cancellationToken)
    {
        var compoundFile = new BoundedCompoundFile(stream, _limits, cancellationToken);
        var selectedStreams = compoundFile.ReadMetadataStreams(cancellationToken);
        var properties = new List<DocumentProperty>();
        var configurationNames = new SortedDictionary<int, string>();

        foreach (var (path, bytes) in selectedStreams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContainsUnresolvedConfigurationIndex(path))
            {
                continue;
            }
            var scope = TryReadConfiguration(path, out var configurationIndex, out var configurationName)
                ? PropertyScope.Configuration
                : PropertyScope.Document;
            if (scope == PropertyScope.Configuration)
            {
                configurationNames.TryAdd(configurationIndex, configurationName);
            }

            if (LooksLikeXml(bytes))
            {
                properties.AddRange(SolidWorksXmlMetadataParser.ParsePropertySectionXml(
                    bytes,
                    scope,
                    scope == PropertyScope.Configuration ? configurationName : null,
                    _limits,
                    cancellationToken));
                continue;
            }

            if (OlePropertySetReader.LooksLikePropertySet(bytes))
            {
                properties.AddRange(OlePropertySetReader.Read(
                    bytes,
                    scope,
                    scope == PropertyScope.Configuration ? configurationName : null,
                    _limits,
                    cancellationToken));
            }
        }

        if (properties.Count > _limits.MaximumPropertyCount)
        {
            throw new InvalidDataException("Property count exceeds configured limits.");
        }

        return new NoApiExtraction(
            Deduplicate(properties),
            Array.Empty<DocumentReference>(),
            configurationNames.Values.ToArray(),
            InternalVersion: null,
            NoApiContainerKind.CompoundFile);
    }

    private static bool LooksLikeXml(ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        while (index < bytes.Length && (bytes[index] == 0 || char.IsWhiteSpace((char)bytes[index])))
        {
            index++;
        }

        return index < bytes.Length && bytes[index] == (byte)'<';
    }

    private static bool TryReadConfiguration(
        string path,
        out int index,
        out string configurationName)
    {
        index = 0;
        configurationName = string.Empty;
        var segments = path.Split('/');
        for (var segmentIndex = 0; segmentIndex + 1 < segments.Length; segmentIndex++)
        {
            if (!segments[segmentIndex].Equals("Configurations", StringComparison.OrdinalIgnoreCase) &&
                !segments[segmentIndex].Equals("Configuration", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            configurationName = segments[segmentIndex + 1];
            index = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(configurationName));
            return !string.IsNullOrWhiteSpace(configurationName);
        }

        return false;
    }

    private static bool ContainsUnresolvedConfigurationIndex(string path)
    {
        var marker = path.IndexOf("Config-", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        var start = marker + "Config-".Length;
        return start < path.Length && char.IsDigit(path[start]);
    }

    private static IReadOnlyList<DocumentProperty> Deduplicate(IEnumerable<DocumentProperty> source)
    {
        var result = new List<DocumentProperty>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source)
        {
            var key = $"{(int)property.Scope}\0{property.ConfigurationName}\0{property.Name}";
            if (seen.Add(key))
            {
                result.Add(property);
            }
        }

        return result;
    }
}

internal sealed class BoundedCompoundFile
{
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FatSector = 0xFFFFFFFD;
    private const uint DifatSector = 0xFFFFFFFC;
    private const uint NoStream = 0xFFFFFFFF;
    private const int DirectoryEntryBytes = 128;

    private readonly FileStream _stream;
    private readonly NoApiExtractionLimits _limits;
    private readonly int _majorVersion;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniStreamCutoff;
    private readonly uint _firstDirectorySector;
    private readonly uint _firstMiniFatSector;
    private readonly uint _miniFatSectorCount;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly IReadOnlyList<CompoundDirectoryEntry> _directory;
    private readonly byte[] _miniStream;
    private readonly long _sectorCount;

    public BoundedCompoundFile(
        FileStream stream,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        _stream = stream;
        _limits = limits;
        Span<byte> header = stackalloc byte[512];
        _stream.Position = 0;
        NoApiMetadataExtractor.ReadExactly(_stream, header, cancellationToken);

        _majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
        var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
        if ((_majorVersion != 3 && _majorVersion != 4) ||
            byteOrder != 0xFFFE ||
            (_majorVersion == 3 && sectorShift != 9) ||
            (_majorVersion == 4 && sectorShift != 12) ||
            miniSectorShift != 6)
        {
            throw new InvalidDataException("Unsupported compound-file header.");
        }

        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniSectorShift;
        if (_stream.Length < _sectorSize || _stream.Length % _sectorSize != 0)
        {
            throw new InvalidDataException("Compound-file length is not sector aligned.");
        }

        _sectorCount = _stream.Length / _sectorSize - 1;
        _firstDirectorySector = BinaryPrimitives.ReadUInt32LittleEndian(header[48..]);
        _miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header[56..]);
        _firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[60..]);
        _miniFatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[64..]);

        var fatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[44..]);
        var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[68..]);
        var difatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[72..]);
        _fat = ReadFat(
            header,
            fatSectorCount,
            firstDifatSector,
            difatSectorCount,
            cancellationToken);
        _miniFat = ReadMiniFat(cancellationToken);
        _directory = ReadDirectory(cancellationToken);
        _miniStream = ReadRootMiniStream(cancellationToken);
    }

    public IReadOnlyDictionary<string, byte[]> ReadMetadataStreams(
        CancellationToken cancellationToken)
    {
        if (_directory.Count == 0 || _directory[0].Type != 5)
        {
            throw new InvalidDataException("Compound-file root entry is missing.");
        }

        var selected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<uint>();
        Traverse(
            _directory[0].Child,
            parentPath: string.Empty,
            depth: 0,
            visited,
            selected,
            cancellationToken);
        return selected;
    }

    private uint[] ReadFat(
        ReadOnlySpan<byte> header,
        uint declaredFatSectorCount,
        uint firstDifatSector,
        uint declaredDifatSectorCount,
        CancellationToken cancellationToken)
    {
        if (declaredFatSectorCount == 0 ||
            declaredFatSectorCount > _limits.MaximumCompoundFatEntries / (_sectorSize / sizeof(uint)))
        {
            throw new InvalidDataException("Compound-file FAT exceeds configured limits.");
        }

        var fatSectors = new List<uint>(checked((int)declaredFatSectorCount));
        for (var index = 0; index < 109 && fatSectors.Count < declaredFatSectorCount; index++)
        {
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(header[(76 + index * 4)..]);
            if (IsRegularSector(sector))
            {
                fatSectors.Add(sector);
            }
        }

        var difatVisited = new HashSet<uint>();
        var difatSector = firstDifatSector;
        var entriesPerDifatSector = _sectorSize / sizeof(uint) - 1;
        for (var chainIndex = 0u;
             chainIndex < declaredDifatSectorCount && IsRegularSector(difatSector);
             chainIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSector(difatSector);
            if (!difatVisited.Add(difatSector))
            {
                throw new InvalidDataException("Cycle in compound-file DIFAT.");
            }

            var bytes = ReadSector(difatSector, cancellationToken);
            for (var index = 0; index < entriesPerDifatSector && fatSectors.Count < declaredFatSectorCount; index++)
            {
                var sector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)));
                if (IsRegularSector(sector))
                {
                    fatSectors.Add(sector);
                }
            }

            difatSector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entriesPerDifatSector * sizeof(uint)));
        }

        if (fatSectors.Count != declaredFatSectorCount)
        {
            throw new InvalidDataException("Compound-file FAT declaration is inconsistent.");
        }

        var entriesPerSector = _sectorSize / sizeof(uint);
        var entryCount = checked(fatSectors.Count * entriesPerSector);
        if (entryCount > _limits.MaximumCompoundFatEntries)
        {
            throw new InvalidDataException("Compound-file FAT exceeds configured limits.");
        }

        var result = new uint[entryCount];
        for (var fatIndex = 0; fatIndex < fatSectors.Count; fatIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSector(fatSectors[fatIndex]);
            var bytes = ReadSector(fatSectors[fatIndex], cancellationToken);
            for (var entryIndex = 0; entryIndex < entriesPerSector; entryIndex++)
            {
                result[fatIndex * entriesPerSector + entryIndex] =
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryIndex * sizeof(uint)));
            }
        }

        return result;
    }

    private uint[] ReadMiniFat(CancellationToken cancellationToken)
    {
        if (_miniFatSectorCount == 0 || !IsRegularSector(_firstMiniFatSector))
        {
            return [];
        }

        var maximumBytes = checked((long)_miniFatSectorCount * _sectorSize);
        if (maximumBytes / sizeof(uint) > _limits.MaximumCompoundFatEntries)
        {
            throw new InvalidDataException("Compound-file mini FAT exceeds configured limits.");
        }

        var bytes = ReadRegularChain(
            _firstMiniFatSector,
            maximumBytes,
            exactSize: true,
            cancellationToken);
        var result = new uint[bytes.Length / sizeof(uint)];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)));
        }

        return result;
    }

    private IReadOnlyList<CompoundDirectoryEntry> ReadDirectory(
        CancellationToken cancellationToken)
    {
        var bytes = ReadRegularChain(
            _firstDirectorySector,
            _limits.MaximumCompoundDirectoryBytes,
            exactSize: false,
            cancellationToken);
        if (bytes.Length == 0 || bytes.Length % DirectoryEntryBytes != 0)
        {
            throw new InvalidDataException("Invalid compound-file directory.");
        }

        var result = new List<CompoundDirectoryEntry>(bytes.Length / DirectoryEntryBytes);
        for (var offset = 0; offset < bytes.Length; offset += DirectoryEntryBytes)
        {
            var entry = bytes.AsSpan(offset, DirectoryEntryBytes);
            var nameByteLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[64..]);
            if (nameByteLength > 64 || (nameByteLength & 1) != 0)
            {
                throw new InvalidDataException("Invalid compound-file directory name.");
            }

            var name = nameByteLength >= 2
                ? Encoding.Unicode.GetString(entry[..(nameByteLength - 2)])
                : string.Empty;
            var type = entry[66];
            if (type is not (0 or 1 or 2 or 5))
            {
                throw new InvalidDataException("Invalid compound-file directory type.");
            }

            var size = _majorVersion == 3
                ? BinaryPrimitives.ReadUInt32LittleEndian(entry[120..])
                : BinaryPrimitives.ReadUInt64LittleEndian(entry[120..]);
            result.Add(new CompoundDirectoryEntry(
                name,
                type,
                BinaryPrimitives.ReadUInt32LittleEndian(entry[68..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[72..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[76..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[116..]),
                size));
        }

        return result;
    }

    private byte[] ReadRootMiniStream(CancellationToken cancellationToken)
    {
        if (_directory.Count == 0)
        {
            return [];
        }

        var root = _directory[0];
        if (root.Size == 0 || !IsRegularSector(root.StartSector))
        {
            return [];
        }

        if (root.Size > (ulong)_limits.MaximumCompoundMiniStreamBytes)
        {
            throw new InvalidDataException("Compound-file mini stream exceeds configured limits.");
        }

        return ReadRegularChain(
            root.StartSector,
            checked((long)root.Size),
            exactSize: true,
            cancellationToken);
    }

    private void Traverse(
        uint nodeIndex,
        string parentPath,
        int depth,
        ISet<uint> visited,
        IDictionary<string, byte[]> selected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (nodeIndex == NoStream)
        {
            return;
        }

        if (depth > _limits.MaximumTreeDepth || nodeIndex >= _directory.Count || !visited.Add(nodeIndex))
        {
            throw new InvalidDataException("Invalid compound-file directory tree.");
        }

        var node = _directory[(int)nodeIndex];
        Traverse(node.Left, parentPath, depth + 1, visited, selected, cancellationToken);

        var path = string.IsNullOrEmpty(parentPath) ? node.Name : parentPath + "/" + node.Name;
        if (path.Length > _limits.MaximumStringCharacters)
        {
            throw new InvalidDataException("Compound-file path exceeds configured limits.");
        }

        if (node.Type == 1 && node.Child != NoStream)
        {
            Traverse(node.Child, path, depth + 1, visited, selected, cancellationToken);
        }
        else if (node.Type == 2 && IsMetadataCandidate(path) && selected.Count < _limits.MaximumStreamCount)
        {
            var bytes = ReadEntry(node, cancellationToken);
            if (path.EndsWith("__Zip", StringComparison.Ordinal))
            {
                bytes = InflateBounded(bytes, cancellationToken);
                path = path[..^"__Zip".Length];
            }

            selected.TryAdd(path, bytes);
        }

        Traverse(node.Right, parentPath, depth + 1, visited, selected, cancellationToken);
    }

    private byte[] ReadEntry(CompoundDirectoryEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Size > (ulong)_limits.MaximumCompressedStreamBytes)
        {
            throw new InvalidDataException("Compound-file metadata stream exceeds configured limits.");
        }

        if (entry.Size == 0)
        {
            return [];
        }

        return entry.Size < _miniStreamCutoff
            ? ReadMiniChain(entry.StartSector, checked((int)entry.Size), cancellationToken)
            : ReadRegularChain(
                entry.StartSector,
                checked((long)entry.Size),
                exactSize: true,
                cancellationToken);
    }

    private byte[] ReadRegularChain(
        uint startSector,
        long maximumBytes,
        bool exactSize,
        CancellationToken cancellationToken)
    {
        if (maximumBytes < 0 || maximumBytes > _limits.MaximumCompoundDirectoryBytes && !exactSize)
        {
            throw new InvalidDataException("Invalid compound-file chain length.");
        }

        using var output = new MemoryStream((int)Math.Min(maximumBytes, int.MaxValue));
        var visited = new HashSet<uint>();
        var sector = startSector;
        while (sector != EndOfChain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSector(sector);
            if (!visited.Add(sector) || sector >= _fat.Length)
            {
                throw new InvalidDataException("Invalid compound-file sector chain.");
            }

            var bytes = ReadSector(sector, cancellationToken);
            var remaining = maximumBytes - output.Length;
            if (remaining <= 0)
            {
                if (exactSize)
                {
                    break;
                }

                throw new InvalidDataException("Compound-file chain exceeds configured limits.");
            }

            var count = (int)Math.Min(bytes.Length, remaining);
            output.Write(bytes, 0, count);
            sector = _fat[sector];
            if (exactSize && output.Length == maximumBytes)
            {
                break;
            }
        }

        if (exactSize && output.Length != maximumBytes)
        {
            throw new InvalidDataException("Compound-file chain is truncated.");
        }

        return output.ToArray();
    }

    private byte[] ReadMiniChain(
        uint startMiniSector,
        int size,
        CancellationToken cancellationToken)
    {
        var output = new byte[size];
        var written = 0;
        var miniSector = startMiniSector;
        var visited = new HashSet<uint>();
        while (written < size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (miniSector == EndOfChain ||
                miniSector >= _miniFat.Length ||
                !visited.Add(miniSector))
            {
                throw new InvalidDataException("Invalid compound-file mini-sector chain.");
            }

            var offset = checked((long)miniSector * _miniSectorSize);
            if (offset < 0 || offset >= _miniStream.Length)
            {
                throw new InvalidDataException("Compound-file mini-sector is out of bounds.");
            }

            var count = Math.Min(_miniSectorSize, size - written);
            if (offset + count > _miniStream.Length)
            {
                throw new InvalidDataException("Compound-file mini-sector is truncated.");
            }

            Buffer.BlockCopy(_miniStream, checked((int)offset), output, written, count);
            written += count;
            miniSector = _miniFat[miniSector];
        }

        return output;
    }

    private byte[] InflateBounded(byte[] compressed, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inflater.Read(buffer);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > _limits.MaximumDecompressedStreamBytes)
            {
                throw new InvalidDataException("Inflated compound-file stream exceeds configured limits.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private byte[] ReadSector(uint sector, CancellationToken cancellationToken)
    {
        ValidateSector(sector);
        var bytes = new byte[_sectorSize];
        _stream.Position = checked(((long)sector + 1) * _sectorSize);
        NoApiMetadataExtractor.ReadExactly(_stream, bytes, cancellationToken);
        return bytes;
    }

    private void ValidateSector(uint sector)
    {
        if (!IsRegularSector(sector) || sector >= _sectorCount)
        {
            throw new InvalidDataException("Compound-file sector is out of bounds.");
        }
    }

    private static bool IsRegularSector(uint sector) =>
        sector is not (FreeSector or EndOfChain or FatSector or DifatSector);

    private static bool IsMetadataCandidate(string path)
    {
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        if (leaf is "\u0005DocumentSummaryInformation" or "\u0005SummaryInformation")
        {
            return true;
        }

        return path.Contains("CustomPropert", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("CfgPropert", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Config-", StringComparison.OrdinalIgnoreCase) &&
               path.Contains("Propert", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("docProps/custom.xml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("Properties.xml", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record CompoundDirectoryEntry(
    string Name,
    byte Type,
    uint Left,
    uint Right,
    uint Child,
    uint StartSector,
    ulong Size);
