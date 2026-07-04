namespace ReScene.SRS;

/// <summary>Flags field in an SRSF (SRS file data) block header.</summary>
[Flags]
internal enum SrsfFlags : ushort
{
    None = 0,
    /// <summary>MKV SimpleBlock elements have been fixed (flags byte cleared for non-keyframes).</summary>
    SimpleBlockFix = 0x1,
    /// <summary>MKV Attachments element has been removed from the sample.</summary>
    AttachmentsRemoved = 0x2,
}
