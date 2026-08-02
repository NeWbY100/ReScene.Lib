namespace ReScene.SRR;

/// <summary>
/// Single source of truth for turning an SFV's listed entries into ordered RAR-volume chains.
/// Shared by <see cref="SRRWriter"/>'s <c>ResolveVolumesAsync</c> SFV branch and the folder-mode
/// subtitle nested-SRR path (<c>CreatorViewModel.GenerateNestedSubtitleSrrsAsync</c>) so the two
/// can never drift: the VM had reimplemented this grouping with a
/// DIVERGENT parse+resolve — <c>SFVFile.ReadFile</c> (splits every space, so it threw on a RAR
/// name containing spaces) plus a raw <see cref="Path.Combine(string, string)"/> (which left a
/// <c>.\</c>-prefixed continuation lexically distinct from its <c>.rar</c> head, splitting one
/// chain into two duplicate-named SRRs). This helper IS the writer's own logic, so both callers
/// are byte-identical by construction.
/// </summary>
public static class SfvVolumeResolver
{
    /// <summary>
    /// Extracts candidate file names from SFV lines ("filename CRC32", CRC being the trailing
    /// whitespace-delimited token so names may themselves contain spaces). Blank and comment
    /// (';') lines are skipped. Callers apply their own RAR-volume filtering. Moved verbatim from
    /// the former <c>SRRWriter.ParseSfvEntryNames</c> (its two call sites now route here).
    /// </summary>
    public static IEnumerable<string> ParseSfvEntryNames(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';'))
            {
                continue;
            }

            int lastSpace = trimmed.LastIndexOf(' ');
            if (lastSpace <= 0)
            {
                continue;
            }

            yield return trimmed[..lastSpace].Trim();
        }
    }

    /// <summary>
    /// Resolves an SFV's listed entries into RAR-volume chains, byte-identically to
    /// <see cref="SRRWriter"/>'s <c>ResolveVolumesAsync</c> SFV branch (SRRWriter.cs — the
    /// <c>IsSfvPath</c> case): (a) parse names via <see cref="ParseSfvEntryNames"/> (last-space
    /// split, spaces tolerated); (b) resolve each via
    /// <see cref="SrrNameCanonicalizer.ResolveSfvEntry"/> (so <c>.\</c>/<c>./</c> segments collapse
    /// onto the same directory as their siblings); (c) keep only
    /// <see cref="RARVolumeIdentifier.IsRARVolume(string)"/> entries; (d) group by
    /// <see cref="RARVolumeIdentifier.GetArchiveSetKey"/> in first-seen order. Returns the chains in
    /// first-seen order; each inner list holds that chain's resolved volume paths in first-seen
    /// LISTING order (the order <see cref="ParseSfvEntryNames"/> yields them).
    /// <para>
    /// The resolver deliberately does NOT sort within a chain: its single caller
    /// that needs volume order — <see cref="SRRWriter"/>'s <c>ResolveVolumesAsync</c>, which folds
    /// these volumes through its own accumulator and sorts EXACTLY ONCE at SRRWriter.cs:568 — must
    /// remain byte-identical to base. A resolver-side sort followed by the writer's sort is an
    /// unstable double-sort (<c>List.Sort</c> is unstable, and <see cref="RARVolumeNameComparer"/>
    /// gives <c>.rNN</c> and <c>.NNN</c> volumes EQUAL rank): for a chain mixing them,
    /// <c>sort(sort(listing)) != sort(listing)</c> on the tied elements, so the writer would embed a
    /// different volume order than base. Feeding LISTING order into the writer's single sort
    /// reproduces base exactly. Sorting is therefore each caller's own responsibility
    /// (<c>SRRWriter</c> at :568; <c>CreatorViewModel.GenerateNestedSubtitleSrrsAsync</c> re-sorts
    /// per chain for its own volume[0]/naming needs).
    /// </para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ResolveOrderedChains(string sfvDirectory, IEnumerable<string> sfvLines)
    {
        var chainOrder = new List<string>();
        var chains = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string entryName in ParseSfvEntryNames(sfvLines))
        {
            string resolved = SrrNameCanonicalizer.ResolveSfvEntry(sfvDirectory, entryName);
            if (!RARVolumeIdentifier.IsRARVolume(resolved))
            {
                continue;
            }

            string key = RARVolumeIdentifier.GetArchiveSetKey(resolved);
            if (!chains.TryGetValue(key, out List<string>? volumes))
            {
                volumes = [];
                chains[key] = volumes;
                chainOrder.Add(key);
            }

            volumes.Add(resolved);
        }

        // Return each chain in LISTING order (no per-chain sort — see the remark above: the caller
        // sorts exactly once, so sorting here would make the writer's sort a byte-diverging
        // double-sort on `.rNN`/`.NNN` ties).
        var result = new List<IReadOnlyList<string>>(chainOrder.Count);
        foreach (string key in chainOrder)
        {
            result.Add(chains[key]);
        }

        return result;
    }
}
