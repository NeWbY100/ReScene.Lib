namespace ReScene.Core.IO;

public class SFVFileEntry(string fileName, string crc)
{
    public string FileName { get; set; } = fileName;

    public string CRC { get; set; } = crc;
}
