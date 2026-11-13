using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class NoApiMetadataExtractorTests
{
    [Fact]
    public void ModernContainerReadsLinkedCachedConfigurationAndMaterialProperties()
    {
        using var fixture = new TemporarySolidWorksFile("modern.SLDPRT");
        var configXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ConfigProperties xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
              <propertySection name="UserDefinedProperties">
                <property name="二级分类" pid="5" IsEquation="False"><vt:lpstr>0404等径接头</vt:lpstr><FPVals><vt:lpstr>&lt;$0$0404等径接头$&gt;</vt:lpstr></FPVals></property>
                <property name="" pid="16777221" IsEquation="False"><vt:lpstr>"SW-二级分类@零件1"</vt:lpstr></property>
                <property name="物料编码" pid="6" IsEquation="False"><vt:lpstr></vt:lpstr></property>
              </propertySection>
            </ConfigProperties>
            """;
        var globalXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Properties xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
              <propertySection name="UserDefinedProperties">
                <property name="项目" pid="2"><vt:lpstr>自包含解析</vt:lpstr></property>
              </propertySection>
            </Properties>
            """;
        var informationXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Properties xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
              <propertySection name="Internal"><property name="SW-Configuration Name" pid="2"><vt:lpstr>默认</vt:lpstr></property></propertySection>
            </Properties>
            """;
        var materialXml = """
            <?xml version="1.0"?>
            <materials><classification name="TC-不锈钢"><material name="拓创材料库|304" matid="759"><physicalproperties><DENS displayname="质量密度" value="8000.000000"/></physicalproperties></material></classification><configurationslist><configuration name="默认" material="拓创材料库|304|759"/></configurationslist></materials>
            """;

        fixture.Write(SyntheticModernContainer.Build(
            17_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/custom.xml"] = Encoding.UTF8.GetBytes(globalXml),
                ["docProps/Config-0-Properties.xml"] = Encoding.UTF8.GetBytes(configXml),
                ["docProps/ISolidWorksInformation.xml"] = Encoding.UTF8.GetBytes(informationXml),
                ["swXmlContents/MATERIALTREE"] = Encoding.UTF8.GetBytes(materialXml)
            }));

        var result = new NoApiMetadataExtractor().Extract(fixture.ValidatedFile, CancellationToken.None);

        Assert.Equal(NoApiContainerKind.ModernChunk, result.ContainerKind);
        Assert.Equal(17_000, result.InternalVersion);
        Assert.Equal(["默认"], result.ConfigurationNames);
        var category = Assert.Single(result.Properties, property => property.Name == "二级分类");
        Assert.Equal("默认", category.ConfigurationName);
        Assert.Equal("\"SW-二级分类@零件1\"", category.RawValue);
        Assert.Equal("0404等径接头", category.DisplayValue);
        Assert.Equal("0404等径接头", category.CachedValue);
        Assert.Equal("0404等径接头", category.ResolvedValue);
        Assert.True(category.IsLinked);
        Assert.Contains(result.Properties, property => property.Name == "项目" && property.Scope == PropertyScope.Document);
        Assert.Contains(result.Properties, property => property.Name == "材料分类" && property.ResolvedValue == "TC-不锈钢");
        Assert.Contains(result.Properties, property => property.Name == "质量密度" && property.ResolvedValue == "8000.000000");
    }

    [Fact]
    public void ModernContainerReadsSolidWorks2026ConfigurationPropertyValue()
    {
        using var fixture = new TemporarySolidWorksFile("modern-2026.SLDPRT");
        var configXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ConfigProperties xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
              <propertySection name="UserDefinedProperties">
                <property name="物料编码" pid="5" IsEquation="False"><vt:lpstr>0404-139</vt:lpstr></property>
                <property name="代号" pid="6" IsEquation="False"><vt:lpstr>0404-139</vt:lpstr></property>
              </propertySection>
            </ConfigProperties>
            """;
        var informationXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Properties xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
              <propertySection name="Internal"><property name="SW-Configuration Name" pid="2"><vt:lpstr>HC276</vt:lpstr></property></propertySection>
            </Properties>
            """;

        fixture.Write(SyntheticModernContainer.Build(
            19_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/Config-0-Properties.xml"] = Encoding.UTF8.GetBytes(configXml),
                ["docProps/ISolidWorksInformation.xml"] = Encoding.UTF8.GetBytes(informationXml)
            }));

        var result = new NoApiMetadataExtractor().Extract(
            fixture.ValidatedFile,
            CancellationToken.None);

        Assert.Equal(NoApiContainerKind.ModernChunk, result.ContainerKind);
        Assert.Equal(19_000, result.InternalVersion);
        Assert.Equal(["HC276"], result.ConfigurationNames);
        foreach (var name in new[] { "物料编码", "代号" })
        {
            var property = Assert.Single(result.Properties, item => item.Name == name);
            Assert.Equal(PropertyScope.Configuration, property.Scope);
            Assert.Equal("HC276", property.ConfigurationName);
            Assert.Equal("0404-139", property.ResolvedValue);
        }
    }

    [Fact]
    public void ModernContainerRejectsUnverifiedVersionWithoutParsingProperties()
    {
        using var fixture = new TemporarySolidWorksFile("future.SLDPRT");
        fixture.Write(SyntheticModernContainer.Build(
            20_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/custom.xml"] = Encoding.UTF8.GetBytes("<Properties />")
            }));

        var exception = Assert.Throws<MetadataWorkerException>(() =>
            new NoApiMetadataExtractor().Extract(fixture.ValidatedFile, CancellationToken.None));

        Assert.Equal(MetadataErrorCodes.NoApiVersionUnsupported, exception.Code);
    }

    [Fact]
    public void ModernContainerRejectsTruncatedDeflatePayload()
    {
        using var fixture = new TemporarySolidWorksFile("truncated.SLDPRT");
        var bytes = SyntheticModernContainer.Build(
            17_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/custom.xml"] = Encoding.UTF8.GetBytes("<Properties />")
            });
        fixture.Write(bytes[..^1]);

        var exception = Assert.Throws<MetadataWorkerException>(() =>
            new NoApiMetadataExtractor().Extract(fixture.ValidatedFile, CancellationToken.None));

        Assert.Equal(MetadataErrorCodes.FileCorrupt, exception.Code);
    }

    [Fact]
    public void ModernContainerFindsMarkerSplitAcrossScanBuffers()
    {
        using var fixture = new TemporarySolidWorksFile("boundary.SLDPRT");
        var xml = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0"?>
            <Properties xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
              <propertySection name="UserDefinedProperties">
                <property name="Boundary" pid="2"><vt:lpstr>Found</vt:lpstr></property>
              </propertySection>
            </Properties>
            """);
        fixture.Write(SyntheticModernContainer.BuildWithSplitMarker(
            17_000,
            "docProps/custom.xml",
            xml));

        var result = new NoApiMetadataExtractor().Extract(
            fixture.ValidatedFile,
            CancellationToken.None);

        Assert.Contains(
            result.Properties,
            property => property.Name == "Boundary" &&
                        property.ResolvedValue == "Found");
    }

    [Fact]
    public void ExtractReusesOneOpenedHandleForProbeAndModernParsing()
    {
        using var fixture = new TemporarySolidWorksFile("single-handle.SLDPRT");
        fixture.Write(SyntheticModernContainer.Build(
            17_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/custom.xml"] = Encoding.UTF8.GetBytes(
                    "<Properties><propertySection name=\"UserDefinedProperties\"><property name=\"PartNo\"><value>123</value></property></propertySection></Properties>")
            }));

        var openCount = 0;
        var extractor = new NoApiMetadataExtractor(
            NoApiExtractionLimits.Default,
            path =>
            {
                openCount++;
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.None);
            });

        var result = extractor.Extract(fixture.ValidatedFile, CancellationToken.None);

        Assert.Equal(1, openCount);
        Assert.Equal(NoApiContainerKind.ModernChunk, result.ContainerKind);
        Assert.Contains(result.Properties, property => property.Name == "PartNo");
    }

    [Fact]
    public void CapturedModernPayloadMatchesDescriptorFallback()
    {
        using var fixture = new TemporarySolidWorksFile("equivalence.SLDPRT");
        fixture.Write(SyntheticModernContainer.Build(
            17_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/custom.xml"] = Encoding.UTF8.GetBytes("<Properties />"),
                ["swXmlContents/KeyWords"] = Encoding.UTF8.GetBytes("<Keywords />")
            }));

        using var stream = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var container = new ModernSolidWorksContainer(
            stream,
            NoApiExtractionLimits.Default);
        var inventory = container.Inventory(CancellationToken.None);
        Assert.All(
            inventory.SelectedStreams.Values,
            selected => Assert.NotNull(selected.CapturedCompressedBytes));

        var captured = container.ReadSelectedStreams(inventory, CancellationToken.None);
        var fallbackInventory = inventory with
        {
            SelectedStreams = inventory.SelectedStreams.ToDictionary(
                static pair => pair.Key,
                static pair => new ModernSelectedChunk(pair.Value.Descriptor, null),
                StringComparer.Ordinal)
        };
        var fallback = container.ReadSelectedStreams(
            fallbackInventory,
            CancellationToken.None);

        Assert.Equal(captured.Keys.Order(), fallback.Keys.Order());
        foreach (var (name, bytes) in captured)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(fallback[name]));
        }
    }

    [Fact]
    public void MaximumLegalModernPayloadSetDecompressesIntoFinalBuffers()
    {
        using var fixture = new TemporarySolidWorksFile("maximum-stream.SLDPRT");
        var custom = new byte[NoApiExtractionLimits.Default.MaximumDecompressedStreamBytes];
        var core = new byte[NoApiExtractionLimits.Default.MaximumDecompressedStreamBytes];
        fixture.Write(SyntheticModernContainer.Build(
            17_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/custom.xml"] = custom,
                ["docProps/core.xml"] = core
            }));

        using var stream = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var container = new ModernSolidWorksContainer(
            stream,
            NoApiExtractionLimits.Default);
        var inventory = container.Inventory(CancellationToken.None);
        var extracted = container.ReadSelectedStreams(
            inventory,
            CancellationToken.None);

        Assert.Equal(2, extracted.Count);
        Assert.Equal(
            NoApiExtractionLimits.Default.MaximumTotalDecompressedBytes,
            extracted.Values.Sum(static bytes => bytes.LongLength));
        var actual = extracted["docProps/core.xml"];
        Assert.Equal(core.Length, actual.Length);
        Assert.All(
            new[] { actual[0], actual[actual.Length / 2], actual[^1] },
            value => Assert.Equal(0, value));
    }

    [Fact]
    public void CaptureBudgetOverflowUsesBoundedDescriptorFallback()
    {
        using var fixture = new TemporarySolidWorksFile("capture-fallback.SLDPRT");
        var custom = new byte[9 * 1024 * 1024];
        var core = new byte[9 * 1024 * 1024];
        new Random(55).NextBytes(custom);
        new Random(56).NextBytes(core);
        fixture.Write(SyntheticModernContainer.Build(
            17_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/custom.xml"] = custom,
                ["docProps/core.xml"] = core
            }));

        using var stream = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var container = new ModernSolidWorksContainer(
            stream,
            NoApiExtractionLimits.Default);
        var inventory = container.Inventory(CancellationToken.None);
        var capturedBytes = inventory.SelectedStreams.Values.Sum(
            static selected => selected.CapturedCompressedBytes?.LongLength ?? 0);

        Assert.InRange(
            capturedBytes,
            1,
            ModernSolidWorksContainer.MaximumCapturedCompressedBytes);
        Assert.Contains(
            inventory.SelectedStreams.Values,
            static selected => selected.CapturedCompressedBytes is null);

        var extracted = container.ReadSelectedStreams(
            inventory,
            CancellationToken.None);
        Assert.True(custom.AsSpan().SequenceEqual(extracted["docProps/custom.xml"]));
        Assert.True(core.AsSpan().SequenceEqual(extracted["docProps/core.xml"]));
        Assert.All(
            inventory.SelectedStreams.Values,
            static selected => Assert.Null(selected.CapturedCompressedBytes));
    }

    [Fact]
    public void CompoundFileReadsGlobalCustomPropertyAndLinkedExpression()
    {
        using var fixture = new TemporarySolidWorksFile("legacy.SLDPRT");
        fixture.Write(SyntheticCompoundFile.Build(
            SyntheticOlePropertySet.Build(
                "物料编码",
                "0404-LEGACY",
                "\"SW-File Name\"")));

        var result = new NoApiMetadataExtractor().Extract(fixture.ValidatedFile, CancellationToken.None);

        Assert.Equal(NoApiContainerKind.CompoundFile, result.ContainerKind);
        Assert.Null(result.InternalVersion);
        var property = Assert.Single(result.Properties, item => item.Name == "物料编码");
        Assert.Equal("0404-LEGACY", property.ResolvedValue);
        Assert.Equal("\"SW-File Name\"", property.RawValue);
        Assert.True(property.IsLinked);
    }

    [Fact]
    public void NoApiExtractorRejectsLegacyDrawingCandidate()
    {
        using var fixture = new TemporarySolidWorksFile("legacy.SLDDRW");
        var candidate = new FileCandidate(
            fixture.Path,
            SolidWorksDocumentKind.Drawing,
            0,
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<MetadataWorkerException>(() =>
            new NoApiMetadataExtractor().Extract(
                new ValidatedFile(fixture.Path, candidate),
                CancellationToken.None));

        Assert.Equal(MetadataErrorCodes.UnsupportedFileType, exception.Code);
    }
    [Fact]
    public void CancellationIsObservedBeforeFileParsing()
    {
        using var fixture = new TemporarySolidWorksFile("cancel.SLDPRT");
        fixture.Write(SyntheticModernContainer.Build(17_000, new Dictionary<string, byte[]>()));
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new NoApiMetadataExtractor().Extract(fixture.ValidatedFile, source.Token));
    }

    [Fact]
    public void OptInRealModernFileSmokeTest()
    {
        var path = Environment.GetEnvironmentVariable("SWSEARCH_REAL_SAMPLE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var info = new FileInfo(path);
        var candidate = new FileCandidate(
            path,
            SolidWorksDocumentKind.Part,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        var stopwatch = Stopwatch.StartNew();
        var result = new NoApiMetadataExtractor().Extract(
            new ValidatedFile(path, candidate),
            CancellationToken.None);
        stopwatch.Stop();
        Console.WriteLine(
            $"real-modern bytes={info.Length} properties={result.Properties.Count} configs={result.ConfigurationNames.Count} ms={stopwatch.Elapsed.TotalMilliseconds:F2}");

        Assert.Equal(NoApiContainerKind.ModernChunk, result.ContainerKind);
        Assert.InRange(
            result.InternalVersion ?? 0,
            ModernSolidWorksMetadataReader.MinimumSupportedInternalVersion,
            ModernSolidWorksMetadataReader.MaximumSupportedInternalVersion);
        Assert.NotEmpty(result.Properties);
        Assert.Contains(result.Properties, property => property.Scope == PropertyScope.Configuration);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var container = new ModernSolidWorksContainer(
            stream,
            NoApiExtractionLimits.Default);
        var inventory = container.Inventory(CancellationToken.None);
        var captured = container.ReadSelectedStreams(inventory, CancellationToken.None);
        var descriptorOnly = inventory with
        {
            SelectedStreams = inventory.SelectedStreams.ToDictionary(
                static pair => pair.Key,
                static pair => new ModernSelectedChunk(pair.Value.Descriptor, null),
                StringComparer.Ordinal)
        };
        var fallback = container.ReadSelectedStreams(
            descriptorOnly,
            CancellationToken.None);
        Assert.Equal(captured.Keys.Order(), fallback.Keys.Order());
        foreach (var (name, bytes) in captured)
        {
            Assert.True(
                bytes.AsSpan().SequenceEqual(fallback[name]),
                $"Captured and descriptor-only reads differ for {name}.");
        }
    }

    [Fact]
    public void OfflineMarkerScanMicrobenchmark()
    {
        const int iterations = 24;
        var bytes = new byte[8 * 1024 * 1024];
        ReadOnlySpan<byte> marker = [0x14, 0x00, 0x06, 0x00, 0x08, 0x00];
        marker.CopyTo(bytes.AsSpan(bytes.Length - marker.Length));

        _ = bytes.AsSpan().IndexOf(marker);
        _ = ScalarIndexOf(bytes, marker);

        var checksum = 0L;
        var scalar = Stopwatch.StartNew();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            checksum += ScalarIndexOf(bytes, marker);
        }
        scalar.Stop();

        var vectorized = Stopwatch.StartNew();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            checksum += bytes.AsSpan().IndexOf(marker);
        }
        vectorized.Stop();

        Console.WriteLine(
            $"marker-scan bytes={bytes.Length} iterations={iterations} scalar-ms={scalar.Elapsed.TotalMilliseconds:F2} span-ms={vectorized.Elapsed.TotalMilliseconds:F2} ratio={scalar.Elapsed.TotalMilliseconds / Math.Max(vectorized.Elapsed.TotalMilliseconds, 0.001):F2} checksum={checksum}");
        Assert.True(checksum > 0);
        Assert.True(
            vectorized.Elapsed <= scalar.Elapsed * 5,
            "Span.IndexOf regressed far beyond the scalar reference.");
    }

    private static int ScalarIndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack[index] == needle[0] &&
                haystack.Slice(index, needle.Length).SequenceEqual(needle))
            {
                return index;
            }
        }

        return -1;
    }
}

