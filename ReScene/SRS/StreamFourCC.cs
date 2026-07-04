namespace ReScene.SRS;

/// <summary>
/// Magic FourCC constants for the STREAM/M2TS container format used in SRS detection.
/// These appear as the first 4 bytes of a STREAM-format SRS file.
/// </summary>
internal static class StreamFourCC
{
    /// <summary>STREAM container magic tag ("STRM").</summary>
    public const string Strm = "STRM";

    /// <summary>M2TS container magic tag ("M2TS").</summary>
    public const string M2ts = "M2TS";
}
