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
    /// Writes the RAR 4.x marker signature (7 bytes: <c>52 61 72 21 1A 07 00</c>).
    /// </summary>
    public RAR4HeaderBuilder AddMarker()
    {
        _writer.Write(RARUtils.RAR4Marker);
        return this;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> verbatim, bypassing all field computation. Used to replay a
    /// previously captured header block's exact bytes (e.g. an original volume's headers into an
    /// SRR RARFile section) rather than recomputing it, guaranteeing byte-for-byte identity with
    /// whatever produced those bytes originally.
    /// </summary>
    public RAR4HeaderBuilder WriteRaw(byte[] bytes)
    {
        _writer.Write(bytes);
        return this;
    }

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
        bool isDirectory = false,
        uint? creationTimeDOS = null,
        uint? accessTimeDOS = null,
        byte[]? mtimeRemainder = null)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;

        // Calculate flags
        RARFileFlags flags = RARFileFlags.LongBlock | extraFlags;
        if (isDirectory)
        {
            flags |= RARFileFlags.Directory;
        }

        if (mtimeRemainder is { Length: > 3 })
        {
            throw new ArgumentOutOfRangeException(nameof(mtimeRemainder), "The mtime remainder must be 0-3 bytes.");
        }

        // Header layout:
        // CRC(2) + Type(1) + Flags(2) + HeaderSize(2) = 7 (base)
        // ADD_SIZE(4) + UNP_SIZE(4) + HOST_OS(1) + FILE_CRC(4) + FILE_TIME(4) + UNP_VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4) = 25
        // + NAME(variable)
        // Extended time (when ExtTime set): flags word(2) + optional mtime remainder(0-3) +
        // optional ctime DOS(4) + optional atime DOS(4). mtime's own DOS date always reuses the
        // base FILE_TIME field — only its sub-second remainder (if any) lives here.
        bool hasExtTime = (extraFlags & RARFileFlags.ExtTime) != 0;
        int mtimeRemainderCount = mtimeRemainder?.Length ?? 0;
        int extTimeSize = 0;
        if (hasExtTime)
        {
            extTimeSize = 2 + mtimeRemainderCount; // extended-time flags word + optional mtime remainder
            if (creationTimeDOS.HasValue)
            {
                extTimeSize += 4; // ctime DOS date
            }

            if (accessTimeDOS.HasValue)
            {
                extTimeSize += 4; // atime DOS date
            }
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

        if (hasExtTime)
        {
            int extTimeOffset = 32 + nameSize;

            // Extended-time rmode nibbles, high->low: mtime | ctime | atime | arctime.
            // Present bit = 0x8; the low two bits are the extra 100ns remainder byte count.
            ushort extFlags = (ushort)((0x8 | mtimeRemainderCount) << 12); // mtime present + remainder count
            if (creationTimeDOS.HasValue)
            {
                extFlags |= 0x0800; // ctime present
            }

            if (accessTimeDOS.HasValue)
            {
                extFlags |= 0x0080; // atime present
            }

            BitConverter.GetBytes(extFlags).CopyTo(header, extTimeOffset);

            // The mtime remainder (if any) immediately follows the flags word; ctime then atime
            // follow that, each as its own DOS date (mtime's DOS date reuses base FILE_TIME).
            int timeOffset = extTimeOffset + 2;
            if (mtimeRemainderCount > 0)
            {
                mtimeRemainder!.CopyTo(header, timeOffset);
                timeOffset += mtimeRemainderCount;
            }

            if (creationTimeDOS.HasValue)
            {
                BitConverter.GetBytes(creationTimeDOS.Value).CopyTo(header, timeOffset);
                timeOffset += 4;
            }

            if (accessTimeDOS.HasValue)
            {
                BitConverter.GetBytes(accessTimeDOS.Value).CopyTo(header, timeOffset);
                timeOffset += 4;
            }
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
    /// File header carrying BOTH RARFileFlags.Unicode and RARFileFlags.Large: name field is
    /// "&lt;ansi&gt;\0&lt;encoded&gt;" (RAR unicode name format), preceded by the 8-byte
    /// HIGH_PACK/HIGH_UNP pair. The builder round-trips the emitted name bytes through
    /// RARUtils.DecodeFileName and throws if the decode does not equal
    /// <paramref name="fileName"/> — the fixture can never drift from the decoder.
    /// </summary>
    public RAR4HeaderBuilder AddUnicodeLargeFileHeader(
        string fileName, ulong packedSize, ulong unpackedSize, uint fileCRC = 0)
    {
        byte[] nameBytes = EncodeUnicodeName(fileName);

        string? decoded = RARUtils.DecodeFileName(nameBytes, hasUnicode: true);
        if (decoded != fileName)
        {
            throw new InvalidOperationException(
                $"Unicode name encoding round-trip mismatch: expected '{fileName}', got '{decoded}'.");
        }

        ushort nameSize = (ushort)nameBytes.Length;
        uint packedSizeLow = (uint)(packedSize & 0xFFFFFFFF);
        uint packedSizeHigh = (uint)(packedSize >> 32);
        uint unpackedSizeLow = (uint)(unpackedSize & 0xFFFFFFFF);
        uint unpackedSizeHigh = (uint)(unpackedSize >> 32);

        const byte hostOS = 2; // Windows
        const uint fileTimeDOS = 0x5A8E3100;
        const byte unpVer = 29;
        const byte method = 0x33; // Normal
        const uint fileAttributes = 0x00000020; // Archive

        RARFileFlags flags = RARFileFlags.LongBlock | RARFileFlags.Large | RARFileFlags.Unicode | RARFileFlags.ExtTime;

        // Header with LARGE adds HIGH_PACK_SIZE(4) + HIGH_UNP_SIZE(4) = 8 extra bytes;
        // ExtTime adds a 2-byte flags word (mtime reuses the base FILE_TIME field).
        ushort headerSize = (ushort)(7 + 25 + 8 + nameSize + 2);

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
        // Composite Unicode name at offset 40
        nameBytes.CopyTo(header, 40);

        int extTimeOffset = 40 + nameSize;
        ushort extFlags = 0x8000; // mtime present, no remainder bytes (reuses base FILE_TIME)
        BitConverter.GetBytes(extFlags).CopyTo(header, extTimeOffset);

        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);

        _writer.Write(header);
        return this;
    }

    /// <summary>
    /// Builds a RAR unicode name field ("&lt;ansi-lossy&gt;\0&lt;encoded&gt;") for
    /// <paramref name="fileName"/>, encoding EVERY character with opcode mode 2 (both bytes
    /// explicit: low byte then high byte) — the simplest strategy the format allows, and the one
    /// <see cref="RARUtils.DecodeFileName"/>'s mode-2 branch decodes unconditionally regardless of
    /// the "standard name" ANSI fallback or the shared high-byte page. The ANSI fallback is
    /// therefore never consulted for the decoded value, only used (lossily) as the pre-NUL
    /// standard name RAR readers fall back to when Unicode decoding is unavailable.
    /// </summary>
    private static byte[] EncodeUnicodeName(string fileName)
    {
        byte[] stdName = Encoding.ASCII.GetBytes(fileName);

        List<byte> encData = [0x00]; // shared high-byte page; unused since every char is mode 2
        int i = 0;
        while (i < fileName.Length)
        {
            int groupSize = Math.Min(4, fileName.Length - i);

            // One flags byte covers up to 4 characters, 2 bits each, MSB-first. Opcode 2 (binary
            // 10) repeated 4 times is 0xAA; a short final group still uses 0xAA; the decoder never
            // reads the unused trailing slots because it stops once encData is exhausted.
            encData.Add(0xAA);
            for (int j = 0; j < groupSize; j++)
            {
                char c = fileName[i + j];
                encData.Add((byte)(c & 0xFF));        // low byte
                encData.Add((byte)((c >> 8) & 0xFF)); // high byte
            }

            i += groupSize;
        }

        return [.. stdName, 0x00, .. encData];
    }

    /// <summary>Old-style recovery block (0x78): base header + LONG_BLOCK ADD_SIZE, data ABSENT
    /// (SRR-stripped shape — real old-style recovery data is never preserved in an SRR).</summary>
    public RAR4HeaderBuilder AddProtectBlock(uint declaredDataSize)
    {
        byte[] header = new byte[11];
        header[2] = (byte)RAR4BlockType.Protect;
        BitConverter.GetBytes((ushort)RARFileFlags.LongBlock).CopyTo(header, 3);
        BitConverter.GetBytes((ushort)11).CopyTo(header, 5);
        BitConverter.GetBytes(declaredDataSize).CopyTo(header, 7);
        WriteCrc(header);
        _writer.Write(header);
        return this;
    }

    /// <summary>MALFORMED end-of-archive block (0x7B) that incorrectly declares an ADD_SIZE via
    /// LONG_BLOCK. Per RAR4HeaderLayout, EndArchive has no ADD_SIZE field — no real writer
    /// produces this shape; it exists solely to test the "malformed EndArchive" detection path.
    /// </summary>
    public RAR4HeaderBuilder AddMalformedEndArchiveWithAddSize(uint declaredAddSize)
    {
        byte[] header = new byte[11];
        header[2] = (byte)RAR4BlockType.EndArchive;
        BitConverter.GetBytes((ushort)RARFileFlags.LongBlock).CopyTo(header, 3);
        BitConverter.GetBytes((ushort)11).CopyTo(header, 5);
        BitConverter.GetBytes(declaredAddSize).CopyTo(header, 7);
        WriteCrc(header);
        _writer.Write(header);
        return this;
    }

    /// <summary>
    /// Computes the RAR4 header CRC16 (the low 16 bits of a CRC32 over the header bytes from
    /// offset 2 onward) and writes it into the header's first 2 bytes. Shared by <see
    /// cref="AddProtectBlock"/> and <see cref="WriteFileShapedHeader"/>.
    /// </summary>
    private static void WriteCrc(byte[] header)
    {
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort crc = (ushort)(crc32 & 0xFFFF);
        BitConverter.GetBytes(crc).CopyTo(header, 0);
    }

    /// <summary>
    /// Writes a RAR4 block using the file-header field layout (base header + ADD_SIZE/UNP_SIZE/
    /// HOST_OS/DATA_CRC/FILE_TIME/UNP_VER/METHOD/NAME_SIZE/ATTR + NAME), with proper CRC — the
    /// layout every RAR4 "file-header-shaped" block shares (the FileHeader block itself has its
    /// own emitter; this one backs the Service-block emitters: CMT/RR/AV/etc.). Does not write
    /// payload bytes — callers write <paramref name="addSize"/> bytes themselves, or omit them
    /// entirely for a declared-but-absent (SRR-stripped) shape.
    /// </summary>
    private void WriteFileShapedHeader(
        byte blockType, string name, uint addSize, RARFileFlags extra,
        byte hostOS = 2, uint fileTimeDOS = 0, byte method = 0x30, uint fileAttributes = 0x00000020)
    {
        byte[] subTypeName = Encoding.ASCII.GetBytes(name);

        // Header: CRC(2) + Type(1) + Flags(2) + HeaderSize(2) = 7
        // ADD_SIZE(4) + UNP_SIZE(4) + HOST_OS(1) + DATA_CRC(4) + FILE_TIME(4) + UNP_VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4) = 25
        // + NAME(variable)
        ushort headerSize = (ushort)(7 + 25 + subTypeName.Length);

        byte[] header = new byte[headerSize];
        header[2] = blockType;
        ushort headerFlags = (ushort)(RARFileFlags.LongBlock | extra);
        BitConverter.GetBytes(headerFlags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(addSize).CopyTo(header, 7);         // ADD_SIZE = packed size
        BitConverter.GetBytes(addSize).CopyTo(header, 11);        // UNP_SIZE (stored: same as ADD_SIZE)
        header[15] = hostOS;                                       // HOST_OS
        BitConverter.GetBytes((uint)0).CopyTo(header, 16);         // DATA_CRC (placeholder)
        BitConverter.GetBytes(fileTimeDOS).CopyTo(header, 20);     // FILE_TIME
        header[24] = 29;                                            // UNP_VER
        header[25] = method;                                        // METHOD
        BitConverter.GetBytes((ushort)subTypeName.Length).CopyTo(header, 26); // NAME_SIZE
        BitConverter.GetBytes(fileAttributes).CopyTo(header, 28);  // ATTR
        subTypeName.CopyTo(header, 32);                             // NAME

        WriteCrc(header);
        _writer.Write(header);
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
        WriteFileShapedHeader((byte)RAR4BlockType.Service, "CMT", (uint)commentData.Length,
            RARFileFlags.SkipIfUnknown, hostOS, fileTimeDOS, method, fileAttributes);
        _writer.Write(commentData); // Write the comment data after header

        return this;
    }

    /// <summary>RAR4 service block (0x7A, file-header layout) named e.g. "RR"/"AV"/"CMT";
    /// <paramref name="includeData"/>=false emits the SRR-stripped shape (header declares
    /// <paramref name="declaredDataSize"/>, data absent).</summary>
    public RAR4HeaderBuilder AddServiceBlock(string name, uint declaredDataSize, bool includeData)
    {
        WriteFileShapedHeader((byte)RAR4BlockType.Service, name, declaredDataSize, RARFileFlags.SkipIfUnknown);

        if (includeData)
        {
            byte[] data = new byte[declaredDataSize];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 251);
            }

            _writer.Write(data);
        }

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
