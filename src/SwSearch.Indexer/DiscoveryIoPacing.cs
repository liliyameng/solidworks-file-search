namespace SwSearch.Indexer;

/// <summary>
/// Intra-scan IO pacing for discovery enumeration. The runtime-level
/// deferral already keeps a NEW scan from starting on a busy system; this
/// policy additionally slows down a scan that is ALREADY running, so the
/// observed kernel-side read storms (full-speed MFT/directory enumeration at
/// ~240 MB/s) yield to foreground work mid-scan.
/// </summary>
/// <remarks>
/// One pacing unit is applied per 1024 enumerated entries (an MFT checkpoint
/// is ~1 MB of metadata reads). Calibration targets, by the sampled system
/// load level from <see cref="SystemLoadMonitor"/>:
/// level 0 — system idle: no delay (fast indexing when nobody is using the
/// machine); level 1 (>=50% cpu): ~60 MB/s; level 2 (>=75%): ~20 MB/s;
/// level 3 (>=85% cpu or &lt;8% available memory): ~5 MB/s.
/// </remarks>
internal static class DiscoveryIoPacing
{
    internal static readonly ReadOnlyMemory<TimeSpan> ChunkDelays = new[]
    {
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(16),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(200),
    };

    /// <summary>Delay applied after one 1024-entry chunk at the given load level.</summary>
    internal static TimeSpan DelayFor(int level)
    {
        return ChunkDelays.Span[Math.Clamp(level, 0, ChunkDelays.Length - 1)];
    }
}
