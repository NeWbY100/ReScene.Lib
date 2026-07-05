namespace ReScene.SRS;

/// <summary>
/// 16-byte ASCII pseudo-GUIDs used to frame SRS-specific objects inside an ASF/WMV container.
/// These replace the real media-packet data in the ASF Data Object during SRS creation.
/// </summary>
internal static class ASFSRSGuids
{
    /// <summary>Pseudo-GUID identifying an SRS File block inside an ASF/WMV container.</summary>
    internal static readonly byte[] GuidSRSFile = "SRSFSRSFSRSFSRSF"u8.ToArray();

    /// <summary>Pseudo-GUID identifying an SRS Track block inside an ASF/WMV container.</summary>
    internal static readonly byte[] GuidSRSTrack = "SRSTSRSTSRSTSRST"u8.ToArray();

    /// <summary>Pseudo-GUID identifying SRS padding inside an ASF/WMV container.</summary>
    internal static readonly byte[] GuidSRSPadding = "PADDINGBYTESDATA"u8.ToArray();
}
