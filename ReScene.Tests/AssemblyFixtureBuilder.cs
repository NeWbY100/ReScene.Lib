using System.Text;
using Force.Crc32;
using ReScene.Core.Cryptography;
using ReScene.RAR;
using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Builds a complete synthetic guided-assembly scenario on disk: an "original" RAR4 volume set
/// (the shape an SRR describes) and a "produced" RAR4 volume set carrying the exact same packed
/// payload bytes, re-split under a different file-header shape — plus the SRR that ties them
/// together. Every later assembly test builds its scenario through <see cref="Build"/> rather than
/// hand-rolling volumes, so all of them exercise the same real, parseable RAR4 layout.
/// </summary>
/// <remarks>
/// Split algorithm (applied independently to the original and produced sets): walk volumes, each
/// with a fixed <c>volumeSize</c> byte budget. Every volume reserves 7 (marker) + 13 (archive
/// header) + 7 (end block) = 27 bytes of overhead; whatever remains is greedily filled with pieces
/// of the current archived file. A piece takes <c>min(remaining budget - this piece's header
/// length, bytes left in the file)</c> bytes; when that is less than the file's remaining bytes,
/// the piece is marked <see cref="RARFileFlags.SplitAfter"/> and — because the piece was sized to
/// consume the entire remaining budget — the volume is complete and a new one starts with the same
/// file marked <see cref="RARFileFlags.SplitBefore"/>. Because a file header's length depends on
/// the shape (whether extended time is present), the two shapes land their split points at
/// different byte offsets for the identical payload — exactly the real-world condition guided
/// assembly must reconcile.
/// </remarks>
internal static class AssemblyFixtureBuilder
{
    private const int MarkerSize = 7;
    private const int ArchiveHeaderSize = 13;
    private const int EndArchiveSize = 7;

    // Fixed RAR4 file-header fields ahead of NAME: base(7) + ADD_SIZE/UNP_SIZE/HOST_OS/FILE_CRC/
    // FILE_TIME/UNP_VER/METHOD/NAME_SIZE/ATTR(25) — see RAR4HeaderBuilder.AddFileHeader.
    private const int FixedFileHeaderFieldsSize = 32;

    // EXT_TIME flags word(2) + a 3-byte mtime remainder: the "5-byte EXT_TIME" shape.
    private const int ExtTimeSize = 5;
    private static readonly byte[] MtimeRemainderBytes = [0x01, 0x02, 0x03];

