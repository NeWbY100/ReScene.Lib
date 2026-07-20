namespace ReScene.RAR;

/// <summary>
/// Reads a RAR archive's packed-file blocks to answer the proof-classification predicates that
/// App.Core's <c>ReleaseScanner</c> rule 4 and the independent proof-RAR pass need — a normative
/// port of pyrescene's proof-RAR block walk (pyrescene-rules-excerpt.txt, <c>remove_unwanted_sfvs</c>
/// L365-379 and <c>has_stored_proof_ext</c> L213-221; both walk the same block stream and apply the
/// same last-4-characters image-extension check, just with different aggregation — last-wins vs.
/// any-wins — hence the shared <see cref="ProofRarFacts"/> seam).
/// </summary>
public static class RarProofInspector
{
    // excerpt: has_stored_proof_ext L213-221 / remove_unwanted_sfvs L369-370 (PROOF_IMAGE_EXTS,
    // matched against the packed file name's LAST FOUR characters — so ".jpg"/".png"/".bmp"/".gif"
    // match directly, and ".jpeg" matches via its own last 4 chars "jpeg" without the leading dot.
    // Preserved verbatim, including the quirk that a name shorter than 4 characters just compares
    // its whole (shorter) tail, same as Python's `name[-4:]` slicing.
    private static readonly string[] _imageLast4 = [".jpg", "jpeg", ".png", ".bmp", ".gif"];

    /// <summary>
    /// Opens <paramref name="rarPath"/> and walks its packed-file (RAR4) blocks. RAR5 archives
    /// report <see cref="ProofRarFacts.Readable"/> = <see langword="false"/> — the ported pyrescene
    /// logic has no RAR5 support (excerpt: "No RAR5 support yet" at L375). A corrupt/truncated
    /// header likewise reports <see langword="false"/>, mirroring the excerpt's caught
    /// <c>ValueError</c> path. [DIVERGENCE: hardening] the excerpt catches only <c>ValueError</c>
    /// and lets other I/O errors crash; this port folds every read failure (RAR5, corrupt headers,
    /// file I/O errors) into the same <c>Readable=false</c> outcome so callers get one warning path
    /// instead of an unhandled exception.
    /// </summary>
    public static ProofRarFacts Inspect(string rarPath)
    {
        try
        {
            using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (RARUtils.IsRAR5Marker(fs))
            {
                return new ProofRarFacts(Readable: false, HasPackedBlocks: false, AnyImage: false, LastPackedIsImage: false);
            }

            fs.Position = 0;
            var reader = new RARHeaderReader(fs);
            bool hasPacked = false;
            bool anyImage = false;
            bool lastIsImage = false;

            while (reader.CanReadBaseHeader)
            {
                RARBlockReadResult? block = reader.ReadBlock(parseContents: true);
                if (block is null)
                {
                    break;
                }

                // excerpt: remove_unwanted_sfvs L368 — `block.rawtype == BlockType.RarPackedFile`
                // is any file-header block (the excerpt never filters directory entries), and every
                // occurrence reassigns skip — last block wins, not first.
                if (block.FileHeader is { } fh)
                {
                    hasPacked = true;
                    lastIsImage = IsImageName(fh.FileName);
                    anyImage |= lastIsImage;
                }

                long target = block.BlockPosition + block.HeaderSize;
                if (block.BlockType is RAR4BlockType.FileHeader or RAR4BlockType.Service)
                {
                    target += block.DataSize;
                }
                else if ((block.Flags & (ushort)RARFileFlags.LongBlock) != 0)
                {
                    target += block.AddSize;
                }

                fs.Position = Math.Min(target, fs.Length);
            }

            return new ProofRarFacts(Readable: true, HasPackedBlocks: hasPacked, AnyImage: anyImage, LastPackedIsImage: lastIsImage);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ProofRarFacts(Readable: false, HasPackedBlocks: false, AnyImage: false, LastPackedIsImage: false);
        }
    }

    private static bool IsImageName(string fileName)
    {
        string tail = fileName.Length >= 4 ? fileName[^4..] : fileName;
        return _imageLast4.Contains(tail, StringComparer.OrdinalIgnoreCase);
    }
}
