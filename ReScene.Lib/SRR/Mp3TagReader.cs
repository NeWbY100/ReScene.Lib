using System.Buffers.Binary;
using System.Text;

namespace SRR;

/// <summary>
/// Utility class for detecting and measuring MP3 tags at the beginning and end of files.
/// Supports ID3v2, ID3v1, Lyrics3v1, Lyrics3v2, APEv1, and APEv2 tags.
/// Based on pyrescene mp3.py implementation.
/// </summary>
public static class Mp3TagReader
{
    /// <summary>
    /// Returns the byte offset where audio data begins (after all header tags).
    /// Handles multiple consecutive ID3v2 tags.
    /// </summary>
    public static long FindAudioStart(Stream stream)
    {
        long audioStart = 0;

        // Check for one or more consecutive ID3v2 tags at the beginning
        while (true)
        {
            stream.Position = audioStart;
            var (found, size) = DetectId3v2(stream);
            if (found)
            {
                audioStart += size;
            }
            else
            {
                break;
            }
        }

        return audioStart;
    }

    /// <summary>
    /// Returns the byte offset where audio data ends (before all footer tags).
    /// Checks for ID3v1, Lyrics3v2, Lyrics3v1, and APEv2/APEv1 tags working
    /// inward from the end of the file.
    /// </summary>
    public static long FindAudioEnd(Stream stream)
    {
        long endOffset = stream.Length;

        // 1) Check for ID3v1 (last 128 bytes)
        var (id3v1Found, id3v1Size) = DetectId3v1(stream);
        if (id3v1Found)
        {
            endOffset -= id3v1Size;
        }

        // 2) Check for Lyrics3v2 (before ID3v1)
        var (lyrics3v2Found, lyrics3v2Size) = DetectLyrics3v2(stream, endOffset);
        if (lyrics3v2Found)
        {
            endOffset -= lyrics3v2Size;
        }
        else
        {
            // 3) Check for Lyrics3v1 (before ID3v1, only if no Lyrics3v2)
            var (lyrics3v1Found, lyrics3v1Size) = DetectLyrics3v1(stream, endOffset);
            if (lyrics3v1Found)
            {
                endOffset -= lyrics3v1Size;
            }
        }

        // 4) Check for APEv2/APEv1 (before ID3v1/Lyrics3)
        var (apeFound, apeSize) = DetectApeTag(stream, endOffset);
        if (apeFound)
        {
            endOffset -= apeSize;
        }

        return endOffset;
    }

    /// <summary>
    /// Detects an ID3v2 tag at the current stream position.
    /// The stream position should be set before calling.
    /// </summary>
    /// <returns>Whether found and the total tag size (header + body, excluding footer).</returns>
    public static (bool found, int size) DetectId3v2(Stream stream)
    {
        long startPos = stream.Position;

        if (stream.Length - startPos < 10)
            return (false, 0);

        Span<byte> header = stackalloc byte[10];
        int read = stream.Read(header);
        if (read < 10)
            return (false, 0);

        // Check "ID3" marker
        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3')
            return (false, 0);

        // Bytes 3-4: version, byte 5: flags, bytes 6-9: syncsafe size
        int size = DecodeSyncSafeInt(header[6], header[7], header[8], header[9]);
        int totalSize = 10 + size; // header (10 bytes) + body

        return (true, totalSize);
    }

    /// <summary>
    /// Detects an ID3v1 tag at the end of the file (last 128 bytes starting with "TAG").
    /// </summary>
    public static (bool found, int size) DetectId3v1(Stream stream)
    {
        if (stream.Length < 128)
            return (false, 0);

        stream.Position = stream.Length - 128;
        Span<byte> marker = stackalloc byte[3];
        int read = stream.Read(marker);
        if (read < 3)
            return (false, 0);

        if (marker[0] == 'T' && marker[1] == 'A' && marker[2] == 'G')
            return (true, 128);

        return (false, 0);
    }

    /// <summary>
    /// Detects a Lyrics3v2 tag before the given end offset.
    /// Lyrics3v2 ends with a 6-byte ASCII decimal size + "LYRICS200".
    /// The size value covers "LYRICSBEGIN" + body (not the 6-byte size field or "LYRICS200").
    /// </summary>
    public static (bool found, int size) DetectLyrics3v2(Stream stream)
    {
        return DetectLyrics3v2(stream, stream.Length);
    }

