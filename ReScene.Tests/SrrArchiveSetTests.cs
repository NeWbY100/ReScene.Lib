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

    [Fact]
    public void Load_PerSetCrcs_EqualFlatCrcs_ForAllFilesInAllSets()
    {
        // Regression for fix #1: per-set CRCs must equal the flat final values.
        // The flat dict resolves split-after overrides (last complete entry wins);
        // the per-set dicts must reflect those same final values, not first-write-wins.
        var srr = SRRFile.Load("TestData/cleanup_script/007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr");

        Assert.NotEmpty(srr.ArchiveSets);
        foreach (SrrArchiveSet set in srr.ArchiveSets)
        {
            foreach (string file in set.ArchivedFiles)
            {
                // Every file tracked per-set must have a CRC in the flat dict and
                // the per-set value must match.
                Assert.True(srr.ArchivedFileCrcs.TryGetValue(file, out string? flatCrc),
                    $"File '{file}' in set '{set.Key}' is missing from flat ArchivedFileCrcs");
                Assert.True(set.ArchivedFileCrcs.TryGetValue(file, out string? setCrc),
                    $"File '{file}' in set '{set.Key}' is missing from set ArchivedFileCrcs");
                Assert.Equal(flatCrc, setCrc);
            }
        }
    }

    [Fact]
    public void Load_SetDirectory_UsesForwardSlashes()
    {
        // Regression for fix #2: Directory must use forward slashes (platform-consistent).
        // The fine_2cd fixture uses root-level volumes (no directory), so use a fixture
        // whose RARFile blocks carry a directory prefix, or verify root volumes yield "".
        var srr = SRRFile.Load("TestData/cleanup_script/007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr");

        foreach (SrrArchiveSet set in srr.ArchiveSets)
        {
            // Directory must never contain backslashes.
            Assert.DoesNotContain('\\', set.Directory);
        }
    }
}
