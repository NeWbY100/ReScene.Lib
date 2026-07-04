namespace ReScene.SRS;

/// <summary>
/// Constants for ASF/WMV container detection and object framing.
/// An ASF object header is always a 16-byte GUID followed by an 8-byte LE64 size field (24 bytes total).
/// </summary>
internal static class AsfGuids
{
    /// <summary>First 4 bytes of the ASF Header Object GUID (30 26 B2 75 ...).</summary>
    internal static ReadOnlySpan<byte> HeaderObjectPrefix => [0x30, 0x26, 0xB2, 0x75];

    /// <summary>First 4 bytes of the ASF Data Object GUID (36 26 B2 75 ...).</summary>
    internal static ReadOnlySpan<byte> DataObjectPrefix => [0x36, 0x26, 0xB2, 0x75];

    /// <summary>Size of an ASF object header: 16-byte GUID + 8-byte LE64 object size.</summary>
    internal const int ObjectHeaderSize = 24;

    /// <summary>Size of an ASF GUID field.</summary>
    internal const int GuidSize = 16;

    /// <summary>Size of the Data Object fileId field (a GUID-width field, distinct from GuidSize by intent).</summary>
    internal const int DataObjectFileIdSize = 16;

    /// <summary>
    /// Total length of the ASF Data Object header retained in an SRS file:
    /// fileId (16) + total packet count (8) + reserved (2) = 26 bytes.
    /// </summary>
    internal const int DataObjectHeaderLength = 26;
}
