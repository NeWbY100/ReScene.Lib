using ReScene.SRR;

namespace ReScene.Tests;

public class SRRArchiveSetTests : TempDirTestBase
{
    // Encodes a DateTime as a packed RAR/DOS date-time (date in the high word, time in the low word).
    // DOS seconds have 2-second granularity, so only even-second DateTimes round-trip exactly.
    private static uint ToDosDate(DateTime dt) =>
        ((uint)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day) << 16)
        | (uint)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));

    [Fact]
    public void Load_TwoSetsShareDirectoryName_EachKeepsItsOwnMcaTimes()
    {
        // Fix #7: directory records were tracked flat (release-wide), so two sets that each archive
        // a same-named directory ("Subs") clobbered each other's m/c/a times (last-write-wins).
        // Same name is the discriminating case; distinct names would not expose the contamination.
        var cd1M = new DateTime(2021, 1, 2, 3, 4, 0);
        var cd1C = new DateTime(2021, 5, 6, 7, 8, 0);
        var cd1A = new DateTime(2021, 9, 10, 11, 12, 0);
        var cd2M = new DateTime(2022, 2, 3, 4, 6, 0);
        var cd2C = new DateTime(2022, 6, 7, 8, 10, 0);
        var cd2A = new DateTime(2022, 10, 11, 12, 14, 0);

        string path = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddRARFileWithHeaders("movie.cd1.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("Subs\\", isDirectory: true, packedSize: 0, unpackedSize: 0,
                           fileTimeDOS: ToDosDate(cd1M), creationTimeDOS: ToDosDate(cd1C), accessTimeDOS: ToDosDate(cd1A))
                       .AddEndArchive();
            })
            .AddRARFileWithHeaders("movie.cd2.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("Subs\\", isDirectory: true, packedSize: 0, unpackedSize: 0,
                           fileTimeDOS: ToDosDate(cd2M), creationTimeDOS: ToDosDate(cd2C), accessTimeDOS: ToDosDate(cd2A))
                       .AddEndArchive();
            })
            .BuildToFile(TempDir, "two_sets_shared_dir.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(2, srr.ArchiveSets.Count);
        SRRArchiveSet cd1 = srr.ArchiveSets.Single(s => s.Key.EndsWith("cd1", StringComparison.OrdinalIgnoreCase));
        SRRArchiveSet cd2 = srr.ArchiveSets.Single(s => s.Key.EndsWith("cd2", StringComparison.OrdinalIgnoreCase));

        // Each set records exactly its own "Subs" directory...
        Assert.Equal("Subs", Assert.Single(cd1.ArchivedDirectories));
        Assert.Equal("Subs", Assert.Single(cd2.ArchivedDirectories));

        // ...with its own three times, uncontaminated by the other set.
        Assert.Equal(cd1M, cd1.ArchivedDirectoryTimestamps["Subs"]);
        Assert.Equal(cd1C, cd1.ArchivedDirectoryCreationTimes["Subs"]);
        Assert.Equal(cd1A, cd1.ArchivedDirectoryAccessTimes["Subs"]);

        Assert.Equal(cd2M, cd2.ArchivedDirectoryTimestamps["Subs"]);
        Assert.Equal(cd2C, cd2.ArchivedDirectoryCreationTimes["Subs"]);
        Assert.Equal(cd2A, cd2.ArchivedDirectoryAccessTimes["Subs"]);
    }

    [Theory]
    [InlineData("DVD1\\aln-re4a.rar", "DVD1/aln-re4a")]
    [InlineData("DVD1\\aln-re4a.r28", "DVD1/aln-re4a")]
    [InlineData("DVD2/aln-re4b.r00", "DVD2/aln-re4b")]
    [InlineData("aln-re4a.rar", "aln-re4a")]            // root-level, old style
    [InlineData("incite-avtak.ue.xvid.cd1.r05", "incite-avtak.ue.xvid.cd1")]
    [InlineData("rls.part01.rar", "rls")]               // new style
    [InlineData("rls.part002.rar", "rls")]
    public void GetArchiveSetKey_StripsVolumeExtension_KeepsDirectory(string path, string expected) => Assert.Equal(expected, RARVolumeIdentifier.GetArchiveSetKey(path));

    [Fact]
    public void Load_DirectoryLessTwoSetRelease_GroupsByBaseName()
    {
        // The in-repo fixture: two sets at root, distinguished only by base name.
        var srr = SRRFile.Load("TestData/cleanup_script/007.A.View.To.A.Kill.1985.UE.iNTERNAL.DVDRip.XviD-iNCiTE.fine_2cd.srr");

        Assert.Equal(2, srr.ArchiveSets.Count);
        SRRArchiveSet cd1 = srr.ArchiveSets.Single(s => s.Key.EndsWith("cd1", StringComparison.OrdinalIgnoreCase));
        SRRArchiveSet cd2 = srr.ArchiveSets.Single(s => s.Key.EndsWith("cd2", StringComparison.OrdinalIgnoreCase));

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
        SRRArchiveSet only = srr.ArchiveSets[0];
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
        foreach (SRRArchiveSet set in srr.ArchiveSets)
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

        foreach (SRRArchiveSet set in srr.ArchiveSets)
        {
            // Directory must never contain backslashes.
            Assert.DoesNotContain('\\', set.Directory);
        }
    }

    [Fact]
    public void Load_TwoSets_ArchivedFilesInOrder_AreIndependentPerSet()
    {
        // Each set's embedded headers appear in their own non-alphabetical order; the per-set
        // ordered lists must reflect only that set's own headers, not a merge of both sets'
        // headers nor the flat SRRFile's release-wide appearance order.
        string path = new SRRTestDataBuilder()
            .AddSRRHeader()
            .AddRARFileWithHeaders("movie.cd1.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("zzz.dat")
                       .AddFileHeader("aaa.dat")
                       .AddEndArchive();
            })
            .AddRARFileWithHeaders("movie.cd2.rar", headers =>
            {
                headers.AddArchiveHeader()
                       .AddFileHeader("mmm.dat")
                       .AddFileHeader("bbb.dat")
                       .AddEndArchive();
            })
            .BuildToFile(TempDir, "two_sets_order.srr");

        var srr = SRRFile.Load(path);

        Assert.Equal(2, srr.ArchiveSets.Count);
        SRRArchiveSet cd1 = srr.ArchiveSets.Single(s => s.Key.EndsWith("cd1", StringComparison.OrdinalIgnoreCase));
        SRRArchiveSet cd2 = srr.ArchiveSets.Single(s => s.Key.EndsWith("cd2", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["zzz.dat", "aaa.dat"], cd1.ArchivedFilesInOrder);
        Assert.Equal(["mmm.dat", "bbb.dat"], cd2.ArchivedFilesInOrder);
        Assert.Equal(["zzz.dat", "aaa.dat", "mmm.dat", "bbb.dat"], srr.ArchivedFilesInOrder);

        // ArchivedFiles (the pre-existing HashSet) stays unaffected by tracking order alongside it.
        Assert.True(cd1.ArchivedFiles.SetEquals(cd1.ArchivedFilesInOrder));
        Assert.True(cd2.ArchivedFiles.SetEquals(cd2.ArchivedFilesInOrder));
    }
}
