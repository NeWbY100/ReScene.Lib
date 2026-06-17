using ReScene.Core.Comparison;
using ReScene.Hex;
using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Covers the non-MKV core of <see cref="FileComparer"/>: SRR archive/file comparison, the bit-exact
/// <see cref="FileComparer.BlockDataMatches"/> payload comparator, and the <see cref="FileComparer.Compare"/>
/// dispatcher's null / type-mismatch behavior. The MKV path is exercised separately in MKVFileDataTests.
/// </summary>
public class FileComparerCoreTests
{
    /// <summary>
    /// In-memory <see cref="IHexDataSource"/> backed by a byte array. Read clamps to the available range
    /// so it faithfully reports a short read when asked past the end (the comparator must treat that as a
    /// mismatch). <paramref name="maxPerRead"/> lets a test force a partial mid-range read.
    /// </summary>
    private sealed class ByteArrayDataSource(byte[] bytes, int maxPerRead = int.MaxValue) : IHexDataSource
    {
        public long Length => bytes.Length;

        public int Read(long position, byte[] buffer, int offset, int count)
        {
            if (position < 0 || position >= bytes.Length)
            {
                return 0;
            }

            int wanted = Math.Min(count, maxPerRead);
            int available = (int)Math.Min(wanted, bytes.Length - position);
            Array.Copy(bytes, position, buffer, offset, available);
            return available;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// Builds an SRRFile with a single archived file (plus optional CRC) and the archive-level
    /// properties under test, so each test can vary exactly one input.
    /// </summary>
    private static SRRFile SrrWithArchivedFile(
        string fileName,
        string? crc = null,
        int? rarVersion = null,
        int? compressionMethod = null,
        bool? isSolid = null)
    {
        var srr = new SRRFile
        {
            ArchivedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fileName },
            ArchivedFileCrcs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            RARVersion = rarVersion,
            CompressionMethod = compressionMethod,
            IsSolidArchive = isSolid,
        };

        if (crc is not null)
        {
            srr.ArchivedFileCrcs[fileName] = crc;
        }

        return srr;
    }

    // ---- CompareSRRFiles --------------------------------------------------

    [Fact]
    public void CompareSRRFiles_IdenticalFiles_ReportsNoDifferences()
    {
        // Two structurally identical SRR snapshots must produce an empty result, or the UI can never
        // declare two SRRs equal.
        SRRFile left = SrrWithArchivedFile("disc.r00", crc: "ABCDEF12", rarVersion: 29, isSolid: false);
        SRRFile right = SrrWithArchivedFile("disc.r00", crc: "ABCDEF12", rarVersion: 29, isSolid: false);
        var result = new CompareResult();

        FileComparer.CompareSRRFiles(left, right, result);

        Assert.Empty(result.ArchiveDifferences);
        Assert.Empty(result.FileDifferences);
        Assert.Empty(result.StoredFileDifferences);
        Assert.Equal(0, result.TotalDifferences);
    }

    [Fact]
    public void CompareSRRFiles_SameFileDifferentCrc_ReportsModifiedWithCrcPropertyDifference()
    {
        // The same archived entry on both sides but with a changed CRC is the canonical "this volume
        // was rebuilt differently" case: one Modified FileDifference carrying a CRC PropertyDifference.
        SRRFile left = SrrWithArchivedFile("disc.r00", crc: "AAAAAAAA");
        SRRFile right = SrrWithArchivedFile("disc.r00", crc: "BBBBBBBB");
        var result = new CompareResult();

        FileComparer.CompareSRRFiles(left, right, result);

        FileDifference diff = Assert.Single(result.FileDifferences);
        Assert.Equal("disc.r00", diff.FileName);
        Assert.Equal(DifferenceType.Modified, diff.Type);

        PropertyDifference crc = Assert.Single(diff.PropertyDifferences);
        Assert.Equal("CRC", crc.PropertyName);
        Assert.Equal("AAAAAAAA", crc.LeftValue);
        Assert.Equal("BBBBBBBB", crc.RightValue);
    }

