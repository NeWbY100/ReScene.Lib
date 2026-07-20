using ReScene.RAR;

namespace ReScene.Tests;

/// <summary>
/// Routes real, on-disk RAR4 fixtures (<see cref="RarFixtures"/>) through the production
/// <see cref="RarProofInspector.Inspect"/> — proving the inspector against real bytes, since
/// ReScene.App.Core.Tests drives the same predicates through an injectable seam with fact
/// literals only (no cross-test-assembly reference; see the multi-set SRR creation plan, Task 5).
/// </summary>
public class RarProofInspectorTests : TempDirTestBase
{
    [Fact]
    public void Inspect_SingleImageEntry_LastPackedIsImage_AndAnyImage_True()
    {
        string path = Path.Combine(TempDir, "p.rar");
        RarFixtures.WriteMultiEntryRarFile(path, "cover.jpg");

        ProofRarFacts facts = RarProofInspector.Inspect(path);

        Assert.True(facts.Readable);
        Assert.True(facts.HasPackedBlocks);
        Assert.True(facts.AnyImage);
        Assert.True(facts.LastPackedIsImage);
    }

    [Fact]
    public void Inspect_LastEntryNotImage_EarlierEntryIsImage_LastBlockWins()
    {
        // pyrescene's proof state machine reassigns `skip` on every packed block it sees — the
        // LAST block decides, not the first. An image followed by a non-image must report
        // LastPackedIsImage=false even though an image block was present (AnyImage=true).
        string path = Path.Combine(TempDir, "p.rar");
        RarFixtures.WriteMultiEntryRarFile(path, "cover.jpg", "readme.txt");

        ProofRarFacts facts = RarProofInspector.Inspect(path);

        Assert.True(facts.Readable);
        Assert.True(facts.HasPackedBlocks);
        Assert.True(facts.AnyImage);
        Assert.False(facts.LastPackedIsImage);
    }

    [Fact]
    public void Inspect_NonImageEntry_LastPackedIsImage_AndAnyImage_False()
    {
        string path = Path.Combine(TempDir, "p.rar");
        RarFixtures.WriteMultiEntryRarFile(path, "readme.txt");

        ProofRarFacts facts = RarProofInspector.Inspect(path);

        Assert.True(facts.Readable);
        Assert.True(facts.HasPackedBlocks);
        Assert.False(facts.AnyImage);
        Assert.False(facts.LastPackedIsImage);
    }

    [Fact]
    public void Inspect_NoPackedBlocks_HasPackedBlocksFalse_ButStillReadable()
    {
        string path = Path.Combine(TempDir, "p.rar");
        RarFixtures.WriteMultiEntryRarFile(path); // marker + archive header + end archive only

        ProofRarFacts facts = RarProofInspector.Inspect(path);

        Assert.True(facts.Readable);
        Assert.False(facts.HasPackedBlocks);
        Assert.False(facts.AnyImage);
        Assert.False(facts.LastPackedIsImage);
    }

    [Fact]
    public void Inspect_RAR5Marker_ReportsUnreadable()
    {
        // excerpt: remove_unwanted_sfvs L374-377 — "No RAR5 support yet" is a caught ValueError
        // in pyrescene; this port surfaces the same outcome as Readable=false.
        string path = Path.Combine(TempDir, "p.rar");
        File.WriteAllBytes(path, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);

        ProofRarFacts facts = RarProofInspector.Inspect(path);

        Assert.False(facts.Readable);
        Assert.False(facts.HasPackedBlocks);
        Assert.False(facts.AnyImage);
        Assert.False(facts.LastPackedIsImage);
    }

    [Fact]
    public void Inspect_MissingFile_ReportsUnreadable_DoesNotThrow()
    {
        string path = Path.Combine(TempDir, "does-not-exist.rar");

        ProofRarFacts facts = RarProofInspector.Inspect(path);

        Assert.False(facts.Readable);
    }
}
