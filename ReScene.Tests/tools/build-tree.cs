#:package Crc32.NET@1.2.0

// Golden-fixture tree builder for multi-set SRR creation (see
// docs/superpowers/specs/2026-07-18-multiset-srr-creation-design.md §6). Run manually via
// `dotnet run build-tree.cs -- <outputBaseDir>` from generate-golden.py — never invoked by the
// xUnit test suite. Writes `tree-2disc/` and `tree-storageonly/` under <outputBaseDir>.
//
// The RAR4 store-mode volume writer below is a line-for-line port of
// ReScene.Tests/RarFixtures.cs's WriteStoreModeRarSet (same marker/archive-header/file-header/
// end-block layout, same flag values) so the golden fixture's RAR bytes are produced by the exact
// byte layout our own writer/tests already rely on, rather than a second, potentially-diverging
// implementation. See README.md for why this file-based C# app was chosen over a pure-Python
// RAR4 writer.

using Force.Crc32;

string baseDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

BuildTwoDiscTree(Path.Combine(baseDir, "tree-2disc"));
BuildStorageOnlyTree(Path.Combine(baseDir, "tree-storageonly"));
BuildFullPipelineTree(Path.Combine(baseDir, "tree-fullpipeline"));

Console.WriteLine($"Trees written under: {baseDir}");
return 0;

static void BuildTwoDiscTree(string root)
{
    ResetDirectory(root);

    File.WriteAllText(Path.Combine(root, "release.nfo"),
        "Fixture.Release.2026.MULTi.DVDRip-GRP\r\n\r\n" +
        "Synthetic 2-disc release tree for the ReScene.Manager Task 3\r\n" +
        "pyrescene golden-fixture byte-equality harness. Not a real release.\r\n");

    BuildRarSet(Path.Combine(root, "CD1"), "a", volumeCount: 2, payloadBytes: 64);
    BuildRarSet(Path.Combine(root, "CD2"), "b", volumeCount: 2, payloadBytes: 64);

    // Excluded subtitle SFV (pyrescene remove_unwanted_sfvs rule: name contains "subs") plus its
    // companion RAR — present on disk for realism, but never opened/stored by either pyrescene or
    // our writer for this tree (the SFV itself is stored verbatim as a stored file; see README).
    BuildRarSet(Path.Combine(root, "Subs"), "subs", volumeCount: 1, payloadBytes: 32);
}

static void BuildStorageOnlyTree(string root)
{
    ResetDirectory(root);

    File.WriteAllText(Path.Combine(root, "release.nfo"),
        "Fixture.Release.2026.NFOFIX-GRP\r\n\r\n" +
        "Synthetic nfo-only (storage-only / fix-release) tree: no SFV, no RAR.\r\n");
}

// tree-fullpipeline (spec §3/§6): same 2-disc + Subs/ shape as tree-2disc, PLUS a Sample/ directory — the
// full-pipeline golden that exercises SRS generation (samples) and the vobsub_srr nested-SRR path
// (subs) together, via `pyrescene.py --vobsub-srr` (no --no-srs).
static void BuildFullPipelineTree(string root)
{
    ResetDirectory(root);

    File.WriteAllText(Path.Combine(root, "release.nfo"),
        "Fixture.Release.2026.MULTi.DVDRip-GRP\r\n\r\n" +
        "Synthetic full-pipeline release tree for the ReScene.Manager Task 9\r\n" +
        "pyrescene golden-fixture byte-equality harness (samples + subs). Not a real release.\r\n");

    BuildRarSet(Path.Combine(root, "CD1"), "a", volumeCount: 2, payloadBytes: 64, useRealCrc: true);
    BuildRarSet(Path.Combine(root, "CD2"), "b", volumeCount: 2, payloadBytes: 64, useRealCrc: true);

    // Excluded subtitle SFV (same shape as tree-2disc's Subs/) — but THIS tree is run WITH
    // --vobsub-srr (unrar available), so subs.rar is actually EXTRACTED (unrar.exe validates the
    // payload's FILE_CRC header field on extraction — useRealCrc:true is REQUIRED here, unlike
    // tree-2disc's Subs/, which is never extracted since that tree only ever runs --no-srs).
    BuildRarSet(Path.Combine(root, "Subs"), "subs", volumeCount: 1, payloadBytes: 32, useRealCrc: true);

    // A plain "stream" type sample (deliberately NOT .vob, to sidestep the excerpt's RAR-backed-vob
    // special case entirely — that path is separately unit-tested in
    // CreatorViewModelArtifactTests.cs with fakes). stream_profile_sample only needs the file's own
    // size + first 256 bytes (SIG_SIZE) + whole-file CRC32 (resample/main.py) — no container
    // structure to get subtly wrong on either implementation's side. 2048 bytes comfortably covers
    // one full signature.
    WriteSampleStream(Path.Combine(root, "Sample", "clip.ts"), size: 2048);
}

