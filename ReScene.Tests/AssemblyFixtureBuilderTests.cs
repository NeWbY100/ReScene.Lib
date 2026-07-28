using System.Text;
using Force.Crc32;
using ReScene.RAR;
using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Self-checks for <see cref="AssemblyFixtureBuilder"/>: proves both the original and produced
/// volume sets it builds are real, parseable RAR4 archives whose packed payload round-trips
/// identically through <see cref="RARStream"/> with no trailing bytes; that every volume walks
/// cleanly Marker -&gt; ArchiveHeader -&gt; FileHeader(s) -&gt; EndArchive to the end of file; that
/// non-final volumes match the configured size; that the two header shapes' split points differ by
/// exactly the expected byte count; that split flags are internally consistent; that every piece's
/// FILE_CRC matches its payload; and that the SRR's embedded headers are byte-identical to the
/// original volume files' headers (not merely re-derived from the same inputs).
/// </summary>
public class AssemblyFixtureBuilderTests : TempDirTestBase
{
    private const int StandardVolumeSize = 15_000;
    private static readonly byte[] Payload = [.. Enumerable.Range(0, 40_000).Select(i => (byte)(i % 251))];

    private AssemblyFixture BuildStandardFixture() =>
        AssemblyFixtureBuilder.Build(
            TempDir, StandardVolumeSize, [("a.bin", Payload)],
            originalHasExtTime: true, producedHasExtTime: false);

    [Fact]
    public void BothSets_ParseWithRARHeaderReader_AndPayloadRoundTrips()
    {
        AssemblyFixture f = BuildStandardFixture();

        Assert.True(f.OriginalVolumePaths.Count >= 3);
        // Packed stream is identical across shapes: RARStream over each set yields payload, with
        // nothing trailing it (no extra packed bytes leaking past the file's declared end).
        foreach (string first in new[] { f.OriginalVolumePaths[0], f.ProducedFirstVolumePath })
        {
            using var rs = new RARStream(first, "a.bin");
            Assert.Equal(Payload.Length, rs.Length);

            byte[] readBack = new byte[Payload.Length];
            rs.ReadExactly(readBack);
            Assert.Equal(Payload, readBack);

            Assert.Equal(0, rs.Read(new byte[1], 0, 1));
        }
    }

    [Fact]
    public void ExtTimeHeader_IsFiveBytesLonger_AndParses()
    {
        AssemblyFixture f = AssemblyFixtureBuilder.Build(
            TempDir, 15_000, [("a.bin", new byte[20_000])], true, false);

        // Read both first volumes' first file headers via RARHeaderReader and assert the
        // HeaderSize difference == 5 (flags word + 3-byte remainder).
        ushort originalHeaderSize = ReadFirstFileHeaderSize(f.OriginalVolumePaths[0]);
        ushort producedHeaderSize = ReadFirstFileHeaderSize(f.ProducedFirstVolumePath);

        Assert.Equal(5, originalHeaderSize - producedHeaderSize);
    }

    [Fact]
    public void BothSets_EveryVolumeWalksCleanlyToEndArchive_AndNonFinalVolumesMatchConfiguredSize()
    {
        AssemblyFixture f = BuildStandardFixture();
        List<WalkedVolume> originalWalk = WalkVolumeSet(f.OriginalVolumePaths[0]);
        List<WalkedVolume> producedWalk = WalkVolumeSet(f.ProducedFirstVolumePath);

        // The fixture's own reported path list matches what a real consumer independently
        // discovers by following RARVolumeNaming from the first volume.
        Assert.Equal(f.OriginalVolumePaths.Count, originalWalk.Count);

        foreach (WalkedVolume v in originalWalk.Concat(producedWalk))
        {
            Assert.True(v.SawArchiveHeader, $"{v.Path}: never saw an ArchiveHeader block.");
            Assert.True(v.SawEndArchive, $"{v.Path}: never saw an EndArchive block.");
            Assert.True(v.CleanEof, $"{v.Path}: {v.FileLength} byte file has trailing data after EndArchive.");
        }

        for (int i = 0; i < originalWalk.Count - 1; i++)
        {
            Assert.Equal(StandardVolumeSize, originalWalk[i].FileLength);
        }

        for (int i = 0; i < producedWalk.Count - 1; i++)
        {
            Assert.Equal(StandardVolumeSize, producedWalk[i].FileLength);
        }
    }

