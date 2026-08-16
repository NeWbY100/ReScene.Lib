using ReScene.SRR;

namespace ReScene;

/// <summary>
/// Destination-safety primitives shared by the SRR and SRS writers: the identity check that stops
/// an output path aliasing one of its own inputs, and the staging path that keeps a failed write
/// from touching a pre-existing destination.
/// </summary>
/// <remarks>
/// <para>
/// Both halves exist because of a real defect. Writers used to open the destination directly with
/// <see cref="FileMode.Create"/> and then, from their catch blocks, delete <c>outputPath</c>
/// unconditionally — including when the throw came from argument or existence validation that runs
/// BEFORE the destination is ever opened. Creating an SRR with an empty volume list therefore
/// deleted a pre-existing, entirely unrelated file at that path.
/// </para>
/// <para>
/// The rule this type encodes: NEVER delete a destination this call did not create. Write to a
/// staging file beside the destination, commit by moving it into place as the last fallible step,
/// and on any failure delete only the staging file.
/// </para>
/// </remarks>
internal static class DestinationTransaction
{
    /// <summary>
    /// The comparison key for an output path: its OS final path when it already exists, else its
    /// DIRECTORY's final path with the file name reattached.
    /// </summary>
    /// <remarks>
    /// The two-branch shape is required because <see cref="SrrNameCanonicalizer.GetFinalPath"/>
    /// resolves through symlinks and junctions and therefore demands an existing target, while the
    /// normal case is an output path that does not exist yet. Resolving the directory and
    /// reattaching the name keeps a link in an ANCESTOR from disguising a self-collision.
    /// </remarks>
    public static string ComputeKey(string outputPath) =>
        File.Exists(outputPath)
            ? SrrNameCanonicalizer.GetFinalPath(outputPath)
            : Path.Combine(
                SrrNameCanonicalizer.GetFinalPath(Path.GetDirectoryName(Path.GetFullPath(outputPath))!),
                Path.GetFileName(outputPath));

    /// <summary>
    /// Throws when <paramref name="outputKey"/> resolves to the same file as any of
    /// <paramref name="candidatePaths"/>, so a writer never destroys one of its own inputs.
    /// </summary>
    /// <remarks>
    /// Candidates that cannot be resolved are SKIPPED rather than treated as a match: a candidate
    /// that does not exist cannot be the destination's alias, and this method is also called with
    /// caller-supplied lists whose entries are validated elsewhere.
    /// </remarks>
    public static void RejectIfMatches(string outputKey, IEnumerable<string> candidatePaths, string sourceKind)
    {
        foreach (string candidate in candidatePaths)
        {
            string candidateKey;
            try
            {
                candidateKey = SrrNameCanonicalizer.GetFinalPath(candidate);
            }
            catch (Exception e) when (e is SrrNameException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            if (string.Equals(outputKey, candidateKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Output path is the same as {sourceKind}: {candidate}");
            }
        }
    }

    /// <summary>
    /// Reserves an unused staging path beside <paramref name="outputPath"/> by creating it
    /// exclusively, and returns it. The caller writes there and commits with <see cref="Commit"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="FileMode.CreateNew"/> rather than <see cref="FileMode.Create"/> so an
    /// astronomically unlikely 8-hex suffix collision with a pre-existing file can never silently
    /// truncate it; on that collision the suffix is regenerated a bounded number of times. The
    /// staging file sits in the destination's OWN directory so the commit is a same-volume move.
    /// </remarks>
    public static string ReserveStagingPath(string outputPath)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string candidate = outputPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Created empty purely to reserve the name; the caller reopens it to write.
                }

                return candidate;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // Regenerate and retry — see remarks.
            }
        }

        throw new IOException($"Could not reserve a staging file beside: {outputPath}");
    }

    /// <summary>Moves a completed staging file into place, replacing any existing destination.</summary>
    public static void Commit(string stagingPath, string outputPath) =>
        File.Move(stagingPath, outputPath, overwrite: true);
}
