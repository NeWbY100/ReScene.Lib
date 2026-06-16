namespace ReScene.SRS;

/// <summary>
/// Track information collected during sample profiling.
/// </summary>
internal class TrackInfo
{
    /// <summary>
    /// The number of leading track-data bytes captured as the signature for
    /// most container formats (used to locate the track in the full release).
    /// </summary>
    public const int SignatureSize = 256;

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

    /// <summary>
    /// Appends bytes from <paramref name="src"/> to <see cref="SignatureBytes"/>,
    /// stopping once the signature reaches <paramref name="maxTotalLen"/> bytes.
    /// No-op when the signature is already at or above the cap.
    /// </summary>
    /// <param name="src">
    /// The source bytes (already sliced to the relevant track-data span).
    /// </param>
    /// <param name="maxTotalLen">
    /// The maximum total length the signature should grow to.
    /// </param>
    public void AppendSignature(ReadOnlySpan<byte> src, int maxTotalLen)
    {
        if (SignatureBytes.Length >= maxTotalLen)
        {
            return;
        }

        int take = Math.Min(maxTotalLen - SignatureBytes.Length, src.Length);
        if (take <= 0)
        {
            return;
        }

        byte[] newSig = new byte[SignatureBytes.Length + take];
        SignatureBytes.CopyTo(newSig, 0);
        src[..take].CopyTo(newSig.AsSpan(SignatureBytes.Length));
        SignatureBytes = newSig;
    }
}
