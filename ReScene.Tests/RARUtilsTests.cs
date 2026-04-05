using Force.Crc32;
using ReScene.RAR;

namespace ReScene.Tests;

public class RARUtilsTests
{
    #region CRC Validation Tests

    [Fact]
    public void CalculateHeaderCrc_ValidHeader_ReturnsCorrectCrc()
    {
        // Build a simple header and verify CRC calculation
        byte[] header = [0x00, 0x00, 0x73, 0x00, 0x08, 0x0D, 0x00]; // Archive header
        uint expectedCrc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
        ushort expectedCrc = (ushort)(expectedCrc32 & 0xFFFF);

        ushort calculated = RARUtils.CalculateHeaderCrc(header);

        Assert.Equal(expectedCrc, calculated);
    }

    [Fact]
    public void CalculateHeaderCrc_TooShortHeader_ReturnsZero()
    {
        byte[] header = [0x00, 0x00]; // Only 2 bytes

        ushort calculated = RARUtils.CalculateHeaderCrc(header);

        Assert.Equal(0, calculated);
    }

    [Fact]
    public void CalculateHeaderCrc_EmptyHeader_ReturnsZero()
    {
        byte[] header = [];

        ushort calculated = RARUtils.CalculateHeaderCrc(header);

        Assert.Equal(0, calculated);
    }

    [Fact]
    public void CalculateHeaderCrc_SingleByteHeader_ReturnsZero()
    {
        byte[] header = [0xFF];

        ushort calculated = RARUtils.CalculateHeaderCrc(header);

        Assert.Equal(0, calculated);
    }

    [Fact]
    public void CalculateHeaderCrc_ThreeByteHeader_ComputesCrcOfOneByte()
    {
        // Exactly 3 bytes: first 2 are CRC field, CRC is computed over 1 byte
        byte[] header = [0x00, 0x00, 0x73];
        uint expectedCrc32 = Crc32Algorithm.Compute(header, 2, 1);
        ushort expectedCrc = (ushort)(expectedCrc32 & 0xFFFF);

        ushort calculated = RARUtils.CalculateHeaderCrc(header);

        Assert.Equal(expectedCrc, calculated);
    }

