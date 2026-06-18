using System.Buffers.Binary;
using Force.Crc32;
using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="OSOHashCalculator"/>. The public surface (<c>ComputeHashes</c>) takes
/// real RAR volume paths and only hashes stored, non-split entries that are at least 64 KiB.
/// <see cref="OSOHashCalculator.ComputeHash"/> is private, so every case is driven end-to-end
/// through a minimal but real RAR4 archive written to disk by <see cref="WriteStoredRar4"/>.
///
/// The independent oracle <see cref="ExpectedOsoHash"/> recomputes the expected value the way the
/// algorithm specifies (fileSize + 64-bit LE sum of the qwords in the first AND last 64 KiB),
/// deliberately NOT by calling the system under test.
/// </summary>
public sealed class OSOHashCalculatorTests : TempDirTestBase
{
    private const int HashChunkSize = 64 * 1024; // 65536 — must mirror OSOHashCalculator.HashChunkSize / MinFileSize.

    // ---- Known-hash cases --------------------------------------------------

    [Fact]
    public void ComputeHashes_ExactlyMinSizeStoredFile_ProducesDocumentedHash()
    {
        // A file of exactly 64 KiB: the "first 64 KiB" and "last 64 KiB" windows are the same
        // bytes, so every qword is summed twice — the documented "fileSize + 2 * sum" behavior.
        byte[] content = BuildPattern(HashChunkSize, seed: 0x1234);
        string rar = WriteStoredRar4("clip.bin", content);

        List<(string FileName, ulong FileSize, byte[] Hash)> results =
            OSOHashCalculator.ComputeHashes([rar]);

        var entry = Assert.Single(results);
        Assert.Equal("clip.bin", entry.FileName);
        Assert.Equal((ulong)HashChunkSize, entry.FileSize);
        Assert.Equal(ExpectedOsoHash(content), entry.Hash);
    }

    [Fact]
    public void ComputeHashes_StoredFileLargerThanChunk_HashesDistinctHeadAndTail()
    {
        // 70000 bytes (> 64 KiB): the head and tail windows now cover different bytes, so the
        // result depends on both ends. The oracle reads the same head/tail slices the SUT does.
        byte[] content = BuildPattern(70_000, seed: 0x9E37);
        string rar = WriteStoredRar4("movie.sample.mkv", content);

        List<(string FileName, ulong FileSize, byte[] Hash)> results =
            OSOHashCalculator.ComputeHashes([rar]);

        var entry = Assert.Single(results);
        Assert.Equal("movie.sample.mkv", entry.FileName);
        Assert.Equal(70_000UL, entry.FileSize);
        Assert.Equal(ExpectedOsoHash(content), entry.Hash);

        // The hash is exactly 8 bytes (a little-endian ulong).
        Assert.Equal(8, entry.Hash.Length);
    }

    // ---- Skip / boundary cases --------------------------------------------

    [Fact]
    public void ComputeHashes_FileOneByteUnderMinimum_IsSkipped()
    {
        // 65535 bytes is one byte short of the 64 KiB minimum, so the algorithm cannot read two
        // full 64 KiB windows and the entry is skipped — no hash produced.
        byte[] content = BuildPattern(HashChunkSize - 1, seed: 0x55);
        string rar = WriteStoredRar4("tooshort.bin", content);

        List<(string FileName, ulong FileSize, byte[] Hash)> results =
            OSOHashCalculator.ComputeHashes([rar]);

        Assert.Empty(results);
    }

    [Fact]
    public void ComputeHashes_CompressedEntry_IsSkipped()
    {
        // A compressed (method != 0) entry is skipped before any read: OSO hashes are only valid
        // against original bytes, which only flow through unchanged for stored entries.
        byte[] content = BuildPattern(HashChunkSize, seed: 0xABCD);
        string rar = WriteRar4("packed.bin", content, compressionMethod: 0x33, splitBefore: false);

        List<(string FileName, ulong FileSize, byte[] Hash)> results =
            OSOHashCalculator.ComputeHashes([rar]);

        Assert.Empty(results);
    }