    [Fact]
    public void CompareSRRFiles_FilePresentOnlyOnLeft_ReportsRemoved()
    {
        // An archived file in the left SRR but absent from the right is Removed (left is the original).
        SRRFile left = SrrWithArchivedFile("only-left.rar", crc: "11111111");
        SRRFile right = new()
        {
            ArchivedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        var result = new CompareResult();

        FileComparer.CompareSRRFiles(left, right, result);

        // Count mismatch (1 vs 0) surfaces as an archive-level diff; the file itself is Removed.
        FileDifference diff = Assert.Single(result.FileDifferences);
        Assert.Equal("only-left.rar", diff.FileName);
        Assert.Equal(DifferenceType.Removed, diff.Type);
        Assert.Empty(diff.PropertyDifferences);
    }

    [Fact]
    public void CompareSRRFiles_FilePresentOnlyOnRight_ReportsAdded()
    {
        // The mirror of the Removed case: a file only on the right is Added.
        SRRFile left = new()
        {
            ArchivedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        SRRFile right = SrrWithArchivedFile("only-right.rar", crc: "22222222");
        var result = new CompareResult();

        FileComparer.CompareSRRFiles(left, right, result);

        FileDifference diff = Assert.Single(result.FileDifferences);
        Assert.Equal("only-right.rar", diff.FileName);
        Assert.Equal(DifferenceType.Added, diff.Type);
    }

    [Fact]
    public void CompareSRRFiles_DifferingArchiveProperties_ReportsMatchingArchiveDifferences()
    {
        // Archive-level properties (RAR version, compression method, solid flag) feed the archive
        // differences list with human-formatted Left/Right values.
        SRRFile left = SrrWithArchivedFile("a.rar", crc: "DEADBEEF",
            rarVersion: 29, compressionMethod: 0x33, isSolid: false);
        SRRFile right = SrrWithArchivedFile("a.rar", crc: "DEADBEEF",
            rarVersion: 50, compressionMethod: 0x35, isSolid: true);
        var result = new CompareResult();

        FileComparer.CompareSRRFiles(left, right, result);

        // No per-file diff: the archived entry and its CRC are identical.
        Assert.Empty(result.FileDifferences);

        PropertyDifference version = Assert.Single(result.ArchiveDifferences, d => d.PropertyName == "RAR Version");
        Assert.Equal("RAR 2.9", version.LeftValue);
        Assert.Equal("RAR 5.0", version.RightValue);

        PropertyDifference method = Assert.Single(result.ArchiveDifferences, d => d.PropertyName == "Compression Method");
        Assert.Equal("Normal", method.LeftValue);
        Assert.Equal("Best", method.RightValue);

        PropertyDifference solid = Assert.Single(result.ArchiveDifferences, d => d.PropertyName == "Solid Archive");
        Assert.Equal("No", solid.LeftValue);
        Assert.Equal("Yes", solid.RightValue);
    }

    [Fact]
    public void CompareSRRFiles_StoredFileOnlyOnOneSide_ReportsStoredFileDifference()
    {
        // Stored (non-RAR) files like the .sfv/.nfo are tracked separately in StoredFileDifferences;
        // a backslash path on one side must normalize to the same key as a forward-slash path.
        var left = new SRRFile();
        left._storedFiles.Add(new SRRStoredFileBlock { FileName = "Sample\\release.nfo" });

        var right = new SRRFile();
        right._storedFiles.Add(new SRRStoredFileBlock { FileName = "Sample/release.nfo" });
        right._storedFiles.Add(new SRRStoredFileBlock { FileName = "release.sfv" });

        var result = new CompareResult();

        FileComparer.CompareSRRFiles(left, right, result);

        // The .nfo matches across the separator normalization, so only release.sfv is Added.
        FileDifference stored = Assert.Single(result.StoredFileDifferences);
        Assert.Equal("release.sfv", stored.FileName);
        Assert.Equal(DifferenceType.Added, stored.Type);
    }

    // ---- BlockDataMatches -------------------------------------------------

    [Fact]
    public void BlockDataMatches_ZeroLength_ReturnsTrueRegardlessOfOffsets()
    {
        // Length 0 short-circuits to true before any bounds or offset checks run.
        var src = new ByteArrayDataSource([1, 2, 3]);

        Assert.True(FileComparer.BlockDataMatches(src, 999, src, -50, 0));
    }

    [Fact]
    public void BlockDataMatches_IdenticalLargeBuffers_ReturnsTrue()
    {
        // 130 KB exceeds the 64 KB internal chunk size, so this drives the multi-chunk loop to
        // completion. Identical content must compare equal.
        byte[] data = new byte[130 * 1024];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 31 + 7);
        }

        var left = new ByteArrayDataSource(data);
        var right = new ByteArrayDataSource((byte[])data.Clone());

        Assert.True(FileComparer.BlockDataMatches(left, 0, right, 0, data.Length));
    }

