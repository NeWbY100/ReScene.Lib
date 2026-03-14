using System.Text;
using Force.Crc32;
using ReScene.RAR;

namespace ReScene.SRR.Tests;

/// <summary>
/// Builds synthetic SRR files for unit testing.
/// SRR format: sequence of blocks (SRR Header, StoredFiles, RarFile references + embedded RAR headers).
/// </summary>
internal class SRRTestDataBuilder
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;

    public SRRTestDataBuilder()
    {
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>
    /// Writes an SRR header block (type 0x69).
    /// </summary>
    public SRRTestDataBuilder AddSrrHeader(string? appName = null)
    {
        ushort flags = appName != null ? (ushort)0x0001 : (ushort)0x0000;

        // Calculate header size
        int headerSize = 7; // base header
        int appNameLen = 0;
        byte[]? appNameBytes = null;
        if (appName != null)
        {
            appNameBytes = Encoding.UTF8.GetBytes(appName);
            appNameLen = appNameBytes.Length;
            headerSize += 2 + appNameLen; // 2 bytes name length + name
        }

        _writer.Write((ushort)0x0000); // CRC (placeholder, SRR doesn't validate)
        _writer.Write((byte)0x69);     // SRR Header type
        _writer.Write(flags);
        _writer.Write((ushort)headerSize);

        if (appNameBytes != null)
        {
            _writer.Write((ushort)appNameLen);
            _writer.Write(appNameBytes);
        }

        return this;
    }

    /// <summary>
    /// Writes an SRR stored file block (type 0x6A) with data.
    /// </summary>
    public SRRTestDataBuilder AddStoredFile(string fileName, byte[] fileData)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        ushort headerSize = (ushort)(7 + 4 + 2 + nameBytes.Length); // base + addSize + nameLen + name
        uint addSize = (uint)fileData.Length;

        _writer.Write((ushort)0x0000);     // CRC
        _writer.Write((byte)0x6A);         // StoredFile type
        _writer.Write((ushort)0x0000);     // flags
        _writer.Write(headerSize);
        _writer.Write(addSize);            // data length
        _writer.Write((ushort)nameBytes.Length);
        _writer.Write(nameBytes);
        _writer.Write(fileData);           // file data

        return this;
    }

    /// <summary>
    /// Writes an SRR RAR file reference block (type 0x71) followed by embedded RAR4 headers.
    /// </summary>
    public SRRTestDataBuilder AddRarFileWithHeaders(string rarFileName, Action<RAR4HeaderBuilder> buildHeaders)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(rarFileName);
        ushort headerSize = (ushort)(7 + 2 + nameBytes.Length); // base + nameLen + name

        _writer.Write((ushort)0x0000);     // CRC
        _writer.Write((byte)0x71);         // RarFile type
        _writer.Write((ushort)0x0000);     // flags
        _writer.Write(headerSize);
        _writer.Write((ushort)nameBytes.Length);
        _writer.Write(nameBytes);

        // Write embedded RAR headers directly after
        var headerBuilder = new RAR4HeaderBuilder(_writer);
        buildHeaders(headerBuilder);

        return this;
    }

    /// <summary>
    /// Writes an SRR OSO hash block (type 0x6B).
    /// </summary>
    public SRRTestDataBuilder AddOsoHash(string fileName, ulong fileSize, byte[] osoHash)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        ushort headerSize = (ushort)(7 + 8 + 8 + 2 + nameBytes.Length); // base + fileSize + hash + nameLen + name

        _writer.Write((ushort)0x0000);     // CRC
        _writer.Write((byte)0x6B);         // OsoHash type
        _writer.Write((ushort)0x0000);     // flags
        _writer.Write(headerSize);
        _writer.Write(fileSize);           // pyrescene order: fileSize first
        _writer.Write(osoHash);            // then hash
        _writer.Write((ushort)nameBytes.Length);
        _writer.Write(nameBytes);

        return this;
    }

    /// <summary>
    /// Writes an SRR RAR padding block (type 0x6C).
    /// </summary>
    public SRRTestDataBuilder AddRarPadding(string rarFileName, uint paddingSize)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(rarFileName);
        ushort headerSize = (ushort)(7 + 4 + 2 + nameBytes.Length); // base + addSize + nameLen + name

        _writer.Write((ushort)0x0000);     // CRC
        _writer.Write((byte)0x6C);         // RarPadding type
        _writer.Write((ushort)0x8000);     // flags with LongBlock
        _writer.Write(headerSize);
        _writer.Write(paddingSize);        // padding size (addSize)
        _writer.Write((ushort)nameBytes.Length);
        _writer.Write(nameBytes);

        // Write actual padding bytes
        _writer.Write(new byte[paddingSize]);

        return this;
    }

    public byte[] Build()
    {
        _writer.Flush();
        return _stream.ToArray();
    }

    public string BuildToFile(string directory, string fileName)
    {
        byte[] data = Build();
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, data);
        return path;
    }
}

