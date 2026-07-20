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

    // The single "we could not verify this RAR at all" outcome — RAR5, a corrupt/truncated header,
    // a malformed size field, or a genuine I/O error all collapse to this same instance so callers
    // get one warning path regardless of cause.
    private static readonly ProofRarFacts _unreadable = new(Readable: false, HasPackedBlocks: false, AnyImage: false, LastPackedIsImage: false);

    /// <summary>
    /// Opens <paramref name="rarPath"/> and walks its packed-file (RAR4) blocks. RAR5 archives
    /// report <see cref="ProofRarFacts.Readable"/> = <see langword="false"/> — the ported pyrescene
    /// logic has no RAR5 support (excerpt: "No RAR5 support yet" at L375). A corrupt/truncated
    /// header, or a block whose declared size would not advance the stream (hostile/malformed
    /// 64-bit packed size), likewise reports <see langword="false"/> rather than hanging or seeking
    /// to an invalid position — mirroring the excerpt's caught <c>ValueError</c> path.
    /// [DIVERGENCE: hardening] the excerpt catches only <c>ValueError</c> and lets other failures
    /// crash; this port folds every read failure into the same <c>Readable=false</c> outcome so
    /// callers get one warning path instead of an unhandled exception or an infinite loop.
    /// </summary>
    public static ProofRarFacts Inspect(string rarPath, CancellationToken ct = default)
    {
        try
        {
            using var fs = new FileStream(rarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (RARUtils.IsRAR5Marker(fs))
            {
                return _unreadable;
            }

            fs.Position = 0;
            var reader = new RARHeaderReader(fs);
            bool hasPacked = false;
            bool anyImage = false;
            bool lastIsImage = false;

            while (reader.CanReadBaseHeader)
            {
                ct.ThrowIfCancellationRequested();

                RARBlockReadResult? block = reader.ReadBlock(parseContents: true);
                if (block is null)
                {
                    // CanReadBaseHeader was true (enough bytes existed for a base header) yet
                    // ReadBlock still failed — a malformed/truncated block header, NOT a clean end
                    // of archive (a clean end is CanReadBaseHeader itself going false on the next
                    // loop check). Treat as corrupt rather than reporting a partial success.
                    return _unreadable;
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

                // Forward-progress guard: a hostile/malformed 64-bit packed size can wrap negative
                // when RARBlockReadResult.DataSize casts it to a signed long, pushing `target`
                // behind (or onto) this very block's own start. Without this check that either
                // throws (a negative Stream.Position, uncaught by the catch below since it isn't
                // an IOException) or re-reads the same bytes forever. Never trust a size field that
                // doesn't move the stream strictly past where this block began.
                if (target <= block.BlockPosition)
                {
                    return _unreadable;
                }

                fs.Position = Math.Min(target, fs.Length);
            }

            return new ProofRarFacts(Readable: true, HasPackedBlocks: hasPacked, AnyImage: anyImage, LastPackedIsImage: lastIsImage);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return _unreadable;
        }
    }

    private static bool IsImageName(string fileName)
    {
        string tail = fileName.Length >= 4 ? fileName[^4..] : fileName;
        return _imageLast4.Contains(tail, StringComparer.OrdinalIgnoreCase);
    }
}
