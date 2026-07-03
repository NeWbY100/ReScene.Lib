namespace ReScene.RAR;

/// <summary>
/// RAR 4.x block/header field layout — the single source of truth for the byte offsets and sizes
/// used when reading, walking, and patching RAR4 headers. Values mirror the on-disk format exactly.
/// </summary>
internal static class Rar4HeaderLayout
{
    // Base block header (all RAR4 blocks): CRC(2) TYPE(1) FLAGS(2) SIZE(2).
    public const int Crc = 0;
    public const int Type = 2;
    public const int Flags = 3;
    public const int HeaderSize = 5;
    public const int BaseHeaderSize = 7;      // CRC 2 + type 1 + flags 2 + size 2
    public const int AddSize = 7;             // ADD_SIZE field offset (file/service blocks)
    public const int AddSizeFieldLength = 4;

    // File-header fixed fields (after the base header).
    public const int HostOs = 15;
    public const int FileTime = 20;
    public const int NameSize = 26;
    public const int Attr = 28;

    // Offset 32 is the end of the fixed file-header fields. It is therefore BOTH the HIGH_PACK_SIZE
    // field offset (present only when RARFileFlags.Large is set) AND the NAME base offset (when Large
    // is clear). Use the name that matches the intent at each call site.
    public const int HighPackSizeOffset = 32;
    public const int FixedFieldsEnd = 32;

    // EXT_TIME (RAR4 extended timestamps). Each of the 4 time fields (mtime/ctime/atime/arctime)
    // has a 4-bit rmode nibble; the low bits give sub-second precision.
    public const int ExtTimeFieldCount = 4;
    public const int ExtTimeNibbleBits = 4;
    public const int ExtTimePresentBit = 0x8;     // time field is present
    public const int ExtTimeRoundUpBit = 0x4;     // +1s rounding
    public const int ExtTimePrecisionMask = 0x3;  // number of extra 100ns remainder bytes (0-3)
    public const int ExtTimeNibbleMask = 0xF;     // one rmode nibble

    // rmode nibble packing inside the ext-time flags word: mtime>>12, ctime>>8, atime>>4, arctime>>0.
    public const int MtimeNibbleShift = 12;       // << 12 / >> 12
    public const int CtimeNibbleShift = 8;
    public const int AtimeNibbleShift = 4;
    public const int MtimeNibbleMask = 0x0FFF;    // clear the mtime nibble

    // RAR4 compression method is stored as ASCII '0'-'6' (0x30-0x36); subtract base to get 0-6.
    public const byte AsciiDigitZero = 0x30;

    // DOS date/time packing (FTIME).
    public const int DosSecondMask = 0x1F;      // *2 seconds
    public const int DosSecondEvenMask = 0x3E;  // encode: keep even seconds before >> 1
    public const int DosMinuteMask = 0x3F;
    public const int DosHourMask = 0x1F;   // 5-bit hour (0-23)
    public const int DosMinuteShift = 5;
    public const int DosHourShift = 11;
    public const int DosDayMask = 0x1F;
    public const int DosMonthMask = 0x0F;
    public const int DosMonthShift = 5;
    public const int DosYearMask = 0x7F;        // 7-bit year (years since 1980)
    public const int DosYearShift = 9;
    public const int DosEpochYear = 1980;
    public const int DosMaxYear = 2107;         // encode clamp (1980 + 0x7F)
}
