using System.Buffers.Binary;
using System.Text;

namespace ReScene.SRS;

/// <summary>
/// Serializes SRSF (file data) and SRST (track data) payloads for SRS files.
/// </summary>
internal static class SRSPayloadSerializer
{
    /// <summary>
    /// Serializes the SRSF (file data) payload - format matches pyrescene exactly.
    /// Layout: flags(2) + appNameLen(2) + appName + fileNameLen(2) + fileName + sampleSize(8) + crc32(4)
    /// </summary>
    public static byte[] SerializeSrsf(string samplePath, long sampleSize, uint sampleCRC32,
        SRSCreationOptions options)
    {
        byte[] appNameBytes = Encoding.UTF8.GetBytes(options.AppName);
        byte[] fileNameBytes = Encoding.UTF8.GetBytes(Path.GetFileName(samplePath));

        int totalLen = 2 + 2 + appNameBytes.Length + 2 + fileNameBytes.Length + 8 + 4;
        byte[] buffer = new byte[totalLen];
        int pos = 0;

        // Flags: SIMPLE_BLOCK_FIX | ATTACHMENTS_REMOVED = 0x03
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), 0x0003);
        pos += 2;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), (ushort)appNameBytes.Length);
        pos += 2;
        appNameBytes.CopyTo(buffer, pos);
        pos += appNameBytes.Length;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), (ushort)fileNameBytes.Length);
        pos += 2;
        fileNameBytes.CopyTo(buffer, pos);
        pos += fileNameBytes.Length;

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(pos), (ulong)sampleSize);
        pos += 8;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), sampleCRC32);

        return buffer;
    }

    /// <summary>
    /// Serializes the SRST (track data) payload - format matches pyrescene exactly.
    /// Layout: flags(2) + trackNum(2|4) + dataLength(4|8) + matchOffset(8) + sigLen(2) + sig
    /// </summary>
    public static byte[] SerializeSrst(TrackInfo track, bool bigFile)
    {
        ushort flags = 0;
        bool bigTrackNumber = track.TrackNumber >= 65536;

        if (bigFile)
        {
            flags |= 0x4;
        }

        if (bigTrackNumber)
        {
            flags |= 0x8;
        }

        int trackNumSize = bigTrackNumber ? 4 : 2;
        int dataLenSize = bigFile ? 8 : 4;
        int totalLen = 2 + trackNumSize + dataLenSize + 8 + 2 + track.SignatureBytes.Length;

        byte[] buffer = new byte[totalLen];
        int pos = 0;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), flags);
        pos += 2;

        if (bigTrackNumber)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)track.TrackNumber);
            pos += 4;
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), (ushort)track.TrackNumber);
            pos += 2;
        }

        if (bigFile)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(pos), (ulong)track.DataLength);
            pos += 8;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)track.DataLength);
            pos += 4;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(pos), (ulong)track.MatchOffset);
        pos += 8;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), (ushort)track.SignatureBytes.Length);
        pos += 2;

        track.SignatureBytes.CopyTo(buffer, pos);

        return buffer;
    }
}
