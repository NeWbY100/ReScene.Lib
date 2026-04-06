namespace ReScene.SRS;

/// <summary>
/// Track information collected during sample profiling.
/// </summary>
internal class TrackInfo
{
    public int TrackNumber
    {
        get; set;
    }
    public long DataLength
    {
        get; set;
    }
    public byte[] SignatureBytes { get; set; } = [];
    public long MatchOffset
    {
        get; set;
    }

    /// <summary>
    /// For MKV tracks: the compression algorithm from ContentCompAlgo.
    /// Null means no compression element present. 3 = header stripping.
    /// </summary>
    public int? CompressionAlgorithm
    {
        get; set;
    }

    /// <summary>
    /// For MKV tracks with header stripping (CompressionAlgorithm == 3):
    /// the stripped header bytes from ContentCompSettings.
    /// </summary>
    public byte[] CompressionSettings { get; set; } = [];
}