static void WriteSampleStream(string path, int size)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    byte[] data = new byte[size];
    new Random(42).NextBytes(data);
    File.WriteAllBytes(path, data);
}

static void ResetDirectory(string dir)
{
    if (Directory.Exists(dir))
    {
        Directory.Delete(dir, recursive: true);
    }

    Directory.CreateDirectory(dir);
}

static void BuildRarSet(string dir, string baseName, int volumeCount, int payloadBytes, bool useRealCrc = false)
{
    Directory.CreateDirectory(dir);
    List<string> volumeNames = WriteStoreModeRarSet(dir, baseName, volumeCount, payloadBytes, useRealCrc);
    WriteCorrectCrcSfv(dir, baseName, volumeNames);
}

static void WriteCorrectCrcSfv(string dir, string baseName, List<string> fileNames)
{
    var lines = new List<string>();
    foreach (string fileName in fileNames)
    {
        byte[] fileBytes = File.ReadAllBytes(Path.Combine(dir, fileName));
        uint crc = Crc32Algorithm.Compute(fileBytes);
        lines.Add($"{fileName} {crc:x8}");
    }

    File.WriteAllLines(Path.Combine(dir, baseName + ".sfv"), lines);
}

// --- Below: verbatim port of ReScene.Tests/RarFixtures.cs's WriteStoreModeRarSet, with the
// RARArchiveFlags/RARFileFlags enum values inlined as literals (those enums are internal to the
// ReScene assembly, unreachable from this standalone file-based app). ---
//
// `useRealCrc` (default false): tree-2disc/tree-storageonly NEVER pass it — those trees'
// RAR bytes (and hence their COMMITTED golden-2disc.srr/golden-storageonly.srr, which embed the
// FILE_CRC field verbatim, copied straight through by pyrescene's/our own SRR-header creation)
// must stay byte-for-byte what was already committed. Only
// BuildFullPipelineTree opts in, because ITS Subs/ set is actually extracted by real `unrar.exe`
// (via --vobsub-srr), which validates FILE_CRC and fails ("checksum error") on the old
// placeholder value.

static List<string> WriteStoreModeRarSet(string dir, string baseName, int volumeCount, int payloadBytes, bool useRealCrc = false)
{
    var fileNames = new List<string>();
    for (int i = 0; i < volumeCount; i++)
    {
        string fileName = i == 0 ? $"{baseName}.rar" : $"{baseName}.r{i - 1:D2}";
        WriteVolume(Path.Combine(dir, fileName), $"{baseName}.dat", payloadBytes, i, volumeCount, useRealCrc);
        fileNames.Add(fileName);
    }

    return fileNames;
}

