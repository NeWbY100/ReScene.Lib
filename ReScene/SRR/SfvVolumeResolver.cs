namespace ReScene.SRR;

/// <summary>
/// Single source of truth for turning an SFV's listed entries into ordered RAR-volume chains.
/// Shared by <see cref="SRRWriter"/>'s <c>ResolveVolumesAsync</c> SFV branch and the folder-mode
/// subtitle nested-SRR path (<c>CreatorViewModel.GenerateNestedSubtitleSrrsAsync</c>) so the two
/// can never drift: codex Task 9 fix-3 (G3/G4) found the VM had reimplemented this grouping with a
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
    /// <c>IsSfvPath</c> case plus its final per-chain sort): (a) parse names via
    /// <see cref="ParseSfvEntryNames"/> (last-space split, spaces tolerated); (b) resolve each via
    /// <see cref="SrrNameCanonicalizer.ResolveSfvEntry"/> (so <c>.\</c>/<c>./</c> segments collapse
    /// onto the same directory as their siblings); (c) keep only
    /// <see cref="RARVolumeIdentifier.IsRARVolume(string)"/> entries; (d) group by
    /// <see cref="RARVolumeIdentifier.GetArchiveSetKey"/> in first-seen order; (e) sort each chain
    /// by <see cref="RARVolumeNameComparer"/>. Returns the chains in first-seen order; each inner
    /// list is one chain's resolved volume paths in volume order.
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

        var result = new List<IReadOnlyList<string>>(chainOrder.Count);
        foreach (string key in chainOrder)
        {
            List<string> volumes = chains[key];
            volumes.Sort(RARVolumeNameComparer.Instance);
            result.Add(volumes);
        }

        return result;
    }
}
