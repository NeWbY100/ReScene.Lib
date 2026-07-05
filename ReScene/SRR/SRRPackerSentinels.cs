namespace ReScene.SRR;

/// <summary>
/// Sentinel values that identify a non-WinRAR custom packer via the RAR4 UNP_SIZE field.
/// </summary>
internal static class SRRPackerSentinels
{
    /// <summary>UNP_SIZE all-ones with LARGE flag (both 32-bit halves = 0xFFFFFFFF) — non-WinRAR packer.</summary>
    internal const ulong PackerSentinelAllOnes = 0xFFFFFFFFFFFFFFFFUL;

    /// <summary>UNP_SIZE 0xFFFFFFFF without LARGE flag — non-WinRAR packer.</summary>
    internal const uint PackerSentinelMaxUint32 = 0xFFFFFFFFU;
}