    [Fact]
    public void ValidateHeaderCrc_ValidCrc_ReturnsTrue()
    {
        byte[] header = [0x00, 0x00, 0x73, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        ushort correctCrc = RARUtils.CalculateHeaderCrc(header);
        BitConverter.GetBytes(correctCrc).CopyTo(header, 0);

        bool valid = RARUtils.ValidateHeaderCrc(correctCrc, header);

        Assert.True(valid);
    }

    [Fact]
    public void ValidateHeaderCrc_InvalidCrc_ReturnsFalse()
    {
        byte[] header = [0xFF, 0xFF, 0x73, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        bool valid = RARUtils.ValidateHeaderCrc(0xFFFF, header);

        Assert.False(valid);
    }

    #endregion

    #region DOS Date/Time Conversion Tests

    [Fact]
    public void DosDateToDateTime_Zero_ReturnsNull()
    {
        DateTime? result = RARUtils.DosDateToDateTime(0);

        Assert.Null(result);
    }

    [Fact]
    public void DosDateToDateTime_ValidDate_ReturnsCorrectDateTime()
    {
        // DOS date/time for 2025-01-15 10:30:00
        // Date part (upper 16 bits): year-1980=45 (7 bits), month=1 (4 bits), day=15 (5 bits)
        // Time part (lower 16 bits): hour=10 (5 bits), minute=30 (6 bits), second/2=0 (5 bits)
        uint yearBits = (45u & 0x7F) << 9;
        uint monthBits = (1u & 0x0F) << 5;
        uint dayBits = 15u & 0x1F;
        uint datePart = yearBits | monthBits | dayBits;

        uint hourBits = (10u & 0x1F) << 11;
        uint minuteBits = (30u & 0x3F) << 5;
        uint secondBits = 0u & 0x1F;
        uint timePart = hourBits | minuteBits | secondBits;

        uint dosTime = (datePart << 16) | timePart;
        DateTime? result = RARUtils.DosDateToDateTime(dosTime);

        Assert.NotNull(result);
        Assert.Equal(2025, result!.Value.Year);
        Assert.Equal(1, result.Value.Month);
        Assert.Equal(15, result.Value.Day);
        Assert.Equal(10, result.Value.Hour);
        Assert.Equal(30, result.Value.Minute);
        Assert.Equal(0, result.Value.Second);
    }

    [Fact]
    public void DosDateToDateTime_Year1980_ReturnsCorrectYear()
    {
        // Year = 0 (1980), Month = 1, Day = 1, Hour = 0, Minute = 0, Second = 0
        uint datePart = (0u << 9) | (1u << 5) | 1u;
        uint dosTime = datePart << 16;

        DateTime? result = RARUtils.DosDateToDateTime(dosTime);

        Assert.NotNull(result);
        Assert.Equal(1980, result!.Value.Year);
    }

    [Fact]
    public void DosDateToDateTime_InvalidDate_ReturnsNull()
    {
        // Invalid: month = 0, day = 0
        uint datePart = (45u << 9) | (0u << 5) | 0u;
        uint dosTime = datePart << 16;

        DateTime? result = RARUtils.DosDateToDateTime(dosTime);

        Assert.Null(result);
    }

    [Fact]
    public void DosDateToDateTime_OddSecond_RoundsDown()
    {
        // DOS time encodes seconds/2, so second=15 means 30 seconds
        uint datePart = (45u << 9) | (6u << 5) | 1u; // 2025-06-01
        uint timePart = (12u << 11) | (0u << 5) | 15u; // 12:00:30
        uint dosTime = (datePart << 16) | timePart;

        DateTime? result = RARUtils.DosDateToDateTime(dosTime);

        Assert.NotNull(result);
        Assert.Equal(30, result!.Value.Second); // 15*2 = 30
    }

    [Fact]
    public void DosDateToDateTime_MaxYear2107_ReturnsCorrectDateTime()
    {
        // Year 2107 = 127 offset from 1980 (max 7-bit value), Month=12, Day=31
        uint datePart = (127u << 9) | (12u << 5) | 31u;
        uint timePart = (23u << 11) | (59u << 5) | 29u; // 23:59:58
        uint dosTime = (datePart << 16) | timePart;

        DateTime? result = RARUtils.DosDateToDateTime(dosTime);

        Assert.NotNull(result);
        Assert.Equal(2107, result!.Value.Year);
        Assert.Equal(12, result.Value.Month);
        Assert.Equal(31, result.Value.Day);
        Assert.Equal(23, result.Value.Hour);
        Assert.Equal(59, result.Value.Minute);
        Assert.Equal(58, result.Value.Second); // 29*2 = 58
    }

    [Theory]
    [InlineData(0u)]   // month 0 is invalid
    [InlineData(13u)]  // month 13 is invalid
    [InlineData(15u)]  // month 15 (max 4-bit) is invalid
    public void DosDateToDateTime_InvalidMonth_ReturnsNull(uint month)
    {
        uint datePart = (45u << 9) | (month << 5) | 15u;
        uint dosTime = datePart << 16 | (12u << 11); // some valid time

        DateTime? result = RARUtils.DosDateToDateTime(dosTime);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(0u)]   // day 0 is invalid
    [InlineData(32u)]  // day 32 is invalid for any month
    public void DosDateToDateTime_InvalidDay_ReturnsNull(uint day)
    {
        uint datePart = (45u << 9) | (6u << 5) | (day & 0x1F);
        uint dosTime = datePart << 16 | (12u << 11);

        DateTime? result = RARUtils.DosDateToDateTime(dosTime);

        Assert.Null(result);
    }

    [Fact]
    public void DosDateToDateTime_Feb29NonLeapYear_ReturnsNull()
    {
        // 2025 is not a leap year. Year offset = 45
        uint datePart = (45u << 9) | (2u << 5) | 29u;
        uint dosTime = datePart << 16 | (12u << 11);

        DateTime? result = RARUtils.DosDateToDateTime(dosTime);

        Assert.Null(result);
    }

    [Fact]
    public void DosDateToDateTime_Feb29LeapYear_ReturnsValidDate()
    {
        // 2024 is a leap year. Year offset = 44
        uint datePart = (44u << 9) | (2u << 5) | 29u;
        uint dosTime = datePart << 16 | (12u << 11);

        DateTime? result = RARUtils.DosDateToDateTime(dosTime);

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2024, 2, 29, 12, 0, 0), result!.Value);
    }

