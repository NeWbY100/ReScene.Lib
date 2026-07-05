namespace ReScene.RAR;

/// <summary>
/// Timestamp precision levels for RAR -tsm/-tsc/-tsa options.
/// Maps directly to the RAR command-line option suffixes (0-4).
/// </summary>
public enum TimestampPrecision : byte
{
    /// <summary>
    /// Time not saved (ts*0, -ts*-)
    /// </summary>
    NotSaved = 0,

    /// <summary>
    /// 1 second precision (ts*1, DOS time only)
    /// </summary>
    OneSecond = 1,

    /// <summary>
    /// ~0.0065536 second precision (ts*2, 1 extra byte)
    /// </summary>
    HighPrecision1 = 2,

    /// <summary>
    /// ~0.0000256 second precision (ts*3, 2 extra bytes)
    /// </summary>
    HighPrecision2 = 3,

    /// <summary>
    /// NTFS 100-nanosecond precision (ts*4, 3 extra bytes)
    /// </summary>
    NtfsPrecision = 4
}
