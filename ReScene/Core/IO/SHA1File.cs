using System.Text;

namespace ReScene.Core.IO;

/// <summary>
/// Reads, parses, and writes SHA-1 hash files containing hash-filename pairs.
/// </summary>
public class SHA1File
{
    /// <summary>
    /// Gets or sets the file info for the SHA-1 file on disk.
    /// </summary>
    public FileInfo? FileInfo { get; set; }

    /// <summary>
    /// Gets or sets the parsed SHA-1 entries.
    /// </summary>
    public List<SHA1FileEntry> Entries { get; set; } = [];

    /// <summary>
    /// Initializes a new empty SHA-1 file.
    /// </summary>
    public SHA1File()
    {
    }

    /// <summary>
    /// Initializes a new SHA-1 file associated with the specified file info.
    /// </summary>
    /// <param name="fileInfo">The file info for the SHA-1 file on disk.</param>
    public SHA1File(FileInfo fileInfo)
    {
        FileInfo = fileInfo;
    }

    /// <summary>
    /// Writes the SHA-1 entries to the specified file path.
    /// </summary>
    /// <param name="filePath">The output file path.</param>
    public void WriteFile(string filePath)
    {
        using FileStream fs = File.OpenWrite(filePath);
        foreach (SHA1FileEntry sha1FileEntry in Entries.OrderBy(s => s.FileName))
        {
            string line = string.Format("{0} *{1}{2}", sha1FileEntry.SHA1, sha1FileEntry.FileName, Environment.NewLine);
            byte[] buffer = Encoding.UTF8.GetBytes(line);
            fs.Write(buffer, 0, buffer.Length);
        }
    }

    /// <summary>
    /// Reads and parses a SHA-1 hash file from disk.
    /// </summary>
    /// <param name="filePath">The path to the SHA-1 file.</param>
    /// <returns>A parsed <see cref="SHA1File"/> instance.</returns>
    public static SHA1File ReadFile(string filePath)
    {
        FileInfo fileInfo = new(filePath);
        SHA1File sha1File = new()
        {
            FileInfo = fileInfo
        };

        string[] fileLines = File.ReadAllLines(sha1File.FileInfo.FullName);
        foreach (string fileLine in fileLines)
        {
            if (fileLine.StartsWith(":") || fileLine.StartsWith("#") || fileLine.StartsWith(";"))
            {
                continue;
            }

            string[] items = fileLine.Split(" *", StringSplitOptions.RemoveEmptyEntries);
            if (items.Length < 2)
            {
                throw new InvalidDataException("Invalid SHA1 file format.");
            }

            string sha1 = items[0];
            string fileName = items[1];
            if (sha1.Length != 40)
            {
                throw new InvalidDataException("Invalid SHA1 file format.");
            }

            sha1File.Entries.Add(new SHA1FileEntry(sha1, fileName));
        }

        return sha1File;
    }
}
