namespace ReScene.Tests;

/// <summary>
/// Placeholder for the full-pipeline golden comparison, enabled in Task 9: a golden regenerated
/// via pyrescene WITHOUT --no-srs (samples + subs present), comparing the complete folder-mode
/// output — including nested-SRR (subtitle) app-name fields normalized the same way as
/// <see cref="GoldenFixtureTests.NormalizeAppName"/> — byte-for-byte. See spec §6
/// (docs/superpowers/specs/2026-07-18-multiset-srr-creation-design.md).
/// </summary>
public class FullPipelineGoldenTests
{
    [Fact(Skip = "Enabled in Task 9 (full pipeline: samples + subs + nested SRRs).")]
    public void FullRelease_MatchesPyresceneGoldenBytes()
    {
        throw new NotImplementedException("Enabled in Task 9.");
    }
}
