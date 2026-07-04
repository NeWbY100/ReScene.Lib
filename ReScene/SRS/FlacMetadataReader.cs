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
internal static class FlacMetadataReader
{
    /// <summary>
    /// Returns the byte offset where FLAC frame data begins (after all metadata blocks).
    /// Handles optional ID3v2 wrapper before the fLaC marker.
    /// </summary>
    /// <param name="stream">
    /// The FLAC file stream.
    /// </param>
    /// <returns>
    /// The byte offset where frame data begins.
    /// </returns>
    public static long FindFrameDataStart(Stream stream)
    {
        stream.Position = 0;

        // Check for ID3v2 wrapper
        (bool id3Found, int id3Size) = DetectId3v2Wrapper(stream);
        long offset = id3Found ? id3Size : 0;

        // Expect fLaC marker
        stream.Position = offset;
        Span<byte> marker = stackalloc byte[FlacConstants.MarkerSize];
        if (stream.Read(marker) < FlacConstants.MarkerSize)
        {
            throw new InvalidDataException("Stream too short to contain fLaC marker.");
        }

        if (marker[0] != 'f' || marker[1] != 'L' || marker[2] != 'a' || marker[3] != 'C')
        {
            throw new InvalidDataException("Expected fLaC marker not found.");
        }

        offset += FlacConstants.MarkerSize; // skip fLaC marker

        // Walk metadata blocks until we find the last one
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        while (stream.Position + FlacConstants.BlockHeaderSize <= stream.Length)
        {
            (bool isLast, byte _, int length) = ReadMetadataBlockHeader(reader);
            stream.Position += length; // skip payload

            if (isLast)
            {
                break;
            }
        }

        return stream.Position;
    }

    /// <summary>
    /// Checks for an ID3v2 tag before the fLaC marker.
    /// Some FLAC files are wrapped with an ID3v2 header.
    /// </summary>
    /// <param name="stream">
    /// The FLAC file stream.
    /// </param>
    /// <returns>
    /// A tuple indicating whether an ID3v2 wrapper was found and its total size.
    /// </returns>
    public static (bool found, int size) DetectId3v2Wrapper(Stream stream)
    {
        stream.Position = 0;

        if (stream.Length < MP3TagReader.Id3v2HeaderSize)
        {
            return (false, 0);
        }

        Span<byte> header = stackalloc byte[MP3TagReader.Id3v2HeaderSize];
        int read = stream.Read(header);
        if (read < MP3TagReader.Id3v2HeaderSize)
        {
            return (false, 0);
        }

        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3')
        {
            return (false, 0);
        }

        int size = MP3TagReader.DecodeSyncSafeInt(header[6], header[7], header[8], header[9]);
        int totalSize = MP3TagReader.Id3v2HeaderSize + size;

        return (true, totalSize);
    }

    /// <summary>
    /// Reads a FLAC metadata block header (4 bytes).
    /// Format: isLast (1 bit) + type (7 bits) + length (3 bytes big-endian).
    /// The length does not include the 4-byte header itself.
    /// </summary>
    /// <param name="reader">
    /// The binary reader positioned at the block header.
    /// </param>
    /// <returns>
    /// A tuple with the last-block flag, block type, and payload length.
    /// </returns>
    public static (bool isLast, byte type, int length) ReadMetadataBlockHeader(BinaryReader reader)
    {
        byte typeByte = reader.ReadByte();
        bool isLast = (typeByte & FlacConstants.LastBlockFlag) != 0;
        byte type = (byte)(typeByte & FlacConstants.BlockTypeMask);

        byte[] sizeBytes = reader.ReadBytes(FlacConstants.BlockSizeFieldWidth);
        if (sizeBytes.Length < FlacConstants.BlockSizeFieldWidth)
        {
            throw new InvalidDataException("Unexpected end of stream reading metadata block header.");
        }

        int length = (sizeBytes[0] << 16) | (sizeBytes[1] << 8) | sizeBytes[2];

        return (isLast, type, length);
    }

    /// <summary>
    /// Gets a human-readable name for a FLAC metadata block type.
    /// </summary>
    /// <param name="type">
    /// The FLAC metadata block type byte.
    /// </param>
    /// <returns>
    /// A human-readable block type name.
    /// </returns>
    public static string GetBlockTypeName(byte type) => (FlacBlockType)type switch
    {
        FlacBlockType.Streaminfo => "STREAMINFO",
        FlacBlockType.Padding => "PADDING",
        FlacBlockType.Application => "APPLICATION",
        FlacBlockType.Seektable => "SEEKTABLE",
        FlacBlockType.VorbisComment => "VORBIS_COMMENT",
        FlacBlockType.Cuesheet => "CUESHEET",
        FlacBlockType.Picture => "PICTURE",
        _ => $"UNKNOWN({type})"
    };
}
