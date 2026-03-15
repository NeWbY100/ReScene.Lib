namespace ReScene.SRS;

/// <summary>
/// Utility class for reading FLAC metadata block headers and detecting
/// ID3v2 wrappers that may precede the fLaC marker.
/// Based on pyrescene flac.py implementation.
/// </summary>
/// <remarks>
/// FLAC metadata block types:
///   0 = STREAMINFO, 1 = PADDING, 2 = APPLICATION, 3 = SEEKTABLE,
///   4 = VORBIS_COMMENT, 5 = CUESHEET, 6 = PICTURE
/// All numbers in FLAC are big-endian and unsigned unless otherwise specified.
/// </remarks>
public static class FlacMetadataReader
{
    /// <summary>
    /// Returns the byte offset where FLAC frame data begins (after all metadata blocks).
    /// Handles optional ID3v2 wrapper before the fLaC marker.
    /// </summary>
    public static long FindFrameDataStart(Stream stream)
    {
        stream.Position = 0;

        // Check for ID3v2 wrapper
        var (id3Found, id3Size) = DetectId3v2Wrapper(stream);
        long offset = id3Found ? id3Size : 0;

        // Expect fLaC marker
        stream.Position = offset;
        Span<byte> marker = stackalloc byte[4];
        if (stream.Read(marker) < 4)
            throw new InvalidDataException("Stream too short to contain fLaC marker.");

        if (marker[0] != 'f' || marker[1] != 'L' || marker[2] != 'a' || marker[3] != 'C')
            throw new InvalidDataException("Expected fLaC marker not found.");

        offset += 4; // skip fLaC marker

        // Walk metadata blocks until we find the last one
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        while (stream.Position + 4 <= stream.Length)
        {
            var (isLast, _, length) = ReadMetadataBlockHeader(reader);
            stream.Position += length; // skip payload

            if (isLast)
                break;
        }

        return stream.Position;
    }

    /// <summary>
    /// Checks for an ID3v2 tag before the fLaC marker.
    /// Some FLAC files are wrapped with an ID3v2 header.
    /// </summary>
    public static (bool found, int size) DetectId3v2Wrapper(Stream stream)
    {
        stream.Position = 0;

        if (stream.Length < 10)
            return (false, 0);

        Span<byte> header = stackalloc byte[10];
        int read = stream.Read(header);
        if (read < 10)
            return (false, 0);

        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3')
            return (false, 0);

        int size = Mp3TagReader.DecodeSyncSafeInt(header[6], header[7], header[8], header[9]);
        int totalSize = 10 + size;

        return (true, totalSize);
    }

    /// <summary>
    /// Reads a FLAC metadata block header (4 bytes).
    /// Format: isLast (1 bit) + type (7 bits) + length (3 bytes big-endian).
    /// The length does not include the 4-byte header itself.
    /// </summary>
    public static (bool isLast, byte type, int length) ReadMetadataBlockHeader(BinaryReader reader)
    {
        byte typeByte = reader.ReadByte();
        bool isLast = (typeByte & 0x80) != 0;
        byte type = (byte)(typeByte & 0x7F);

        byte[] sizeBytes = reader.ReadBytes(3);
        if (sizeBytes.Length < 3)
            throw new InvalidDataException("Unexpected end of stream reading metadata block header.");

        int length = (sizeBytes[0] << 16) | (sizeBytes[1] << 8) | sizeBytes[2];

        return (isLast, type, length);
    }

    /// <summary>
    /// Gets a human-readable name for a FLAC metadata block type.
    /// </summary>
    public static string GetBlockTypeName(byte type) => type switch
    {
        0 => "STREAMINFO",
        1 => "PADDING",
        2 => "APPLICATION",
        3 => "SEEKTABLE",
        4 => "VORBIS_COMMENT",
        5 => "CUESHEET",
        6 => "PICTURE",
        _ => $"UNKNOWN({type})"
    };
}
