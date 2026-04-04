using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Streams;
using DiscUtils.Udf;

namespace ReScene.SRS;

/// <summary>
/// Extracts media files from ISO disc images for SRS reconstruction.
/// Supports both ISO 9660 and UDF file systems.
/// </summary>
public static class IsoMediaExtractor
{
    private static readonly HashSet<string> _mediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vob", ".m2ts", ".ts", ".mpg", ".mpeg", ".evo", ".m2v",
        ".avi", ".mkv", ".mp4", ".wmv", ".m4v", ".mov",
        ".flac", ".mp3"
    };

    /// <summary>
    /// Lists all media files found inside an ISO image.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <returns>List of file paths inside the ISO.</returns>
    public static List<string> ListMediaFiles(string isoPath)
    {
        var mediaFiles = new List<string>();

        using var isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using DiscFileSystem? disc = OpenDisc(isoStream);

        if (disc is null)
        {
            return mediaFiles;
        }

        foreach (string file in disc.GetFiles("", "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (_mediaExtensions.Contains(ext))
            {
                // Normalize path separators and remove leading backslash
                string normalized = file.Replace('\\', '/').TrimStart('/');
                mediaFiles.Add(normalized);
            }
        }

        mediaFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return mediaFiles;
    }

    /// <summary>
    /// Extracts a specific file from an ISO image to a destination path.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <param name="innerPath">Path of the file inside the ISO (as returned by ListMediaFiles).</param>
    /// <param name="destPath">Destination path to write the extracted file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Optional progress callback (0-100).</param>
    public static async Task ExtractFileAsync(
        string isoPath,
        string innerPath,
        string destPath,
        Action<int>? progress = null,
        CancellationToken ct = default)
    {
        using var isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using DiscFileSystem? disc = OpenDisc(isoStream);

        if (disc is null)
        {
            throw new InvalidOperationException("Unable to open ISO image. Unsupported format.");
        }

        // DiscUtils uses backslash paths internally
        string discPath = "\\" + innerPath.Replace('/', '\\');

        if (!disc.FileExists(discPath))
        {
            throw new FileNotFoundException($"File not found in ISO: {innerPath}");
        }

        using SparseStream source = disc.OpenFile(discPath, FileMode.Open, FileAccess.Read);
        long totalSize = source.Length;

        string? destDir = Path.GetDirectoryName(destPath);
        if (destDir is not null)
        {
            Directory.CreateDirectory(destDir);
        }

        await Task.Run(() =>
        {
            using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[80 * 1024];
            long copied = 0;
            int lastPercent = -1;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                dest.Write(buffer, 0, read);
                copied += read;

                if (totalSize > 0)
                {
                    int percent = (int)(copied * 100 / totalSize);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Invoke(percent);
                    }
                }
            }
        }, ct);
    }

    /// <summary>
    /// Checks whether the given file path appears to be an ISO image.
    /// </summary>
    /// <param name="filePath">File path to check.</param>
    /// <returns>True if the file extension indicates an ISO image.</returns>
    public static bool IsIsoFile(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return ext.Equals(".iso", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".img", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens an ISO image as a DiscFileSystem, trying UDF first then ISO 9660.
    /// </summary>
    private static DiscFileSystem? OpenDisc(Stream isoStream)
    {
        // Try UDF first (most DVDs use UDF)
        try
        {
            if (UdfReader.Detect(isoStream))
            {
                isoStream.Position = 0;
                return new UdfReader(isoStream);
            }
        }
        catch
        {
            // Fall through to ISO 9660
        }

        // Try ISO 9660
        try
        {
            isoStream.Position = 0;
            return new CDReader(isoStream, true);
        }
        catch
        {
            return null;
        }
    }
}
