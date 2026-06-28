using System.Text;

namespace ReScene.Core.IO;

/// <summary>
/// Reads and parses SFV (Simple File Verification) files containing filename-CRC32 pairs.
/// </summary>
public class SFVFile
{
    /// <summary>
    /// Gets or sets the file info for the SFV file on disk.
    /// </summary>
    public FileInfo? FileInfo
    {
        get; set;
    }

    /// <summary>
    /// Gets the parsed SFV entries.
    /// </summary>
    public IList<SFVFileEntry> Entries { get; } = [];

    /// <summary>
    /// Initializes a new empty SFV file.
    /// </summary>
    public SFVFile()
    {
    }

    /// <summary>
    /// Initializes a new SFV file associated with the specified file info.
    /// </summary>
    /// <param name="fileInfo">
    /// The file info for the SFV file on disk.
    /// </param>
    public SFVFile(FileInfo fileInfo)
    {
        FileInfo = fileInfo;
    }

    /// <summary>
    /// Reads and parses an SFV file from disk.
    /// </summary>
    /// <param name="filePath">
    /// The path to the SFV file.
    /// </param>
    /// <returns>
    /// A parsed <see cref="SFVFile"/> instance.
    /// </returns>
    public static SFVFile ReadFile(string filePath)
    {
        var sfvFile = ParseLines(File.ReadAllLines(filePath), tolerant: false);
        sfvFile.FileInfo = new FileInfo(filePath);
        return sfvFile;
    }

    /// <summary>Parses SFV text from raw bytes (decoded as Latin-1, the SFV norm), tolerant or strict.</summary>
    public static SFVFile ParseBytes(byte[] data, bool tolerant)
    {
        string text = Encoding.Latin1.GetString(data);
        return ParseLines(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), tolerant);
    }

    /// <summary>
    /// Parses SFV lines into filename-CRC entries. When <paramref name="tolerant"/> is true,
    /// malformed lines are skipped; otherwise an <see cref="InvalidDataException"/> is thrown
    /// (the legacy <see cref="ReadFile"/> contract).
    /// </summary>
    public static SFVFile ParseLines(IEnumerable<string> lines, bool tolerant)
    {
        SFVFile sfvFile = new();
        foreach (string fileLine in lines)
        {
            if (string.IsNullOrEmpty(fileLine) || fileLine.StartsWith(':') || fileLine.StartsWith('#') || fileLine.StartsWith(';'))
            {
                continue;
            }

            string[] items = fileLine.Split(" ", StringSplitOptions.RemoveEmptyEntries);
            if (items.Length < 2 || items[1].Length != 8)
            {
                if (tolerant)
                {
                    continue;
                }

                throw new InvalidDataException("Invalid SFV file format.");
            }

            sfvFile.Entries.Add(new SFVFileEntry(items[0], items[1].ToLowerInvariant()));
        }

        return sfvFile;
    }
}
