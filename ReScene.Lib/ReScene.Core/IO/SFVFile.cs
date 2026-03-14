namespace ReScene.Core.IO;

public class SFVFile
{
    public FileInfo? FileInfo { get; set; }

    public List<SFVFileEntry> Entries { get; set; } = [];

    public SFVFile()
    {
    }

    public SFVFile(FileInfo fileInfo)
    {
        FileInfo = fileInfo;
    }

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
