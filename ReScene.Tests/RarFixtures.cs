using System.Text;
using Force.Crc32;
using ReScene.RAR;

namespace ReScene.Tests;

/// <summary>
/// Builds real, on-disk multi-volume RAR4 test archives — not the headers-only synthetic SRR
/// data <see cref="SRRTestDataBuilder"/>/<see cref="RAR4HeaderBuilder"/> produce. Extends the
/// <c>CreateMinimalRAR4File</c> idiom from <see cref="SRRWriterTests"/> (marker + archive header
/// + one store-mode (method 0x30) file header with real fake packed data + end block) to emit an
/// N-volume set, so <c>SRRWriter.CreateFromInputsAsync</c> has real files to open from disk (SFV-
/// referenced volumes, direct-.rar first-volume inputs, and volume-chain walking all need actual
/// bytes on disk, unlike the header-only fixtures the other builders emit).
/// </summary>
internal static class RarFixtures
{
    private static readonly byte[] RAR4Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    /// <summary>
    /// Writes an old-style-named (<c>{baseName}.rar</c>, <c>{baseName}.r00</c>, ...) store-mode
    /// RAR4 volume set of <paramref name="volumeCount"/> volumes into <paramref name="dir"/>.
    /// </summary>
    public static void WriteStoreModeRarSet(string dir, string baseName, int volumeCount, int payloadBytes)
    {
        for (int i = 0; i < volumeCount; i++)
        {
            string fileName = i == 0 ? $"{baseName}.rar" : $"{baseName}.r{i - 1:D2}";
            WriteVolume(Path.Combine(dir, fileName), $"{baseName}.dat", payloadBytes, i, volumeCount);
        }
    }

    /// <summary>
    /// Writes a new-style-named (<c>{baseName}.partN.rar</c>, <paramref name="digitWidth"/>-digit
    /// zero-padded) store-mode RAR4 volume set — for first-volume-rule tests that need
    /// part1/part01/part001 naming.
    /// </summary>
    public static void WriteStoreModePartRarSet(
        string dir, string baseName, int volumeCount, int payloadBytes, int digitWidth)
    {
        for (int i = 0; i < volumeCount; i++)
        {
            string partNum = (i + 1).ToString($"D{digitWidth}");
            WriteVolume(Path.Combine(dir, $"{baseName}.part{partNum}.rar"), $"{baseName}.dat", payloadBytes, i, volumeCount);
        }
    }

    private static void WriteVolume(string path, string archivedFileName, int payloadBytes, int index, int volumeCount)
    {
        RARArchiveFlags archiveFlags = RARArchiveFlags.Volume | RARArchiveFlags.NewNumbering;
        if (index == 0)
        {
            archiveFlags |= RARArchiveFlags.FirstVolume;
        }

        RARFileFlags fileFlags = RARFileFlags.LongBlock | RARFileFlags.ExtTime;
        if (volumeCount > 1)
        {
            if (index > 0)
            {
                fileFlags |= RARFileFlags.SplitBefore;
            }

            if (index < volumeCount - 1)
            {
                fileFlags |= RARFileFlags.SplitAfter;
            }
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        writer.Write(RAR4Marker);
        WriteArchiveHeader(writer, archiveFlags);
        byte[] payload = new byte[payloadBytes];
        WriteFileHeader(writer, archivedFileName, (uint)payload.Length, fileFlags);
        writer.Write(payload);
        WriteEndArchive(writer);
    }

    private static void WriteArchiveHeader(BinaryWriter writer, RARArchiveFlags flags)
    {
        ushort headerSize = 13;
        byte[] header = new byte[headerSize];
        header[2] = 0x73;
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);

        WriteCrc(header);
        writer.Write(header);
    }

    private static void WriteFileHeader(BinaryWriter writer, string fileName, uint packedSize, RARFileFlags flags)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
        ushort nameSize = (ushort)nameBytes.Length;
        int extTimeSize = (flags & RARFileFlags.ExtTime) != 0 ? 2 : 0;
        ushort headerSize = (ushort)(7 + 25 + nameSize + extTimeSize);

        byte[] header = new byte[headerSize];
        header[2] = 0x74;
        BitConverter.GetBytes((ushort)flags).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);
        BitConverter.GetBytes(packedSize).CopyTo(header, 7);    // ADD_SIZE (packed size)
        BitConverter.GetBytes(packedSize).CopyTo(header, 11);   // UNP_SIZE (store: unpacked == packed)
        header[15] = 2;                                          // HOST_OS (Windows)
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(header, 16);   // FILE_CRC
        BitConverter.GetBytes(0x5A8E3100u).CopyTo(header, 20);   // FILE_TIME (DOS)
        header[24] = 29;                                         // UNP_VER
        header[25] = 0x30;                                       // METHOD: Store
        BitConverter.GetBytes(nameSize).CopyTo(header, 26);
        BitConverter.GetBytes(0x00000020u).CopyTo(header, 28);   // ATTR
        nameBytes.CopyTo(header, 32);

        if ((flags & RARFileFlags.ExtTime) != 0)
        {
            BitConverter.GetBytes((ushort)0x8000).CopyTo(header, 32 + nameSize);
        }

        WriteCrc(header);
        writer.Write(header);
    }

    private static void WriteEndArchive(BinaryWriter writer)
    {
        ushort headerSize = 7;
        byte[] header = new byte[headerSize];
        header[2] = 0x7B;
        BitConverter.GetBytes((ushort)0).CopyTo(header, 3);
        BitConverter.GetBytes(headerSize).CopyTo(header, 5);

        WriteCrc(header);
        writer.Write(header);
    }

    private static void WriteCrc(byte[] header)
    {
        uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);
    }
}