    [Fact]
    public void ComputeHashes_SplitBeforeEntry_IsSkipped()
    {
        // An entry continued from a previous volume (IsSplitBefore) is a tail fragment, not a whole
        // file, so it must be skipped. A NORMAL stored file is placed FIRST so RARStream can be
        // constructed; otherwise a regressed IsSplitBefore guard would be masked by the
        // "you must start with the first volume" validation throwing (and being swallowed) for a
        // split-before first file. With this layout, dropping the guard would hash the split entry
        // and turn this test red.
        byte[] whole = BuildPattern(HashChunkSize, seed: 0x1111);
        byte[] continued = BuildPattern(HashChunkSize, seed: 0xBEEF);
        string rar = WriteTwoFileStoredRar4(
            ("whole.bin", whole, SplitBefore: false),
            ("continued.bin", continued, SplitBefore: true));

        List<(string FileName, ulong FileSize, byte[] Hash)> results =
            OSOHashCalculator.ComputeHashes([rar]);

        var entry = Assert.Single(results);
        Assert.Equal("whole.bin", entry.FileName);
        Assert.DoesNotContain(results, r => r.FileName == "continued.bin");
    }

    [Fact]
    public void ComputeHashes_EmptyVolumeList_ReturnsEmpty()
    {
        // The documented short-circuit: no volumes means no work and an empty result.
        List<(string FileName, ulong FileSize, byte[] Hash)> results =
            OSOHashCalculator.ComputeHashes([]);

        Assert.Empty(results);
    }

