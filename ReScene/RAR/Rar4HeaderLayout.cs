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
}
