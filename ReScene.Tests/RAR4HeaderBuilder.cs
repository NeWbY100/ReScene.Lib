using System.Text;
using Force.Crc32;
using ReScene.RAR;

namespace ReScene.Tests;

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
        uint fileCRC = 0xDEADBEEF,
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
        BitConverter.GetBytes(fileCRC).CopyTo(header, 16);       // FILE_CRC
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
        uint fileCRC = 0xDEADBEEF,
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
        BitConverter.GetBytes(fileCRC).CopyTo(header, 16);
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