internal sealed class TemporarySolidWorksFile : IDisposable
{
    private readonly string _directory;

    public TemporarySolidWorksFile(string fileName)
    {
        _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SwSearch.NoApi.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, fileName);
    }

    public string Path { get; }

    public ValidatedFile ValidatedFile
    {
        get
        {
            var info = new FileInfo(Path);
            var kind = System.IO.Path.GetExtension(Path).ToUpperInvariant() switch
            {
                ".SLDASM" => SolidWorksDocumentKind.Assembly,
                _ => SolidWorksDocumentKind.Part
            };
            return new ValidatedFile(
                Path,
                new FileCandidate(
                    Path,
                    kind,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        }
    }

    public void Write(ReadOnlySpan<byte> bytes) => File.WriteAllBytes(Path, bytes.ToArray());

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

internal static class SyntheticModernContainer
{
    private const int Key = 4;

    public static byte[] Build(int version, IReadOnlyDictionary<string, byte[]> streams)
    {
        using var output = new MemoryStream();
        output.Write([0x54, 0x4D, 0x49, 0xCD, 0, 0, 0, Key]);
        WriteChunk(output, $"_MO_VERSION_{version}/Biography", Encoding.ASCII.GetBytes("version"));
        foreach (var (name, bytes) in streams)
        {
            WriteChunk(output, name, bytes);
        }

        return output.ToArray();
    }

    public static byte[] BuildWithSplitMarker(
        int version,
        string streamName,
        byte[] streamBytes)
    {
        using var output = new MemoryStream();
        output.Write([0x54, 0x4D, 0x49, 0xCD, 0, 0, 0, Key]);
        WriteChunk(output, $"_MO_VERSION_{version}/Biography", Encoding.ASCII.GetBytes("version"));

        // The next scan starts at the end of the version chunk. Place the next
        // six-byte marker so three bytes are in each adjacent 64 KiB read.
        var padding = ModernSolidWorksContainer.ScanBufferBytes - 3 - 4;
        output.Write(new byte[padding]);
        WriteChunk(output, streamName, streamBytes);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string name, byte[] uncompressed)
    {
        var compressed = Compress(uncompressed);
        var encodedName = Encoding.ASCII.GetBytes(name);
        for (var index = 0; index < encodedName.Length; index++)
        {
            encodedName[index] = RotateRight(encodedName[index], Key);
        }

        Span<byte> header = stackalloc byte[30];
        ReadOnlySpan<byte> marker = [0x14, 0x00, 0x06, 0x00, 0x08, 0x00];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xAABBCCDD);
        marker.CopyTo(header[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], 65_536);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..], checked((uint)compressed.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], checked((uint)uncompressed.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header[26..], checked((uint)encodedName.Length));
        output.Write(header);
        output.Write(encodedName);
        output.Write(compressed);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflater = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflater.Write(bytes);
        }

        return output.ToArray();
    }

    private static byte RotateRight(byte value, int shift) =>
        (byte)((value >> shift) | (value << (8 - shift)));
}

