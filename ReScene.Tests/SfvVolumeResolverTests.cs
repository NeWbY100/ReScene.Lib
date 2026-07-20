using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Direct unit tests for the shared SFV-&gt;ordered-chains resolver (<see cref="SfvVolumeResolver"/>),
/// the single source of truth codex Task 9 fix-3 (G3/G4) extracted from
/// <c>SRRWriter.ResolveVolumesAsync</c>'s SFV branch so the folder-mode subtitle nested-SRR path
/// (<c>CreatorViewModel.GenerateNestedSubtitleSrrsAsync</c>) can never diverge from the writer
/// again. The two divergences these guard: a spaced RAR name (the old VM copy split every space
/// and threw) and a <c>.\</c>-prefixed continuation (the old VM copy left it lexically distinct
/// from its head, splitting one chain into two same-named SRRs).
/// </summary>
public sealed class SfvVolumeResolverTests : IDisposable
{
    private readonly string _dir;

    public SfvVolumeResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sfvres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "x");

    private static string[][] BaseNames(IReadOnlyList<IReadOnlyList<string>> chains)
        => [.. chains.Select(c => c.Select(v => Path.GetFileName(v)!).ToArray())];

    [Fact]
    public void ParseSfvEntryNames_SpacedName_KeepsSpaces_SkipsCommentsAndBlanks()
    {
        string[] lines =
        [
            "; comment",
            "",
            "sub title.rar 12345678",
            "eng.rar deadbeef",
        ];

        Assert.Equal(["sub title.rar", "eng.rar"], SfvVolumeResolver.ParseSfvEntryNames(lines));
    }

    [Fact]
    public void ResolveOrderedChains_SpacedRarName_OneChain_BothVolumesSorted()
    {
        Touch("sub title.rar");
        Touch("sub title.r00");
        string[] lines = ["sub title.rar 00000000", "sub title.r00 00000000"];

        string[][] chains = BaseNames(SfvVolumeResolver.ResolveOrderedChains(_dir, lines));

        string[] chain = Assert.Single(chains);
        Assert.Equal(["sub title.rar", "sub title.r00"], chain);
    }

    [Fact]
    public void ResolveOrderedChains_DotSlashContinuation_FoldsWithHead_OneChain()
    {
        Touch("eng.rar");
        Touch("eng.r00");
        string[] lines = ["eng.rar 00000000", @".\eng.r00 00000000"];

        string[][] chains = BaseNames(SfvVolumeResolver.ResolveOrderedChains(_dir, lines));

        string[] chain = Assert.Single(chains);
        Assert.Equal(["eng.rar", "eng.r00"], chain);
    }

    [Fact]
    public void ResolveOrderedChains_TwoDistinctChains_KeptSeparate_InFirstSeenOrder()
    {
        Touch("eng.rar");
        Touch("jpn.rar");
        string[] lines = ["jpn.rar 00000000", "eng.rar 00000000"];

        string[][] chains = BaseNames(SfvVolumeResolver.ResolveOrderedChains(_dir, lines));

        Assert.Equal(2, chains.Length);
        Assert.Equal(["jpn.rar"], chains[0]); // first-seen order preserved (jpn before eng)
        Assert.Equal(["eng.rar"], chains[1]);
    }

    [Fact]
    public void ResolveOrderedChains_NonRarEntries_Ignored()
    {
        Touch("subs.idx");
        Touch("readme.txt");
        string[] lines = ["subs.idx 00000000", "readme.txt 00000000"];

        Assert.Empty(SfvVolumeResolver.ResolveOrderedChains(_dir, lines));
    }
}