static void WriteVolume(string path, string archivedFileName, int payloadBytes, int index, int volumeCount, bool useRealCrc)
{
    const ushort ArchiveVolume = 0x0001, ArchiveNewNumbering = 0x0010, ArchiveFirstVolume = 0x0100;
    ushort archiveFlags = ArchiveVolume | ArchiveNewNumbering;
    if (index == 0)
    {
        archiveFlags |= ArchiveFirstVolume;
    }

    const ushort FileLongBlock = 0x8000, FileExtTime = 0x1000, FileSplitBefore = 0x0001, FileSplitAfter = 0x0002;
    ushort fileFlags = FileLongBlock | FileExtTime;
    if (volumeCount > 1)
    {
        if (index > 0)
        {
            fileFlags |= FileSplitBefore;
        }

        if (index < volumeCount - 1)
        {
            fileFlags |= FileSplitAfter;
        }
    }

    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var writer = new BinaryWriter(fs);

    writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }); // RAR4 marker
    WriteArchiveHeader(writer, archiveFlags);
    byte[] payload = new byte[payloadBytes];
    uint fileCrc = useRealCrc ? Crc32Algorithm.Compute(payload) : 0xDEADBEEFu;
    WriteFileHeader(writer, archivedFileName, (uint)payload.Length, fileFlags, fileCrc);
    writer.Write(payload);
    WriteEndArchive(writer);
}

static void WriteArchiveHeader(BinaryWriter writer, ushort flags)
{
    ushort headerSize = 13;
    byte[] header = new byte[headerSize];
    header[2] = 0x73;
    BitConverter.GetBytes(flags).CopyTo(header, 3);
    BitConverter.GetBytes(headerSize).CopyTo(header, 5);

    WriteCrc(header);
    writer.Write(header);
}

static void WriteFileHeader(BinaryWriter writer, string fileName, uint packedSize, ushort flags, uint fileCrc)
{
    const ushort FileExtTime = 0x1000;
    byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName);
    ushort nameSize = (ushort)nameBytes.Length;
    int extTimeSize = (flags & FileExtTime) != 0 ? 2 : 0;
    ushort headerSize = (ushort)(7 + 25 + nameSize + extTimeSize);

    byte[] header = new byte[headerSize];
    header[2] = 0x74;
    BitConverter.GetBytes(flags).CopyTo(header, 3);
    BitConverter.GetBytes(headerSize).CopyTo(header, 5);
    BitConverter.GetBytes(packedSize).CopyTo(header, 7);    // ADD_SIZE (packed size)
    BitConverter.GetBytes(packedSize).CopyTo(header, 11);   // UNP_SIZE (store: unpacked == packed)
    header[15] = 2;                                          // HOST_OS (Windows)
    BitConverter.GetBytes(fileCrc).CopyTo(header, 16);       // FILE_CRC (real payload CRC32 or the 0xDEADBEEF placeholder — see WriteStoreModeRarSet's useRealCrc remark)
    BitConverter.GetBytes(0x5A8E3100u).CopyTo(header, 20);   // FILE_TIME (DOS)
    header[24] = 29;                                         // UNP_VER
    header[25] = 0x30;                                       // METHOD: Store
    BitConverter.GetBytes(nameSize).CopyTo(header, 26);
    BitConverter.GetBytes(0x00000020u).CopyTo(header, 28);   // ATTR
    nameBytes.CopyTo(header, 32);

    if ((flags & FileExtTime) != 0)
    {
        BitConverter.GetBytes((ushort)0x8000).CopyTo(header, 32 + nameSize);
    }

    WriteCrc(header);
    writer.Write(header);
}

static void WriteEndArchive(BinaryWriter writer)
{
    ushort headerSize = 7;
    byte[] header = new byte[headerSize];
    header[2] = 0x7B;
    BitConverter.GetBytes((ushort)0).CopyTo(header, 3);
    BitConverter.GetBytes(headerSize).CopyTo(header, 5);

    WriteCrc(header);
    writer.Write(header);
}

static void WriteCrc(byte[] header)
{
    uint crc32 = Crc32Algorithm.Compute(header, 2, header.Length - 2);
    BitConverter.GetBytes((ushort)(crc32 & 0xFFFF)).CopyTo(header, 0);
}