internal static class SyntheticOlePropertySet
{
    private static readonly Guid UserDefinedPropertiesFormat =
        new("D5CDD505-2E9C-101B-9397-08002B2CF9AE");

    public static byte[] Build(string name, string display, string linked)
    {
        var blocks = new List<(uint Id, byte[] Bytes)>
        {
            (0, DictionaryBlock(2, name)),
            (1, Int16Block(65001)),
            (2, StringBlock(display)),
            (0x01000002, StringBlock(linked))
        };
        var tableBytes = 8 + blocks.Count * 8;
        using var section = new MemoryStream();
        section.SetLength(tableBytes);
        section.Position = tableBytes;
        var entries = new List<(uint Id, uint Offset)>();
        foreach (var block in blocks)
        {
            entries.Add((block.Id, checked((uint)section.Position)));
            section.Write(block.Bytes);
            while ((section.Position & 3) != 0)
            {
                section.WriteByte(0);
            }
        }

        var sectionBytes = section.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(sectionBytes, checked((uint)sectionBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(sectionBytes.AsSpan(4), checked((uint)entries.Count));
        for (var index = 0; index < entries.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sectionBytes.AsSpan(8 + index * 8), entries[index].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(sectionBytes.AsSpan(12 + index * 8), entries[index].Offset);
        }

        var result = new byte[48 + sectionBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, 0xFFFE);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), 1);
        UserDefinedPropertiesFormat.TryWriteBytes(result.AsSpan(28, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44), 48);
        sectionBytes.CopyTo(result.AsSpan(48));
        return result;
    }

