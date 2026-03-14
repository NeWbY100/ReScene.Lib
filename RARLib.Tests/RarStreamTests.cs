using System.Text;
using Force.Crc32;

namespace RARLib.Tests;

public class RarStreamTests
{
    private static readonly byte[] Rar4Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    #region RAR4 Builder Helpers

    /// <summary>
    /// Builds a RAR4 archive header with valid CRC.
    /// </summary>
    private static byte[] BuildArchiveHeader(RARArchiveFlags flags = RARArchiveFlags.None)
    {
        ushort headerSize = 13;
        byte[] header = new byte[headerSize];
        header[2] = 0x73; // ArchiveHeader
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);
        return header;
    }

    /// <summary>
    /// Builds a RAR4 file header with valid CRC and specified flags.
    /// Method is the raw byte (e.g. 0x30 for store).
    /// </summary>
    private static byte[] BuildFileHeader(string fileName, uint packedSize, uint unpackedSize,
        byte method = 0x30, byte unpVer = 29,
        RARFileFlags extraFlags = RARFileFlags.ExtTime)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;
        RARFileFlags flags = RARFileFlags.LongBlock | extraFlags;

        int extTimeSize = (extraFlags & RARFileFlags.ExtTime) != 0 ? 2 : 0;
        ushort headerSize = (ushort)(7 + 25 + nameSize + extTimeSize);

        byte[] header = new byte[headerSize];
        header[2] = 0x74;
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(packedSize).CopyTo(header, 7);
        BitConverter.GetBytes(unpackedSize).CopyTo(header, 11);
        header[15] = 2; // Windows
        BitConverter.GetBytes(0x5A8E3100u).CopyTo(header, 20); // DOS time
        header[24] = unpVer;
        header[25] = method;
        BitConverter.GetBytes(nameSize).CopyTo(header, 26);
        BitConverter.GetBytes(0x00000020u).CopyTo(header, 28); // file attributes
        nameBytes.CopyTo(header, 32);

        if ((extraFlags & RARFileFlags.ExtTime) != 0)
        {
            ushort extFlags = 0x8000; // mtime present, no extra bytes
            BitConverter.GetBytes(extFlags).CopyTo(header, 32 + nameSize);
        }

        uint crc32Full = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32Full & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);
        return header;
    }

    /// <summary>
    /// Builds a minimal end-of-archive block with valid CRC.
    /// </summary>
    private static byte[] BuildEndArchive()
    {
        byte[] header = new byte[7];
        header[2] = 0x7B;
        BitConverter.GetBytes((ushort)7).CopyTo(header, 5);
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);
        return header;
    }

    /// <summary>
    /// Builds a complete synthetic RAR4 archive file (marker + archive header + file header + data + end).
    /// Returns the path to the temporary file.
    /// </summary>
    private static string BuildSyntheticRar4(string dir, string archiveName, string fileName,
        byte[] fileData, RARArchiveFlags archiveFlags = RARArchiveFlags.None,
        RARFileFlags extraFileFlags = RARFileFlags.ExtTime)
    {
        string path = Path.Combine(dir, archiveName);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(Rar4Marker);
        fs.Write(BuildArchiveHeader(archiveFlags));
        fs.Write(BuildFileHeader(fileName, (uint)fileData.Length, (uint)fileData.Length,
            method: 0x30, extraFlags: extraFileFlags));
        fs.Write(fileData);
        fs.Write(BuildEndArchive());

        return path;
    }

    /// <summary>
    /// Builds a synthetic RAR4 volume that represents a continuation (SplitBefore set).
    /// </summary>
    private static string BuildSyntheticRar4Volume(string dir, string archiveName, string fileName,
        byte[] fileData, RARArchiveFlags archiveFlags, bool splitBefore, bool splitAfter)
    {
        string path = Path.Combine(dir, archiveName);

        RARFileFlags extraFlags = RARFileFlags.ExtTime;
        if (splitBefore) extraFlags |= RARFileFlags.SplitBefore;
        if (splitAfter) extraFlags |= RARFileFlags.SplitAfter;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(Rar4Marker);
        fs.Write(BuildArchiveHeader(archiveFlags));
        fs.Write(BuildFileHeader(fileName, (uint)fileData.Length, (uint)fileData.Length,
            method: 0x30, extraFlags: extraFlags));
        fs.Write(fileData);
        fs.Write(BuildEndArchive());

        return path;
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_NonExistentFile_ThrowsFileNotFoundException()
    {
        string fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".rar");
        Assert.Throws<FileNotFoundException>(() => new RarStream(fakePath));
    }

    [Fact]
    public void Constructor_SingleVolume_OpensSuccessfully()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = "Hello, World!"u8.ToArray();
            string path = BuildSyntheticRar4(dir, "test.rar", "hello.txt", data);

            using var stream = new RarStream(path);

            Assert.Equal(data.Length, stream.Length);
            Assert.Equal("hello.txt", stream.PackedFileName);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Constructor_NullPackedFileName_UsesFirstFileFound()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = "content"u8.ToArray();
            string path = BuildSyntheticRar4(dir, "test.rar", "auto.dat", data);

            using var stream = new RarStream(path, null);

            Assert.Equal("auto.dat", stream.PackedFileName);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Constructor_SpecificPackedFileName_FindsCorrectFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = "target file data"u8.ToArray();
            string path = BuildSyntheticRar4(dir, "test.rar", "target.bin", data);

            using var stream = new RarStream(path, "target.bin");

            Assert.Equal("target.bin", stream.PackedFileName);
            Assert.Equal(data.Length, stream.Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Constructor_FileNotInArchive_ThrowsArgumentException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = "some data"u8.ToArray();
            string path = BuildSyntheticRar4(dir, "test.rar", "actual.txt", data);

            Assert.Throws<ArgumentException>(() => new RarStream(path, "nonexistent.txt"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Property Tests

    [Fact]
    public void CanRead_NotDisposed_ReturnsTrue()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);
            using var stream = new RarStream(path);
            Assert.True(stream.CanRead);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CanSeek_NotDisposed_ReturnsTrue()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);
            using var stream = new RarStream(path);
            Assert.True(stream.CanSeek);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CanWrite_ReturnsFalse()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);
            using var stream = new RarStream(path);
            Assert.False(stream.CanWrite);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Length_ReturnsPackedFileLength()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = new byte[256];
            Random.Shared.NextBytes(data);
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);
            using var stream = new RarStream(path);
            Assert.Equal(256, stream.Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Position_InitiallyZero()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);
            using var stream = new RarStream(path);
            Assert.Equal(0, stream.Position);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Write_ThrowsNotSupportedException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);
            using var stream = new RarStream(path);
            Assert.Throws<NotSupportedException>(() => stream.Write([0x00], 0, 1));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SetLength_ThrowsNotSupportedException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);
            using var stream = new RarStream(path);
            Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Read Tests

    [Fact]
    public void Read_SingleVolume_ReadsEntireFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] expected = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog.");
            string path = BuildSyntheticRar4(dir, "test.rar", "fox.txt", expected);

            using var stream = new RarStream(path);
            byte[] buffer = new byte[expected.Length];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);

            Assert.Equal(expected.Length, bytesRead);
            Assert.Equal(expected, buffer);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_PartialRead_ReturnsRequestedBytes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            byte[] buffer = new byte[4];
            int bytesRead = stream.Read(buffer, 0, 4);

            Assert.Equal(4, bytesRead);
            Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33 }, buffer);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_PastEnd_ReturnsZero()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            // Read all data first
            byte[] buf = new byte[3];
            stream.ReadExactly(buf, 0, 3);

            // Now try to read past end
            byte[] buf2 = new byte[10];
            int bytesRead = stream.Read(buf2, 0, 10);

            Assert.Equal(0, bytesRead);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_MoreThanAvailable_ReturnsAvailableBytes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0xAA, 0xBB, 0xCC];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            byte[] buffer = new byte[100];
            int bytesRead = stream.Read(buffer, 0, 100);

            Assert.Equal(3, bytesRead);
            Assert.Equal(0xAA, buffer[0]);
            Assert.Equal(0xBB, buffer[1]);
            Assert.Equal(0xCC, buffer[2]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_WithOffset_WritesToCorrectPosition()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x11, 0x22, 0x33];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            byte[] buffer = new byte[10];
            int bytesRead = stream.Read(buffer, 5, 3);

            Assert.Equal(3, bytesRead);
            Assert.Equal(0x00, buffer[4]); // before offset
            Assert.Equal(0x11, buffer[5]);
            Assert.Equal(0x22, buffer[6]);
            Assert.Equal(0x33, buffer[7]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_ZeroCount_ReturnsZero()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            byte[] buffer = new byte[10];
            int bytesRead = stream.Read(buffer, 0, 0);

            Assert.Equal(0, bytesRead);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Seek Tests

    [Fact]
    public void Seek_Begin_SetsPositionFromStart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x00, 0x11, 0x22, 0x33, 0x44];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            long result = stream.Seek(3, SeekOrigin.Begin);

            Assert.Equal(3, result);
            Assert.Equal(3, stream.Position);

            byte[] buffer = new byte[1];
            stream.ReadExactly(buffer, 0, 1);
            Assert.Equal(0x33, buffer[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Seek_Current_MovesRelativeToCurrentPosition()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x00, 0x11, 0x22, 0x33, 0x44];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            stream.Position = 2;
            long result = stream.Seek(1, SeekOrigin.Current);

            Assert.Equal(3, result);

            byte[] buffer = new byte[1];
            stream.ReadExactly(buffer, 0, 1);
            Assert.Equal(0x33, buffer[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Seek_End_MovesRelativeToEnd()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x00, 0x11, 0x22, 0x33, 0x44];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            long result = stream.Seek(-2, SeekOrigin.End);

            Assert.Equal(3, result);

            byte[] buffer = new byte[1];
            stream.ReadExactly(buffer, 0, 1);
            Assert.Equal(0x33, buffer[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Seek_NegativeResult_ThrowsArgumentOutOfRangeException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Seek_BeyondEnd_AllowedButReadReturnsZero()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            long result = stream.Seek(100, SeekOrigin.Begin);

            Assert.Equal(100, result);

            byte[] buffer = new byte[10];
            int bytesRead = stream.Read(buffer, 0, 10);
            Assert.Equal(0, bytesRead);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Seek_ToVariousPositions_ReadsCorrectData()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = new byte[256];
            for (int i = 0; i < 256; i++) data[i] = (byte)i;
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);

            // Read byte at position 0
            byte[] buf = new byte[1];
            stream.ReadExactly(buf, 0, 1);
            Assert.Equal(0x00, buf[0]);

            // Seek to 128
            stream.Seek(128, SeekOrigin.Begin);
            stream.ReadExactly(buf, 0, 1);
            Assert.Equal(0x80, buf[0]);

            // Seek to 255 (last byte)
            stream.Seek(255, SeekOrigin.Begin);
            stream.ReadExactly(buf, 0, 1);
            Assert.Equal(0xFF, buf[0]);

            // Seek backwards
            stream.Seek(50, SeekOrigin.Begin);
            stream.ReadExactly(buf, 0, 1);
            Assert.Equal(50, buf[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Position Property Tests

    [Fact]
    public void Position_Set_NegativeValue_ThrowsArgumentOutOfRangeException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Position_AdvancesAfterRead()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            byte[] buf = new byte[3];
            stream.ReadExactly(buf, 0, 3);

            Assert.Equal(3, stream.Position);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_ClosesAllHandles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            var stream = new RarStream(path);
            byte[] buf = new byte[3];
            stream.ReadExactly(buf, 0, 3); // This opens the internal stream
            stream.Dispose();

            // After disposal, CanRead should return false
            Assert.False(stream.CanRead);
            Assert.False(stream.CanSeek);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            var stream = new RarStream(path);
            stream.Dispose();
            stream.Dispose(); // Should not throw
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_AfterDispose_ThrowsObjectDisposedException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            var stream = new RarStream(path);
            stream.Dispose();

            byte[] buf = new byte[1];
            Assert.Throws<ObjectDisposedException>(() => stream.Read(buf, 0, 1));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Seek_AfterDispose_ThrowsObjectDisposedException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            var stream = new RarStream(path);
            stream.Dispose();

            Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Length_AfterDispose_ThrowsObjectDisposedException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            var stream = new RarStream(path);
            stream.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Position_GetAfterDispose_ThrowsObjectDisposedException()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            var stream = new RarStream(path);
            stream.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ = stream.Position);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Multi-Volume Tests (New-Style Naming)

    [Fact]
    public void Read_MultiVolume_NewStyle_ReadsAcrossVolumes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Create a file split across 3 new-style volumes
            byte[] part1Data = [0x01, 0x02, 0x03, 0x04, 0x05];
            byte[] part2Data = [0x06, 0x07, 0x08, 0x09, 0x0A];
            byte[] part3Data = [0x0B, 0x0C, 0x0D];

            var archFlags = RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering | RARArchiveFlags.FirstVolume;

            BuildSyntheticRar4Volume(dir, "test.part1.rar", "data.bin", part1Data,
                archFlags, splitBefore: false, splitAfter: true);
            BuildSyntheticRar4Volume(dir, "test.part2.rar", "data.bin", part2Data,
                RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering,
                splitBefore: true, splitAfter: true);
            BuildSyntheticRar4Volume(dir, "test.part3.rar", "data.bin", part3Data,
                RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering,
                splitBefore: true, splitAfter: false);

            string firstVolume = Path.Combine(dir, "test.part1.rar");
            using var stream = new RarStream(firstVolume);

            Assert.Equal(13, stream.Length);

            byte[] buffer = new byte[13];
            int bytesRead = stream.Read(buffer, 0, 13);

            Assert.Equal(13, bytesRead);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05,
                                      0x06, 0x07, 0x08, 0x09, 0x0A,
                                      0x0B, 0x0C, 0x0D }, buffer);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_MultiVolume_NewStyle_SeekAcrossVolumes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] part1Data = [0x10, 0x20, 0x30, 0x40, 0x50];
            byte[] part2Data = [0x60, 0x70, 0x80, 0x90, 0xA0];

            var archFlags = RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering | RARArchiveFlags.FirstVolume;

            BuildSyntheticRar4Volume(dir, "test.part1.rar", "data.bin", part1Data,
                archFlags, splitBefore: false, splitAfter: true);
            BuildSyntheticRar4Volume(dir, "test.part2.rar", "data.bin", part2Data,
                RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering,
                splitBefore: true, splitAfter: false);

            string firstVolume = Path.Combine(dir, "test.part1.rar");
            using var stream = new RarStream(firstVolume);

            // Seek into second volume and read
            // Position 7 = part2Data[2] = 0x80
            stream.Seek(7, SeekOrigin.Begin);
            byte[] buf = new byte[1];
            stream.ReadExactly(buf, 0, 1);
            Assert.Equal(0x80, buf[0]);

            // Seek back into first volume
            stream.Seek(2, SeekOrigin.Begin);
            stream.ReadExactly(buf, 0, 1);
            Assert.Equal(0x30, buf[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_MultiVolume_NewStyle_ReadSpanningBoundary()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] part1Data = [0xAA, 0xBB, 0xCC];
            byte[] part2Data = [0xDD, 0xEE, 0xFF];

            var archFlags = RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering | RARArchiveFlags.FirstVolume;

            BuildSyntheticRar4Volume(dir, "test.part1.rar", "data.bin", part1Data,
                archFlags, splitBefore: false, splitAfter: true);
            BuildSyntheticRar4Volume(dir, "test.part2.rar", "data.bin", part2Data,
                RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering,
                splitBefore: true, splitAfter: false);

            string firstVolume = Path.Combine(dir, "test.part1.rar");
            using var stream = new RarStream(firstVolume);

            // Read 4 bytes starting at position 1, which spans the boundary
            stream.Seek(1, SeekOrigin.Begin);
            byte[] buffer = new byte[4];
            int bytesRead = stream.Read(buffer, 0, 4);

            Assert.Equal(4, bytesRead);
            Assert.Equal(new byte[] { 0xBB, 0xCC, 0xDD, 0xEE }, buffer);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Multi-Volume Tests (Old-Style Naming)

    [Fact]
    public void Read_MultiVolume_OldStyle_ReadsAcrossVolumes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Old-style: test.rar -> test.r00 -> test.r01
            byte[] part1Data = [0x01, 0x02, 0x03];
            byte[] part2Data = [0x04, 0x05, 0x06];
            byte[] part3Data = [0x07, 0x08];

            // No NewNumbering flag = old style
            var archFlags = RARArchiveFlags.Volume | RARArchiveFlags.FirstVolume;

            BuildSyntheticRar4Volume(dir, "test.rar", "data.bin", part1Data,
                archFlags, splitBefore: false, splitAfter: true);
            BuildSyntheticRar4Volume(dir, "test.r00", "data.bin", part2Data,
                RARArchiveFlags.Volume, splitBefore: true, splitAfter: true);
            BuildSyntheticRar4Volume(dir, "test.r01", "data.bin", part3Data,
                RARArchiveFlags.Volume, splitBefore: true, splitAfter: false);

            string firstVolume = Path.Combine(dir, "test.rar");
            using var stream = new RarStream(firstVolume);

            Assert.Equal(8, stream.Length);

            byte[] buffer = new byte[8];
            int bytesRead = stream.Read(buffer, 0, 8);

            Assert.Equal(8, bytesRead);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }, buffer);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Real File Tests

    [Fact]
    public void Read_RealRar4StoredFile_ReadsCorrectContent()
    {
        // test_wrar40_m0.rar contains testfile.txt stored (no compression)
        string path = Path.Combine(TestDataPath, "test_wrar40_m0.rar");
        if (!File.Exists(path))
            return; // Skip if test data not available

        using var stream = new RarStream(path, "testfile.txt");

        Assert.Equal(131, stream.Length);

        byte[] buffer = new byte[(int)stream.Length];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(131, bytesRead);

        string content = Encoding.UTF8.GetString(buffer);
        Assert.StartsWith("This is a test file for RAR compression testing.", content);
        Assert.Contains("WinRARRed RARLib decompression test.", content);
    }

    [Fact]
    public void Read_RealRar4StoredFile_SeekAndRead()
    {
        string path = Path.Combine(TestDataPath, "test_wrar40_m0.rar");
        if (!File.Exists(path))
            return;

        using var stream = new RarStream(path, "testfile.txt");

        // "This" is at position 0
        byte[] buf = new byte[4];
        stream.ReadExactly(buf, 0, 4);
        Assert.Equal("This", Encoding.ASCII.GetString(buf));

        // Seek back to start and read again
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(buf, 0, 4);
        Assert.Equal("This", Encoding.ASCII.GetString(buf));
    }

    [Fact]
    public void Read_RealRar5CompressedFile_ReadsPackedData()
    {
        // RAR5 archives can be opened; for compressed files we get raw compressed data
        string path = Path.Combine(TestDataPath, "test_rar5_m3.rar");
        if (!File.Exists(path))
            return;

        using var stream = new RarStream(path);

        // Should have non-zero length (the packed data size)
        Assert.True(stream.Length > 0);

        // Should be able to read some bytes
        byte[] buf = new byte[Math.Min(16, (int)stream.Length)];
        int bytesRead = stream.Read(buf, 0, buf.Length);
        Assert.True(bytesRead > 0);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Read_EmptyBuffer_ReturnsZero()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01, 0x02, 0x03];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            byte[] buffer = [];
            int bytesRead = stream.Read(buffer, 0, 0);
            Assert.Equal(0, bytesRead);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_SequentialSmallReads_ReadsCorrectData()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = new byte[10];
            for (int i = 0; i < 10; i++) data[i] = (byte)(i * 10);
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);

            using var stream = new RarStream(path);
            byte[] buf = new byte[1];

            for (int i = 0; i < 10; i++)
            {
                int bytesRead = stream.Read(buf, 0, 1);
                Assert.Equal(1, bytesRead);
                Assert.Equal((byte)(i * 10), buf[0]);
            }

            // Next read should return 0
            int finalRead = stream.Read(buf, 0, 1);
            Assert.Equal(0, finalRead);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Flush_DoesNotThrow()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RarStreamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = [0x01];
            string path = BuildSyntheticRar4(dir, "test.rar", "f.bin", data);
            using var stream = new RarStream(path);
            stream.Flush(); // Should be a no-op
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion
}
