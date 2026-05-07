namespace ReScene.SRR;

/// <summary>
/// Compares RAR volume file paths for correct ordering.
/// Supports both new-style (.part01.rar) and old-style (.rar, .r00, .r01) naming conventions.
/// </summary>
public class RARVolumeNameComparer : IComparer<string>
{
    /// <summary>
    /// Shared singleton instance.
    /// </summary>
    public static RARVolumeNameComparer Instance { get; } = new();

    public int Compare(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        string nameA = Path.GetFileName(a);
        string nameB = Path.GetFileName(b);

        // Handle new-style naming: name.part01.rar, name.part02.rar
        int partNumA = ExtractPartNumber(nameA);
        int partNumB = ExtractPartNumber(nameB);

        if (partNumA >= 0 && partNumB >= 0)
        {
            return partNumA.CompareTo(partNumB);
        }

        // Handle old-style naming: name.rar, name.r00, name.r01, etc.
        string extA = Path.GetExtension(nameA).ToLowerInvariant();
        string extB = Path.GetExtension(nameB).ToLowerInvariant();

        int orderA = GetOldStyleOrder(extA);
        int orderB = GetOldStyleOrder(extB);

        return orderA.CompareTo(orderB);
    }

    private static int ExtractPartNumber(string fileName)
    {
        // Look for .partNN.rar pattern
        string lower = fileName.ToLowerInvariant();
        int partIdx = lower.LastIndexOf(".part", StringComparison.Ordinal);
        if (partIdx < 0)
        {
            return -1;
        }

        int dotRar = lower.IndexOf(".rar", partIdx + 5, StringComparison.Ordinal);
        if (dotRar < 0)
        {
            return -1;
        }

        string numStr = lower[(partIdx + 5)..dotRar];
        return int.TryParse(numStr, out int num) ? num : -1;
    }

    private static int GetOldStyleOrder(string ext)
    {
        // .rar is always first
        if (ext == ".rar")
        {
            return -1;
        }

        // .r00, .r01, ..., .s00, .s01, etc.
        if (ext.Length == 4 && ext[0] == '.' && char.IsLetter(ext[1]))
        {
            int letterOffset = (ext[1] - 'r') * 100;
            if (int.TryParse(ext[2..], out int num))
            {
                return letterOffset + num;
            }
        }

        // .001, .002, etc.
        if (ext.Length == 4 && ext[0] == '.' && int.TryParse(ext[1..], out int numExt))
        {
            return numExt;
        }

        return int.MaxValue;
    }
}
