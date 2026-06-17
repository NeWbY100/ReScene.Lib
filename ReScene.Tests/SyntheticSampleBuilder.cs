using System.Buffers.Binary;
using System.Text;

namespace ReScene.Tests;

/// <summary>
/// Shared helpers for building minimal synthetic media sample files (AVI, MKV,
/// MP4, FLAC, MP3, stream) used by the SRS writer/parser/rebuilder test suites.
///
/// Each builder takes the full destination <c>path</c> and returns it, so callers
/// keep ownership of their own temp directory and file naming. Byte content is
/// deterministic: <see cref="CreateTestData"/> seeds a fresh <see cref="Random"/>
/// per call (default seed 42), and the MKV builder exposes a separate
/// <c>block2Seed</c> so callers that need byte-different second blocks (e.g. the
/// rebuilder suite, which uses seed 99) can reproduce their exact bytes.
/// </summary>
internal static class SyntheticSampleBuilder
{
    /// <summary>
    /// Builds a minimal valid AVI file:
    /// RIFF AVI { LIST hdrl { avih }, LIST movi { 00dc(data), 01wb(data) } }.
    /// Video chunk uses 512 bytes (seed 42), audio chunk 256 bytes (seed 42).
    /// </summary>
    public static string BuildAvi(string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Build inner content first to get sizes
        var moviContent = new MemoryStream();
        var moviWriter = new BinaryWriter(moviContent);

        // Video chunk 00dc
        byte[] videoData = CreateTestData(512);
        moviWriter.Write(Encoding.ASCII.GetBytes("00dc"));
        moviWriter.Write((uint)videoData.Length);
        moviWriter.Write(videoData);

        // Audio chunk 01wb
        byte[] audioData = CreateTestData(256);
        moviWriter.Write(Encoding.ASCII.GetBytes("01wb"));
        moviWriter.Write((uint)audioData.Length);
        moviWriter.Write(audioData);

        byte[] moviBytes = moviContent.ToArray();

        // Build hdrl (minimal: just an avih chunk)
        var hdrlContent = new MemoryStream();
        var hdrlWriter = new BinaryWriter(hdrlContent);
        byte[] avihData = new byte[56]; // minimal avih
        hdrlWriter.Write(Encoding.ASCII.GetBytes("avih"));
        hdrlWriter.Write((uint)avihData.Length);
        hdrlWriter.Write(avihData);
        byte[] hdrlBytes = hdrlContent.ToArray();

        // Calculate total sizes
        uint hdrlSize = (uint)(4 + hdrlBytes.Length); // "hdrl" + children
        uint moviSize = (uint)(4 + moviBytes.Length); // "movi" + children
        uint riffSize = (uint)(4 + 8 + hdrlSize + 8 + moviSize); // "AVI " + LIST headers

        // Write RIFF header
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffSize);
        bw.Write(Encoding.ASCII.GetBytes("AVI "));

        // LIST hdrl
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(hdrlSize);
        bw.Write(Encoding.ASCII.GetBytes("hdrl"));
        bw.Write(hdrlBytes);