    public static (bool found, int size) DetectLyrics3v2(Stream stream, long endOffset)
    {
        // Need at least 6 (size) + 9 ("LYRICS200") = 15 bytes before endOffset
        if (endOffset - 15 < 0)
            return (false, 0);

        stream.Position = endOffset - 15;
        Span<byte> footer = stackalloc byte[15];
        int read = stream.Read(footer);
        if (read < 15)
            return (false, 0);

        // Check for "LYRICS200" at the end
        ReadOnlySpan<byte> lyrics200 = "LYRICS200"u8;
        if (!footer.Slice(6, 9).SequenceEqual(lyrics200))
            return (false, 0);

        // Parse 6-byte ASCII decimal size
        string sizeStr = Encoding.ASCII.GetString(footer.Slice(0, 6));
        if (!int.TryParse(sizeStr, out int lyricsSize))
            return (false, 0);

        // Total tag size = content size + 6 (size field) + 9 ("LYRICS200")
        int totalSize = lyricsSize + 6 + 9;
        return (true, totalSize);
    }

    /// <summary>
    /// Detects a Lyrics3v1 tag before the given end offset.
    /// Lyrics3v1 ends with "LYRICSEND" and begins with "LYRICSBEGIN".
    /// Max content is ~5100 bytes.
    /// </summary>
    public static (bool found, int size) DetectLyrics3v1(Stream stream)
    {
        return DetectLyrics3v1(stream, stream.Length);
    }

    public static (bool found, int size) DetectLyrics3v1(Stream stream, long endOffset)
    {
        // Need at least 9 bytes ("LYRICSEND") before endOffset
        if (endOffset - 9 < 0)
            return (false, 0);

        stream.Position = endOffset - 9;
        Span<byte> endMarker = stackalloc byte[9];
        int read = stream.Read(endMarker);
        if (read < 9)
            return (false, 0);

        if (!endMarker.SequenceEqual("LYRICSEND"u8))
            return (false, 0);

        // Search backward for "LYRICSBEGIN" within ~5100 bytes
        int searchSize = (int)Math.Min(5100, endOffset);
        long searchStart = endOffset - searchSize;

        stream.Position = searchStart;
        byte[] searchData = new byte[searchSize];
        read = stream.Read(searchData, 0, searchSize);

        ReadOnlySpan<byte> beginMarker = "LYRICSBEGIN"u8;
        int index = searchData.AsSpan(0, read).IndexOf(beginMarker);

        if (index < 0)
            return (false, 0);

        long absoluteStart = searchStart + index;
        int totalSize = (int)(endOffset - absoluteStart);
        return (true, totalSize);
    }

    /// <summary>
    /// Detects an APEv2 tag before the given end offset.
    /// APEv2 has a 32-byte footer with "APETAGEX" preamble.
    /// </summary>
    public static (bool found, int size) DetectApeV2(Stream stream)
    {
        return DetectApeTag(stream, stream.Length);
    }

    /// <summary>
    /// Detects an APEv1 tag. Uses the same detection as APEv2 but
    /// version 1000 means no header (only footer + items).
    /// </summary>
    public static (bool found, int size) DetectApeV1(Stream stream)
    {
        return DetectApeTag(stream, stream.Length);
    }

    /// <summary>
    /// Detects an APE tag (v1 or v2) before the given end offset.
    /// Reads the 32-byte footer to determine version and tag size.
    /// </summary>
    public static (bool found, int size) DetectApeTag(Stream stream, long endOffset)
    {
        // APE footer is 32 bytes
        if (endOffset - 32 < 0)
            return (false, 0);

        stream.Position = endOffset - 32;
        Span<byte> footer = stackalloc byte[32];
        int read = stream.Read(footer);
        if (read < 32)
            return (false, 0);

        // Check "APETAGEX" preamble
        if (!footer.Slice(0, 8).SequenceEqual("APETAGEX"u8))
            return (false, 0);

        // Bytes 8-11: version (LE uint32) - 1000 for APEv1, 2000 for APEv2
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(8));

        // Bytes 12-15: tag size including footer and all items, but NOT the header
        uint tagSize = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(12));

        // APEv2 has a 32-byte header in addition; APEv1 has no header
        int headerSize = version == 2000 ? 32 : 0;

        int totalSize = (int)tagSize + headerSize;
        return (true, totalSize);
    }

    /// <summary>
    /// Decodes a 4-byte ID3v2 syncsafe integer. Each byte uses only 7 bits (MSB always 0).
    /// </summary>
    public static int DecodeSyncSafeInt(byte b0, byte b1, byte b2, byte b3)
    {
        return (b0 << 21) | (b1 << 14) | (b2 << 7) | b3;
    }

    /// <summary>
    /// Encodes an integer as a 4-byte ID3v2 syncsafe integer.
    /// </summary>
    public static byte[] EncodeSyncSafeInt(int size)
    {
        return
        [
            (byte)((size >> 21) & 0x7F),
            (byte)((size >> 14) & 0x7F),
            (byte)((size >> 7) & 0x7F),
            (byte)(size & 0x7F)
        ];
    }
}
