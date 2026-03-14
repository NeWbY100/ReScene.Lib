using RARLib;
using SRRLib;

namespace ReScene.Core.Comparison;

/// <summary>
/// Holds a parsed SRR file along with detailed RAR block data for each embedded volume.
/// </summary>
public class SRRFileData
{
    public SRRFile SrrFile { get; set; } = null!;

    /// <summary>
    /// Detailed RAR blocks per volume, keyed by volume filename.
    /// </summary>
    public Dictionary<string, List<RARDetailedBlock>> VolumeDetailedBlocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SRRFileData Load(string filePath)
    {
        var srrFile = SRRFile.Load(filePath);
        var volumeBlocks = new Dictionary<string, List<RARDetailedBlock>>(StringComparer.OrdinalIgnoreCase);

        if (srrFile.RarFiles.Count > 0)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                foreach (var rarFile in srrFile.RarFiles)
                {
                    try
                    {
                        long embeddedStart = rarFile.BlockPosition + rarFile.HeaderSize;
                        fs.Position = embeddedStart;

                        var detailedBlocks = RARDetailedParser.ParseFromPosition(fs);
                        volumeBlocks[rarFile.FileName] = detailedBlocks;
                    }
                    catch
                    {
                        // Skip volumes with unparseable embedded RAR data
                    }
                }
            }
            catch
            {
                // Skip if file cannot be re-opened
            }
        }

        return new SRRFileData
        {
            SrrFile = srrFile,
            VolumeDetailedBlocks = volumeBlocks
        };
    }
}
