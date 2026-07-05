namespace ReScene.SRS;

/// <summary>
/// Result of SRS sample reconstruction.
/// </summary>
public record SRSReconstructionResult(
    bool Success,
    bool CRCMatch,
    uint ExpectedCRC,
    uint ActualCRC,
    long ExpectedSize,
    long ActualSize,
    string? ErrorMessage);
