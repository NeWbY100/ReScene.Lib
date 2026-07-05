using System.Text;

namespace ReScene.Tests;

/// <summary>
/// Builds synthetic SRR files for unit testing.
/// SRR format: sequence of blocks (SRR Header, StoredFiles, RARFile references + embedded RAR headers).
/// </summary>
internal class SRRTestDataBuilder
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;

    public SRRTestDataBuilder()
    {
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>
    /// Writes an SRR header block (type 0x69).
    /// </summary>
    public SRRTestDataBuilder AddSRRHeader(string? appName = null)
    {
        ushort flags = appName != null ? (ushort)0x0001 : (ushort)0x0000;

        // Calculate header size
        int headerSize = 7; // base header
        int appNameLen = 0;
        byte[]? appNameBytes = null;
        if (appName != null)
        {
            appNameBytes = Encoding.UTF8.GetBytes(appName);
            appNameLen = appNameBytes.Length;
            headerSize += 2 + appNameLen; // 2 bytes name length + name
        }

        _writer.Write((ushort)0x6969); // CRC sentinel
        _writer.Write((byte)0x69);     // SRR Header type
        _writer.Write(flags);
        _writer.Write((ushort)headerSize);

        if (appNameBytes != null)
        {
            _writer.Write((ushort)appNameLen);
            _writer.Write(appNameBytes);
        }

        return this;
    }

    /// <summary>
    /// Writes an SRR stored file block (type 0x6A) with data.
    /// </summary>
    public SRRTestDataBuilder AddStoredFile(string fileName, byte[] fileData)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        ushort headerSize = (ushort)(7 + 4 + 2 + nameBytes.Length); // base + addSize + nameLen + name
        uint addSize = (uint)fileData.Length;

        _writer.Write((ushort)0x6A6A);     // CRC sentinel
        _writer.Write((byte)0x6A);         // StoredFile type
        _writer.Write((ushort)0x0000);     // flags
        _writer.Write(headerSize);
        _writer.Write(addSize);            // data length
        _writer.Write((ushort)nameBytes.Length);
        _writer.Write(nameBytes);
        _writer.Write(fileData);           // file data

        return this;
    }

    /// <summary>
    /// Writes an SRR RAR file reference block (type 0x71) followed by embedded RAR4 headers.
    /// </summary>
    public SRRTestDataBuilder AddRarFileWithHeaders(string rarFileName, Action<RAR4HeaderBuilder> buildHeaders)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(rarFileName);
        ushort headerSize = (ushort)(7 + 2 + nameBytes.Length); // base + nameLen + name

        _writer.Write((ushort)0x7171);     // CRC sentinel
        _writer.Write((byte)0x71);         // RARFile type
        _writer.Write((ushort)0x0000);     // flags
        _writer.Write(headerSize);
        _writer.Write((ushort)nameBytes.Length);
        _writer.Write(nameBytes);

        // Write embedded RAR headers directly after
        var headerBuilder = new RAR4HeaderBuilder(_writer);
        buildHeaders(headerBuilder);

        return this;
    }

    /// <summary>
    /// Writes an SRR OSO hash block (type 0x6B).
    /// </summary>
    public SRRTestDataBuilder AddOSOHash(string fileName, ulong fileSize, byte[] osoHash)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        ushort headerSize = (ushort)(7 + 8 + 8 + 2 + nameBytes.Length); // base + fileSize + hash + nameLen + name

        _writer.Write((ushort)0x6B6B);     // CRC sentinel
        _writer.Write((byte)0x6B);         // OSOHash type
        _writer.Write((ushort)0x0000);     // flags
        _writer.Write(headerSize);
        _writer.Write(fileSize);           // pyrescene order: fileSize first
        _writer.Write(osoHash);            // then hash
        _writer.Write((ushort)nameBytes.Length);
        _writer.Write(nameBytes);

        return this;
    }

    /// <summary>
    /// Writes an SRR RAR padding block (type 0x6C).
    /// </summary>
    public SRRTestDataBuilder AddRarPadding(string rarFileName, uint paddingSize)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(rarFileName);
        ushort headerSize = (ushort)(7 + 4 + 2 + nameBytes.Length); // base + addSize + nameLen + name

        _writer.Write((ushort)0x6C6C);     // CRC sentinel
        _writer.Write((byte)0x6C);         // RARPadding type
        _writer.Write((ushort)0x8000);     // flags with LongBlock
        _writer.Write(headerSize);
        _writer.Write(paddingSize);        // padding size (addSize)
        _writer.Write((ushort)nameBytes.Length);
        _writer.Write(nameBytes);

        // Write actual padding bytes
        _writer.Write(new byte[paddingSize]);

        return this;
    }

    public byte[] Build()
    {
        _writer.Flush();
        return _stream.ToArray();
    }

    public string BuildToFile(string directory, string fileName)
    {
        byte[] data = Build();
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, data);
        return path;
    }
}
