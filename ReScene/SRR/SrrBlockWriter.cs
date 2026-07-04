using System.Text;

namespace ReScene.SRR;

/// <summary>
/// Shared writers for SRR block framing. Centralizes block-emission logic that was previously
/// duplicated verbatim across the SRR creation and editing paths.
/// </summary>
internal static class SrrBlockWriter
{
    /// <summary>
    /// Writes an SRR Stored File block (type <c>0x6A</c>): header (sentinel CRC, type, LONG_BLOCK
    /// flags, header size, data length, name length, name) followed by the file payload.
    /// </summary>
    public static void WriteStoredFileBlock(BinaryWriter writer, string fileName, byte[] fileData)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        ushort headerSize = (ushort)(SrrBlockLayout.BaseHeaderSize + SrrBlockLayout.AddSizeFieldLength + SrrBlockLayout.NameLengthFieldLength + nameBytes.Length);
        uint addSize = (uint)fileData.Length;

        writer.Write(SrrBlockLayout.StoredFileSentinel);    // CRC (SRR stored file sentinel)
        writer.Write((byte)SRRBlockType.StoredFile);        // StoredFile type
        writer.Write((ushort)SRRBlockFlags.LongBlock);      // flags: LONG_BLOCK
        writer.Write(headerSize);
        writer.Write(addSize);                  // data length
        writer.Write((ushort)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write(fileData);                 // file data
    }
}
