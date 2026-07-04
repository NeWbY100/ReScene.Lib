using System.Buffers.Binary;
using System.Text;

namespace ReScene.SRS;

/// <summary>
/// Shared MP4/ISO-BMFF constants and helpers used by the parser, profiler, and rebuilder.
/// </summary>
internal static class MP4Atoms
{
    /// <summary>
    /// Atoms that contain nested child atoms (and so must be descended into) rather than
    /// raw payload. Kept identical across the profiler, parser, and rebuilder so they agree
    /// on the box hierarchy. Deliberately excludes FullBox-style containers such as
    /// <c>meta</c>/<c>ilst</c>, whose 4-byte version/flags prefix would misalign naive recursion.
    /// </summary>
    public static readonly HashSet<string> ContainerAtoms = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "stbl", "edts", "udta"
    };

    /// <summary>
    /// Counts the top-level <c>mdat</c> atoms in an MP4 stream by walking atom headers only.
    /// Used to reject multi-mdat (fragmented) MP4, which the single contiguous-track model cannot
    /// reconstruct (see <c>docs/known-limitations.md</c> #13).
    /// <para>
    /// Stepping over an <c>mdat</c> differs by stream: in an original sample the payload is present,
    /// so step by the full declared size; in an SRS the payload is stripped while the header retains
    /// the original size, so step by the header only to reach the next atom. Passing the wrong mode
    /// for an SRS overshoots EOF and under-counts. Leaves the stream position indeterminate.
    /// </para>
    /// </summary>
    /// <param name="fs">MP4 (sample or SRS) stream.</param>
    /// <param name="mdatPayloadStripped"><see langword="true"/> for an SRS stream; <see langword="false"/> for an original sample.</param>
    public static int CountMdatAtoms(Stream fs, bool mdatPayloadStripped)
    {
        long end = fs.Length;
        fs.Position = 0;
        int count = 0;

        while (fs.Position + Mp4AtomTypes.AtomHeaderSize <= end)
        {
            long atomStart = fs.Position;

            byte[] sizeBytes = new byte[4];
            fs.ReadExactly(sizeBytes, 0, 4);
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(sizeBytes);

            byte[] typeBytes = new byte[4];
            fs.ReadExactly(typeBytes, 0, 4);
            string type = Encoding.ASCII.GetString(typeBytes);

            int headerSize = Mp4AtomTypes.AtomHeaderSize;
            long totalSize;

            if (size32 == Mp4AtomTypes.ExtendedSizeSentinel)
            {
                if (atomStart + Mp4AtomTypes.AtomExtendedHeaderSize > end)
                {
                    break;
                }

                byte[] extBytes = new byte[8];
                fs.ReadExactly(extBytes, 0, 8);
                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extBytes);
                headerSize = Mp4AtomTypes.AtomExtendedHeaderSize;
            }
            else if (size32 == Mp4AtomTypes.ToEndSentinel)
            {
                totalSize = end - atomStart;
            }
            else
            {
                totalSize = size32;
            }

            if (totalSize < headerSize)
            {
                break;
            }

            if (type == "mdat")
            {
                count++;
                fs.Position = mdatPayloadStripped ? atomStart + headerSize : atomStart + totalSize;
            }
            else
            {
                fs.Position = atomStart + totalSize;
            }
        }

        return count;
    }
}

internal interface IContainerHandler
{
    public SRSContainerType ContainerType { get; }
    public (List<TrackInfo> Tracks, uint CRC32, long TotalSize) Profile(
        string samplePath,
        Action<long, long, int>? reportScanProgress,
        CancellationToken ct);
    public void WriteSRS(string outputPath, string samplePath, List<TrackInfo> tracks, long sampleSize, uint sampleCRC32, SRSCreationOptions options, CancellationToken ct);
}