    [Fact]
    public void ComputeHashes_UnreadableEntry_EmitsWarningAndSkips()
    {
        // A stored entry whose declared packed size (>= 64 KiB) exceeds the bytes actually present
        // makes ComputeHash's ReadExactly hit EOF. The entry must be skipped AND surfaced through
        // the onWarning channel rather than dropped silently by a bare catch.
        string path = Path.Combine(TempDir, "truncated.rar");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);
            fs.Write(BuildArchiveHeader());
            // Header claims 70000 packed bytes, but only 100 actually follow.
            fs.Write(BuildFileHeader("truncated.bin", packedSize: 70_000, method: 0x30, splitBefore: false));
            fs.Write(new byte[100]);
        }

        var warnings = new List<string>();
        List<(string FileName, ulong FileSize, byte[] Hash)> results =
            OSOHashCalculator.ComputeHashes([path], warnings.Add);

        Assert.Empty(results);
        string warning = Assert.Single(warnings);
        Assert.Contains("truncated.bin", warning, StringComparison.Ordinal);
        Assert.Contains("OSO hash skipped", warning, StringComparison.Ordinal);
    }

    // ---- Oracle ------------------------------------------------------------

    /// <summary>
    /// Recomputes the OSO hash exactly as the algorithm specifies, independently of the SUT:
    /// fileSize + the 64-bit little-endian sum of every 8-byte qword in the first 64 KiB and the
    /// last 64 KiB (ulong arithmetic wraps, matching the implementation).
    /// </summary>
    private static byte[] ExpectedOsoHash(byte[] content)
    {
        ulong hash = (ulong)content.Length;

        ReadOnlySpan<byte> head = content.AsSpan(0, HashChunkSize);
        ReadOnlySpan<byte> tail = content.AsSpan(content.Length - HashChunkSize, HashChunkSize);

        for (int i = 0; i < HashChunkSize; i += 8)
        {
            hash += BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(i, 8));
            hash += BinaryPrimitives.ReadUInt64LittleEndian(tail.Slice(i, 8));
        }

        byte[] result = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(result, hash);
        return result;
    }

    // ---- Test fixtures -----------------------------------------------------

    /// <summary>
    /// Deterministic, non-trivial byte pattern so head and tail windows differ and qword sums are
    /// not all zero. A simple LCG keyed by <paramref name="seed"/> gives reproducible content.
    /// </summary>
    private static byte[] BuildPattern(int length, uint seed)
    {
        byte[] buffer = new byte[length];
        uint state = seed == 0 ? 1u : seed;
        for (int i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            buffer[i] = (byte)(state >> 24);
        }

        return buffer;
    }

    /// <summary>
    /// Writes a single-file stored (method 0) RAR4 archive and returns its path.
    /// </summary>
    private string WriteStoredRar4(string fileName, byte[] content) =>
        WriteRar4(fileName, content, compressionMethod: 0x30, splitBefore: false);

    /// <summary>
    /// Writes a minimal single-file RAR4 archive to <see cref="TempDirTestBase.TempDir"/>:
    /// 7-byte marker, a 13-byte archive header (0x73), then one file header (0x74) immediately
    /// followed by <paramref name="content"/> as the packed data. <paramref name="compressionMethod"/>
    /// is the raw method byte (0x30 = store, 0x33 = normal); <paramref name="splitBefore"/> sets the
    /// SPLIT_BEFORE flag so the entry looks continued from a previous volume.
    /// </summary>
    private string WriteRar4(string fileName, byte[] content, byte compressionMethod, bool splitBefore)
    {
        string path = Path.Combine(TempDir, fileName + ".rar");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        // RAR4 marker: "Rar!\x1A\x07\x00".
        fs.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        fs.Write(BuildArchiveHeader());
        fs.Write(BuildFileHeader(fileName, (uint)content.Length, compressionMethod, splitBefore));
        fs.Write(content); // Packed data follows the file header directly (PackedSize == content.Length).

        return path;
    }

    /// <summary>
    /// Writes a two-file stored RAR4 archive (marker, archive header, then each file header
    /// immediately followed by its packed data). Used to isolate the per-entry IsSplitBefore guard:
    /// the first file is a normal stored entry so RARStream construction succeeds.
    /// </summary>
    private string WriteTwoFileStoredRar4(
        (string Name, byte[] Content, bool SplitBefore) first,
        (string Name, byte[] Content, bool SplitBefore) second)
    {
        string path = Path.Combine(TempDir, first.Name + ".twofile.rar");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        fs.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);
        fs.Write(BuildArchiveHeader());
        fs.Write(BuildFileHeader(first.Name, (uint)first.Content.Length, 0x30, first.SplitBefore));
        fs.Write(first.Content);
        fs.Write(BuildFileHeader(second.Name, (uint)second.Content.Length, 0x30, second.SplitBefore));
        fs.Write(second.Content);

        return path;
    }

    /// <summary>13-byte RAR4 archive header (0x73) with a valid header CRC.</summary>
    private static byte[] BuildArchiveHeader()
    {
        const ushort headerSize = 13;
        byte[] header = new byte[headerSize];
        header[2] = 0x73; // ArchiveHeader type.
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), 0x0000); // Flags.
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), headerSize);
        // Reserved1 (2) + Reserved2 (4) remain zero.
        WriteHeaderCrc(header);
        return header;
    }

    /// <summary>
    /// RAR4 file header (0x74) with a valid header CRC. Layout matches what
    /// <see cref="ReScene.RAR.RARHeaderReader"/> parses: base(7) + ADD_SIZE(4) + UNP_SIZE(4) +
    /// HOST_OS(1) + FILE_CRC(4) + FILE_TIME(4) + UNP_VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4) + NAME.
    /// LONG_BLOCK is set so the reader treats ADD_SIZE as packed-data length; no ExtTime is used.
    /// </summary>
    private static byte[] BuildFileHeader(string fileName, uint packedSize, byte method, bool splitBefore)
    {
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;
        ushort headerSize = (ushort)(7 + 25 + nameSize);

        ushort flags = 0x8000; // LONG_BLOCK.
        if (splitBefore)
        {
            flags |= 0x0001; // SPLIT_BEFORE.
        }

        byte[] header = new byte[headerSize];
        header[2] = 0x74; // FileHeader type.
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(7), packedSize);   // ADD_SIZE == packed size.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(11), packedSize);  // UNP_SIZE (stored: equals packed).
        header[15] = 2;                                                            // HOST_OS = Windows.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0u);          // FILE_CRC (unused by hash).
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0x5A8E3100u); // FILE_TIME (DOS).
        header[24] = 29;                                                           // UNP_VER.
        header[25] = method;                                                       // METHOD (0x30 = store).
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), nameSize);    // NAME_SIZE.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 0x20u);       // ATTR = Archive.
        nameBytes.CopyTo(header.AsSpan(32));                                       // NAME.

        WriteHeaderCrc(header);
        return header;
    }

    /// <summary>Writes the lower 16 bits of CRC-32 over the header (from the type byte) into bytes 0-1.</summary>
    private static void WriteHeaderCrc(byte[] header)
    {
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0), (ushort)(crc32 & 0xFFFF));
    }
}
