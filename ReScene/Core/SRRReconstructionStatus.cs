namespace ReScene.Core;

/// <summary>Outcome of an SRR-guided reconstruction or preflight.</summary>
internal enum SRRReconstructionStatus
{
    /// <summary>All requested volumes written (and verified, where CRCs were supplied).</summary>
    Success,

    /// <summary>Preflight declined: a required payload is not present in the SRR.</summary>
    UnsupportedSrr,

    /// <summary>The packed source ended before the last requested ADD_SIZE byte.</summary>
    SourceExhausted,

    /// <summary>Volumes written but hash comparison failed (custom-packer path only —
    /// unreachable on Manager assembly calls, which pass no hashes).</summary>
    VerificationFailed,

    /// <summary>I/O or parse failure (includes source-open failures such as
    /// RARStream's ArgumentException when no target header is visible).</summary>
    Error,
}
