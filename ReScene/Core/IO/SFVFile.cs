namespace ReScene.Core.IO;

/// <summary>
/// Reads and parses SFV (Simple File Verification) files containing filename-CRC32 pairs.
/// </summary>
public class SFVFile
{
    /// <summary>
    /// Gets or sets the file info for the SFV file on disk.
    /// </summary>
    public FileInfo? FileInfo { get; set; }

    /// <summary>
    /// Gets or sets the parsed SFV entries.
    /// </summary>
    public List<SFVFileEntry> Entries { get; set; } = [];

    /// <summary>
    /// Initializes a new empty SFV file.
    /// </summary>
    public SFVFile()
    {
    }

    /// <summary>
    /// Initializes a new SFV file associated with the specified file info.
    /// </summary>
    /// <param name="fileInfo">The file info for the SFV file on disk.</param>
    public SFVFile(FileInfo fileInfo)
    {
        FileInfo = fileInfo;
    }

    /// <summary>
    /// Reads and parses an SFV file from disk.
    /// </summary>
    /// <param name="filePath">The path to the SFV file.</param>
    /// <returns>A parsed <see cref="SFVFile"/> instance.</returns>
    public static SFVFile ReadFile(string filePath)
    {
        FileInfo fileInfo = new(filePath);
        SFVFile sfvFile = new()
        {
            FileInfo = fileInfo
        };

        string[] fileLines = File.ReadAllLines(sfvFile.FileInfo.FullName);
        foreach (string fileLine in fileLines)
        {
            if (string.IsNullOrEmpty(fileLine) || fileLine.StartsWith(":") || fileLine.StartsWith("#") || fileLine.StartsWith(";"))
            {
                continue;
            }

            string[] items = fileLine.Split(" ", StringSplitOptions.RemoveEmptyEntries);
            if (items.Length < 2)
            {
                throw new InvalidDataException("Invalid SFV file format.");
            }

            string fileName = items[0];
            string crc32 = items[1];
            if (crc32.Length != 8)
            {
                throw new InvalidDataException("Invalid SFV file format.");
            }

            sfvFile.Entries.Add(new SFVFileEntry(fileName, crc32.ToLower()));
        }

        return sfvFile;
    }
}