    [Fact]
    public void OriginalVsProduced_BudgetLimitedPieces_DifferByExactlyFiveBytes()
    {
        AssemblyFixture f = BuildStandardFixture();
        List<WalkedPiece> originalPieces = [.. WalkVolumeSet(f.OriginalVolumePaths[0]).SelectMany(v => v.Pieces)];
        List<WalkedPiece> producedPieces = [.. WalkVolumeSet(f.ProducedFirstVolumePath).SelectMany(v => v.Pieces)];

        // Only pieces that are still budget-limited (SplitAfter on BOTH sides) are guaranteed to
        // differ by exactly headerLen(produced) - headerLen(original) = 5 bytes: once either shape
        // finishes the file, its final piece is sized by "bytes remaining", not by volume budget,
        // and the two shapes have consumed different cumulative totals by then.
        int comparable = 0;
        for (int i = 0; i < originalPieces.Count && i < producedPieces.Count; i++)
        {
            if (!originalPieces[i].SplitAfter || !producedPieces[i].SplitAfter)
            {
                break;
            }

            Assert.Equal(5, (int)producedPieces[i].AddSize - (int)originalPieces[i].AddSize);
            comparable++;
        }

        Assert.True(comparable > 0, "Expected at least one budget-limited volume pair to compare.");
    }

    [Fact]
    public void SplitFlags_AreConsistentAcrossEveryPiece_ForBothSets()
    {
        AssemblyFixture f = BuildStandardFixture();
        AssertSplitFlagsConsistent([.. WalkVolumeSet(f.OriginalVolumePaths[0]).SelectMany(v => v.Pieces)]);
        AssertSplitFlagsConsistent([.. WalkVolumeSet(f.ProducedFirstVolumePath).SelectMany(v => v.Pieces)]);
    }

    [Fact]
    public void EveryPiece_FileCrcMatchesCrc32OfItsPayload_ForBothSets()
    {
        AssemblyFixture f = BuildStandardFixture();
        IEnumerable<WalkedPiece> allPieces = WalkVolumeSet(f.OriginalVolumePaths[0]).SelectMany(v => v.Pieces)
            .Concat(WalkVolumeSet(f.ProducedFirstVolumePath).SelectMany(v => v.Pieces));

        foreach (WalkedPiece piece in allPieces)
        {
            Assert.Equal(Crc32Algorithm.Compute(piece.Payload), piece.FileCrc);
        }
    }

    [Fact]
    public void SrrEmbeddedHeaders_AreByteIdenticalToOriginalVolumeHeaders()
    {
        AssemblyFixture f = BuildStandardFixture();
        List<WalkedVolume> originalWalk = WalkVolumeSet(f.OriginalVolumePaths[0]);
        Dictionary<string, (long Start, long End)> sections = WalkSrrRarFileSections(f.SrrPath);
        byte[] srrBytes = File.ReadAllBytes(f.SrrPath);

        Assert.Equal(f.OriginalVolumeNames.Count, originalWalk.Count);

        for (int i = 0; i < f.OriginalVolumeNames.Count; i++)
        {
            string name = f.OriginalVolumeNames[i];
            Assert.True(sections.TryGetValue(name, out (long Start, long End) range),
                $"SRR has no RARFile section named '{name}'.");

            byte[] embedded = srrBytes[(int)range.Start..(int)range.End];
            Assert.Equal(originalWalk[i].HeaderOnlyBytes, embedded);
        }
    }