    #endregion

    #region Filename Decoding Tests

    [Fact]
    public void DecodeFileName_EmptyBytes_ReturnsNull()
    {
        string? result = RARUtils.DecodeFileName([], false);

        Assert.Null(result);
    }

    [Fact]
    public void DecodeFileName_AsciiWithoutUnicode_ReturnsString()
    {
        byte[] nameBytes = "testfile.txt"u8.ToArray();

        string? result = RARUtils.DecodeFileName(nameBytes, false);

        Assert.Equal("testfile.txt", result);
    }

    [Fact]
    public void DecodeFileName_UnicodeFlagNoNullSeparator_DecodesAsUtf8()
    {
        byte[] nameBytes = "test.txt"u8.ToArray();

        string? result = RARUtils.DecodeFileName(nameBytes, true);

        Assert.Equal("test.txt", result);
    }

    [Fact]
    public void DecodeFileName_UnicodeWithNullSeparator_DecodesUnicode()
    {
        // Standard name: "test.txt" followed by null, then Unicode encoding data
        byte[] stdName = "test.txt"u8.ToArray();
        // Simplest Unicode encoding: just the null separator with empty stdName
        byte[] nameBytes = [.. stdName, 0x00]; // null separator, no encoded data

        string? result = RARUtils.DecodeFileName(nameBytes, true);

        // Should fallback to standard name when no encoded data after null
        Assert.NotNull(result);
        Assert.Equal("test.txt", result);
    }

    [Fact]
    public void DecodeFileName_UnicodeWithEncodedData_DecodesCorrectly()
    {
        // RAR Unicode encoding format:
        // stdName (ASCII) + 0x00 + encData
        // encData: first byte = high byte, then pairs of (flags, data)
        // flag type 0: literal low byte with high=0
        // We'll encode "tEst" using the RAR Unicode encoder:
        // stdName = "test", encData encodes Unicode chars

        // Simple case: stdName "AB", then encode to reproduce "AB" in Unicode
        // encData[0] = hi byte (0x00 for ASCII range)
        // Then flags byte, each 2 bits selects the mode for next char
        // Mode 0 = literal low byte + high=0
        // For "AB": flags=0b00_00_00_00, then 'A', 'B'
        byte[] stdName = "AB"u8.ToArray();
        byte[] encData = [0x00, 0x00, (byte)'A', (byte)'B']; // hi=0, flags=0, then 2 literal low bytes
        byte[] nameBytes = [.. stdName, 0x00, .. encData];

        string? result = RARUtils.DecodeFileName(nameBytes, true);

        Assert.NotNull(result);
        Assert.Equal("AB", result);
    }

