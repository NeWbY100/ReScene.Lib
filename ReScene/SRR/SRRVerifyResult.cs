namespace ReScene.SRR;

/// <summary>
/// Aggregate result of an <see cref="SRRVerifier"/> run.
/// </summary>
public sealed class SRRVerifyResult
{
    /// <summary>
    /// Gets a value indicating whether the file passed verification (no <see cref="SRRVerifyIssueSeverity.Error"/> issues).
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Gets the list of issues found during verification.
    /// </summary>
    public required IReadOnlyList<SRRVerifyIssue> Issues { get; init; }

    /// <summary>
    /// Gets the number of blocks successfully parsed.
    /// </summary>
    public required int BlocksScanned { get; init; }

    /// <summary>
    /// Gets the size of the SRR file in bytes.
    /// </summary>
    public required long FileSize { get; init; }
}
