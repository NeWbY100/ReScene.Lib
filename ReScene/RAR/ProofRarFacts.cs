namespace ReScene.RAR;

/// <summary>
/// Facts about a proof RAR's packed-file blocks, as read by <see cref="RarProofInspector.Inspect"/>.
/// Two independent callers consume this: <c>ReleaseScanner</c> rule 4 (App.Core) needs
/// <see cref="LastPackedIsImage"/> — pyrescene's proof state machine treats the LAST packed
/// block's image-ness as authoritative, reassigning on every block it sees (see
/// <c>remove_unwanted_sfvs</c>) — while the independent proof-RAR pass needs
/// <see cref="AnyImage"/> (see <c>has_stored_proof_ext</c>, which returns as soon as
/// any packed block matches). One seam serves both distinct predicates.
/// </summary>
/// <param name="Readable">
/// <see langword="false"/> when the archive could not be walked at all — RAR5 container (no RAR5
/// support in the ported logic) or a corrupt/truncated RAR4 header. Callers treat this the same
/// way pyrescene's caught <c>ValueError</c> does: warn and exclude.
/// </param>
/// <param name="HasPackedBlocks">
/// <see langword="true"/> when at least one file-header (packed-file) block was encountered.
/// <see langword="false"/> leaves <see cref="LastPackedIsImage"/> and <see cref="AnyImage"/> at
/// their default <see langword="false"/>, matching pyrescene's <c>skip</c> variable never leaving
/// its initial <see langword="false"/> value when no packed block is seen.
/// </param>
/// <param name="AnyImage">
/// <see langword="true"/> when ANY packed-file block's name matches a proof image extension.
/// </param>
/// <param name="LastPackedIsImage">
/// <see langword="true"/> when the LAST packed-file block encountered (in header order) matches a
/// proof image extension — last-block-wins, mirroring pyrescene's <c>skip = True/False</c>
/// reassignment inside the block loop rather than an early exit.
/// </param>
public sealed record ProofRarFacts(bool Readable, bool HasPackedBlocks, bool AnyImage, bool LastPackedIsImage);
