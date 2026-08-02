using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// The full-pipeline golden: <c>tree-fullpipeline</c> (2-disc RAR sets + a Sample/ media file +
/// a Subs/ subtitle SFV+RAR) run through the local pyrescene checkout's
/// <c>--vobsub-srr --no-isdb</c> (NO <c>--no-srs</c>) — the ONE combination that exercises SRS
/// generation for samples AND real subtitle-RAR extraction (needs unrar.exe on PATH; see
/// <c>TestData/multiset/README.md</c>'s "UnRAR provenance"). Compares our own writer's equivalent
/// output (built via the SAME lib-level SRSWriter/SRRWriter calls CreatorViewModel's folder-mode
/// staging uses in production — <see cref="BuildStoredListForFullPipeline"/> mirrors
/// ReScene.App.Core.ViewModels.CreatorViewModel.BuildNestedSubtitleStoredFiles exactly, since a lib
/// test cannot reference App.Core) after DEEP app-name normalization: the outer header (via
/// <see cref="GoldenFixtureTests.NormalizeAppName"/>), the nested "Subs/subs.srr" stored payload's
/// OWN header (same normalizer, recursively — it is itself a full SRR byte-for-byte), and the
/// "Sample/clip.srs" stored payload's SRSF appName field (a DIFFERENT format — see
/// <see cref="NormalizeSrsfAppName"/>).
///
/// STATUS: PASSES — byte-identical after deep normalization. This test originally found a real
/// divergence (first byte 607, deep-normalized): our (pre-existing) nested-SRR
/// generation ALSO stored the subtitle SFV's own bytes (and any sibling .nfo files) INSIDE the
/// nested SRR, while pyrescene's real --vobsub-srr nested SRR contains ONLY the extracted RAR
/// volume block(s) — nothing else. NOT the SRS/nested-SRR-header app-name fields (both normalize
/// away cleanly, verified independently before this was even found). Fixed
/// (parallels the RECOVERY_BLOCKS_REMOVED precedent):
/// CreatorViewModel.BuildNestedSubtitleStoredFiles now returns no additional files at all, fixed
/// globally (both the folder-mode and pre-existing wizard/Advanced-tab callers). See the README's
/// "KNOWN DIVERGENCE" section (marked resolved) for the full writeup.
/// </summary>
public class FullPipelineGoldenTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    private static string Data(string rel) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "multiset", rel);

    #region NormalizeSrsfAppName hand-built byte vectors (trust-anchor rigor, mirroring
    // GoldenFixtureTests.NormalizeAppName's own vectors — an
    // unvalidated normalizer can silently mask a real writer bug on both sides of a comparison)

    [Fact]
    public void NormalizeSrsfAppName_DifferingNameLengths_ConvergeToIdenticalBytes()
    {
        byte[] trailing = [0xAA, 0xBB, 0xCC];
        byte[] shortName = BuildSrsfBuffer(appName: "AB", fileName: "clip.ts", trailing);
        byte[] longName = BuildSrsfBuffer(appName: "pyReSample 0.7", fileName: "clip.ts", trailing);

        byte[] resultShort = NormalizeSrsfAppName(shortName);
        byte[] resultLong = NormalizeSrsfAppName(longName);

        Assert.Equal(resultShort, resultLong);
        // Every non-appName byte (flags, fileName, trailing) survives unchanged.
        Assert.Equal(trailing, resultShort[^trailing.Length..]);
    }

    [Fact]
    public void NormalizeSrsfAppName_TagNotFound_Throws()
    {
        byte[] buf = [0x01, 0x02, 0x03, 0x04];
        Assert.Throws<InvalidOperationException>(() => NormalizeSrsfAppName(buf));
    }

    [Fact]
    public void NormalizeSrsfAppName_InconsistentChunkLength_Throws()
    {
        byte[] input = BuildSrsfBuffer(appName: "X", fileName: "clip.ts", trailing: [], chunkLengthOverride: 9999);
        var ex = Assert.Throws<InvalidOperationException>(() => NormalizeSrsfAppName(input));
        Assert.Contains("chunk length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeSrsfAppName_AppNameLengthOverrunsChunk_Throws()
    {
        byte[] input = BuildSrsfBuffer(appName: "X", fileName: "clip.ts", trailing: [], appNameLenOverride: 9999);
        var ex = Assert.Throws<InvalidOperationException>(() => NormalizeSrsfAppName(input));
        Assert.Contains("overruns", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a synthetic SRSF-bearing buffer (a fake 4-byte marker + "SRSF" + LE length + payload)
    /// — an input fixture, NOT a call into production code. The override parameters let a test
    /// deliberately craft an internally-inconsistent chunk.
    /// </summary>
    private static byte[] BuildSrsfBuffer(
        string appName, string fileName, byte[] trailing, uint? chunkLengthOverride = null, ushort? appNameLenOverride = null)
    {
        byte[] appNameBytes = System.Text.Encoding.UTF8.GetBytes(appName);
        byte[] fileNameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
        ushort appNameLen = appNameLenOverride ?? (ushort)appNameBytes.Length;

        using var payload = new MemoryStream();
        using (var pw = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            pw.Write((ushort)0x0003); // flags (SIMPLE_BLOCK_FIX | ATTACHMENTS_REMOVED, per resample/main.py)
            pw.Write(appNameLen);
            pw.Write(appNameBytes);
            pw.Write((ushort)fileNameBytes.Length);
            pw.Write(fileNameBytes);
            pw.Write((long)2048); // sampleSize
            pw.Write(0x547D2660u); // crc32
        }

        byte[] payloadBytes = payload.ToArray();
        uint chunkLength = chunkLengthOverride ?? (uint)(8 + payloadBytes.Length);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)'S'); w.Write((byte)'T'); w.Write((byte)'R'); w.Write((byte)'M'); // fake marker
            w.Write(8);
            w.Write((byte)'S'); w.Write((byte)'R'); w.Write((byte)'S'); w.Write((byte)'F');
            w.Write(chunkLength);
            w.Write(payloadBytes);
            w.Write(trailing);
        }

        return ms.ToArray();
    }

    #endregion

    [Fact]
    public async Task FullRelease_MatchesPyresceneGoldenBytes()
    {
        string tree = Data("tree-fullpipeline");
        string output = Path.Combine(Path.GetTempPath(), "gfp-" + Guid.NewGuid().ToString("N") + ".srr");

        string workDir = Path.Combine(Path.GetTempPath(), "gfp-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        _tempDirs.Add(workDir);

        List<StoredFileEntry> additionalFiles = await BuildStoredListForFullPipeline(tree, workDir);

        SRRCreationResult r = await new SRRWriter().CreateFromInputsAsync(
            output,
            [Path.Combine(tree, "CD1", "a.sfv"), Path.Combine(tree, "CD2", "b.sfv")],
            tree, storeRelativePaths: true,
            additionalFiles: additionalFiles);

        Assert.Null(r.ErrorMessage);

        byte[] golden = NormalizeDeep(File.ReadAllBytes(Data("golden-fullpipeline.srr")));
        byte[] ours = NormalizeDeep(File.ReadAllBytes(output));

        Assert.Equal(golden, ours);
    }

    /// <summary>
    /// Mirrors CreatorViewModel's folder-mode artifact staging EXACTLY: generates
    /// "Sample/clip.srs" via <see cref="SRSWriter.CreateAsync"/> (no collision — a single sample,
    /// stem "Sample/clip", extension dropped) and "Subs/subs.srr" via
    /// <see cref="SRRWriter.CreateFromSFVAsync"/> — fix (user-approved, golden-verified): the
    /// nested SRR is RAR-blocks-ONLY (no embedded SFV, no additionalFiles at all;
    /// CreatorViewModel.BuildNestedSubtitleStoredFiles now returns null), matching pyReScene's real
    /// <c>--vobsub-srr</c> output exactly. The subtitle SFV's own bytes are stored separately, in
    /// the OUTER SRR only (the scanner's pass-10 stores every SFV; no redundant re-add). Then
    /// returns the full additionalFiles list in the same order CreatorViewModel's merge/
    /// pass-10-reorder would produce: nfo, generated SRS, nested SRR, its SFV (no proof pairs in
    /// this tree, so ApplyProofBeforeSfvReorder is a no-op — verified separately by
    /// CreatorViewModelArtifactTests).
    ///
    /// NOTE: this list's ORDER is HAND-BUILT/hardcoded above, not
    /// produced by running the actual scanner/VM ordering logic (a lib test cannot reference
    /// App.Core at all). This golden therefore validates the WRITER's byte-for-byte output GIVEN a
    /// correct, reference-order stored list — it does NOT validate that ReleaseScanner/
    /// CreatorViewModel actually PRODUCE that order in production (e.g. main-sfv deferral, proof-
    /// directory splice reconciliation, multi-chain subtitle naming). That ordering correctness is
    /// covered separately by ReScene.App.Core.Tests (ReleaseScannerStoredTests/
    /// ReleaseScannerMainSetTests and CreatorViewModelArtifactTests, added
    /// alongside this note) — a passing golden alone does NOT prove the scanner/VM ordering is
    /// correct, only that the writer is, so don't read it as full-pipeline ordering validation.
    /// </summary>
    private static async Task<List<StoredFileEntry>> BuildStoredListForFullPipeline(string tree, string workDir)
    {
        var srsOptions = new SRSCreationOptions { AppName = "ReScene.NET" };
        var srrOptions = new SRRCreationOptions { AppName = "ReScene.NET" };

        string srsPath = Path.Combine(workDir, "clip.srs");
        SRSCreationResult srsResult = await new SRSWriter().CreateAsync(
            srsPath, Path.Combine(tree, "Sample", "clip.ts"), srsOptions);
        Assert.True(srsResult.Success, srsResult.ErrorMessage);

        string subsSfv = Path.Combine(tree, "Subs", "subs.sfv");
        string nestedSrrPath = Path.Combine(workDir, "subs.srr");
        SRRCreationResult nestedResult = await new SRRWriter().CreateFromSFVAsync(
            nestedSrrPath, subsSfv, additionalFiles: null, srrOptions);
        Assert.True(nestedResult.Success, nestedResult.ErrorMessage);

        return
        [
            new StoredFileEntry("release.nfo", Path.Combine(tree, "release.nfo")),
            new StoredFileEntry("Sample/clip.srs", srsPath),
            new StoredFileEntry("Subs/subs.srr", nestedSrrPath),
            new StoredFileEntry("Subs/subs.sfv", subsSfv),
        ];
    }

    /// <summary>
    /// Deep app-name normalization for the full-pipeline golden: normalizes the outer header (via
    /// <see cref="GoldenFixtureTests.NormalizeAppName"/>), then recursively normalizes any stored
    /// payload that is itself a nested SRR (its own header block — same format, same normalizer)
    /// or an SRS stream payload (its SRSF appName field — <see cref="NormalizeSrsfAppName"/>).
    /// Splicing a differently-sized normalized replacement into the outer byte stream also patches
    /// that stored file's own 4-byte "AddSize"/FileLength header field (at
    /// <c>BlockPosition + 7</c>, right after the 7-byte base header —
    /// <c>SRRFileParser.ParseStoredFileBlock</c>) so nothing downstream reads a stale length.
    /// </summary>
    private static byte[] NormalizeDeep(byte[] srrBytes)
    {
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, srrBytes);
            var srr = SRRFile.Load(tmp);

            var edits = new List<(long Offset, int Length, byte[] Replacement)>();
            foreach (SRRStoredFileBlock stored in srr.StoredFiles)
            {
                byte[] payload = new byte[stored.FileLength];
                Array.Copy(srrBytes, stored.DataOffset, payload, 0, stored.FileLength);

                byte[]? normalized = stored.FileName switch
                {
                    _ when stored.FileName.EndsWith(".srr", StringComparison.OrdinalIgnoreCase)
                        && LooksLikeSrrHeader(payload) => GoldenFixtureTests.NormalizeAppName(payload),
                    _ when stored.FileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase) =>
                        NormalizeSrsfAppName(payload),
                    _ => null,
                };

                if (normalized is not null)
                {
                    edits.Add((stored.DataOffset, payload.Length, normalized));
                    edits.Add((stored.BlockPosition + 7, 4, BitConverter.GetBytes((uint)normalized.Length)));
                }
            }

            byte[] result = srrBytes;
            foreach ((long offset, int length, byte[] replacement) in edits.OrderByDescending(e => e.Offset))
            {
                using var ms = new MemoryStream();
                ms.Write(result, 0, (int)offset);
                ms.Write(replacement, 0, replacement.Length);
                ms.Write(result, (int)offset + length, result.Length - (int)offset - length);
                result = ms.ToArray();
            }

            return GoldenFixtureTests.NormalizeAppName(result);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static bool LooksLikeSrrHeader(byte[] buf) =>
        buf.Length >= 3 && BitConverter.ToUInt16(buf, 0) == 0x6969 && buf[2] == 0x69;

    /// <summary>
    /// Normalizes an SRSF chunk's app-name field within a stored SRS stream payload (a
    /// KNOWN RISK, resolved: pyrescene's SRS writer and ours produce byte-identical SRSF payloads
    /// for a plain stream-type sample except this field — verified by hand-decoding both sides'
    /// raw bytes). Layout (resample/main.py: FileData.serialize_as_stream == serialize_as_mp3,
    /// TrackData likewise): a container-specific marker, then the literal tag <c>"SRSF"</c> + a
    /// little-endian uint32 chunk length (== 8 + payload length), then the payload itself:
    /// flags(2) + appNameLen(2) + appName + fileNameLen(2) + fileName + sampleSize(8) + crc32(4).
    /// Unlike the outer/nested SRR header (which OMITS the app-name field entirely when unset),
    /// SRSF always carries one — no conditional skip. Validates the tag is found and every length
    /// field is internally consistent (mirroring <see cref="GoldenFixtureTests.NormalizeAppName"/>'s
    /// trust-anchor rigor) before rewriting.
    /// </summary>
    internal static byte[] NormalizeSrsfAppName(byte[] buf)
    {
        byte[] tagBytes = "SRSF"u8.ToArray();
        int tagIndex = IndexOf(buf, tagBytes);
        if (tagIndex < 0 || tagIndex + 8 > buf.Length)
        {
            throw new InvalidOperationException("SRSF tag not found in the stored .srs payload.");
        }

        uint chunkLength = BitConverter.ToUInt32(buf, tagIndex + 4);
        int payloadStart = tagIndex + 8;
        // chunkLength (serialize_as_mp3: S_LONG.pack(4 + 4 + len(data))) counts the "SRSF" tag(4) +
        // this length field(4) + the payload — i.e. it's measured from tagIndex, not payloadStart.
        long payloadEnd = tagIndex + chunkLength;
        if (payloadEnd < payloadStart || payloadEnd > buf.Length)
        {
            throw new InvalidOperationException(
                $"SRSF chunk length {chunkLength} at offset {tagIndex} is inconsistent with the buffer ({buf.Length} bytes).");
        }

        if (payloadStart + 4 > buf.Length)
        {
            throw new InvalidOperationException("SRSF payload is too short to contain flags + app-name length.");
        }

        ushort flags = BitConverter.ToUInt16(buf, payloadStart);
        ushort appNameLen = BitConverter.ToUInt16(buf, payloadStart + 2);
        long appNameEnd = payloadStart + 4 + appNameLen;
        if (appNameEnd + 2 > payloadEnd)
        {
            throw new InvalidOperationException(
                $"SRSF app-name length {appNameLen} overruns the {chunkLength}-byte chunk.");
        }

        const string replacement = "NORMALIZED";
        byte[] repl = System.Text.Encoding.UTF8.GetBytes(replacement);

        using var ms = new MemoryStream();
        ms.Write(buf, 0, payloadStart);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(flags);
            w.Write((ushort)repl.Length);
            w.Write(repl);
        }

        ms.Write(buf, (int)appNameEnd, buf.Length - (int)appNameEnd);
        byte[] rewritten = ms.ToArray();

        // Fix up the chunk's own declared length (payload grew/shrank by repl.Length - appNameLen).
        int delta = repl.Length - appNameLen;
        BitConverter.GetBytes((uint)(chunkLength + delta)).CopyTo(rewritten, tagIndex + 4);
        return rewritten;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
