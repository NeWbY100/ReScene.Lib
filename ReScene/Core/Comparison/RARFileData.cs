using ReScene.RAR;
using ReScene.RAR.Decompression;

namespace ReScene.Core.Comparison;

/// <summary>
/// Holds parsed RAR archive data (headers, file entries, comments) for comparison.
/// </summary>
public class RARFileData
{
    /// <summary>
    /// Gets or sets the path to the RAR file.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the file uses RAR 5.x format.
    /// </summary>
    public bool IsRAR5
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the RAR 4.x archive header, if present.
    /// </summary>
    public RARArchiveHeader? ArchiveHeader
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the RAR 5.x archive info, if present.
    /// </summary>
    public RAR5ArchiveInfo? RAR5ArchiveInfo
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the RAR 4.x file headers found in the archive.
    /// </summary>
    public List<RARFileHeader> FileHeaders { get; set; } = [];

    /// <summary>
    /// Gets or sets the RAR 5.x file info entries found in the archive.
    /// </summary>
    public List<RAR5FileInfo> RAR5FileInfos { get; set; } = [];

    /// <summary>
    /// Gets or sets the archive comment text, if present.
    /// </summary>
    public string? Comment
    {
        get; set;
    }

    /// <summary>
    /// Loads and parses a RAR file, returning its header and file entry data.
    /// </summary>
    /// <param name="filePath">The path to the RAR file.</param>
    /// <returns>A populated <see cref="RARFileData"/> instance.</returns>
    public static RARFileData Load(string filePath)
    {
        var data = new RARFileData { FilePath = filePath };

        using FileStream fs = File.OpenRead(filePath);
        using var reader = new BinaryReader(fs);

        data.IsRAR5 = RAR5HeaderReader.IsRAR5(fs);
        fs.Position = 0;

        if (data.IsRAR5)
        {
            LoadRAR5Data(fs, data);
        }
        else
        {
            LoadRAR4Data(reader, data);
        }

        return data;
    }

    private static void LoadRAR4Data(BinaryReader reader, RARFileData data)
    {
        var headerReader = new RARHeaderReader(reader);

        while (headerReader.CanReadBaseHeader)
        {
            RARBlockReadResult? block = headerReader.ReadBlock(parseContents: true);
            if (block == null)
            {
                break;
            }

            if (block.ArchiveHeader != null)
            {
                data.ArchiveHeader = block.ArchiveHeader;
            }

            if (block.FileHeader != null)
            {
                data.FileHeaders.Add(block.FileHeader);
            }

            if (block.ServiceBlockInfo != null && block.ServiceBlockInfo.SubType == "CMT")
            {
                var commentData = headerReader.ReadServiceBlockData(block);
                if (commentData != null)
                {
                    data.Comment = block.ServiceBlockInfo.IsStored
                        ? System.Text.Encoding.UTF8.GetString(commentData)
                        : RARDecompressor.DecompressComment(
                            commentData,
                            (int)block.ServiceBlockInfo.UnpackedSize,
                            block.ServiceBlockInfo.CompressionMethod,
                            isRAR5: false);
                }
            }

            headerReader.SkipBlock(block, includeData: block.BlockType != RAR4BlockType.FileHeader);
        }
    }

    private static void LoadRAR5Data(Stream stream, RARFileData data)
    {
        stream.Seek(8, SeekOrigin.Begin);
        var headerReader = new RAR5HeaderReader(stream);

        while (headerReader.CanReadBaseHeader)
        {
            RAR5BlockReadResult? block = headerReader.ReadBlock();
            if (block == null)
            {
                break;
            }

            if (block.ArchiveInfo != null)
            {
                data.RAR5ArchiveInfo = block.ArchiveInfo;
            }

            if (block.FileInfo != null)
            {
                data.RAR5FileInfos.Add(block.FileInfo);
            }

            if (block.ServiceBlockInfo != null && block.ServiceBlockInfo.SubType == "CMT")
            {
                var commentData = headerReader.ReadServiceBlockData(block);
                if (commentData != null)
                {
                    data.Comment = block.ServiceBlockInfo.IsStored
                        ? System.Text.Encoding.UTF8.GetString(commentData).TrimEnd('\0')
                        : RARDecompressor.DecompressComment(
                            commentData,
                            (int)block.ServiceBlockInfo.UnpackedSize,
                            (byte)(block.ServiceBlockInfo.CompressionMethod == 0 ? 0x30 : 0x30 + block.ServiceBlockInfo.CompressionMethod),
                            isRAR5: true);
                }
            }

            headerReader.SkipBlock(block);
        }
    }
}