    [Fact]
    public void BlockDataMatches_LargeBuffersDifferingInLastByte_ReturnsFalse()
    {
        // A single differing byte in the final chunk must still be caught — proves the loop compares
        // every chunk, not just the first.
        byte[] data = new byte[130 * 1024];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i & 0xFF);
        }

        byte[] modified = (byte[])data.Clone();
        modified[^1] ^= 0xFF;

        var left = new ByteArrayDataSource(data);
        var right = new ByteArrayDataSource(modified);

        Assert.False(FileComparer.BlockDataMatches(left, 0, right, 0, data.Length));
    }

    [Fact]
    public void BlockDataMatches_NegativeOffset_ReturnsFalse()
    {
        // A negative offset is invalid and must fail fast rather than reading out of bounds.
        var src = new ByteArrayDataSource([0, 1, 2, 3, 4, 5, 6, 7]);

        Assert.False(FileComparer.BlockDataMatches(src, -1, src, 0, 4));
    }

    [Fact]
    public void BlockDataMatches_RangeOnePastLength_ReturnsFalse()
    {
        // offset + length == Length + 1 overruns the source by one byte and must be rejected.
        byte[] data = [0, 1, 2, 3];
        var src = new ByteArrayDataSource(data);

        Assert.False(FileComparer.BlockDataMatches(src, 1, src, 0, data.Length));
    }

    [Fact]
    public void BlockDataMatches_ShortReadMidRange_ReturnsFalse()
    {
        // The bounds check passes, but a source that returns fewer bytes than requested mid-stream
        // (e.g. a truncated/locked file) must be treated as a mismatch, not silently truncate.
        byte[] data = new byte[100 * 1024];
        Array.Fill(data, (byte)0x5A);

        // Right source caps each read well under the 64 KB chunk, forcing a short read on the first chunk.
        var left = new ByteArrayDataSource(data);
        var right = new ByteArrayDataSource((byte[])data.Clone(), maxPerRead: 1024);

        Assert.False(FileComparer.BlockDataMatches(left, 0, right, 0, data.Length));
    }

    // ---- Compare dispatcher ----------------------------------------------

    [Fact]
    public void Compare_BothNull_ReportsFileTypeDifferenceWithUnknownNames()
    {
        // Null inputs match no specific branch, so the dispatcher falls through to a "File Type"
        // archive difference reporting both sides as Unknown.
        CompareResult result = FileComparer.Compare(null, null);

        PropertyDifference diff = Assert.Single(result.ArchiveDifferences);
        Assert.Equal("File Type", diff.PropertyName);
        Assert.Equal("Unknown", diff.LeftValue);
        Assert.Equal("Unknown", diff.RightValue);
        Assert.Empty(result.FileDifferences);
    }

    [Fact]
    public void Compare_TypeMismatch_ReportsFileTypeDifference()
    {
        // An SRR on one side and a RAR on the other don't share a comparison branch, so the fallback
        // surfaces the differing file-type names rather than attempting a cross-type compare.
        var leftSrr = new SRRFileData { SRRFile = SrrWithArchivedFile("a.rar") };
        var rightRar = new RARFileData { IsRAR5 = true };

        CompareResult result = FileComparer.Compare(leftSrr, rightRar);

        PropertyDifference diff = Assert.Single(result.ArchiveDifferences);
        Assert.Equal("File Type", diff.PropertyName);
        Assert.Equal("SRR File", diff.LeftValue);
        Assert.Equal("RAR 5.x", diff.RightValue);
    }

    [Fact]
    public void Compare_TwoSrrFileData_DispatchesToSrrComparison()
    {
        // A matched SRRFileData pair must route through CompareSRRFiles (not the fallback), so a CRC
        // change shows up as a per-file Modified difference rather than a File Type difference.
        var left = new SRRFileData { SRRFile = SrrWithArchivedFile("disc.r00", crc: "0000FFFF") };
        var right = new SRRFileData { SRRFile = SrrWithArchivedFile("disc.r00", crc: "FFFF0000") };

        CompareResult result = FileComparer.Compare(left, right);

        Assert.DoesNotContain(result.ArchiveDifferences, d => d.PropertyName == "File Type");
        FileDifference file = Assert.Single(result.FileDifferences);
        Assert.Equal(DifferenceType.Modified, file.Type);
        Assert.Equal("CRC", Assert.Single(file.PropertyDifferences).PropertyName);
    }
}