    [Fact]
    public void DecodeFileName_UnicodeWithHighByteEncoding_DecodesCorrectly()
    {
        // Encode a character with a non-zero high byte using mode 1
        // Mode 1: literal low byte + hi (the first byte of encData)
        // To get U+0441 (Cyrillic 'с'): hi=0x04, low=0x41
        // flags = 0b01_00_00_00 = 0x40 (first pair is mode 1, rest mode 0 padding)
        byte[] stdName = "x"u8.ToArray();
        byte[] encData = [0x04, 0x40, 0x41]; // hi=0x04, flags=0x40 (mode 1 for first char), low=0x41
        byte[] nameBytes = [.. stdName, 0x00, .. encData];

        string? result = RARUtils.DecodeFileName(nameBytes, true);

        Assert.NotNull(result);
        // Should contain U+0441 (Cyrillic small letter ES)
        Assert.Contains("\u0441", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeFileName_UnicodeWithMode2FullPair_DecodesCorrectly()
    {
        // Mode 2: two literal bytes (low, high) fully specifying a Unicode char
        // To get U+4E2D (Chinese '中'): low=0x2D, hi=0x4E
        // flags = 0b10_00_00_00 = 0x80
        byte[] stdName = "z"u8.ToArray();
        byte[] encData = [0x00, 0x80, 0x2D, 0x4E]; // hi=0(unused for mode2), flags=0x80, low=0x2D, hi=0x4E
        byte[] nameBytes = [.. stdName, 0x00, .. encData];

        string? result = RARUtils.DecodeFileName(nameBytes, true);

        Assert.NotNull(result);
        Assert.Contains("\u4E2D", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeFileName_HighBytesWithoutUnicodeFlag_UsesOemEncoding()
    {
        // Bytes with high bit set (0x80+) without Unicode flag should use OEM encoding fallback
        byte[] nameBytes = [0x74, 0x65, 0x73, 0x74, 0xE9]; // "test" + 0xE9 (accented char in many codepages)

        string? result = RARUtils.DecodeFileName(nameBytes, false);

        Assert.NotNull(result);
        Assert.StartsWith("test", result, StringComparison.Ordinal);
        Assert.Equal(5, result!.Length); // 5 characters decoded
    }

    [Fact]
    public void DecodeFileName_NullSeparatorAtStart_ReturnsUtf8Fallback()
    {
        // Null byte at position 0: stdName is empty, no encData
        byte[] nameBytes = [0x00];

        string? result = RARUtils.DecodeFileName(nameBytes, true);

        // nullIndex=0, stdName is empty, encData is empty
        // Falls through to UTF-8 decode of original bytes
        Assert.NotNull(result);
    }

    #endregion

    #region Dictionary Size Tests

    [Theory]
    [InlineData(RARFileFlags.DictSize64, 64)]
    [InlineData(RARFileFlags.DictSize128, 128)]
    [InlineData(RARFileFlags.DictSize256, 256)]
    [InlineData(RARFileFlags.DictSize512, 512)]
    [InlineData(RARFileFlags.DictSize1024, 1024)]
    [InlineData(RARFileFlags.DictSize2048, 2048)]
    [InlineData(RARFileFlags.DictSize4096, 4096)]
    public void GetDictionarySize_ReturnsCorrectSize(RARFileFlags flags, int expectedKB)
    {
        int result = RARUtils.GetDictionarySize(flags);

        Assert.Equal(expectedKB, result);
    }

    [Theory]
    [InlineData(0x0000, 64)]
    [InlineData(0x0020, 128)]
    [InlineData(0x0040, 256)]
    [InlineData(0x0060, 512)]
    [InlineData(0x0080, 1024)]
    [InlineData(0x00A0, 2048)]
    [InlineData(0x00C0, 4096)]
    [InlineData(0x00E0, 0)]  // Directory entry
    public void GetDictionarySize_AllFlagCombinations_ReturnsExpectedSize(ushort flagValue, int expectedKB)
    {
        RARFileFlags flags = (RARFileFlags)flagValue;

        int result = RARUtils.GetDictionarySize(flags);

        Assert.Equal(expectedKB, result);
    }

    [Fact]
    public void GetDictionarySize_FlagsWithOtherBitsSet_IgnoresNonDictBits()
    {
        // DictSize512 (0x0060) combined with other flags like Unicode (0x0200) and ExtTime (0x1000)
        RARFileFlags flags = RARFileFlags.DictSize512 | RARFileFlags.Unicode | RARFileFlags.ExtTime;

        int result = RARUtils.GetDictionarySize(flags);

        Assert.Equal(512, result);
    }

    [Fact]
    public void GetDictionarySize_DirectoryFlag_ReturnsZero()
    {
        int result = RARUtils.GetDictionarySize(RARFileFlags.Directory);

        Assert.Equal(0, result);
    }

    [Fact]
    public void IsDirectory_DirectoryFlag_ReturnsTrue()
    {
        bool result = RARUtils.IsDirectory(RARFileFlags.Directory);

        Assert.True(result);
    }

    [Fact]
    public void IsDirectory_NormalFile_ReturnsFalse()
    {
        bool result = RARUtils.IsDirectory(RARFileFlags.None);

        Assert.False(result);
    }

    [Fact]
    public void DictionarySizes_HasCorrectLength() => Assert.Equal(8, RARUtils.DictionarySizes.Length);

    [Fact]
    public void DictionarySizes_ContainsExpectedValues() => Assert.Equal([64, 128, 256, 512, 1024, 2048, 4096, 0], RARUtils.DictionarySizes);

    #endregion
}