    private static byte[] DictionaryBlock(uint id, string name)
    {
        var encoded = Encoding.UTF8.GetBytes(name + "\0");
        var result = new byte[12 + encoded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)encoded.Length));
        encoded.CopyTo(result.AsSpan(12));
        return result;
    }

    private static byte[] Int16Block(ushort value)
    {
        var result = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(result, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), value);
        return result;
    }

    private static byte[] StringBlock(string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value + "\0");
        var result = new byte[8 + encoded.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, 30);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)encoded.Length));
        encoded.CopyTo(result.AsSpan(8));
        return result;
    }
}

internal static class SyntheticCompoundFile
{
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FatSector = 0xFFFFFFFD;

    public static byte[] Build(byte[] propertySet)
    {
        const int sectorSize = 512;
        const int streamSectors = 8;
        const int fatSectorIndex = 9;
        var result = new byte[sectorSize * 11];
        var header = result.AsSpan(0, sectorSize);
        ReadOnlySpan<byte> signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        signature.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[24..], 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header[30..], 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(header[60..], EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(header[68..], EndOfChain);
        for (var index = 0; index < 109; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[(76 + index * 4)..], FreeSector);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header[76..], fatSectorIndex);

        var directory = result.AsSpan(sectorSize, sectorSize);
        WriteDirectoryEntry(directory, "Root Entry", 5, child: 1, startSector: EndOfChain, size: 0);
        WriteDirectoryEntry(
            directory[128..],
            "\u0005DocumentSummaryInformation",
            2,
            child: FreeSector,
            startSector: 1,
            size: 4096);

        propertySet.CopyTo(result.AsSpan(sectorSize * 2, propertySet.Length));
        var fat = result.AsSpan(sectorSize * (fatSectorIndex + 1), sectorSize);
        for (var index = 0; index < sectorSize / sizeof(uint); index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fat[(index * 4)..], FreeSector);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(fat, EndOfChain);
        for (var sector = 1; sector < streamSectors; sector++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fat[(sector * 4)..], checked((uint)(sector + 1)));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(fat[(streamSectors * 4)..], EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[(fatSectorIndex * 4)..], FatSector);
        return result;
    }

    private static void WriteDirectoryEntry(
        Span<byte> entry,
        string name,
        byte type,
        uint child,
        uint startSector,
        ulong size)
    {
        var encodedName = Encoding.Unicode.GetBytes(name + "\0");
        encodedName.CopyTo(entry);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], checked((ushort)encodedName.Length));
        entry[66] = type;
        entry[67] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], FreeSector);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], FreeSector);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], child);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], startSector);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[120..], size);
    }
}