        // LIST movi
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(moviSize);
        bw.Write(Encoding.ASCII.GetBytes("movi"));
        bw.Write(moviBytes);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal MKV file with EBML header + Segment + Cluster + two
    /// SimpleBlocks (track 1 = 512 bytes seed 42, track 2 = 256 bytes
    /// <paramref name="block2Seed"/>).
    /// </summary>
    /// <param name="block2Seed">
    /// Seed for the second SimpleBlock's data. The rebuilder suite uses 99 so
    /// its sample/movie files share identical track-2 bytes; the writer/parser
    /// suites use the default 42.
    /// </param>
    public static string BuildMkv(string path, int block2Seed = 42)
    {
        using var ms = new MemoryStream();

        // EBML Header element (ID: 0x1A45DFA3)
        byte[] ebmlContent = BuildEbmlHeaderContent();
        WriteEbmlElement(ms, 0x1A45DFA3, ebmlContent);

        // Segment (ID: 0x18538067) containing a Cluster with SimpleBlocks
        var segContent = new MemoryStream();

        // Cluster (ID: 0x1F43B675)
        var clusterContent = new MemoryStream();

        // SimpleBlock (ID: 0xA3): track=1, timecode=0, flags=0x80, then data
        byte[] blockData = CreateTestData(512);
        byte[] simpleBlockPayload = new byte[1 + 2 + 1 + blockData.Length]; // trackVint(1) + timecode(2) + flags(1) + data
        simpleBlockPayload[0] = 0x81; // Track 1 as VINT
        simpleBlockPayload[1] = 0; // Timecode MSB
        simpleBlockPayload[2] = 0; // Timecode LSB
        simpleBlockPayload[3] = 0x80; // Flags (keyframe)
        blockData.CopyTo(simpleBlockPayload, 4);
        WriteEbmlElement(clusterContent, 0xA3, simpleBlockPayload);

        // Second SimpleBlock for track 2
        byte[] blockData2 = CreateTestData(256, block2Seed);
        byte[] simpleBlockPayload2 = new byte[1 + 2 + 1 + blockData2.Length];
        simpleBlockPayload2[0] = 0x82; // Track 2 as VINT
        simpleBlockPayload2[1] = 0;
        simpleBlockPayload2[2] = 0;
        simpleBlockPayload2[3] = 0x80;
        blockData2.CopyTo(simpleBlockPayload2, 4);
        WriteEbmlElement(clusterContent, 0xA3, simpleBlockPayload2);

        WriteEbmlElement(segContent, 0x1F43B675, clusterContent.ToArray());

        WriteEbmlElement(ms, 0x18538067, segContent.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal MP4 file: ftyp + moov + mdat (mdat = 1024 bytes seed 42).
    /// </summary>
    public static string BuildMp4(string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // ftyp atom
        byte[] ftypData = Encoding.ASCII.GetBytes("isom\x00\x00\x02\x00isomiso2mp41");
        WriteAtomBE(bw, "ftyp", ftypData);

        // moov atom (minimal)
        byte[] moovData = new byte[32];
        WriteAtomBE(bw, "moov", moovData);

        // mdat atom with stream data
        byte[] mdatData = CreateTestData(1024);
        WriteAtomBE(bw, "mdat", mdatData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal FLAC file: fLaC marker + STREAMINFO block + frame data
    /// (512 bytes seed 42).
    /// </summary>
    public static string BuildFlac(string path)
    {
        using var ms = new MemoryStream();

        // fLaC marker
        ms.Write(Encoding.ASCII.GetBytes("fLaC"));

        // STREAMINFO metadata block (type=0, last=true)
        byte[] streamInfo = new byte[34]; // Standard STREAMINFO size
        byte header = 0x80; // is_last=1, type=0
        ms.WriteByte(header);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(34); // BE24 size

        ms.Write(streamInfo);

        // Frame data (simulated)
        byte[] frameData = CreateTestData(512);
        ms.Write(frameData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal MP3 file: ID3v2 header + audio sync frames
    /// (512 bytes seed 42, with the first two bytes forced to the MP3 sync word).
    /// </summary>
    public static string BuildMp3(string path)
    {
        using var ms = new MemoryStream();

        // ID3v2 header
        ms.Write(Encoding.ASCII.GetBytes("ID3"));
        ms.WriteByte(3); // version major
        ms.WriteByte(0); // version minor
        ms.WriteByte(0); // flags

        // ID3v2 size (syncsafe, 4 bytes) = 10 bytes of ID3 payload
        int id3Payload = 10;
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte((byte)id3Payload);

        // ID3 payload
        ms.Write(new byte[id3Payload]);

        // MP3 sync frames (0xFF 0xFB = MPEG1 Layer3)
        byte[] audioData = CreateTestData(512);
        audioData[0] = 0xFF;
        audioData[1] = 0xFB;
        ms.Write(audioData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a minimal VOB/stream file (1024 bytes seed 42).
    /// </summary>
    public static string BuildStream(string path)
    {
        byte[] data = CreateTestData(1024);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>
    /// Builds a minimal WMV/ASF file: a Header Object followed by a Data Object
    /// whose body is 16-byte file ID + 8-byte packet count (2) + 2-byte reserved +
    /// two 300-byte packets (seed 123). The packet region is exactly what the SRS
    /// writer strips and the rebuilder must restore from the media file.
    /// </summary>
    public static string BuildWmv(string path)
    {
        // ASF Header Object GUID: 75B22630-668E-11CF-A6D9-00AA0062CE6C
        byte[] headerGuid =
        [
            0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
            0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C,
        ];
        // ASF Data Object GUID: 75B22636-668E-11CF-A6D9-00AA0062CE6C (prefix 36 26 B2 75)
        byte[] dataGuid =
        [
            0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
            0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C,
        ];

        using var ms = new MemoryStream();

        // Header Object (non-data) with a small verbatim body.
        WriteAsfObject(ms, headerGuid, CreateTestData(32, seed: 7));

        // Data Object body: file ID (16) + total packets (8, LE) + reserved (2) + packets.
        using var dataBody = new MemoryStream();
        dataBody.Write(CreateTestData(16, seed: 5));
        Span<byte> packetCount = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(packetCount, 2);
        dataBody.Write(packetCount);
        dataBody.Write(new byte[2]); // reserved
        dataBody.Write(CreateTestData(600, seed: 123)); // 2 x 300-byte packets
        WriteAsfObject(ms, dataGuid, dataBody.ToArray());

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>Writes an ASF object: 16-byte GUID + 8-byte little-endian size + body.</summary>
    public static void WriteAsfObject(Stream stream, byte[] guid, byte[] body)
    {
        stream.Write(guid);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)(guid.Length + 8 + body.Length));
        stream.Write(size);
        stream.Write(body);
    }

    /// <summary>
    /// Deterministic test data: a fresh <see cref="Random"/> seeded with
    /// <paramref name="seed"/> (default 42) filling <paramref name="size"/> bytes.
    /// </summary>
    public static byte[] CreateTestData(int size, int seed = 42)
    {
        byte[] data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Writes an EBML element: ID (1-4 bytes) + VINT size + data.</summary>
    public static void WriteEbmlElement(Stream stream, ulong id, byte[] data)
    {
        byte[] idBytes = EncodeEbmlId(id);
        stream.Write(idBytes);

        byte[] sizeBytes = EncodeEbmlSize(data.Length);
        stream.Write(sizeBytes);

        stream.Write(data);
    }

    public static byte[] EncodeEbmlId(ulong id)
    {
        if (id < 0x100)
        {
            return [(byte)id];
        }

        if (id < 0x10000)
        {
            return [(byte)(id >> 8), (byte)(id & 0xFF)];
        }

        if (id < 0x1000000)
        {
            return [(byte)(id >> 16), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
        }

        return [(byte)(id >> 24), (byte)((id >> 16) & 0xFF), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF)];
    }

    public static byte[] EncodeEbmlSize(long value)
    {
        if (value < 0x7F)
        {
            return [(byte)(0x80 | value)];
        }

        if (value < 0x3FFF)
        {
            return [(byte)(0x40 | (value >> 8)), (byte)(value & 0xFF)];
        }

        if (value < 0x1FFFFF)
        {
            return [(byte)(0x20 | (value >> 16)), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
        }

        return [(byte)(0x10 | (value >> 24)), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];
    }

    /// <summary>Writes an MP4 atom with the standard 8-byte big-endian header.</summary>
    public static void WriteAtomBE(BinaryWriter bw, string type, byte[] data)
    {
        uint totalSize = (uint)(8 + data.Length);
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sizeBytes, totalSize);
        bw.Write(sizeBytes);
        bw.Write(Encoding.ASCII.GetBytes(type));
        bw.Write(data);
    }

    /// <summary>
    /// Builds a minimal EBML header body: version=1, read version=1,
    /// max id length=4, max size length=8, doctype=matroska.
    /// </summary>
    public static byte[] BuildEbmlHeaderContent()
    {
        var ms = new MemoryStream();
        // EBMLVersion (0x4286) = 1
        WriteEbmlElement(ms, 0x4286, [1]);
        // EBMLReadVersion (0x42F7) = 1
        WriteEbmlElement(ms, 0x42F7, [1]);
        // EBMLMaxIDLength (0x42F2) = 4
        WriteEbmlElement(ms, 0x42F2, [4]);
        // EBMLMaxSizeLength (0x42F3) = 8
        WriteEbmlElement(ms, 0x42F3, [8]);
        // DocType (0x4282) = "matroska"
        WriteEbmlElement(ms, 0x4282, Encoding.ASCII.GetBytes("matroska"));
        return ms.ToArray();
    }
}