    /// <summary>
    /// Builds, under <paramref name="dir"/>: <c>originals/</c> (the original header shape),
    /// <c>produced/</c> (the produced header shape, same packed payload, re-split so each volume's
    /// total size equals <paramref name="volumeSize"/>), and the SRR (header + one RARFile section
    /// per original volume, embedding that volume's headers verbatim, flagged <see
    /// cref="SRRBlockFlags.RecoveryBlocksRemoved"/> as every real-world writer sets it). Every
    /// volume in both sets is a real, parseable RAR4 file — marker, archive header, file header(s)
    /// with <see cref="RARFileFlags.SplitBefore"/>/<see cref="RARFileFlags.SplitAfter"/> as needed,
    /// Store-method payload bytes, and an end block — so <see cref="RARHeaderReader"/> and <see
    /// cref="RARStream"/> can walk either set exactly as they would a real release.
    /// </summary>
    /// <param name="dir">Directory to build the fixture under (must already exist).</param>
    /// <param name="volumeSize">Total bytes per volume; the last volume of a set may be shorter.</param>
    /// <param name="archivedFiles">The archived files, in archive order, with their full payload bytes.</param>
    /// <param name="originalHasExtTime">
    /// Whether the original shape's file headers carry a 5-byte EXT_TIME field (flags word +
    /// 3-byte mtime remainder), as opposed to no EXT_TIME field at all.
    /// </param>
    /// <param name="producedHasExtTime">Same as <paramref name="originalHasExtTime"/>, for the produced shape.</param>
    /// <param name="volumePrefix">Volume base name; old-style naming: "{volumePrefix}.rar", ".r00", ".r01", …</param>
    /// <param name="directoryPrefix">
    /// When set (e.g. "CD1"), volumes are placed under a same-named subdirectory of both
    /// <c>originals/</c> and <c>produced/</c>, and every qualified name (SRR sections, <see
    /// cref="AssemblyFixture.OriginalVolumeNames"/>) is "CD1/t.rar"-style.
    /// </param>
    public static AssemblyFixture Build(
        string dir,
        int volumeSize,
        IReadOnlyList<(string Name, byte[] Payload)> archivedFiles,
        bool originalHasExtTime,
        bool producedHasExtTime,
        string volumePrefix = "t",
        string? directoryPrefix = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        if (archivedFiles.Count == 0)
        {
            throw new ArgumentException("At least one archived file is required.", nameof(archivedFiles));
        }

        string originalsRoot = Path.Combine(dir, "originals");
        string producedRoot = Path.Combine(dir, "produced");

        List<VolumePlan> originalPlan = PlanVolumes(archivedFiles, volumeSize, originalHasExtTime);
        List<VolumePlan> producedPlan = PlanVolumes(archivedFiles, volumeSize, producedHasExtTime);

        List<string> originalPaths = WriteVolumeSet(originalsRoot, directoryPrefix, volumePrefix, originalPlan);
        List<string> producedPaths = WriteVolumeSet(producedRoot, directoryPrefix, volumePrefix, producedPlan);

        List<string> originalNames =
            [.. originalPaths.Select(p => QualifiedName(directoryPrefix, Path.GetFileName(p)))];

        SRRTestDataBuilder srrBuilder = new SRRTestDataBuilder().AddSRRHeader("fixture");
        for (int i = 0; i < originalPlan.Count; i++)
        {
            VolumePlan volume = originalPlan[i];
            srrBuilder = srrBuilder.AddRARFileWithHeaders(
                originalNames[i],
                (ushort)SRRBlockFlags.RecoveryBlocksRemoved,
                hb => EmitVolume(hb, volume, writePayload: null));
        }

        string srrPath = srrBuilder.BuildToFile(dir, "fixture.srr");

        Dictionary<string, string> expectedCrcs = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < originalPaths.Count; i++)
        {
            expectedCrcs[originalNames[i]] = HashCalculator.Calculate(HashType.CRC32, originalPaths[i]);
        }