    private static ushort ReadFirstFileHeaderSize(string volumePath)
    {
        using FileStream fs = new(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = RARUtils.RAR4Marker.Length; // skip the 7-byte marker
        var reader = new RARHeaderReader(fs);

        while (fs.Position < fs.Length)
        {
            RARBlockReadResult? block = reader.ReadBlock();
            if (block == null)
            {
                break;
            }

            if (block.BlockType == RAR4BlockType.FileHeader)
            {
                return block.HeaderSize;
            }

            reader.SkipBlock(block);
        }

        throw new InvalidOperationException($"No file header found in '{volumePath}'.");
    }

    /// <summary>
    /// Walks a full old-style volume set starting at <paramref name="firstVolumePath"/>, following
    /// <see cref="RARVolumeNaming.GetNextVolumePath"/> exactly as <see cref="RARStream"/> does.
    /// Reads every block of every volume via <see cref="RARHeaderReader"/>, capturing each
    /// FileHeader piece's split flags/ADD_SIZE/FILE_CRC/payload plus the volume's header-only bytes
    /// (everything except payload regions) and whether it reached a clean EndArchive with no
    /// trailing data.
    /// </summary>
    private static List<WalkedVolume> WalkVolumeSet(string firstVolumePath)
    {
        List<WalkedVolume> volumes = [];
        string? currentPath = firstVolumePath;

        while (currentPath != null && File.Exists(currentPath))
        {
            byte[] fileBytes = File.ReadAllBytes(currentPath);
            using MemoryStream fs = new(fileBytes, writable: false);
            fs.Position = RARUtils.RAR4Marker.Length;
            var reader = new RARHeaderReader(fs);

            using MemoryStream headerOnly = new();
            headerOnly.Write(fileBytes, 0, RARUtils.RAR4Marker.Length);

            bool sawArchiveHeader = false;
            bool sawEndArchive = false;
            List<WalkedPiece> pieces = [];

            while (fs.Position < fs.Length)
            {
                RARBlockReadResult? block = reader.ReadBlock();
                if (block == null)
                {
                    break;
                }

                headerOnly.Write(fileBytes, (int)block.BlockPosition, block.HeaderSize);

                if (block.BlockType == RAR4BlockType.ArchiveHeader)
                {
                    sawArchiveHeader = true;
                    fs.Position = block.BlockPosition + block.HeaderSize;
                }
                else if (block.BlockType == RAR4BlockType.FileHeader && block.FileHeader is { } fh)
                {
                    byte[] payload = new byte[fh.PackedSize];
                    fs.Position = block.BlockPosition + block.HeaderSize;
                    fs.ReadExactly(payload); // advances fs.Position past the payload
                    pieces.Add(new WalkedPiece(fh.FileName, fh.IsSplitBefore, fh.IsSplitAfter, (uint)fh.PackedSize, fh.FileCRC, payload));
                }
                else if (block.BlockType == RAR4BlockType.EndArchive)
                {
                    sawEndArchive = true;
                    fs.Position = block.BlockPosition + block.HeaderSize;
                    break;
                }
                else
                {
                    fs.Position = block.BlockPosition + block.HeaderSize;
                }
            }

            bool cleanEof = fs.Position == fs.Length;
            volumes.Add(new WalkedVolume(
                currentPath, fileBytes.Length, sawArchiveHeader, sawEndArchive, cleanEof, pieces, headerOnly.ToArray()));
            currentPath = RARVolumeNaming.GetNextVolumePath(currentPath, isOldNaming: true);
        }

        return volumes;
    }

    /// <summary>
    /// Walks an SRR file's top-level blocks, returning each RARFile (0x71) section's name mapped to
    /// the byte range \[start,end) of its embedded RAR headers (marker through end block, no
    /// payload — an SRR never stores packed data). Embedded RAR4 blocks are parsed via <see
    /// cref="RARHeaderReader"/> for correct field-aware skipping (a FileHeader's ADD_SIZE field is
    /// present but must NOT be skipped, since no payload follows it inside an SRR); the 7-byte
    /// marker that precedes each section's embedded headers is skipped as raw bytes, since it has
    /// no CRC/type/flags/size framing of its own.
    /// </summary>
    private static Dictionary<string, (long Start, long End)> WalkSrrRarFileSections(string srrPath)
    {
        using FileStream fs = new(srrPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using BinaryReader reader = new(fs);

        Dictionary<string, (long Start, long End)> sections = new(StringComparer.Ordinal);
        string? currentName = null;
        long currentStart = 0;
        bool expectMarker = false;

        while (fs.Position < fs.Length)
        {
            if (expectMarker)
            {
                fs.Position += RARUtils.RAR4Marker.Length;
                expectMarker = false;
                continue;
            }

            long blockStart = fs.Position;
            reader.ReadUInt16(); // CRC, not validated here
            byte blockType = reader.ReadByte();
            ushort flags = reader.ReadUInt16();
            ushort headerSize = reader.ReadUInt16();

            bool isSrrBlock = blockType is (byte)SRRBlockType.Header or (byte)SRRBlockType.StoredFile
                or (byte)SRRBlockType.OSOHash or (byte)SRRBlockType.RARPadding or (byte)SRRBlockType.RARFile;

            uint addSize = 0;
            if (isSrrBlock && ((flags & (ushort)SRRBlockFlags.LongBlock) != 0 || blockType == (byte)SRRBlockType.StoredFile))
            {
                addSize = reader.ReadUInt32();
            }

            if (isSrrBlock)
            {
                if (currentName != null)
                {
                    sections[currentName] = (currentStart, blockStart);
                    currentName = null;
                }

                if (blockType == (byte)SRRBlockType.RARFile)
                {
                    ushort nameLen = reader.ReadUInt16();
                    currentName = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
                    currentStart = blockStart + headerSize + addSize;
                    expectMarker = true;
                }

                fs.Position = blockStart + headerSize + addSize;
            }
            else
            {
                fs.Position = blockStart;
                var rarReader = new RARHeaderReader(fs);
                RARBlockReadResult? block = rarReader.ReadBlock()
                    ?? throw new InvalidDataException($"Malformed embedded RAR header at SRR offset {blockStart}.");
                fs.Position = block.BlockPosition + block.HeaderSize;
            }
        }

        if (currentName != null)
        {
            sections[currentName] = (currentStart, fs.Length);
        }

        return sections;
    }

    /// <summary>
    /// Asserts <paramref name="pieces"/> (one archived file's pieces, in order) carry the correct
    /// split flags: the first piece never has SplitBefore, the last never has SplitAfter, and every
    /// piece in between has both.
    /// </summary>
    private static void AssertSplitFlagsConsistent(IReadOnlyList<WalkedPiece> pieces)
    {
        Assert.True(pieces.Count > 0);
        Assert.False(pieces[0].SplitBefore, "The first piece of a file must not have SplitBefore.");
        Assert.False(pieces[^1].SplitAfter, "The last piece of a file must not have SplitAfter.");

        for (int i = 1; i < pieces.Count; i++)
        {
            Assert.True(pieces[i].SplitBefore, $"Piece {i} continues a split file and must have SplitBefore.");
        }

        for (int i = 0; i < pieces.Count - 1; i++)
        {
            Assert.True(pieces[i].SplitAfter, $"Piece {i} is not the file's last piece and must have SplitAfter.");
        }
    }

    /// <summary>One FileHeader block's parsed split flags plus its actual payload bytes read back from disk.</summary>
    private sealed record WalkedPiece(
        string FileName, bool SplitBefore, bool SplitAfter, uint AddSize, uint FileCrc, byte[] Payload);

    /// <summary>
    /// One volume file's walk result: whether it reached ArchiveHeader/EndArchive cleanly with no
    /// trailing bytes, its file pieces, and its header-only bytes (everything except payload
    /// regions) for byte-identity comparison against an SRR's embedded headers.
    /// </summary>
    private sealed record WalkedVolume(
        string Path, long FileLength, bool SawArchiveHeader, bool SawEndArchive, bool CleanEof,
        IReadOnlyList<WalkedPiece> Pieces, byte[] HeaderOnlyBytes);
}
