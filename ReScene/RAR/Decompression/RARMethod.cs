namespace ReScene.RAR.Decompression;

/// <summary>
/// RAR compression methods.
/// </summary>
internal enum RARMethod
{
    /// <summary>
    /// Store (no compression)
    /// </summary>
    Store = 0x30,

    /// <summary>
    /// Fastest compression
    /// </summary>
    Fastest = 0x31,

    /// <summary>
    /// Fast compression
    /// </summary>
    Fast = 0x32,

    /// <summary>
    /// Normal compression
    /// </summary>
    Normal = 0x33,

    /// <summary>
    /// Good compression
    /// </summary>
    Good = 0x34,

    /// <summary>
    /// Best compression
    /// </summary>
    Best = 0x35
}
