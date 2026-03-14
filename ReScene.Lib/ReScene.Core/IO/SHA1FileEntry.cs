namespace ReScene.Core.IO;

public class SHA1FileEntry(string sha1, string fileName)
{
    public string SHA1 { get; set; } = sha1;

    public string FileName { get; set; } = fileName;
}
