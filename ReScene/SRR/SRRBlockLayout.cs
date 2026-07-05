namespace ReScene.SRR;

/// <summary>
/// SRR block framing constants — the single source of truth for the byte sizes and CRC "sentinels"
/// shared by the SRR writer, editor, and verifier. Values mirror the on-disk SRR format exactly.
/// </summary>
internal static class SRRBlockLayout
{
    // Base SRR block header: CRC(2) + Type(1) + Flags(2) + Size(2).
    public const int BaseHeaderSize = 7;
    public const int AddSizeFieldLength = 4;    // ADD_SIZE / data-length field
    public const int NameLengthFieldLength = 2; // inline name-length prefix (framing only)

    // Each SRR block's 2-byte CRC is a fixed sentinel, not a real CRC.
    public const ushort HeaderSentinel = 0x6969;
    public const ushort StoredFileSentinel = 0x6A6A;
    public const ushort OSOSentinel = 0x6B6B;
    public const ushort RARPaddingSentinel = 0x6C6C;
    public const ushort RARFileSentinel = 0x7171;

    // OSO (OpenSubtitles) hash-block payload field sizes.
    public const int OsoFileSizeLength = 8;  // ulong file size
    public const int OsoHashLength = 8;      // 8-byte hash
    public const int OsoFixedPayloadSize = OsoFileSizeLength + OsoHashLength + NameLengthFieldLength; // 18
}
