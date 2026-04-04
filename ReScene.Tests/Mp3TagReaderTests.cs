using ReScene.SRS;
using System.Buffers.Binary;
using System.Text;

namespace ReScene.Tests;

/// <summary>
/// Tests for Mp3TagReader: ID3v2, ID3v1, Lyrics3v1, Lyrics3v2, APEv2/v1 detection,
/// syncsafe integer encoding/decoding, and FindAudioStart/FindAudioEnd.
/// Uses synthetic MemoryStream data.
/// </summary>
public class Mp3TagReaderTests
{
    #region SyncSafe Integer Tests

    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0x00, 0)]
    [InlineData(0x00, 0x00, 0x00, 0x7F, 127)]
    [InlineData(0x00, 0x00, 0x01, 0x00, 128)]
    [InlineData(0x00, 0x00, 0x02, 0x01, 257)]
    [InlineData(0x00, 0x01, 0x00, 0x00, 16384)]
    [InlineData(0x01, 0x00, 0x00, 0x00, 2097152)]
    [InlineData(0x7F, 0x7F, 0x7F, 0x7F, 268435455)] // max value
    public void DecodeSyncSafeInt_CorrectValues(byte b0, byte b1, byte b2, byte b3, int expected)
    {
        int result = Mp3TagReader.DecodeSyncSafeInt(b0, b1, b2, b3);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(257)]
    [InlineData(16384)]
    [InlineData(2097152)]
    [InlineData(268435455)]
    public void EncodeSyncSafeInt_RoundTrips(int value)
    {
        byte[] encoded = Mp3TagReader.EncodeSyncSafeInt(value);
        Assert.Equal(4, encoded.Length);
        int decoded = Mp3TagReader.DecodeSyncSafeInt(encoded[0], encoded[1], encoded[2], encoded[3]);
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void EncodeSyncSafeInt_TopBitsAlwaysZero()
    {
        byte[] encoded = Mp3TagReader.EncodeSyncSafeInt(268435455); // max
        foreach (byte b in encoded)
        {
            Assert.Equal(0, b & 0x80);
        }
    }

    #endregion

    #region ID3v2 Detection Tests

    [Fact]
    public void DetectId3v2_ValidTag_ReturnsCorrectSize()
    {
        // Build a minimal ID3v2 header: "ID3" + version(2) + flags(1) + size(4)
        // Size = 100 bytes body -> syncsafe = 0x00 0x00 0x00 0x64
        var ms = new MemoryStream();
        ms.Write("ID3"u8);
        ms.Write([0x04, 0x00]); // version 2.4.0
        ms.WriteByte(0x00);     // flags
        ms.Write(Mp3TagReader.EncodeSyncSafeInt(100)); // body size = 100
        ms.Write(new byte[100]); // body padding
        ms.Write(new byte[50]);  // audio data

        ms.Position = 0;
        (bool found, int size) = Mp3TagReader.DetectId3v2(ms);

        Assert.True(found);
        Assert.Equal(110, size); // 10 header + 100 body
    }

    [Fact]
    public void DetectId3v2_NoTag_ReturnsFalse()
    {
        // Stream starts with audio sync word
        var ms = new MemoryStream([0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        ms.Position = 0;

        (bool found, int _) = Mp3TagReader.DetectId3v2(ms);
        Assert.False(found);
    }

    [Fact]
    public void DetectId3v2_TooShort_ReturnsFalse()
    {
        var ms = new MemoryStream([0x49, 0x44, 0x33]); // "ID3" but only 3 bytes
        ms.Position = 0;

        (bool found, int _) = Mp3TagReader.DetectId3v2(ms);
        Assert.False(found);
    }

    [Fact]
    public void DetectId3v2_AtNonZeroPosition_Works()
    {
        var ms = new MemoryStream();
        // First ID3v2 tag, 50 bytes body
        ms.Write("ID3"u8);
        ms.Write([0x03, 0x00, 0x00]);
        ms.Write(Mp3TagReader.EncodeSyncSafeInt(50));
        ms.Write(new byte[50]);
        // Second ID3v2 tag at position 60
        ms.Write("ID3"u8);
        ms.Write([0x03, 0x00, 0x00]);
        ms.Write(Mp3TagReader.EncodeSyncSafeInt(30));
        ms.Write(new byte[30]);

        // Detect second tag
        ms.Position = 60;
        (bool found, int size) = Mp3TagReader.DetectId3v2(ms);

        Assert.True(found);
        Assert.Equal(40, size); // 10 + 30
    }

    #endregion

    #region ID3v1 Detection Tests

    [Fact]
    public void DetectId3v1_ValidTag_ReturnsTrue()
    {
        var ms = new MemoryStream();
        ms.Write(new byte[200]); // audio data
        // ID3v1 at end: "TAG" + 125 bytes
        ms.Write("TAG"u8);
        ms.Write(new byte[125]);

        (bool found, int size) = Mp3TagReader.DetectId3v1(ms);

        Assert.True(found);
        Assert.Equal(128, size);
    }

    [Fact]
    public void DetectId3v1_NoTag_ReturnsFalse()
    {
        var ms = new MemoryStream(new byte[256]); // all zeros

        (bool found, int _) = Mp3TagReader.DetectId3v1(ms);
        Assert.False(found);
    }

    [Fact]
    public void DetectId3v1_FileTooShort_ReturnsFalse()
    {
        var ms = new MemoryStream(new byte[64]);

        (bool found, int _) = Mp3TagReader.DetectId3v1(ms);
        Assert.False(found);
    }

    #endregion

    #region Lyrics3v2 Detection Tests

    [Fact]
    public void DetectLyrics3v2_ValidTag_ReturnsCorrectSize()
    {
        // Build a file with Lyrics3v2 before ID3v1
        var ms = new MemoryStream();
        ms.Write(new byte[100]); // audio data

        // Lyrics3v2 block: "LYRICSBEGIN" + content + size(6 chars) + "LYRICS200"
        byte[] begin = "LYRICSBEGIN"u8.ToArray();
        byte[] content = new byte[50];
        int lyricsInnerSize = begin.Length + content.Length; // = 61
        string sizeStr = lyricsInnerSize.ToString("D6"); // "000061"
        byte[] sizeField = Encoding.ASCII.GetBytes(sizeStr);
        byte[] end = "LYRICS200"u8.ToArray();

        ms.Write(begin);
        ms.Write(content);
        ms.Write(sizeField);
        ms.Write(end);

        // ID3v1 at end
        ms.Write("TAG"u8);
        ms.Write(new byte[125]);

        // endOffset = just before ID3v1
        long endOffset = ms.Length - 128;

        (bool found, int size) = Mp3TagReader.DetectLyrics3v2(ms, endOffset);

        Assert.True(found);
        // Total = lyricsInnerSize + 6 (size field) + 9 ("LYRICS200") = 61 + 6 + 9 = 76
        Assert.Equal(76, size);
    }

    [Fact]
    public void DetectLyrics3v2_NoTag_ReturnsFalse()
    {
        var ms = new MemoryStream(new byte[256]);
        // Put ID3v1 at end
        ms.Position = ms.Length - 128;
        ms.Write("TAG"u8);
        ms.Position = 0;

        long endOffset = ms.Length - 128;
        (bool found, int _) = Mp3TagReader.DetectLyrics3v2(ms, endOffset);

        Assert.False(found);
    }

    #endregion

    #region Lyrics3v1 Detection Tests

    [Fact]
    public void DetectLyrics3v1_ValidTag_ReturnsCorrectSize()
    {
        var ms = new MemoryStream();
        ms.Write(new byte[100]); // audio data

        // Lyrics3v1: "LYRICSBEGIN" + content + "LYRICSEND"
        byte[] begin = "LYRICSBEGIN"u8.ToArray();
        byte[] content = Encoding.ASCII.GetBytes("Some lyrics text here");
        byte[] end = "LYRICSEND"u8.ToArray();

        long tagStart = ms.Position;
        ms.Write(begin);
        ms.Write(content);
        ms.Write(end);
        long tagEnd = ms.Position;
        int expectedSize = (int)(tagEnd - tagStart);

        // ID3v1 at end
        ms.Write("TAG"u8);
        ms.Write(new byte[125]);

        long endOffset = ms.Length - 128;

        (bool found, int size) = Mp3TagReader.DetectLyrics3v1(ms, endOffset);

        Assert.True(found);
        Assert.Equal(expectedSize, size);
    }

    [Fact]
    public void DetectLyrics3v1_NoTag_ReturnsFalse()
    {
        var ms = new MemoryStream(new byte[256]);

        (bool found, int _) = Mp3TagReader.DetectLyrics3v1(ms, 256);
        Assert.False(found);
    }

    #endregion

    #region APE Tag Detection Tests

    [Fact]
    public void DetectApeTag_APEv2_ReturnsCorrectSize()
    {
        var ms = new MemoryStream();
        ms.Write(new byte[100]); // audio data

        // APEv2 header (32 bytes)
        long headerPos = ms.Position;
        ms.Write("APETAGEX"u8);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, 2000);
        ms.Write(version);
        // Tag size (including footer, excluding header) = 64
        Span<byte> tagSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tagSize, 64);
        ms.Write(tagSize);
        // Item count
        ms.Write(new byte[4]);
        // Flags (header present)
        Span<byte> flags = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(flags, 0xA0000000); // has header flag
        ms.Write(flags);
        ms.Write(new byte[8]); // reserved

        // Tag items (fill to make footer appear at right offset)
        ms.Write(new byte[32]); // items

        // APEv2 footer (32 bytes)
        ms.Write("APETAGEX"u8);
        ms.Write(version);
        ms.Write(tagSize); // same tag size
        ms.Write(new byte[4]); // item count
        ms.Write(new byte[4]); // flags (footer)
        ms.Write(new byte[8]); // reserved

        long endOffset = ms.Length;

        (bool found, int size) = Mp3TagReader.DetectApeTag(ms, endOffset);

        Assert.True(found);
        // Total = tagSize(64) + header(32) = 96
        Assert.Equal(96, size);
    }

    [Fact]
    public void DetectApeTag_APEv1_ReturnsCorrectSize()
    {
        var ms = new MemoryStream();
        ms.Write(new byte[100]); // audio data

        // APEv1 has no header, just footer + items
        // Items: 32 bytes
        ms.Write(new byte[32]);

        // APEv1 footer (32 bytes)
        ms.Write("APETAGEX"u8);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, 1000);
        ms.Write(version);
        // Tag size = footer(32) + items(32) = 64
        Span<byte> tagSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tagSize, 64);
        ms.Write(tagSize);
        ms.Write(new byte[4]); // item count
        ms.Write(new byte[4]); // flags
        ms.Write(new byte[8]); // reserved

        long endOffset = ms.Length;

        (bool found, int size) = Mp3TagReader.DetectApeTag(ms, endOffset);

        Assert.True(found);
        // APEv1: no header, total = tagSize(64) + header(0) = 64
        Assert.Equal(64, size);
    }

    [Fact]
    public void DetectApeTag_NoTag_ReturnsFalse()
    {
        var ms = new MemoryStream(new byte[256]);

        (bool found, int _) = Mp3TagReader.DetectApeTag(ms, 256);
        Assert.False(found);
    }

    #endregion

    #region FindAudioStart Tests

    [Fact]
    public void FindAudioStart_NoTags_ReturnsZero()
    {
        // MP3 file starting with sync word
        var ms = new MemoryStream([0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

        long result = Mp3TagReader.FindAudioStart(ms);
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindAudioStart_SingleId3v2_ReturnsCorrectOffset()
    {
        var ms = new MemoryStream();
        ms.Write("ID3"u8);
        ms.Write([0x04, 0x00, 0x00]);
        ms.Write(Mp3TagReader.EncodeSyncSafeInt(200));
        ms.Write(new byte[200]);
        ms.Write([0xFF, 0xFB]); // audio sync word

        long result = Mp3TagReader.FindAudioStart(ms);
        Assert.Equal(210, result); // 10 + 200
    }

    [Fact]
    public void FindAudioStart_MultipleId3v2_SkipsAll()
    {
        var ms = new MemoryStream();

        // First ID3v2: 50 bytes body
        ms.Write("ID3"u8);
        ms.Write([0x03, 0x00, 0x00]);
        ms.Write(Mp3TagReader.EncodeSyncSafeInt(50));
        ms.Write(new byte[50]);

        // Second ID3v2: 30 bytes body
        ms.Write("ID3"u8);
        ms.Write([0x03, 0x00, 0x00]);
        ms.Write(Mp3TagReader.EncodeSyncSafeInt(30));
        ms.Write(new byte[30]);

        // Audio data
        ms.Write([0xFF, 0xFB]);

        long result = Mp3TagReader.FindAudioStart(ms);
        Assert.Equal(100, result); // (10+50) + (10+30) = 100
    }

    #endregion

    #region FindAudioEnd Tests

    [Fact]
    public void FindAudioEnd_NoFooterTags_ReturnsFileLength()
    {
        var ms = new MemoryStream(new byte[500]);

        long result = Mp3TagReader.FindAudioEnd(ms);
        Assert.Equal(500, result);
    }

    [Fact]
    public void FindAudioEnd_Id3v1Only_ReturnsCorrectOffset()
    {
        var ms = new MemoryStream();
        ms.Write(new byte[372]); // audio
        ms.Write("TAG"u8);
        ms.Write(new byte[125]); // rest of ID3v1

        long result = Mp3TagReader.FindAudioEnd(ms);
        Assert.Equal(372, result);
    }

    [Fact]
    public void FindAudioEnd_Id3v1AndApeV2_ReturnsCorrectOffset()
    {
        var ms = new MemoryStream();
        ms.Write(new byte[200]); // audio

        long apeStart = ms.Position;
        // APEv2 header
        ms.Write("APETAGEX"u8);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, 2000);
        ms.Write(version);
        Span<byte> tagSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tagSize, 64); // footer + items = 64
        ms.Write(tagSize);
        ms.Write(new byte[4]); // item count
        ms.Write(new byte[4]); // flags
        ms.Write(new byte[8]); // reserved

        // Items
        ms.Write(new byte[32]);

        // APEv2 footer
        ms.Write("APETAGEX"u8);
        ms.Write(version);
        ms.Write(tagSize);
        ms.Write(new byte[4]);
        ms.Write(new byte[4]);
        ms.Write(new byte[8]);

        // ID3v1
        ms.Write("TAG"u8);
        ms.Write(new byte[125]);

        long result = Mp3TagReader.FindAudioEnd(ms);
        Assert.Equal(200, result); // audio ends before APE header
    }

    [Fact]
    public void FindAudioEnd_AllFooterTags_ReturnsCorrectOffset()
    {
        // Build: audio + APEv2 + Lyrics3v2 + ID3v1
        var ms = new MemoryStream();
        ms.Write(new byte[200]); // audio
        long audioEndExpected = ms.Position;

        // APEv2 (header + items + footer)
        ms.Write("APETAGEX"u8);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, 2000);
        ms.Write(version);
        Span<byte> tagSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tagSize, 48); // footer(32) + items(16)
        ms.Write(tagSize);
        ms.Write(new byte[4]); // item count
        ms.Write(new byte[4]); // flags
        ms.Write(new byte[8]); // reserved
        // Items: 16 bytes
        ms.Write(new byte[16]);
        // Footer
        ms.Write("APETAGEX"u8);
        ms.Write(version);
        ms.Write(tagSize);
        ms.Write(new byte[4]);
        ms.Write(new byte[4]);
        ms.Write(new byte[8]);

        // Lyrics3v2
        byte[] lyricsContent = "LYRICSBEGIN"u8.ToArray();
        byte[] lyricsBody = new byte[20];
        int lyricsInnerSize = lyricsContent.Length + lyricsBody.Length;
        ms.Write(lyricsContent);
        ms.Write(lyricsBody);
        ms.Write(Encoding.ASCII.GetBytes(lyricsInnerSize.ToString("D6")));
        ms.Write("LYRICS200"u8);

        // ID3v1
        ms.Write("TAG"u8);
        ms.Write(new byte[125]);

        long result = Mp3TagReader.FindAudioEnd(ms);
        Assert.Equal(audioEndExpected, result);
    }

    #endregion

    #region Combined Start/End Tests

    [Fact]
    public void FindAudioStartAndEnd_FullyTaggedFile_CorrectBoundaries()
    {
        var ms = new MemoryStream();

        // ID3v2 header: 80 bytes body
        ms.Write("ID3"u8);
        ms.Write([0x04, 0x00, 0x00]);
        ms.Write(Mp3TagReader.EncodeSyncSafeInt(80));
        ms.Write(new byte[80]); // body

        long expectedAudioStart = ms.Position; // = 90

        // Audio data: 300 bytes of sync frames
        byte[] audio = new byte[300];
        audio[0] = 0xFF;
        audio[1] = 0xFB;
        ms.Write(audio);

        long expectedAudioEnd = ms.Position; // = 390

        // ID3v1 at end
        ms.Write("TAG"u8);
        ms.Write(new byte[125]);

        long audioStart = Mp3TagReader.FindAudioStart(ms);
        long audioEnd = Mp3TagReader.FindAudioEnd(ms);

        Assert.Equal(expectedAudioStart, audioStart);
        Assert.Equal(expectedAudioEnd, audioEnd);
        Assert.Equal(300, audioEnd - audioStart);
    }

    #endregion
}
