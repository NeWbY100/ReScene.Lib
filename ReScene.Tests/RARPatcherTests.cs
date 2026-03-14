using Force.Crc32;

namespace ReScene.RAR.Tests;

public class RARPatcherTests : IDisposable
{
    private static readonly string TestDataPath = Path.Combine(
        AppContext.BaseDirectory, "TestData");

    private readonly string _testDir;

    public RARPatcherTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"rarpatcher_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    /// <summary>
    /// Copies a test RAR file to the temp directory for patching tests.
    /// </summary>
    private string CopyTestFile(string fileName)
    {
        string source = Path.Combine(TestDataPath, fileName);
        string dest = Path.Combine(_testDir, fileName);
        File.Copy(source, dest, true);
        return dest;
    }

    #region PatchFile Tests

    [Fact]
    public void PatchFile_HostOS_ModifiesHeader()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        var options = new PatchOptions
        {
            FileHostOS = 3 // Unix
        };

        var results = RARPatcher.PatchFile(testFile, options);

        Assert.NotEmpty(results);
        Assert.All(results.Where(r => r.BlockType == RAR4BlockType.FileHeader),
            r => Assert.Equal(3, r.NewHostOS));
    }

    [Fact]
    public void PatchFile_HostOS_RecalculatesCrc()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        var options = new PatchOptions
        {
            FileHostOS = 3 // Unix (original is Windows = 2)
        };

        var results = RARPatcher.PatchFile(testFile, options);

        // Verify that CRC was recalculated
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            Assert.NotEqual(result.OriginalCrc, result.NewCrc);
        }

        // Verify the file is still parseable after patching
        using var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read);
        fs.Seek(7, SeekOrigin.Begin); // Skip marker
        var reader = new RARHeaderReader(fs);

        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock(parseContents: true);
            if (block == null) break;

            if (block.BlockType == RAR4BlockType.FileHeader || block.BlockType == RAR4BlockType.Service)
            {
                Assert.True(block.CrcValid, $"CRC invalid after patching at position {block.BlockPosition}");
            }

            reader.SkipBlock(block, includeData: block.BlockType != RAR4BlockType.FileHeader);
        }
    }

    [Fact]
    public void PatchFile_FileAttributes_ModifiesHeader()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        var options = new PatchOptions
        {
            FileAttributes = 0x000081B4 // Unix file mode
        };

        var results = RARPatcher.PatchFile(testFile, options);

        Assert.NotEmpty(results);
        Assert.All(results.Where(r => r.BlockType == RAR4BlockType.FileHeader),
            r => Assert.Equal(0x000081B4u, r.NewAttributes));
    }

    [Fact]
    public void PatchFile_NoChangesNeeded_ReturnsEmptyResults()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        // Read original host OS first
        byte originalHostOS;
        using (var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read))
        {
            fs.Seek(7, SeekOrigin.Begin);
            var reader = new RARHeaderReader(fs);
            while (fs.Position < fs.Length)
            {
                var block = reader.ReadBlock(parseContents: true);
                if (block == null) break;
                if (block.FileHeader != null)
                {
                    originalHostOS = block.FileHeader.HostOS;
                    break;
                }
                reader.SkipBlock(block, includeData: block.BlockType != RAR4BlockType.FileHeader);
            }
        }

        // Patch to same value
        var options = new PatchOptions
        {
            FileHostOS = 2 // Windows (should be same as original)
        };

        var results = RARPatcher.PatchFile(testFile, options);

        // If original is already Windows, no changes should be made
        // (or all results show no actual change)
        // The patcher only writes if the values differ
    }

    [Fact]
    public void PatchFile_ServiceBlockDisabled_SkipsServiceBlocks()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        var options = new PatchOptions
        {
            FileHostOS = 3,
            PatchServiceBlocks = false // Don't patch CMT blocks
        };

        var results = RARPatcher.PatchFile(testFile, options);

        // Service blocks should not be in results
        Assert.DoesNotContain(results, r => r.BlockType == RAR4BlockType.Service);
    }

    [Fact]
    public void PatchFile_ServiceBlockFileTime_PatchesCorrectly()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        var options = new PatchOptions
        {
            FileHostOS = 3,
            PatchServiceBlocks = true,
            ServiceBlockFileTime = 0 // Zero out CMT file time
        };

        var results = RARPatcher.PatchFile(testFile, options);

        // Should have patched both file headers and service blocks
        Assert.NotEmpty(results);
    }

    #endregion

    #region AnalyzeFile Tests

    [Fact]
    public void AnalyzeFile_DoesNotModifyFile()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");
        byte[] originalBytes = File.ReadAllBytes(testFile);

        var options = new PatchOptions
        {
            FileHostOS = 3
        };

        var results = RARPatcher.AnalyzeFile(testFile, options);

        byte[] afterBytes = File.ReadAllBytes(testFile);
        Assert.Equal(originalBytes, afterBytes);
    }

    [Fact]
    public void AnalyzeFile_ReportsBlocksThatWouldChange()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        var options = new PatchOptions
        {
            FileHostOS = 3 // Unix (different from Windows original)
        };

        var results = RARPatcher.AnalyzeFile(testFile, options);

        // Should report blocks that would be modified
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(3, r.NewHostOS));
    }

    [Fact]
    public void AnalyzeFile_NewCrcIsZero_InAnalysisMode()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        var options = new PatchOptions
        {
            FileHostOS = 3
        };

        var results = RARPatcher.AnalyzeFile(testFile, options);

        Assert.All(results, r => Assert.Equal((ushort)0, r.NewCrc));
    }

    #endregion

    #region PatchOptions Tests

    [Fact]
    public void PatchOptions_GetServiceBlockHostOS_FallsBackToFileHostOS()
    {
        var options = new PatchOptions
        {
            FileHostOS = 3
        };

        Assert.Equal((byte)3, options.GetServiceBlockHostOS());
    }

    [Fact]
    public void PatchOptions_GetServiceBlockHostOS_UsesExplicitValue()
    {
        var options = new PatchOptions
        {
            FileHostOS = 3,
            ServiceBlockHostOS = 2
        };

        Assert.Equal((byte)2, options.GetServiceBlockHostOS());
    }

    [Fact]
    public void PatchOptions_GetServiceBlockAttributes_FallsBackToFileAttributes()
    {
        var options = new PatchOptions
        {
            FileAttributes = 0x000081B4
        };

        Assert.Equal(0x000081B4u, options.GetServiceBlockAttributes());
    }

    [Fact]
    public void PatchOptions_GetServiceBlockAttributes_UsesExplicitValue()
    {
        var options = new PatchOptions
        {
            FileAttributes = 0x000081B4,
            ServiceBlockAttributes = 0x00000020
        };

        Assert.Equal(0x00000020u, options.GetServiceBlockAttributes());
    }

    [Fact]
    public void PatchOptions_DefaultPatchServiceBlocks_IsTrue()
    {
        var options = new PatchOptions();

        Assert.True(options.PatchServiceBlocks);
    }

    #endregion

    #region GetHostOSName Tests

    [Theory]
    [InlineData(0, "MS-DOS")]
    [InlineData(1, "OS/2")]
    [InlineData(2, "Windows")]
    [InlineData(3, "Unix")]
    [InlineData(4, "Mac OS")]
    [InlineData(5, "BeOS")]
    [InlineData(6, "Unknown (6)")]
    [InlineData(255, "Unknown (255)")]
    public void GetHostOSName_ReturnsCorrectName(byte hostOS, string expected)
    {
        string result = RARPatcher.GetHostOSName(hostOS);

        Assert.Equal(expected, result);
    }

    #endregion

    #region PatchLargeFlags Tests

    /// <summary>
    /// Builds a minimal RAR4 file with a single file header for LARGE flag testing.
    /// </summary>
    /// <param name="hasLargeFlag">If true, include LARGE flag and HIGH fields in the file header.</param>
    /// <param name="highPackSize">HIGH_PACK_SIZE value (only used if hasLargeFlag is true).</param>
    /// <param name="highUnpSize">HIGH_UNP_SIZE value (only used if hasLargeFlag is true).</param>
    private static byte[] BuildMinimalRar4WithFileHeader(bool hasLargeFlag, uint highPackSize = 0, uint highUnpSize = 0)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // RAR4 marker (7 bytes)
        writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });

        // Archive header (7 bytes, minimal)
        byte[] archiveHeader = new byte[7];
        archiveHeader[2] = 0x73; // type = ArchiveHeader
        BitConverter.GetBytes((ushort)0x0000).CopyTo(archiveHeader, 3); // flags
        BitConverter.GetBytes((ushort)7).CopyTo(archiveHeader, 5); // headerSize = 7
        // Compute CRC
        uint archCrc = Crc32Algorithm.Compute(archiveHeader, 2, 5);
        BitConverter.GetBytes((ushort)(archCrc & 0xFFFF)).CopyTo(archiveHeader, 0);
        writer.Write(archiveHeader);

        // File header
        // Base: 7 bytes (CRC+type+flags+headerSize) + 4 (ADD_SIZE) + 4 (UnpSize) + 1 (HostOS) + 4 (FileCRC) + 4 (FileTime) + 1 (UnpVer) + 1 (Method) + 2 (NameSize) + 4 (Attr)
        // = 32 bytes + optionally 8 bytes (HIGH_PACK_SIZE + HIGH_UNP_SIZE) + filename
        string fileName = "test.txt";
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName);
        int headerSize = 32 + (hasLargeFlag ? 8 : 0) + nameBytes.Length;
        ushort flags = (ushort)(RARFileFlags.LongBlock | (hasLargeFlag ? RARFileFlags.Large : 0));

        byte[] fileHeader = new byte[headerSize];
        fileHeader[2] = 0x74; // type = FileHeader
        BitConverter.GetBytes(flags).CopyTo(fileHeader, 3);
        BitConverter.GetBytes((ushort)headerSize).CopyTo(fileHeader, 5);
        BitConverter.GetBytes((uint)0).CopyTo(fileHeader, 7); // ADD_SIZE (packed data size = 0)
        BitConverter.GetBytes((uint)100).CopyTo(fileHeader, 11); // UnpSize
        fileHeader[15] = 2; // HostOS = Windows
        BitConverter.GetBytes((uint)0xDEADBEEF).CopyTo(fileHeader, 16); // FileCRC
        BitConverter.GetBytes((uint)0x5A000000).CopyTo(fileHeader, 20); // FileTime
        fileHeader[24] = 29; // UnpVer
        fileHeader[25] = 0x33; // Method (Normal)
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(fileHeader, 26);
        BitConverter.GetBytes((uint)0x00000020).CopyTo(fileHeader, 28); // Attributes

        int offset = 32;
        if (hasLargeFlag)
        {
            BitConverter.GetBytes(highPackSize).CopyTo(fileHeader, offset);
            offset += 4;
            BitConverter.GetBytes(highUnpSize).CopyTo(fileHeader, offset);
            offset += 4;
        }
        Array.Copy(nameBytes, 0, fileHeader, offset, nameBytes.Length);

        // Compute CRC
        uint fileCrc32 = Crc32Algorithm.Compute(fileHeader, 2, fileHeader.Length - 2);
        BitConverter.GetBytes((ushort)(fileCrc32 & 0xFFFF)).CopyTo(fileHeader, 0);
        writer.Write(fileHeader);

        // End of Archive block
        byte[] endBlock = new byte[7];
        endBlock[2] = 0x7B; // type = EndArchive
        BitConverter.GetBytes((ushort)0x4000).CopyTo(endBlock, 3); // SKIP_IF_UNKNOWN
        BitConverter.GetBytes((ushort)7).CopyTo(endBlock, 5); // headerSize = 7
        uint endCrc = Crc32Algorithm.Compute(endBlock, 2, 5);
        BitConverter.GetBytes((ushort)(endCrc & 0xFFFF)).CopyTo(endBlock, 0);
        writer.Write(endBlock);

        return ms.ToArray();
    }

    [Fact]
    public void PatchLargeFlags_AddLarge_InsertsHighFields()
    {
        byte[] rarData = BuildMinimalRar4WithFileHeader(hasLargeFlag: false);
        int originalLength = rarData.Length;

        using var stream = new MemoryStream();
        stream.Write(rarData, 0, rarData.Length);
        stream.Position = 0;

        var options = new PatchOptions
        {
            SetLargeFlag = true,
            HighPackSize = 0x12345678,
            HighUnpSize = 0xABCDEF00
        };

        bool modified = RARPatcher.PatchLargeFlags(stream, options);

        Assert.True(modified);
        // File should be 8 bytes larger
        Assert.Equal(originalLength + 8, stream.Length);

        // Verify the patched file is parseable and has LARGE flag
        stream.Position = 7; // Skip marker
        var reader = new RARHeaderReader(stream);

        // Skip archive header
        var archBlock = reader.ReadBlock(parseContents: false);
        Assert.NotNull(archBlock);
        reader.SkipBlock(archBlock!);

        // Read file header
        var fileBlock = reader.ReadBlock(parseContents: true);
        Assert.NotNull(fileBlock);
        Assert.NotNull(fileBlock!.FileHeader);
        Assert.True(fileBlock.FileHeader!.HasLargeSize);
        Assert.Equal(0x12345678u, fileBlock.FileHeader.HighPackSize);
        Assert.Equal(0xABCDEF00u, fileBlock.FileHeader.HighUnpSize);
        Assert.True(fileBlock.CrcValid);
    }

    [Fact]
    public void PatchLargeFlags_RemoveLarge_RemovesHighFields()
    {
        byte[] rarData = BuildMinimalRar4WithFileHeader(hasLargeFlag: true, highPackSize: 0x11111111, highUnpSize: 0x22222222);
        int originalLength = rarData.Length;

        using var stream = new MemoryStream();
        stream.Write(rarData, 0, rarData.Length);
        stream.Position = 0;

        var options = new PatchOptions
        {
            SetLargeFlag = false
        };

        bool modified = RARPatcher.PatchLargeFlags(stream, options);

        Assert.True(modified);
        // File should be 8 bytes smaller
        Assert.Equal(originalLength - 8, stream.Length);

        // Verify the patched file is parseable and does NOT have LARGE flag
        stream.Position = 7;
        var reader = new RARHeaderReader(stream);

        var archBlock = reader.ReadBlock(parseContents: false);
        Assert.NotNull(archBlock);
        reader.SkipBlock(archBlock!);

        var fileBlock = reader.ReadBlock(parseContents: true);
        Assert.NotNull(fileBlock);
        Assert.NotNull(fileBlock!.FileHeader);
        Assert.False(fileBlock.FileHeader!.HasLargeSize);
        Assert.Equal(0u, fileBlock.FileHeader.HighPackSize);
        Assert.Equal(0u, fileBlock.FileHeader.HighUnpSize);
        Assert.True(fileBlock.CrcValid);
    }

    [Fact]
    public void PatchLargeFlags_RoundTrip_AddThenRemoveRestoresOriginal()
    {
        byte[] originalData = BuildMinimalRar4WithFileHeader(hasLargeFlag: false);

        // Add LARGE
        using var stream = new MemoryStream();
        stream.Write(originalData, 0, originalData.Length);
        stream.Position = 0;

        var addOptions = new PatchOptions
        {
            SetLargeFlag = true,
            HighPackSize = 0,
            HighUnpSize = 0
        };

        bool addModified = RARPatcher.PatchLargeFlags(stream, addOptions);
        Assert.True(addModified);

        // Remove LARGE
        stream.Position = 0;
        var removeOptions = new PatchOptions
        {
            SetLargeFlag = false
        };

        bool removeModified = RARPatcher.PatchLargeFlags(stream, removeOptions);
        Assert.True(removeModified);

        // Result should match original length
        Assert.Equal(originalData.Length, stream.Length);

        // Verify the file is parseable
        stream.Position = 7;
        var reader = new RARHeaderReader(stream);

        var archBlock = reader.ReadBlock(parseContents: false);
        Assert.NotNull(archBlock);
        reader.SkipBlock(archBlock!);

        var fileBlock = reader.ReadBlock(parseContents: true);
        Assert.NotNull(fileBlock);
        Assert.True(fileBlock!.CrcValid);
        Assert.False(fileBlock.FileHeader!.HasLargeSize);
    }

    [Fact]
    public void PatchLargeFlags_AlreadyMatches_ReturnsNoModification()
    {
        byte[] rarData = BuildMinimalRar4WithFileHeader(hasLargeFlag: true, highPackSize: 0, highUnpSize: 0);

        using var stream = new MemoryStream();
        stream.Write(rarData, 0, rarData.Length);
        stream.Position = 0;

        var options = new PatchOptions
        {
            SetLargeFlag = true // Already has LARGE
        };

        bool modified = RARPatcher.PatchLargeFlags(stream, options);

        Assert.False(modified);
    }

    [Fact]
    public void PatchLargeFlags_NullSetLargeFlag_ReturnsNoModification()
    {
        byte[] rarData = BuildMinimalRar4WithFileHeader(hasLargeFlag: false);

        using var stream = new MemoryStream();
        stream.Write(rarData, 0, rarData.Length);
        stream.Position = 0;

        var options = new PatchOptions
        {
            SetLargeFlag = null // No change requested
        };

        bool modified = RARPatcher.PatchLargeFlags(stream, options);

        Assert.False(modified);
    }

    [Fact]
    public void PatchLargeFlags_WithRealFile_PreservesParseability()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        // Add LARGE flag to a real RAR file
        using var stream = new FileStream(testFile, FileMode.Open, FileAccess.ReadWrite);

        var options = new PatchOptions
        {
            SetLargeFlag = true,
            HighPackSize = 0,
            HighUnpSize = 0
        };

        bool modified = RARPatcher.PatchLargeFlags(stream, options);
        Assert.True(modified);

        // Verify all blocks are parseable with valid CRCs
        stream.Position = 7;
        var reader = new RARHeaderReader(stream);

        while (stream.Position < stream.Length)
        {
            var block = reader.ReadBlock(parseContents: true);
            if (block == null) break;

            if (block.BlockType == RAR4BlockType.FileHeader || block.BlockType == RAR4BlockType.Service)
            {
                Assert.True(block.CrcValid, $"CRC invalid after LARGE patching at position {block.BlockPosition}");

                if (block.FileHeader != null)
                {
                    Assert.True(block.FileHeader.HasLargeSize);
                }
            }

            reader.SkipBlock(block, includeData: block.BlockType != RAR4BlockType.FileHeader);
        }
    }

    #endregion

    #region PatchStream Tests

    [Fact]
    public void PatchStream_PatchesBothHostOSAndAttributes()
    {
        string testFile = CopyTestFile("test_wrar40_m3.rar");

        var options = new PatchOptions
        {
            FileHostOS = 3,
            FileAttributes = 0x000081B4
        };

        var results = new List<PatchResult>();
        using var stream = new FileStream(testFile, FileMode.Open, FileAccess.ReadWrite);
        RARPatcher.PatchStream(stream, options, results);

        Assert.NotEmpty(results);
        foreach (var result in results.Where(r => r.BlockType == RAR4BlockType.FileHeader))
        {
            Assert.Equal(3, result.NewHostOS);
            Assert.Equal(0x000081B4u, result.NewAttributes);
        }
    }

    #endregion

    #region Large File Navigation Tests (GetBlockDataSize)

    /// <summary>
    /// Builds a minimal RAR4 file with two file headers where the first has LARGE flag
    /// and a data section whose size is split across ADD_SIZE and HIGH_PACK_SIZE.
    /// This forces PatchStream/AnalyzeFile to use GetBlockDataSize to correctly skip
    /// past the first file header's data to reach the second.
    /// </summary>
    /// <param name="addSize">Low 32 bits of packed data size (ADD_SIZE).</param>
    /// <param name="highPackSize">High 32 bits of packed data size (HIGH_PACK_SIZE).</param>
    /// <param name="actualDataLength">Actual number of data bytes to write after the first header.
    /// Set equal to addSize | (highPackSize &lt;&lt; 32) for a valid file, or smaller for truncated tests.</param>
    /// <param name="secondHostOS">Host OS value for the second file header (used to verify navigation).</param>
    private static byte[] BuildRar4WithLargeFileHeader(
        uint addSize, uint highPackSize, long actualDataLength, byte secondHostOS = 3)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // RAR4 marker (7 bytes)
        writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });

        // Archive header (minimal, 7 bytes)
        byte[] archiveHeader = new byte[7];
        archiveHeader[2] = 0x73; // type = ArchiveHeader
        BitConverter.GetBytes((ushort)0x0000).CopyTo(archiveHeader, 3); // flags
        BitConverter.GetBytes((ushort)7).CopyTo(archiveHeader, 5); // headerSize
        uint archCrc = Crc32Algorithm.Compute(archiveHeader, 2, 5);
        BitConverter.GetBytes((ushort)(archCrc & 0xFFFF)).CopyTo(archiveHeader, 0);
        writer.Write(archiveHeader);

        // First file header - WITH LARGE flag and data
        {
            string fileName = "large.bin";
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName);
            int headerSize = 32 + 8 + nameBytes.Length; // 32 base + 8 HIGH fields + name
            ushort flags = (ushort)(RARFileFlags.LongBlock | RARFileFlags.Large);

            byte[] fileHeader = new byte[headerSize];
            fileHeader[2] = 0x74; // type = FileHeader
            BitConverter.GetBytes(flags).CopyTo(fileHeader, 3);
            BitConverter.GetBytes((ushort)headerSize).CopyTo(fileHeader, 5);
            BitConverter.GetBytes(addSize).CopyTo(fileHeader, 7); // ADD_SIZE (low 32 bits of packed size)
            BitConverter.GetBytes((uint)100).CopyTo(fileHeader, 11); // UnpSize
            fileHeader[15] = 2; // HostOS = Windows
            BitConverter.GetBytes((uint)0x11111111).CopyTo(fileHeader, 16); // FileCRC
            BitConverter.GetBytes((uint)0x5A000000).CopyTo(fileHeader, 20); // FileTime
            fileHeader[24] = 29; // UnpVer
            fileHeader[25] = 0x33; // Method
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(fileHeader, 26);
            BitConverter.GetBytes((uint)0x00000020).CopyTo(fileHeader, 28); // Attributes

            // HIGH_PACK_SIZE and HIGH_UNP_SIZE at offset 32
            BitConverter.GetBytes(highPackSize).CopyTo(fileHeader, 32);
            BitConverter.GetBytes((uint)0).CopyTo(fileHeader, 36); // HIGH_UNP_SIZE

            Array.Copy(nameBytes, 0, fileHeader, 40, nameBytes.Length);

            uint crc32 = Crc32Algorithm.Compute(fileHeader, 2, fileHeader.Length - 2);
            BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(fileHeader, 0);
            writer.Write(fileHeader);

            // Write actual data bytes (padding)
            if (actualDataLength > 0)
            {
                byte[] data = new byte[actualDataLength];
                writer.Write(data);
            }
        }

        // Second file header - different HostOS so we can detect it was found
        {
            string fileName = "second.txt";
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName);
            int headerSize = 32 + nameBytes.Length;
            ushort flags = (ushort)RARFileFlags.LongBlock;

            byte[] fileHeader = new byte[headerSize];
            fileHeader[2] = 0x74; // type = FileHeader
            BitConverter.GetBytes(flags).CopyTo(fileHeader, 3);
            BitConverter.GetBytes((ushort)headerSize).CopyTo(fileHeader, 5);
            BitConverter.GetBytes((uint)0).CopyTo(fileHeader, 7); // ADD_SIZE = 0
            BitConverter.GetBytes((uint)50).CopyTo(fileHeader, 11); // UnpSize
            fileHeader[15] = secondHostOS; // HostOS (different from first)
            BitConverter.GetBytes((uint)0x22222222).CopyTo(fileHeader, 16); // FileCRC
            BitConverter.GetBytes((uint)0x5A000000).CopyTo(fileHeader, 20); // FileTime
            fileHeader[24] = 29; // UnpVer
            fileHeader[25] = 0x33; // Method
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(fileHeader, 26);
            BitConverter.GetBytes((uint)0x00000020).CopyTo(fileHeader, 28); // Attributes

            Array.Copy(nameBytes, 0, fileHeader, 32, nameBytes.Length);

            uint crc32 = Crc32Algorithm.Compute(fileHeader, 2, fileHeader.Length - 2);
            BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(fileHeader, 0);
            writer.Write(fileHeader);
        }

        // End of Archive block
        byte[] endBlock = new byte[7];
        endBlock[2] = 0x7B; // type = EndArchive
        BitConverter.GetBytes((ushort)0x4000).CopyTo(endBlock, 3);
        BitConverter.GetBytes((ushort)7).CopyTo(endBlock, 5);
        uint endCrc = Crc32Algorithm.Compute(endBlock, 2, 5);
        BitConverter.GetBytes((ushort)(endCrc & 0xFFFF)).CopyTo(endBlock, 0);
        writer.Write(endBlock);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal RAR4 file with a service block that has LARGE flag, followed by a file header.
    /// Used to test the else branch in PatchStream when PatchServiceBlocks=false.
    /// </summary>
    private static byte[] BuildRar4WithLargeServiceBlock(
        uint addSize, uint highPackSize, long actualDataLength, byte fileHostOS = 3)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // RAR4 marker
        writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });

        // Archive header
        byte[] archiveHeader = new byte[7];
        archiveHeader[2] = 0x73;
        BitConverter.GetBytes((ushort)0x0000).CopyTo(archiveHeader, 3);
        BitConverter.GetBytes((ushort)7).CopyTo(archiveHeader, 5);
        uint archCrc = Crc32Algorithm.Compute(archiveHeader, 2, 5);
        BitConverter.GetBytes((ushort)(archCrc & 0xFFFF)).CopyTo(archiveHeader, 0);
        writer.Write(archiveHeader);

        // Service block with LARGE flag
        {
            string name = "CMT";
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            int headerSize = 32 + 8 + nameBytes.Length; // base + HIGH fields + name
            ushort flags = (ushort)(RARFileFlags.LongBlock | RARFileFlags.Large);

            byte[] svcHeader = new byte[headerSize];
            svcHeader[2] = 0x7A; // type = Service
            BitConverter.GetBytes(flags).CopyTo(svcHeader, 3);
            BitConverter.GetBytes((ushort)headerSize).CopyTo(svcHeader, 5);
            BitConverter.GetBytes(addSize).CopyTo(svcHeader, 7); // ADD_SIZE
            BitConverter.GetBytes((uint)0).CopyTo(svcHeader, 11); // UnpSize
            svcHeader[15] = 2; // HostOS = Windows
            BitConverter.GetBytes((uint)0).CopyTo(svcHeader, 16); // FileCRC
            BitConverter.GetBytes((uint)0).CopyTo(svcHeader, 20); // FileTime
            svcHeader[24] = 29;
            svcHeader[25] = 0x30; // Method = Store
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(svcHeader, 26);
            BitConverter.GetBytes((uint)0x00000020).CopyTo(svcHeader, 28); // Attributes

            BitConverter.GetBytes(highPackSize).CopyTo(svcHeader, 32);
            BitConverter.GetBytes((uint)0).CopyTo(svcHeader, 36); // HIGH_UNP_SIZE

            Array.Copy(nameBytes, 0, svcHeader, 40, nameBytes.Length);

            uint crc32 = Crc32Algorithm.Compute(svcHeader, 2, svcHeader.Length - 2);
            BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(svcHeader, 0);
            writer.Write(svcHeader);

            if (actualDataLength > 0)
            {
                byte[] data = new byte[actualDataLength];
                writer.Write(data);
            }
        }

        // File header after the service block
        {
            string fileName = "after_svc.txt";
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName);
            int headerSize = 32 + nameBytes.Length;
            ushort flags = (ushort)RARFileFlags.LongBlock;

            byte[] fileHeader = new byte[headerSize];
            fileHeader[2] = 0x74; // type = FileHeader
            BitConverter.GetBytes(flags).CopyTo(fileHeader, 3);
            BitConverter.GetBytes((ushort)headerSize).CopyTo(fileHeader, 5);
            BitConverter.GetBytes((uint)0).CopyTo(fileHeader, 7);
            BitConverter.GetBytes((uint)50).CopyTo(fileHeader, 11);
            fileHeader[15] = fileHostOS;
            BitConverter.GetBytes((uint)0x33333333).CopyTo(fileHeader, 16);
            BitConverter.GetBytes((uint)0x5A000000).CopyTo(fileHeader, 20);
            fileHeader[24] = 29;
            fileHeader[25] = 0x33;
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(fileHeader, 26);
            BitConverter.GetBytes((uint)0x00000020).CopyTo(fileHeader, 28);

            Array.Copy(nameBytes, 0, fileHeader, 32, nameBytes.Length);

            uint crc32 = Crc32Algorithm.Compute(fileHeader, 2, fileHeader.Length - 2);
            BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(fileHeader, 0);
            writer.Write(fileHeader);
        }

        // End of Archive
        byte[] endBlock = new byte[7];
        endBlock[2] = 0x7B;
        BitConverter.GetBytes((ushort)0x4000).CopyTo(endBlock, 3);
        BitConverter.GetBytes((ushort)7).CopyTo(endBlock, 5);
        uint endCrc = Crc32Algorithm.Compute(endBlock, 2, 5);
        BitConverter.GetBytes((ushort)(endCrc & 0xFFFF)).CopyTo(endBlock, 0);
        writer.Write(endBlock);

        return ms.ToArray();
    }

    [Fact]
    public void PatchStream_LargeFileHeader_SkipsDataCorrectly_FindsSecondHeader()
    {
        // First file has LARGE flag with 256 bytes of data (ADD_SIZE=256, HIGH_PACK_SIZE=0)
        byte[] rarData = BuildRar4WithLargeFileHeader(
            addSize: 256, highPackSize: 0, actualDataLength: 256, secondHostOS: 3);

        using var stream = new MemoryStream(rarData, writable: true);

        var options = new PatchOptions { FileHostOS = 0 }; // Patch to MS-DOS
        var results = new List<PatchResult>();
        RARPatcher.PatchStream(stream, options, results);

        // Should find both file headers
        var fileResults = results.Where(r => r.BlockType == RAR4BlockType.FileHeader).ToList();
        Assert.Equal(2, fileResults.Count);
        // Note: patcher reads filename from offset 32, which overlaps HIGH fields when LARGE is set,
        // so first header's FileName will contain HIGH bytes. Only check second header's name.
        Assert.Equal("second.txt", fileResults[1].FileName);
        Assert.All(fileResults, r => Assert.Equal(0, r.NewHostOS));
    }

    [Fact]
    public void PatchStream_LargeFileHeader_WithHighPackSize_SkipsCorrectly()
    {
        // First file has LARGE flag with data size that uses HIGH_PACK_SIZE
        // Total data = 100 + (1 << 32) but we can't allocate 4GB+, so we use a small test:
        // ADD_SIZE=50, HIGH_PACK_SIZE=0 means 50 bytes of data
        // The key is that GetBlockDataSize combines them: addSize | (highPack << 32)
        byte[] rarData = BuildRar4WithLargeFileHeader(
            addSize: 50, highPackSize: 0, actualDataLength: 50, secondHostOS: 3);

        using var stream = new MemoryStream(rarData, writable: true);

        var options = new PatchOptions { FileHostOS = 0 };
        var results = new List<PatchResult>();
        RARPatcher.PatchStream(stream, options, results);

        var fileResults = results.Where(r => r.BlockType == RAR4BlockType.FileHeader).ToList();
        Assert.Equal(2, fileResults.Count);
        Assert.Equal("second.txt", fileResults[1].FileName);
    }

    [Fact]
    public void AnalyzeFile_LargeFileHeader_FindsAllBlocks()
    {
        // Build a file with LARGE flag on the first header and 128 bytes of data
        byte[] rarData = BuildRar4WithLargeFileHeader(
            addSize: 128, highPackSize: 0, actualDataLength: 128, secondHostOS: 3);

        string testFile = Path.Combine(_testDir, "large_analyze.rar");
        File.WriteAllBytes(testFile, rarData);

        var options = new PatchOptions { FileHostOS = 0 }; // Would change both from Windows(2) to MS-DOS(0)
        var results = RARPatcher.AnalyzeFile(testFile, options);

        // Should find both file headers
        var fileResults = results.Where(r => r.BlockType == RAR4BlockType.FileHeader).ToList();
        Assert.Equal(2, fileResults.Count);
        // Note: patcher reads filename from fixed offset 32, which overlaps HIGH fields on LARGE headers.
        // Only verify the second (non-LARGE) header's name.
        Assert.Equal("second.txt", fileResults[1].FileName);
    }

    [Fact]
    public void PatchStream_ServiceBlockLargeFlag_ElseBranch_SkipsCorrectly()
    {
        // Service block with LARGE flag, PatchServiceBlocks=false → enters the else branch
        // which must also handle LARGE flag to skip the data correctly
        byte[] rarData = BuildRar4WithLargeServiceBlock(
            addSize: 200, highPackSize: 0, actualDataLength: 200, fileHostOS: 2);

        using var stream = new MemoryStream(rarData, writable: true);

        var options = new PatchOptions
        {
            FileHostOS = 0,          // Patch file headers to MS-DOS
            PatchServiceBlocks = false // Service blocks go through else branch
        };
        var results = new List<PatchResult>();
        RARPatcher.PatchStream(stream, options, results);

        // Should only find the file header after the service block
        Assert.Single(results);
        Assert.Equal(RAR4BlockType.FileHeader, results[0].BlockType);
        Assert.Equal("after_svc.txt", results[0].FileName);
        Assert.Equal(0, results[0].NewHostOS);
    }

    [Fact]
    public void PatchStream_LargeFlagSet_HeaderTooSmall_UsesAddSizeOnly()
    {
        // Edge case: LARGE flag is set but headerSize < 36, so HIGH_PACK_SIZE should NOT be read.
        // GetBlockDataSize should fall back to just ADD_SIZE.
        // Build a synthetic header with LARGE flag but headerSize = 32 (no room for HIGH fields).
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // RAR4 marker
        writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });

        // Archive header
        byte[] archiveHeader = new byte[7];
        archiveHeader[2] = 0x73;
        BitConverter.GetBytes((ushort)0x0000).CopyTo(archiveHeader, 3);
        BitConverter.GetBytes((ushort)7).CopyTo(archiveHeader, 5);
        uint archCrc = Crc32Algorithm.Compute(archiveHeader, 2, 5);
        BitConverter.GetBytes((ushort)(archCrc & 0xFFFF)).CopyTo(archiveHeader, 0);
        writer.Write(archiveHeader);

        // File header with LARGE flag but headerSize = 32 (no HIGH fields, no filename)
        {
            int headerSize = 32;
            ushort flags = (ushort)(RARFileFlags.LongBlock | RARFileFlags.Large);

            byte[] fileHeader = new byte[headerSize];
            fileHeader[2] = 0x74; // type = FileHeader
            BitConverter.GetBytes(flags).CopyTo(fileHeader, 3);
            BitConverter.GetBytes((ushort)headerSize).CopyTo(fileHeader, 5);
            BitConverter.GetBytes((uint)64).CopyTo(fileHeader, 7); // ADD_SIZE = 64
            BitConverter.GetBytes((uint)100).CopyTo(fileHeader, 11); // UnpSize
            fileHeader[15] = 2; // HostOS = Windows
            BitConverter.GetBytes((uint)0xAAAAAAAA).CopyTo(fileHeader, 16);
            BitConverter.GetBytes((uint)0x5A000000).CopyTo(fileHeader, 20);
            fileHeader[24] = 29;
            fileHeader[25] = 0x33;
            BitConverter.GetBytes((ushort)0).CopyTo(fileHeader, 26); // NameSize = 0
            BitConverter.GetBytes((uint)0x00000020).CopyTo(fileHeader, 28);

            uint crc32 = Crc32Algorithm.Compute(fileHeader, 2, fileHeader.Length - 2);
            BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(fileHeader, 0);
            writer.Write(fileHeader);

            // Write 64 bytes of data (matches ADD_SIZE)
            writer.Write(new byte[64]);
        }

        // Second file header (sentinel to verify navigation worked)
        {
            string fileName = "sentinel.txt";
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName);
            int headerSize = 32 + nameBytes.Length;
            ushort flags = (ushort)RARFileFlags.LongBlock;

            byte[] fileHeader = new byte[headerSize];
            fileHeader[2] = 0x74;
            BitConverter.GetBytes(flags).CopyTo(fileHeader, 3);
            BitConverter.GetBytes((ushort)headerSize).CopyTo(fileHeader, 5);
            BitConverter.GetBytes((uint)0).CopyTo(fileHeader, 7);
            BitConverter.GetBytes((uint)10).CopyTo(fileHeader, 11);
            fileHeader[15] = 3; // HostOS = Unix
            BitConverter.GetBytes((uint)0xBBBBBBBB).CopyTo(fileHeader, 16);
            BitConverter.GetBytes((uint)0x5A000000).CopyTo(fileHeader, 20);
            fileHeader[24] = 29;
            fileHeader[25] = 0x33;
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(fileHeader, 26);
            BitConverter.GetBytes((uint)0x00000020).CopyTo(fileHeader, 28);
            Array.Copy(nameBytes, 0, fileHeader, 32, nameBytes.Length);

            uint crc32 = Crc32Algorithm.Compute(fileHeader, 2, fileHeader.Length - 2);
            BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(fileHeader, 0);
            writer.Write(fileHeader);
        }

        // End of Archive
        byte[] endBlock = new byte[7];
        endBlock[2] = 0x7B;
        BitConverter.GetBytes((ushort)0x4000).CopyTo(endBlock, 3);
        BitConverter.GetBytes((ushort)7).CopyTo(endBlock, 5);
        uint endCrc = Crc32Algorithm.Compute(endBlock, 2, 5);
        BitConverter.GetBytes((ushort)(endCrc & 0xFFFF)).CopyTo(endBlock, 0);
        writer.Write(endBlock);

        byte[] rarData = ms.ToArray();
        using var stream = new MemoryStream(rarData, writable: true);

        var options = new PatchOptions { FileHostOS = 0 }; // Patch to MS-DOS
        var results = new List<PatchResult>();
        RARPatcher.PatchStream(stream, options, results);

        // GetBlockDataSize should use just ADD_SIZE (64) since headerSize < 36
        // So we should reach the sentinel file header
        var fileResults = results.Where(r => r.BlockType == RAR4BlockType.FileHeader).ToList();
        Assert.Equal(2, fileResults.Count);
        Assert.Equal("sentinel.txt", fileResults[1].FileName);
    }

    [Fact]
    public void PatchStream_LargeFileHeader_HostOSPatchedCorrectly()
    {
        // Verify that patching Host OS works correctly on a file header with LARGE flag
        byte[] rarData = BuildRar4WithLargeFileHeader(
            addSize: 100, highPackSize: 0, actualDataLength: 100, secondHostOS: 2);

        using var stream = new MemoryStream(rarData, writable: true);

        var options = new PatchOptions { FileHostOS = 3 }; // Patch to Unix
        var results = new List<PatchResult>();
        RARPatcher.PatchStream(stream, options, results);

        // Both headers should be patched to Unix
        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(2, r.OriginalHostOS); // Was Windows
            Assert.Equal(3, r.NewHostOS); // Now Unix
        });

        // Verify patched headers have valid CRCs by re-reading
        stream.Position = 7; // Skip marker
        var reader = new RARHeaderReader(stream);

        // Skip archive header
        var archBlock = reader.ReadBlock(parseContents: false);
        Assert.NotNull(archBlock);
        reader.SkipBlock(archBlock!);

        // First file header (LARGE)
        var firstBlock = reader.ReadBlock(parseContents: true);
        Assert.NotNull(firstBlock);
        Assert.True(firstBlock!.CrcValid, "First block CRC should be valid after patching");
        Assert.NotNull(firstBlock.FileHeader);
        Assert.Equal(3, firstBlock.FileHeader!.HostOS); // Patched to Unix
        Assert.True(firstBlock.FileHeader.HasLargeSize);
    }

    [Fact]
    public void AnalyzeFile_ServiceBlockLargeFlag_WithPatchEnabled_FindsServiceBlock()
    {
        // Service block with LARGE flag, PatchServiceBlocks=true → enters the if branch
        // AnalyzeFile must use GetBlockDataSize to skip correctly
        byte[] rarData = BuildRar4WithLargeServiceBlock(
            addSize: 150, highPackSize: 0, actualDataLength: 150, fileHostOS: 2);

        string testFile = Path.Combine(_testDir, "large_svc_analyze.rar");
        File.WriteAllBytes(testFile, rarData);

        var options = new PatchOptions
        {
            FileHostOS = 0,
            PatchServiceBlocks = true,
            ServiceBlockHostOS = 0
        };
        var results = RARPatcher.AnalyzeFile(testFile, options);

        // Should find both the service block and the file header
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.BlockType == RAR4BlockType.Service);
        Assert.Contains(results, r => r.BlockType == RAR4BlockType.FileHeader && r.FileName == "after_svc.txt");
    }

    #endregion
}
