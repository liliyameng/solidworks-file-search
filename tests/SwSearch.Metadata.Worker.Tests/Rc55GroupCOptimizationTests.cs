using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker.Tests;

public sealed class Rc55GroupCOptimizationTests
{
    [Fact]
    public async Task StaRunnerReusesOneSerialThread()
    {
        var firstThread = await StaTaskRunner.RunAsync(
            () => Environment.CurrentManagedThreadId);
        var secondThread = await StaTaskRunner.RunAsync(
            () => Environment.CurrentManagedThreadId);

        Assert.Equal(firstThread, secondThread);

        var active = 0;
        var maximumActive = 0;
        var work = Enumerable.Range(0, 8)
            .Select(_ => StaTaskRunner.RunAsync(() =>
            {
                var current = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximumActive, current);
                Thread.SpinWait(50_000);
                Interlocked.Decrement(ref active);
                return Environment.CurrentManagedThreadId;
            }))
            .ToArray();

        var threads = await Task.WhenAll(work);
        Assert.All(threads, thread => Assert.Equal(firstThread, thread));
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public void PipelineEvidenceAvoidsASecondFileSystemCandidateQuery()
    {
        using var fixture = new TemporarySolidWorksFile("evidence.SLDPRT");
        fixture.Write(SyntheticModernContainer.Build(
            17_000,
            new Dictionary<string, byte[]>
            {
                ["docProps/custom.xml"] =
                    System.Text.Encoding.UTF8.GetBytes("<Properties />")
            }));
        var info = new FileInfo(fixture.Path);
        var evidence = new MetadataFileEvidence(
            fixture.Path,
            fixture.Path,
            SolidWorksDocumentKind.Part,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            "RC55:FIXTURE");
        var accessor = new CountingFileSystemAccessor();
        var engine = new MetadataExtractionEngine(
            new FileRequestValidator(accessor),
            new NoApiMetadataExtractor(),
            new EmptyPropertyStore());

        var result = ((IEvidenceAwareMetadataExtractionEngine)engine).Extract(
            evidence,
            CancellationToken.None);

        Assert.Equal(MetadataSource.NoApi, result.Source);
        Assert.Equal(0, accessor.ReadCount);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var observed = Volatile.Read(ref location);
            while (observed < value)
            {
                var prior = Interlocked.CompareExchange(ref location, value, observed);
                if (prior == observed)
                {
                    return;
                }

                observed = prior;
            }
        }
    }

    private sealed class CountingFileSystemAccessor : IFileSystemAccessor
    {
        public int ReadCount { get; private set; }

        public FileCandidate ReadCandidate(string fullPath, SolidWorksDocumentKind kind)
        {
            ReadCount++;
            throw new InvalidOperationException("Evidence-aware extraction must reuse the pipeline evidence.");
        }
    }

    private sealed class EmptyPropertyStore : IWindowsPropertyStoreExtractor
    {
        public PropertyStoreExtraction Extract(ValidatedFile file) =>
            new(Array.Empty<DocumentProperty>(), MetadataErrorCodes.PropertyStoreEmpty);
    }
}
