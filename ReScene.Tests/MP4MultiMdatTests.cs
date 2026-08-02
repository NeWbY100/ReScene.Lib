using System.Buffers.Binary;
using System.Text;
using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Tests multi-mdat (fragmented) MP4 detection. A fragmented MP4 cannot be represented
/// by the single contiguous-track model, so it is refused cleanly on both the creation side
/// (<see cref="MP4ContainerHandler.Profile"/>, sample with mdat payloads present) and the rebuild
/// side (<see cref="MP4ContainerRebuilder"/>, SRS with mdat payloads stripped).
/// </summary>
public class MP4MultiMdatTests
{
    // A full atom: 4-byte big-endian size (= 8 + payload) + 4-char type + zero-filled payload.
    private static byte[] Atom(string type, int payloadLength)
    {
        byte[] atom = new byte[MP4AtomTypes.AtomHeaderSize + payloadLength];
        BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)(MP4AtomTypes.AtomHeaderSize + payloadLength));
        Encoding.ASCII.GetBytes(type).CopyTo(atom, 4);
        return atom;
    }

    // An SRS-style mdat: an 8-byte header declaring the ORIGINAL (large) size, with the payload
    // stripped (no payload bytes physically follow) — exactly what MP4ContainerHandler.WriteSRS emits.
    private static byte[] StrippedMdatHeader(uint originalPayloadLength)
    {
        byte[] header = new byte[MP4AtomTypes.AtomHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, MP4AtomTypes.AtomHeaderSize + originalPayloadLength);
        Encoding.ASCII.GetBytes("mdat").CopyTo(header, 4);
        return header;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    // ── CountMdatAtoms: sample (payloads present, mdatPayloadStripped: false) ──

    [Fact]
    public void CountMdatAtoms_Sample_NoMdat_ReturnsZero()
    {
        using var ms = new MemoryStream(Concat(Atom("ftyp", 8), Atom("moov", 16)));
        Assert.Equal(0, MP4Atoms.CountMdatAtoms(ms, mdatPayloadStripped: false));
    }

    [Fact]
    public void CountMdatAtoms_Sample_SingleMdat_ReturnsOne()
    {
        using var ms = new MemoryStream(Concat(Atom("ftyp", 8), Atom("mdat", 100), Atom("moov", 16)));
        Assert.Equal(1, MP4Atoms.CountMdatAtoms(ms, mdatPayloadStripped: false));
    }

    [Fact]
    public void CountMdatAtoms_Sample_TwoMdats_ReturnsTwo()
    {
        using var ms = new MemoryStream(Concat(Atom("ftyp", 8), Atom("mdat", 40), Atom("mdat", 40)));
        Assert.Equal(2, MP4Atoms.CountMdatAtoms(ms, mdatPayloadStripped: false));
    }

    // ── CountMdatAtoms: SRS (payloads stripped, mdatPayloadStripped: true) ──

    [Fact]
    public void CountMdatAtoms_SRS_TwoStrippedMdats_ReturnsTwo()
    {
        // Two mdat headers declaring large original sizes but with NO payload bytes present —
        // the real SRS shape. Only header-only stepping reaches the second mdat.
        byte[] srs = Concat(Atom("ftyp", 8), StrippedMdatHeader(1_000_000), StrippedMdatHeader(2_000_000), Atom("moov", 8));
        using var ms = new MemoryStream(srs);
        Assert.Equal(2, MP4Atoms.CountMdatAtoms(ms, mdatPayloadStripped: true));
    }

    [Fact]
    public void CountMdatAtoms_SRS_WrongMode_OvershootsAndUndercounts()
    {
        // Guard against regressing to the old bug: walking a stripped SRS with the SAMPLE stepping
        // rule (mdatPayloadStripped: false) advances by the mdat's huge declared size, overshoots
        // EOF after the first mdat, and returns 1 — which would leave the guard silent.
        byte[] srs = Concat(Atom("ftyp", 8), StrippedMdatHeader(1_000_000), StrippedMdatHeader(2_000_000), Atom("moov", 8));
        using var ms = new MemoryStream(srs);
        Assert.Equal(1, MP4Atoms.CountMdatAtoms(ms, mdatPayloadStripped: false));
    }

    // ── Creation refuses multi-mdat samples ──

    [Fact]
    public void Profile_MultiMdatSample_ThrowsNotSupported()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rescene-mp4c-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string samplePath = Path.Combine(dir, "sample.mp4");
            File.WriteAllBytes(samplePath, Concat(Atom("ftyp", 8), Atom("mdat", 40), Atom("mdat", 40)));

            var handler = new MP4ContainerHandler();

            Assert.Throws<NotSupportedException>(() =>
                handler.Profile(samplePath, null, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Rebuild refuses multi-mdat SRS (from another tool), before writing output ──

    [Fact]
    public void Rebuild_MultiMdatSRS_ThrowsNotSupportedBeforeWritingOutput()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rescene-mp4r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string srsPath = Path.Combine(dir, "sample.srs");
            string mediaPath = Path.Combine(dir, "media.mp4");
            string outPath = Path.Combine(dir, "out.mp4");
            // Real SRS shape: stripped mdat headers (large declared size, no payload).
            File.WriteAllBytes(srsPath, Concat(
                Atom("ftyp", 8), StrippedMdatHeader(1_000_000), StrippedMdatHeader(2_000_000)));
            File.WriteAllBytes(mediaPath, new byte[16]);

            var rebuilder = new MP4ContainerRebuilder();

            Assert.Throws<NotSupportedException>(() => rebuilder.Rebuild(
                srsPath, [], mediaPath,
                [], outPath, null, null, CancellationToken.None));

            // The guard fires before the output file is opened, so no partial file is left behind.
            Assert.False(File.Exists(outPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
