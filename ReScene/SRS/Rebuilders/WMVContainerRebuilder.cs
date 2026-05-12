using System.Buffers.Binary;
using System.Text;

namespace ReScene.SRS;

/// <summary>
/// Rebuilds a WMV/ASF sample by replaying the ASF object structure from the SRS file,
/// skipping SRS GUID objects. Body data is copied verbatim from SRS.
/// </summary>
internal class WMVContainerRebuilder : IContainerRebuilder
{
    private static readonly byte[] _guidSRSFile = Encoding.ASCII.GetBytes("SRSFSRSFSRSFSRSF");
    private static readonly byte[] _guidSRSTrack = Encoding.ASCII.GetBytes("SRSTSRSTSRSTSRST");
    private static readonly byte[] _guidSRSPadding = Encoding.ASCII.GetBytes("PADDINGBYTESDATA");

    public SRSContainerType ContainerType => SRSContainerType.WMV;

    public void Rebuild(
        string srsFilePath,
        Dictionary<uint, SRSTrackDataBlock> tracks,
        string mediaFilePath,
        Dictionary<uint, long> trackOffsets,
        string outputPath,
        Action<string, int, int, double>? reportProgress,
        Action<string, long, long, int>? reportScanProgress,
        CancellationToken ct)
    {
        using var srsFs = new FileStream(srsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(srsFs);
        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        byte[] sizeBuffer = new byte[8];

        while (srsFs.Position + 24 <= srsFs.Length)
        {
            ct.ThrowIfCancellationRequested();
            long objStart = srsFs.Position;

            byte[] guid = reader.ReadBytes(16);
            ulong totalSize = reader.ReadUInt64();

            if (totalSize < 24)
            {
                break;
            }

            long objEnd = objStart + (long)totalSize;

            // Skip SRS objects
            if (GuidEquals(guid, _guidSRSFile) ||
                GuidEquals(guid, _guidSRSTrack) ||
                GuidEquals(guid, _guidSRSPadding))
            {
                srsFs.Position = objEnd;
                continue;
            }

            // Write header
            outFs.Write(guid);
            BinaryPrimitives.WriteUInt64LittleEndian(sizeBuffer, totalSize);
            outFs.Write(sizeBuffer);

            // Copy body verbatim
            long bodySize = (long)totalSize - 24;
            if (bodySize > 0)
            {
                StreamUtilities.CopyBytes(srsFs, outFs, bodySize);
            }

            srsFs.Position = objEnd;
        }
    }

    private static bool GuidEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        return a.AsSpan().SequenceEqual(b);
    }
}
