namespace RARLib.Tests;

public class RARFlagsTests
{
    #region RAR4BlockType Tests

    [Theory]
    [InlineData(RAR4BlockType.Marker, 0x72)]
    [InlineData(RAR4BlockType.ArchiveHeader, 0x73)]
    [InlineData(RAR4BlockType.FileHeader, 0x74)]
    [InlineData(RAR4BlockType.Comment, 0x75)]
    [InlineData(RAR4BlockType.Service, 0x7A)]
    [InlineData(RAR4BlockType.EndArchive, 0x7B)]
    public void RAR4BlockType_HasExpectedValues(RAR4BlockType type, byte expectedValue)
    {
        Assert.Equal(expectedValue, (byte)type);
    }

    #endregion

    #region RARArchiveFlags Tests

    [Fact]
    public void RARArchiveFlags_Volume_CorrectValue()
    {
        Assert.Equal(0x0001, (ushort)RARArchiveFlags.Volume);
    }

    [Fact]
    public void RARArchiveFlags_Solid_CorrectValue()
    {
        Assert.Equal(0x0008, (ushort)RARArchiveFlags.Solid);
    }

    [Fact]
    public void RARArchiveFlags_Protected_CorrectValue()
    {
        Assert.Equal(0x0040, (ushort)RARArchiveFlags.Protected);
    }

    [Fact]
    public void RARArchiveFlags_Password_CorrectValue()
    {
        Assert.Equal(0x0080, (ushort)RARArchiveFlags.Password);
    }

    [Fact]
    public void RARArchiveFlags_FirstVolume_CorrectValue()
    {
        Assert.Equal(0x0100, (ushort)RARArchiveFlags.FirstVolume);
    }

    [Fact]
    public void RARArchiveFlags_CombineFlags_Works()
    {
        var flags = RARArchiveFlags.Volume | RARArchiveFlags.Solid | RARArchiveFlags.Protected;
        Assert.True(flags.HasFlag(RARArchiveFlags.Volume));
        Assert.True(flags.HasFlag(RARArchiveFlags.Solid));
        Assert.True(flags.HasFlag(RARArchiveFlags.Protected));
        Assert.False(flags.HasFlag(RARArchiveFlags.Password));
    }

    #endregion

    #region RARFileFlags Tests

    [Fact]
    public void RARFileFlags_SplitBefore_CorrectValue()
    {
        Assert.Equal(0x0001, (ushort)RARFileFlags.SplitBefore);
    }

    [Fact]
    public void RARFileFlags_SplitAfter_CorrectValue()
    {
        Assert.Equal(0x0002, (ushort)RARFileFlags.SplitAfter);
    }

    [Fact]
    public void RARFileFlags_ExtTime_CorrectValue()
    {
        Assert.Equal(0x1000, (ushort)RARFileFlags.ExtTime);
    }

    [Fact]
    public void RARFileFlags_LongBlock_CorrectValue()
    {
        Assert.Equal(0x8000, (ushort)RARFileFlags.LongBlock);
    }

    [Fact]
    public void RARFileFlags_DictionaryMask_ExtractsCorrectly()
    {
        // Test each dictionary size flag extracts the correct 3-bit value
        Assert.Equal(0, ((ushort)RARFileFlags.DictSize64 & RARFlagMasks.DictionarySizeMask) >> RARFlagMasks.DictionarySizeShift);
        Assert.Equal(1, ((ushort)RARFileFlags.DictSize128 & RARFlagMasks.DictionarySizeMask) >> RARFlagMasks.DictionarySizeShift);
        Assert.Equal(2, ((ushort)RARFileFlags.DictSize256 & RARFlagMasks.DictionarySizeMask) >> RARFlagMasks.DictionarySizeShift);
        Assert.Equal(3, ((ushort)RARFileFlags.DictSize512 & RARFlagMasks.DictionarySizeMask) >> RARFlagMasks.DictionarySizeShift);
        Assert.Equal(4, ((ushort)RARFileFlags.DictSize1024 & RARFlagMasks.DictionarySizeMask) >> RARFlagMasks.DictionarySizeShift);
        Assert.Equal(5, ((ushort)RARFileFlags.DictSize2048 & RARFlagMasks.DictionarySizeMask) >> RARFlagMasks.DictionarySizeShift);
        Assert.Equal(6, ((ushort)RARFileFlags.DictSize4096 & RARFlagMasks.DictionarySizeMask) >> RARFlagMasks.DictionarySizeShift);
        Assert.Equal(7, ((ushort)RARFileFlags.Directory & RARFlagMasks.DictionarySizeMask) >> RARFlagMasks.DictionarySizeShift);
    }

    #endregion

    #region TimestampPrecision Tests

    [Theory]
    [InlineData(TimestampPrecision.NotSaved, 0)]
    [InlineData(TimestampPrecision.OneSecond, 1)]
    [InlineData(TimestampPrecision.HighPrecision1, 2)]
    [InlineData(TimestampPrecision.HighPrecision2, 3)]
    [InlineData(TimestampPrecision.NtfsPrecision, 4)]
    public void TimestampPrecision_HasExpectedValues(TimestampPrecision precision, byte expectedValue)
    {
        Assert.Equal(expectedValue, (byte)precision);
    }

    #endregion

    #region RARFlagMasks Tests

