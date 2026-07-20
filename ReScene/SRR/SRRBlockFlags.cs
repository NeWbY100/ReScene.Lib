namespace ReScene.SRR;

/// <summary>
/// Generic SRR block flags.
/// </summary>
[Flags]
public enum SRRBlockFlags : ushort
{
    /// <summary>
    /// No flags set.
    /// </summary>
    None = 0x0000,

    /// <summary>
    /// SRR RAR-file (0x71) block only: the archive's recovery records were stripped when this
    /// block was written. pyReScene sets this unconditionally on every RAR-file block it writes,
    /// "even if there aren't RR" (rescene/rar.py, SrrRarFileBlock.__init__) — real-world SRRs
    /// (and, per that same comment, modern ReScene .NET) always carry it. Our SRRs are always
    /// header-only/recovery-stripped, so the flag is semantically accurate here too. Our reader
    /// (<see cref="SRRRARFileBlock"/>) does parse and populate this flag on every read — but no
    /// consumer branches on its value for RAR-file blocks, so setting it doesn't change round-trip
    /// behavior; it only brings our byte output into pyReScene parity.
    /// </summary>
    RecoveryBlocksRemoved = 0x0001,

    /// <summary>
    /// Skip this block if the type is unknown.
    /// </summary>
    SkipIfUnknown = 0x4000,

    /// <summary>
    /// Block has an additional size field (long block).
    /// </summary>
    LongBlock = 0x8000
}
