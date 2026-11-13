// MFC field ordering and version gates were adapted from openswx
// (MIT, Copyright (c) 2026 openswx contributors). See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.Text;

namespace SwSearch.Metadata.Worker;

internal static class MfcConfigurationNameReader
{
    private const int VersionUnknown1 = 1_341;
    private const int VersionParent = 2_037;
    private const int VersionUnknown2 = 2_136;
    private const int VersionAlternateName = 2_610;
    private const int VersionDword10 = 3_928;
    private const int VersionDword11 = 11_142;
    private const int VersionDword12 = 12_155;
    private const int VersionDword13 = 12_216;

    public static IReadOnlyDictionary<int, string> Read(
        ReadOnlySpan<byte> bytes,
        int version,
        NoApiExtractionLimits limits)
    {
        var reader = new BoundedBinaryReader(bytes, limits.MaximumStringCharacters);
        var classes = new Dictionary<int, string>();
        if (!TryReadClassTag(ref reader, classes, out var outerClass) ||
            string.IsNullOrEmpty(outerClass))
        {
            return new Dictionary<int, string>();
        }

        if (!reader.TryReadUInt16(out var count16))
        {
            return new Dictionary<int, string>();
        }

        uint count = count16;
        if (count16 == ushort.MaxValue && !reader.TryReadUInt32(out count))
        {
            return new Dictionary<int, string>();
        }

        if (count == 0 || count > limits.MaximumConfigurationCount)
        {
            return new Dictionary<int, string>();
        }

        var result = new Dictionary<int, string>();
        for (var index = 0u; index < count; index++)
        {
            if (!TryReadClassTag(ref reader, classes, out _) ||
                !reader.TrySkip(sizeof(uint)) ||
                !reader.TryReadMfcString(out var name) ||
                !reader.TryReadUInt32(out var configId) ||
                configId > int.MaxValue)
            {
                return result;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                result.TryAdd((int)configId, name);
            }

            if (version >= VersionUnknown1 && !reader.TrySkip(sizeof(uint)))
            {
                return result;
            }

            if (version >= VersionParent &&
                (!reader.TryReadMfcString(out _) || !reader.TrySkip(sizeof(uint))))
            {
                return result;
            }

            if (version >= VersionUnknown2 && !reader.TrySkip(sizeof(uint)))
            {
                return result;
            }

            if (version >= VersionAlternateName &&
                (!reader.TryReadMfcString(out _) || !reader.TryReadMfcString(out _)))
            {
                return result;
            }

            if (version >= VersionDword10 && !reader.TrySkip(sizeof(uint)))
            {
                return result;
            }

            if (version >= VersionDword11 && !reader.TrySkip(sizeof(uint)))
            {
                return result;
            }

            if (version >= VersionDword12 && !reader.TrySkip(sizeof(uint)))
            {
                return result;
            }

            if (version >= VersionDword13 && !reader.TrySkip(sizeof(uint)))
            {
                return result;
            }
        }

        return result;
    }

    private static bool TryReadClassTag(
        ref BoundedBinaryReader reader,
        IDictionary<int, string> classes,
        out string className)
    {
        className = string.Empty;
        if (!reader.TryReadUInt16(out var tag))
        {
            return false;
        }

        if (tag == 0)
        {
            return true;
        }

        if (tag <= 0x7FFF)
        {
            className = "<object-reference>";
            return true;
        }

        if (tag == ushort.MaxValue)
        {
            if (!reader.TryReadUInt16(out _) ||
                !reader.TryReadUInt16(out var nameLength) ||
                !reader.TryReadAscii(nameLength, out className))
            {
                return false;
            }

            classes.TryAdd(classes.Count + 1, className);
            return true;
        }

        var classIndex = tag & 0x7FFF;
        className = classes.TryGetValue(classIndex, out var knownName)
            ? knownName
            : "<class-reference>";
        return true;
    }

    private ref struct BoundedBinaryReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly int _maximumStringCharacters;
        private int _position;

        public BoundedBinaryReader(ReadOnlySpan<byte> bytes, int maximumStringCharacters)
        {
            _bytes = bytes;
            _maximumStringCharacters = maximumStringCharacters;
        }

        public bool TryReadUInt16(out ushort value)
        {
            value = 0;
            if (_position > _bytes.Length - sizeof(ushort))
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes[_position..]);
            _position += sizeof(ushort);
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            value = 0;
            if (_position > _bytes.Length - sizeof(uint))
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[_position..]);
            _position += sizeof(uint);
            return true;
        }

        public bool TrySkip(int count)
        {
            if (count < 0 || _position > _bytes.Length - count)
            {
                return false;
            }

            _position += count;
            return true;
        }

        public bool TryReadAscii(int characterCount, out string value)
        {
            value = string.Empty;
            if (characterCount < 0 ||
                characterCount > _maximumStringCharacters ||
                _position > _bytes.Length - characterCount)
            {
                return false;
            }

            value = Encoding.ASCII.GetString(_bytes.Slice(_position, characterCount));
            _position += characterCount;
            return true;
        }

        public bool TryReadMfcString(out string value)
        {
            value = string.Empty;
            var start = _position;
            if (!TryReadByte(out var first))
            {
                return false;
            }

            if (first == 0)
            {
                return true;
            }

            if (first != byte.MaxValue)
            {
                if (TryReadAscii(first, out value))
                {
                    return true;
                }

                _position = start;
                return false;
            }

            if (!TryReadByte(out var second))
            {
                _position = start;
                return false;
            }

            if (second == 0xFE)
            {
                if (!TryReadByte(out var lengthMarker))
                {
                    _position = start;
                    return false;
                }

                uint length = lengthMarker;
                if (lengthMarker == byte.MaxValue)
                {
                    if (!TryReadByte(out var extendedMarker))
                    {
                        _position = start;
                        return false;
                    }

                    if (extendedMarker == 0xFE)
                    {
                        if (!TryReadUInt16(out var length16))
                        {
                            _position = start;
                            return false;
                        }

                        length = length16;
                    }
                    else if (extendedMarker == byte.MaxValue)
                    {
                        if (!TryReadUInt32(out length))
                        {
                            _position = start;
                            return false;
                        }
                    }
                    else
                    {
                        length = extendedMarker;
                    }
                }

                if (length > _maximumStringCharacters ||
                    length > int.MaxValue / sizeof(char) ||
                    !TryReadUtf16((int)length, out value))
                {
                    _position = start;
                    return false;
                }

                return true;
            }

            if (second == byte.MaxValue)
            {
                if (!TryReadByte(out var length))
                {
                    _position = start;
                    return false;
                }

                if (length == 0)
                {
                    return true;
                }

                if (TryReadUtf16(length, out value))
                {
                    return true;
                }

                _position = start;
                return false;
            }

            if (TryReadAscii(second, out value))
            {
                return true;
            }

            _position = start;
            return false;
        }

        private bool TryReadByte(out byte value)
        {
            value = 0;
            if (_position >= _bytes.Length)
            {
                return false;
            }

            value = _bytes[_position++];
            return true;
        }

        private bool TryReadUtf16(int characterCount, out string value)
        {
            value = string.Empty;
            var byteCount = checked(characterCount * sizeof(char));
            if (characterCount > _maximumStringCharacters ||
                _position > _bytes.Length - byteCount)
            {
                return false;
            }

            value = Encoding.Unicode.GetString(_bytes.Slice(_position, byteCount));
            _position += byteCount;
            return true;
        }
    }
}
