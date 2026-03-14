using System.Text;

namespace ReScene.Core.IO;

public class SHA1File
{
    public FileInfo? FileInfo { get; set; }

    public List<SHA1FileEntry> Entries { get; set; } = [];

    public SHA1File()
    {
    }

    public SHA1File(FileInfo fileInfo)
    {
        FileInfo = fileInfo;
    }

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
