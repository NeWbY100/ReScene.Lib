namespace ReScene.Core;

/// <summary>Typed result of <see cref="SRRReconstructor"/> operations.</summary>
internal sealed record SRRReconstructionResult(
    SRRReconstructionStatus Status,
    IReadOnlyList<string> WrittenPaths,
    string? Diagnostic)
{
    public static SRRReconstructionResult Ok(IReadOnlyList<string> written) =>
        new(SRRReconstructionStatus.Success, written, null);

    public static SRRReconstructionResult Fail(SRRReconstructionStatus status, string diagnostic,
        IReadOnlyList<string>? written = null) =>
        new(status, written ?? [], diagnostic);
}