    [Fact]
    public void RARFlagMasks_DictionarySizeMask_IsCorrect()
    {
        Assert.Equal(0x00E0, RARFlagMasks.DictionarySizeMask);
    }

    [Fact]
    public void RARFlagMasks_DictionarySizeShift_IsCorrect()
    {
        Assert.Equal(5, RARFlagMasks.DictionarySizeShift);
    }

    [Fact]
    public void RARFlagMasks_SaltLength_IsCorrect()
    {
        Assert.Equal(8, RARFlagMasks.SaltLength);
    }

    #endregion

    #region RARFileHeader Convenience Properties Tests

    [Fact]
    public void RARFileHeader_IsSplitBefore_WhenFlagSet()
    {
        var header = new RARFileHeader { Flags = RARFileFlags.SplitBefore };
        Assert.True(header.IsSplitBefore);
    }

    [Fact]
    public void RARFileHeader_IsSplitAfter_WhenFlagSet()
    {
        var header = new RARFileHeader { Flags = RARFileFlags.SplitAfter };
        Assert.True(header.IsSplitAfter);
    }

    [Fact]
    public void RARFileHeader_IsEncrypted_WhenFlagSet()
    {
        var header = new RARFileHeader { Flags = RARFileFlags.Password };
        Assert.True(header.IsEncrypted);
    }

    [Fact]
    public void RARFileHeader_HasUnicodeName_WhenFlagSet()
    {
        var header = new RARFileHeader { Flags = RARFileFlags.Unicode };
        Assert.True(header.HasUnicodeName);
    }

    [Fact]
    public void RARFileHeader_HasExtendedTime_WhenFlagSet()
    {
        var header = new RARFileHeader { Flags = RARFileFlags.ExtTime };
        Assert.True(header.HasExtendedTime);
    }

    [Fact]
    public void RARFileHeader_HasLargeSize_WhenFlagSet()
    {
        var header = new RARFileHeader { Flags = RARFileFlags.Large };
        Assert.True(header.HasLargeSize);
    }

    [Fact]
    public void RARFileHeader_NoFlags_AllFalse()
    {
        var header = new RARFileHeader { Flags = RARFileFlags.None };
        Assert.False(header.IsSplitBefore);
        Assert.False(header.IsSplitAfter);
        Assert.False(header.IsEncrypted);
        Assert.False(header.HasUnicodeName);
        Assert.False(header.HasExtendedTime);
        Assert.False(header.HasLargeSize);
    }

    #endregion

    #region RARArchiveHeader Convenience Properties Tests

    [Fact]
    public void RARArchiveHeader_IsVolume_WhenFlagSet()
    {
        var header = new RARArchiveHeader { Flags = RARArchiveFlags.Volume };
        Assert.True(header.IsVolume);
    }

    [Fact]
    public void RARArchiveHeader_IsSolid_WhenFlagSet()
    {
        var header = new RARArchiveHeader { Flags = RARArchiveFlags.Solid };
        Assert.True(header.IsSolid);
    }

    [Fact]
    public void RARArchiveHeader_HasRecoveryRecord_WhenFlagSet()
    {
        var header = new RARArchiveHeader { Flags = RARArchiveFlags.Protected };
        Assert.True(header.HasRecoveryRecord);
    }

    [Fact]
    public void RARArchiveHeader_HasNewVolumeNaming_WhenFlagSet()
    {
        var header = new RARArchiveHeader { Flags = RARArchiveFlags.NewNumbering };
        Assert.True(header.HasNewVolumeNaming);
    }

    [Fact]
    public void RARArchiveHeader_IsFirstVolume_WhenFlagSet()
    {
        var header = new RARArchiveHeader { Flags = RARArchiveFlags.FirstVolume };
        Assert.True(header.IsFirstVolume);
    }

    [Fact]
    public void RARArchiveHeader_HasEncryptedHeaders_WhenFlagSet()
    {
        var header = new RARArchiveHeader { Flags = RARArchiveFlags.Password };
        Assert.True(header.HasEncryptedHeaders);
    }

    [Fact]
    public void RARArchiveHeader_IsLocked_WhenFlagSet()
    {
        var header = new RARArchiveHeader { Flags = RARArchiveFlags.Lock };
        Assert.True(header.IsLocked);
    }

    #endregion

    #region RARServiceBlockInfo Tests

    [Fact]
    public void RARServiceBlockInfo_IsStored_WhenMethod0x30()
    {
        var info = new RARServiceBlockInfo { CompressionMethod = 0x30 };
        Assert.True(info.IsStored);
    }

    [Fact]
    public void RARServiceBlockInfo_NotStored_WhenMethod0x33()
    {
        var info = new RARServiceBlockInfo { CompressionMethod = 0x33 };
        Assert.False(info.IsStored);
    }

    [Fact]
    public void RARServiceBlockInfo_HasZeroedFileTime_WhenZero()
    {
        var info = new RARServiceBlockInfo { FileTimeDOS = 0 };
        Assert.True(info.HasZeroedFileTime);
    }

    [Fact]
    public void RARServiceBlockInfo_HasZeroedFileTime_WhenNonZero()
    {
        var info = new RARServiceBlockInfo { FileTimeDOS = 0x12345678 };
        Assert.False(info.HasZeroedFileTime);
    }

    #endregion
}
