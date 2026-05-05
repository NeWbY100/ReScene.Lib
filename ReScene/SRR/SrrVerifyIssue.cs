namespace ReScene.SRR;

/// <summary>
/// A single issue reported by <see cref="SRRVerifier"/> while validating an SRR file.
/// </summary>
public sealed class SrrVerifyIssue
{
    /// <summary>
    /// Gets the severity of the issue.
    /// </summary>
    public required SrrVerifyIssueSeverity Severity { get; init; }

    /// <summary>
    /// Gets the human-readable description of the issue.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the byte offset in the SRR file at which the issue was detected.
    /// </summary>
    public long Offset { get; init; }

    /// <summary>
    /// Gets the SRR block type byte associated with the issue, or <see langword="null"/>
    /// if the issue is not block-specific.
    /// </summary>
    public byte? BlockType { get; init; }
}