/// <summary>
/// Builds RAR 4.x header blocks for embedding inside SRR test data.
/// </summary>
internal class RAR4HeaderBuilder(BinaryWriter writer)
{
    private readonly BinaryWriter _writer = writer;

    /// <summary>
    /// Writes a RAR 4.x archive header block (0x73) with proper CRC.
    /// </summary>
    public RAR4HeaderBuilder AddArchiveHeader(RARArchiveFlags flags = RARArchiveFlags.None)
    {
        // Archive header: CRC(2) + Type(1) + Flags(2) + HeaderSize(2) + Reserved1(2) + Reserved2(4) = 13
        ushort headerSize = 13;
        byte[] header = new byte[headerSize];

        header[2] = 0x73; // ArchiveHeader
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        // Reserved1 at offset 7 = 0x0000
        // Reserved2 at offset 9 = 0x00000000

        // Calculate CRC
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        _writer.Write(header);
        return this;
    }

    /// <summary>
    /// Writes a RAR 4.x file header block (0x74) with proper CRC.
    /// Note: In SRR files, file headers have NO data following them (headers only).
    /// </summary>
    public RAR4HeaderBuilder AddFileHeader(
        string fileName,
        uint packedSize = 1024,
        uint unpackedSize = 1024,
        byte hostOS = 2,          // Windows
        uint fileCrc = 0xDEADBEEF,
        uint fileTimeDOS = 0x5A8E3100, // ~2025-04-22 06:08:00
        byte unpVer = 29,
        byte method = 0x33,       // Normal
        uint fileAttributes = 0x00000020, // Archive
        RARFileFlags extraFlags = RARFileFlags.ExtTime,
        bool isDirectory = false)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;

        // Calculate flags
        RARFileFlags flags = RARFileFlags.LongBlock | extraFlags;
        if (isDirectory)
        {
            flags |= RARFileFlags.Directory;
        }

        // Header layout:
        // CRC(2) + Type(1) + Flags(2) + HeaderSize(2) = 7 (base)
        // ADD_SIZE(4) + UNP_SIZE(4) + HOST_OS(1) + FILE_CRC(4) + FILE_TIME(4) + UNP_VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4) = 25
        // + NAME(variable)
        int extTimeSize = 0;
        if ((extraFlags & RARFileFlags.ExtTime) != 0)
        {
            extTimeSize = 2; // Just the flags word, no extra time data
        }
        ushort headerSize = (ushort)(7 + 25 + nameSize + extTimeSize);

        byte[] header = new byte[headerSize];
        // Skip CRC at offset 0-1 (fill later)
        header[2] = 0x74; // FileHeader
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(packedSize).CopyTo(header, 7);    // ADD_SIZE = packed size
        BitConverter.GetBytes(unpackedSize).CopyTo(header, 11); // UNP_SIZE
        header[15] = hostOS;                                     // HOST_OS
        BitConverter.GetBytes(fileCrc).CopyTo(header, 16);       // FILE_CRC
        BitConverter.GetBytes(fileTimeDOS).CopyTo(header, 20);   // FILE_TIME
        header[24] = unpVer;                                     // UNP_VER
        header[25] = method;                                     // METHOD
        BitConverter.GetBytes(nameSize).CopyTo(header, 26);      // NAME_SIZE
        BitConverter.GetBytes(fileAttributes).CopyTo(header, 28); // ATTR
        nameBytes.CopyTo(header, 32);                             // NAME

        if ((extraFlags & RARFileFlags.ExtTime) != 0)
        {
            int extTimeOffset = 32 + nameSize;
            // Extended time flags: mtime present with no extra bytes = 0x8000
            ushort extFlags = 0x8000;
            BitConverter.GetBytes(extFlags).CopyTo(header, extTimeOffset);
        }

        // Calculate CRC
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        _writer.Write(header);

        // In SRR files, no actual file data follows the header