        return new AssemblyFixture(srrPath, originalPaths, originalNames, producedPaths[0], expectedCrcs);
    }

    /// <summary>
    /// Splits <paramref name="archivedFiles"/> into fixed-<paramref name="volumeSize"/> volume
    /// plans using the greedy packing described in the class remarks.
    /// </summary>
    private static List<VolumePlan> PlanVolumes(
        IReadOnlyList<(string Name, byte[] Payload)> archivedFiles, int volumeSize, bool hasExtTime)
    {
        const int overhead = MarkerSize + ArchiveHeaderSize + EndArchiveSize;

        RARFileFlags extTimeFlag = hasExtTime ? RARFileFlags.ExtTime : RARFileFlags.None;
        byte[]? mtimeRemainder = hasExtTime ? MtimeRemainderBytes : null;
        int extTimeSize = hasExtTime ? ExtTimeSize : 0;

        List<VolumePlan> volumes = [];
        int fileIndex = 0;
        int offsetInFile = 0;
        bool continuingFile = false;

        while (fileIndex < archivedFiles.Count)
        {
            int budget = volumeSize - overhead;
            List<FilePiece> pieces = [];

            while (fileIndex < archivedFiles.Count)
            {
                (string name, byte[] payload) = archivedFiles[fileIndex];
                int remainingInFile = payload.Length - offsetInFile;
                int headerLen = FixedFileHeaderFieldsSize + Encoding.ASCII.GetByteCount(name) + extTimeSize;

                int take = Math.Min(budget - headerLen, remainingInFile);
                if (take <= 0)
                {
                    break; // no room left in this volume for (a piece of) the current file
                }

                byte[] pieceData = new byte[take];
                Array.Copy(payload, offsetInFile, pieceData, 0, take);

                // Piece-local CRC, not the whole file's: matches rar semantics closely enough for
                // parsing purposes, and assembly copies headers verbatim, so no consumer recomputes
                // or validates this value against anything else.
                uint pieceCrc = Crc32Algorithm.Compute(pieceData);

                bool splitAfter = take < remainingInFile;
                RARFileFlags pieceFlags = extTimeFlag;
                if (continuingFile)
                {
                    pieceFlags |= RARFileFlags.SplitBefore;
                }

                if (splitAfter)
                {
                    pieceFlags |= RARFileFlags.SplitAfter;
                }

                pieces.Add(new FilePiece(name, pieceData, pieceCrc, pieceFlags, mtimeRemainder));

                budget -= headerLen + take;
                offsetInFile += take;

                if (!splitAfter)
                {
                    fileIndex++;
                    offsetInFile = 0;
                    continuingFile = false;
                    continue; // a fresh file may still fit in this same volume's remaining budget
                }

                // This piece consumed the volume's entire remaining budget by construction (take
                // was capped at budget - headerLen), so nothing else fits in this volume.
                continuingFile = true;
                break;
            }

            if (pieces.Count == 0)
            {
                throw new InvalidOperationException(
                    $"volumeSize {volumeSize} is too small to fit even one byte of " +
                    $"'{archivedFiles[fileIndex].Name}' (hasExtTime={hasExtTime}); increase volumeSize.");
            }

            RARArchiveFlags archiveFlags = RARArchiveFlags.Volume;
            if (volumes.Count == 0)
            {
                archiveFlags |= RARArchiveFlags.FirstVolume;
            }

            volumes.Add(new VolumePlan(archiveFlags, pieces));
        }

        return volumes;
    }

    /// <summary>
    /// Writes one physical old-style volume set — real files with payload bytes — named via <see
    /// cref="RARVolumeNaming.GetNextVolumePath"/> starting from <c>{volumePrefix}.rar</c>, the same
    /// authority <see cref="RARStream"/> itself uses to walk a set.
    /// </summary>
    private static List<string> WriteVolumeSet(
        string setRoot, string? directoryPrefix, string volumePrefix, IReadOnlyList<VolumePlan> plan)
    {
        string setDir = directoryPrefix is null ? setRoot : Path.Combine(setRoot, directoryPrefix);
        Directory.CreateDirectory(setDir);

        List<string> paths = new(plan.Count);
        string? currentPath = Path.Combine(setDir, $"{volumePrefix}.rar");

        foreach (VolumePlan volume in plan)
        {
            if (currentPath is null)
            {
                throw new InvalidOperationException("RARVolumeNaming produced no path for the next volume.");
            }

            using (FileStream fs = new(currentPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new(fs))
            {
                EmitVolume(new RAR4HeaderBuilder(bw), volume, bw.Write);
            }

            paths.Add(currentPath);
            currentPath = RARVolumeNaming.GetNextVolumePath(currentPath, isOldNaming: true);
        }

        return paths;
    }

    /// <summary>
    /// Emits one volume's marker + archive header + file header(s) + end block onto <paramref
    /// name="headerBuilder"/>. When <paramref name="writePayload"/> is given, each piece's payload
    /// bytes are written immediately after its header — a real volume file; when null, only
    /// headers are emitted — the exact shape an SRR RARFile section embeds. Both callers share this
    /// method so the embedded SRR headers are byte-identical to the ones written into the original
    /// volume file, as the guided-assembly feature requires.
    /// </summary>
    private static void EmitVolume(RAR4HeaderBuilder headerBuilder, VolumePlan plan, Action<byte[]>? writePayload)
    {
        headerBuilder.AddMarker();
        headerBuilder.AddArchiveHeader(plan.ArchiveFlags);

        foreach (FilePiece piece in plan.Pieces)
        {
            headerBuilder.AddFileHeader(
                piece.FileName,
                packedSize: (uint)piece.Data.Length,
                unpackedSize: (uint)piece.Data.Length,
                fileCRC: piece.FileCrc,
                method: 0x30, // Store — packed bytes ARE the payload, matching RARStream's raw read
                extraFlags: piece.ExtraFlags,
                mtimeRemainder: piece.MtimeRemainder);

            writePayload?.Invoke(piece.Data);
        }

        headerBuilder.AddEndArchive();
    }

    private static string QualifiedName(string? directoryPrefix, string fileName) =>
        directoryPrefix is null ? fileName : $"{directoryPrefix}/{fileName}";

    /// <summary>One split piece of one archived file, ready for <see cref="RAR4HeaderBuilder.AddFileHeader"/>.</summary>
    private sealed record FilePiece(
        string FileName, byte[] Data, uint FileCrc, RARFileFlags ExtraFlags, byte[]? MtimeRemainder);

    /// <summary>One volume's archive-header flags plus the file pieces to emit inside it.</summary>
    private sealed record VolumePlan(RARArchiveFlags ArchiveFlags, IReadOnlyList<FilePiece> Pieces);
}
