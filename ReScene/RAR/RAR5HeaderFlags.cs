namespace ReScene.RAR;

/// <summary>
/// RAR 5.0 common header flags (HFL_*) from unrar headers.hpp
/// </summary>
[Flags]
internal enum RAR5HeaderFlags : ulong
{
    /// <summary>
    /// Extra area is present (HFL_EXTRA).
    /// </summary>
    ExtraArea = 0x0001,

    /// <summary>
    /// Data area is present (HFL_DATA).
    /// </summary>
    DataArea = 0x0002,

    /// <summary>
    /// Skip this header if unknown (HFL_SKIPIFUNKNOWN).
    /// </summary>
    SkipIfUnknown = 0x0004,

    /// <summary>
    /// Data continued from previous volume (HFL_SPLITBEFORE).
    /// </summary>
    SplitBefore = 0x0008,

    /// <summary>
    /// Data continues in next volume (HFL_SPLITAFTER).
    /// </summary>
    SplitAfter = 0x0010,

    /// <summary>
    /// Child of preceding file header (HFL_CHILD).
    /// </summary>
    Child = 0x0020,

    /// <summary>
    /// Preserve host modification (HFL_INHERITED).
    /// </summary>
    Inherited = 0x0040
}
