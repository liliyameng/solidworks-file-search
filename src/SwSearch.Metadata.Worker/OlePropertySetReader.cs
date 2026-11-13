using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal static class OlePropertySetReader
{
    private static readonly Guid SummaryInformationFormat =
        new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid UserDefinedPropertiesFormat =
        new("D5CDD505-2E9C-101B-9397-08002B2CF9AE");

    static OlePropertySetReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool LooksLikePropertySet(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 28 &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes) == 0xFFFE &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) is > 0 and <= 16;

    public static IReadOnlyList<DocumentProperty> Read(
        ReadOnlySpan<byte> bytes,
        PropertyScope scope,
        string? configurationName,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        if (!LooksLikePropertySet(bytes))
        {
            return [];
        }

        var sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        if (sectionCount > 16 || 28L + sectionCount * 20L > bytes.Length)
        {
            throw new InvalidDataException("Invalid OLE property-set section table.");
        }

        var result = new List<DocumentProperty>();
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptorOffset = 28 + sectionIndex * 20;
            var format = new Guid(bytes.Slice(descriptorOffset, 16));
            var sectionOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(descriptorOffset + 16)..]);
            if (sectionOffset > int.MaxValue || sectionOffset + 8L > bytes.Length)
            {
                throw new InvalidDataException("OLE property-set section is out of bounds.");
            }

            ReadSection(
                bytes,
                checked((int)sectionOffset),
                format,
                scope,
                configurationName,
                limits,
                result,
                cancellationToken);
        }

        return result;
    }

    private static void ReadSection(
        ReadOnlySpan<byte> stream,
        int sectionOffset,
        Guid format,
        PropertyScope scope,
        string? configurationName,
        NoApiExtractionLimits limits,
        ICollection<DocumentProperty> output,
        CancellationToken cancellationToken)
    {
        var sectionSize = BinaryPrimitives.ReadUInt32LittleEndian(stream[sectionOffset..]);
        var propertyCount = BinaryPrimitives.ReadUInt32LittleEndian(stream[(sectionOffset + 4)..]);
        if (sectionSize < 8 ||
            sectionOffset + sectionSize > stream.Length ||
            propertyCount > limits.MaximumPropertyCount ||
            8L + propertyCount * 8L > sectionSize)
        {
            throw new InvalidDataException("Invalid OLE property-set section.");
        }

        var section = stream.Slice(sectionOffset, checked((int)sectionSize));
        var entries = new List<OlePropertyEntry>(checked((int)propertyCount));
        for (var index = 0; index < propertyCount; index++)
        {
            var tableOffset = 8 + checked((int)index) * 8;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(section[tableOffset..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(section[(tableOffset + 4)..]);
            if (offset >= sectionSize)
            {
                throw new InvalidDataException("OLE property value is out of bounds.");
            }

            entries.Add(new OlePropertyEntry(id, checked((int)offset), 0));
        }

        entries.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        for (var index = 0; index < entries.Count; index++)
        {
            var end = index + 1 < entries.Count ? entries[index + 1].Offset : section.Length;
            if (end < entries[index].Offset)
            {
                throw new InvalidDataException("Overlapping OLE property values.");
            }

            entries[index] = entries[index] with { Length = end - entries[index].Offset };
        }

        var codePage = 1252;
        var codePageEntry = entries.FirstOrDefault(static entry => entry.Id == 1);
        if (codePageEntry is not null &&
            TryReadTypedValue(section.Slice(codePageEntry.Offset, codePageEntry.Length), 1252, limits, out var codePageValue) &&
            int.TryParse(codePageValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCodePage) &&
            parsedCodePage is > 0 and <= ushort.MaxValue)
        {
            codePage = parsedCodePage;
        }

        if (format == UserDefinedPropertiesFormat)
        {
            var dictionaryEntry = entries.FirstOrDefault(static entry => entry.Id == 0);
            if (dictionaryEntry is null)
            {
                return;
            }

            var names = ReadDictionary(
                section.Slice(dictionaryEntry.Offset, dictionaryEntry.Length),
                codePage,
                limits);
            var values = new Dictionary<uint, string>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Id is 0 or 1)
                {
                    continue;
                }

                if (TryReadTypedValue(
                        section.Slice(entry.Offset, entry.Length),
                        codePage,
                        limits,
                        out var value))
                {
                    values.TryAdd(entry.Id, value);
                }
            }

            foreach (var (id, name) in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (id >= 0x01000000 ||
                    string.IsNullOrWhiteSpace(name) ||
                    !values.TryGetValue(id, out var display))
                {
                    continue;
                }

                values.TryGetValue(0x01000000 | id, out var linked);
                output.Add(new DocumentProperty(
                    name,
                    linked ?? display,
                    display,
                    scope,
                    configurationName,
                    MetadataSource.NoApi,
                    DisplayValue: display,
                    CachedValue: display,
                    IsLinked: linked is not null));
            }
        }
        else if (format == SummaryInformationFormat)
        {
            var fixedNames = new Dictionary<uint, string>
            {
                [2] = "Title",
                [3] = "Subject",
                [4] = "Author",
                [5] = "Keywords",
                [6] = "Comments",
                [8] = "LastSavedBy",
                [12] = "CreateDateTime",
                [13] = "LastSaveDateTime"
            };
            foreach (var entry in entries)
            {
                if (fixedNames.TryGetValue(entry.Id, out var name) &&
                    TryReadTypedValue(
                        section.Slice(entry.Offset, entry.Length),
                        codePage,
                        limits,
                        out var value) &&
                    !string.IsNullOrEmpty(value))
                {
                    output.Add(new DocumentProperty(
                        name,
                        value,
                        value,
                        PropertyScope.Document,
                        null,
                        MetadataSource.NoApi,
                        DisplayValue: value,
                        CachedValue: value));
                }
            }
        }
    }

    private static IReadOnlyDictionary<uint, string> ReadDictionary(
        ReadOnlySpan<byte> bytes,
        int codePage,
        NoApiExtractionLimits limits)
    {
        if (bytes.Length < 4)
        {
            throw new InvalidDataException("OLE property dictionary is truncated.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > limits.MaximumPropertyCount)
        {
            throw new InvalidDataException("OLE property dictionary exceeds configured limits.");
        }

        var result = new Dictionary<uint, string>();
        var offset = 4;
        var encoding = ResolveEncoding(codePage);
        for (var index = 0; index < count; index++)
        {
            if (offset > bytes.Length - 8)
            {
                throw new InvalidDataException("OLE property dictionary is truncated.");
            }

            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            var characterCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            offset += 8;
            if (characterCount > limits.MaximumStringCharacters)
            {
                throw new InvalidDataException("OLE property name exceeds configured limits.");
            }

            var byteCount = codePage == 1200
                ? checked((int)characterCount * sizeof(char))
                : checked((int)characterCount);
            if (byteCount < 0 || offset > bytes.Length - byteCount)
            {
                throw new InvalidDataException("OLE property dictionary string is out of bounds.");
            }

            var rawName = bytes.Slice(offset, byteCount);
            var name = encoding.GetString(rawName).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.TryAdd(id, name);
            }

            offset += byteCount;
            offset = (offset + 3) & ~3;
        }

        return result;
    }

    private static bool TryReadTypedValue(
        ReadOnlySpan<byte> bytes,
        int codePage,
        NoApiExtractionLimits limits,
        out string value)
    {
        value = string.Empty;
        if (bytes.Length < 4)
        {
            return false;
        }

        var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var data = bytes[4..];
        try
        {
            switch (type)
            {
                case 2 when data.Length >= 2:
                    value = BinaryPrimitives.ReadUInt16LittleEndian(data).ToString(CultureInfo.InvariantCulture);
                    return true;
                case 3 when data.Length >= 4:
                    value = BinaryPrimitives.ReadInt32LittleEndian(data).ToString(CultureInfo.InvariantCulture);
                    return true;
                case 5 when data.Length >= 8:
                    value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data))
                        .ToString("R", CultureInfo.InvariantCulture);
                    return true;
                case 11 when data.Length >= 2:
                    value = BinaryPrimitives.ReadInt16LittleEndian(data) == 0 ? "False" : "True";
                    return true;
                case 18 when data.Length >= 2:
                    value = BinaryPrimitives.ReadUInt16LittleEndian(data).ToString(CultureInfo.InvariantCulture);
                    return true;
                case 19 when data.Length >= 4:
                    value = BinaryPrimitives.ReadUInt32LittleEndian(data).ToString(CultureInfo.InvariantCulture);
                    return true;
                case 30:
                    return TryReadLengthPrefixedString(data, ResolveEncoding(codePage), limits, false, out value);
                case 31:
                    return TryReadLengthPrefixedString(data, Encoding.Unicode, limits, true, out value);
                case 64 when data.Length >= 8:
                {
                    var fileTime = BinaryPrimitives.ReadInt64LittleEndian(data);
                    if (fileTime < 0)
                    {
                        return false;
                    }

                    value = DateTime.FromFileTimeUtc(fileTime).ToString("O", CultureInfo.InvariantCulture);
                    return true;
                }
                default:
                    return false;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryReadLengthPrefixedString(
        ReadOnlySpan<byte> data,
        Encoding encoding,
        NoApiExtractionLimits limits,
        bool lengthIsCharacters,
        out string value)
    {
        value = string.Empty;
        if (data.Length < 4)
        {
            return false;
        }

        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (declaredLength > limits.MaximumStringCharacters)
        {
            throw new InvalidDataException("OLE property value exceeds configured limits.");
        }

        var byteCount = lengthIsCharacters
            ? checked((int)declaredLength * sizeof(char))
            : checked((int)declaredLength);
        if (byteCount < 0 || byteCount > data.Length - 4)
        {
            return false;
        }

        value = encoding.GetString(data.Slice(4, byteCount)).TrimEnd('\0');
        return true;
    }

    private static Encoding ResolveEncoding(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }

    private sealed record OlePropertyEntry(uint Id, int Offset, int Length);
}