        return this;
    }

    /// <summary>
    /// Writes a RAR 4.x file header block (0x74) with LARGE flag and 64-bit sizes, for testing custom packer detection.
    /// </summary>
    public RAR4HeaderBuilder AddFileHeaderWithLargeSize(
        string fileName,
        uint packedSizeLow = 1024,
        uint packedSizeHigh = 0,
        uint unpackedSizeLow = 1024,
        uint unpackedSizeHigh = 0,
        byte hostOS = 2,
        uint fileCrc = 0xDEADBEEF,
        uint fileTimeDOS = 0x5A8E3100,
        byte unpVer = 29,
        byte method = 0x33,
        uint fileAttributes = 0x00000020,
        RARFileFlags extraFlags = RARFileFlags.ExtTime)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;

        RARFileFlags flags = RARFileFlags.LongBlock | RARFileFlags.Large | extraFlags;

        // Header with LARGE adds HIGH_PACK_SIZE(4) + HIGH_UNP_SIZE(4) = 8 extra bytes
        int extTimeSize = (extraFlags & RARFileFlags.ExtTime) != 0 ? 2 : 0;
        ushort headerSize = (ushort)(7 + 25 + 8 + nameSize + extTimeSize);

        byte[] header = new byte[headerSize];
        header[2] = 0x74; // FileHeader
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(packedSizeLow).CopyTo(header, 7);     // ADD_SIZE (low packed)
        BitConverter.GetBytes(unpackedSizeLow).CopyTo(header, 11);  // UNP_SIZE (low unpacked)
        header[15] = hostOS;
        BitConverter.GetBytes(fileCrc).CopyTo(header, 16);
        BitConverter.GetBytes(fileTimeDOS).CopyTo(header, 20);
        header[24] = unpVer;
        header[25] = method;
        BitConverter.GetBytes(nameSize).CopyTo(header, 26);
        BitConverter.GetBytes(fileAttributes).CopyTo(header, 28);
        // HIGH_PACK_SIZE at offset 32
        BitConverter.GetBytes(packedSizeHigh).CopyTo(header, 32);
        // HIGH_UNP_SIZE at offset 36
        BitConverter.GetBytes(unpackedSizeHigh).CopyTo(header, 36);
        // Filename at offset 40
        nameBytes.CopyTo(header, 40);

        if ((extraFlags & RARFileFlags.ExtTime) != 0)
        {
            int extTimeOffset = 40 + nameSize;
            ushort extFlags = 0x8000;
            BitConverter.GetBytes(extFlags).CopyTo(header, extTimeOffset);
        }

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        _writer.Write(header);
        return this;
    }

    /// <summary>
    /// Writes a RAR 4.x CMT service block (0x7A) with stored comment data and proper CRC.
    /// </summary>
    public RAR4HeaderBuilder AddCmtServiceBlock(
        string commentText,
        byte hostOS = 2,
        uint fileTimeDOS = 0,
        byte method = 0x30,     // Store (0x30)
        uint fileAttributes = 0x00000020)
    {
        byte[] commentData = Encoding.UTF8.GetBytes(commentText);
        byte[] subTypeName = Encoding.ASCII.GetBytes("CMT");

        uint addSize = (uint)commentData.Length; // packed size = data size for stored

        // Header: CRC(2) + Type(1) + Flags(2) + HeaderSize(2) = 7
        // ADD_SIZE(4) + UNP_SIZE(4) + HOST_OS(1) + DATA_CRC(4) + FILE_TIME(4) + UNP_VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4) = 25
        // + NAME("CMT" = 3)
        ushort headerSize = (ushort)(7 + 25 + subTypeName.Length);

        byte[] header = new byte[headerSize];
        header[2] = 0x7A; // Service block
        ushort flags = (ushort)(RARFileFlags.LongBlock | RARFileFlags.SkipIfUnknown);
        BitConverter.GetBytes(flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(addSize).CopyTo(header, 7);       // ADD_SIZE = packed size
        BitConverter.GetBytes((uint)commentData.Length).CopyTo(header, 11); // UNP_SIZE
        header[15] = hostOS;                                     // HOST_OS
        BitConverter.GetBytes((uint)0).CopyTo(header, 16);       // DATA_CRC (placeholder)
        BitConverter.GetBytes(fileTimeDOS).CopyTo(header, 20);   // FILE_TIME
        header[24] = 29;                                          // UNP_VER
        header[25] = method;                                      // METHOD
        BitConverter.GetBytes((ushort)subTypeName.Length).CopyTo(header, 26); // NAME_SIZE
        BitConverter.GetBytes(fileAttributes).CopyTo(header, 28); // ATTR
        subTypeName.CopyTo(header, 32);                           // NAME = "CMT"

        // Calculate header CRC
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        _writer.Write(header);
        _writer.Write(commentData); // Write the comment data after header

        return this;
    }

    /// <summary>
    /// Writes a RAR 4.x end of archive block (0x7B) with proper CRC.
    /// </summary>
    public RAR4HeaderBuilder AddEndArchive()
    {
        ushort headerSize = 7;
        byte[] header = new byte[headerSize];
        header[2] = 0x7B; // EndArchive
        BitConverter.GetBytes((ushort)0).CopyTo(header, 3);       // Flags
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        _writer.Write(header);
        return this;
    }
}
