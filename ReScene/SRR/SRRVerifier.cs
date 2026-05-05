namespace ReScene.SRR;

/// <summary>
/// Validates the structural integrity of an SRR file. The verifier walks each block,
/// checks header sanity, CRC sentinels, and block sizes against the file length, and
/// returns a structured <see cref="SrrVerifyResult"/>.
/// </summary>
public static class SRRVerifier
{
    /// <summary>
    /// Verifies the structural integrity of the SRR file at the given path.
    /// </summary>
    /// <param name="srrFilePath">
    /// Absolute path to the SRR file to verify.
    /// </param>
    /// <returns>
    /// A <see cref="SrrVerifyResult"/> describing the outcome.
    /// </returns>
    public static SrrVerifyResult Verify(string srrFilePath)
    {
        throw new NotImplementedException();
    }
}
