using ReScene.SRR;

namespace ReScene.Tests;

public class SrrArchiveSetTests
{
    [Theory]
    [InlineData("DVD1\\aln-re4a.rar", "DVD1/aln-re4a")]
    [InlineData("DVD1\\aln-re4a.r28", "DVD1/aln-re4a")]
    [InlineData("DVD2/aln-re4b.r00", "DVD2/aln-re4b")]
    [InlineData("aln-re4a.rar", "aln-re4a")]            // root-level, old style
    [InlineData("incite-avtak.ue.xvid.cd1.r05", "incite-avtak.ue.xvid.cd1")]
    [InlineData("rls.part01.rar", "rls")]               // new style
    [InlineData("rls.part002.rar", "rls")]
    public void GetArchiveSetKey_StripsVolumeExtension_KeepsDirectory(string path, string expected)
    {
        Assert.Equal(expected, RARVolumeIdentifier.GetArchiveSetKey(path));
    }

    [Fact]
    public void Load_DirectoryLessTwoSetRelease_GroupsByBaseName()
    {
        // The in-repo fixture: two sets at root, distinguished only by base name.
        var srr = SRRFile.Load("TestData/cleanup_script/007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr");

        Assert.Equal(2, srr.ArchiveSets.Count);
        SrrArchiveSet cd1 = srr.ArchiveSets.Single(s => s.Key.EndsWith("cd1", StringComparison.OrdinalIgnoreCase));
        SrrArchiveSet cd2 = srr.ArchiveSets.Single(s => s.Key.EndsWith("cd2", StringComparison.OrdinalIgnoreCase));

        // Each set's volumes all share its base name; the two sets are disjoint.
        Assert.NotEmpty(cd1.VolumeNames);
        Assert.NotEmpty(cd2.VolumeNames);
        Assert.All(cd1.VolumeNames, v => Assert.Contains("cd1", v, StringComparison.OrdinalIgnoreCase));
        Assert.All(cd2.VolumeNames, v => Assert.Contains("cd2", v, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(cd1.ArchivedFiles.Intersect(cd2.ArchivedFiles, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_SingleSetRelease_YieldsOneSetEqualToFlatUnion()
    {
        var srr = SRRFile.Load("TestData/store_little/store_little.srr");

        Assert.Single(srr.ArchiveSets);
        SrrArchiveSet only = srr.ArchiveSets[0];
        Assert.Equal(srr.ArchivedFiles.OrderBy(x => x), only.ArchivedFiles.OrderBy(x => x));
        Assert.Equal(
            srr.ArchivedFileCrcs.OrderBy(kv => kv.Key),
            only.ArchivedFileCrcs.OrderBy(kv => kv.Key));
        Assert.Equal(srr.RARFiles.Select(r => r.FileName), only.VolumeNames);
        Assert.Equal(srr.CompressionMethod, only.CompressionMethod);
    }
}
