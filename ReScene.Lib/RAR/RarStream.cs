using System.Text.RegularExpressions;

namespace RAR;

/// <summary>
/// Represents a logical byte range within a single RAR volume for a packed file.
/// </summary>
/// <param name="ArchivePath">Full path to the RAR volume file.</param>
/// <param name="LogicalStart">Start byte position in the logical file (inclusive).</param>
/// <param name="LogicalEnd">End byte position in the logical file (inclusive).</param>
/// <param name="DataOffset">Byte offset within the physical RAR file where this segment's data begins.</param>
internal record RarVolume(string ArchivePath, long LogicalStart, long LogicalEnd, long DataOffset);

/// <summary>
/// Provides transparent read-only streaming access to a file packed across
/// one or more RAR archive volumes. Supports both RAR4 and RAR5 formats,
/// and both old-style (.rar/.r00/.r01) and new-style (.partNN.rar) volume naming.
/// Only stored (m0) files are guaranteed to return correct content; compressed
/// files will return the raw compressed bytes.
/// </summary>
public partial class RarStream : Stream
{
    private static readonly byte[] Rar5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    private readonly List<RarVolume> _volumes = [];
    private readonly Dictionary<string, FileStream> _openStreams = new(StringComparer.OrdinalIgnoreCase);
    private long _length;
    private long _position;
    private RarVolume? _currentVolume;
    private bool _disposed;

    /// <summary>
    /// The name of the packed file being streamed.
    /// </summary>
    public string? PackedFileName { get; private set; }

    /// <summary>
    /// Opens a packed file from a RAR archive set for streaming.
    /// </summary>
    /// <param name="firstRarPath">Path to the first RAR volume.</param>
    /// <param name="packedFileName">
    /// Name of the file inside the archive to stream. If null, the first file found is used.
    /// </param>
    /// <exception cref="FileNotFoundException">The specified RAR file does not exist.</exception>
    /// <exception cref="ArgumentException">
    /// The specified file is not the first volume, the archive contains no files,
    /// or the requested file was not found in the archive.
    /// </exception>
    public RarStream(string firstRarPath, string? packedFileName = null)
    {
        if (!File.Exists(firstRarPath))
            throw new FileNotFoundException("RAR archive not found.", firstRarPath);

        // Validate this is the first volume
        ValidateFirstVolume(firstRarPath);

        // Scan all volumes and build the logical map
        string? currentPath = firstRarPath;
        bool? isOldNaming = null;

        while (currentPath != null && File.Exists(currentPath))
        {
            isOldNaming = ProcessVolume(currentPath, ref packedFileName, isOldNaming);
            currentPath = GetNextVolumePath(currentPath, isOldNaming.Value);
        }

        PackedFileName = packedFileName;

        if (_volumes.Count == 0)
            throw new ArgumentException("File not found in the archive.", nameof(packedFileName));

        _currentVolume = _volumes[0];
    }

    /// <summary>
    /// Internal constructor for testing with pre-built volumes.
    /// </summary>
    internal RarStream(List<RarVolume> volumes, long length)
    {
        _volumes = volumes;
        _length = length;
        if (_volumes.Count > 0)
            _currentVolume = _volumes[0];
    }

    #region Stream Properties

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _length;
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
            UpdateCurrentVolume();
        }
    }

    #endregion

    #region Stream Methods

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
            throw new ArgumentException("The sum of offset and count is greater than the buffer length.");

        if (_currentVolume == null || count == 0)
            return 0;

        int totalBytesRead = 0;

        while (count > 0 && _currentVolume != null)
        {
            FileStream fs = GetOrOpenStream(_currentVolume.ArchivePath);

            // Seek to the correct position within this volume's data
            long offsetInVolume = _position - _currentVolume.LogicalStart;
            fs.Seek(_currentVolume.DataOffset + offsetInVolume, SeekOrigin.Begin);

            // How many bytes are available in this volume segment?
            long availableInVolume = _currentVolume.LogicalEnd - _position + 1;
            int toRead = (int)Math.Min(count, availableInVolume);

            int bytesRead = fs.Read(buffer, offset, toRead);
            if (bytesRead == 0)
                break;

            totalBytesRead += bytesRead;
            offset += bytesRead;
            count -= bytesRead;
            _position += bytesRead;

            // If we've moved past this volume, update to the next
            if (_position > _currentVolume.LogicalEnd)
                UpdateCurrentVolume();

            // If we've reached the end of the logical file, stop
            if (_position >= _length)
                break;
        }

        return totalBytesRead;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long destination = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentException("Invalid seek origin.", nameof(origin))
        };

        ArgumentOutOfRangeException.ThrowIfNegative(destination, nameof(offset));

        _position = destination;
        UpdateCurrentVolume();
        return _position;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // No-op for read-only stream
    }

    /// <inheritdoc/>
    public override void SetLength(long value) =>
        throw new NotSupportedException("RarStream is read-only.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("RarStream is read-only.");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                foreach (var stream in _openStreams.Values)
                {
                    try { stream.Dispose(); }
                    catch { /* ignore errors during cleanup */ }
                }
                _openStreams.Clear();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    #endregion

    #region Volume Discovery

    /// <summary>
    /// Validates that the given path is the first volume in the set.
    /// </summary>
    private static void ValidateFirstVolume(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool isRar5 = IsRar5(fs);
        fs.Position = isRar5 ? 8 : 7; // skip marker

        if (isRar5)
        {
            var reader = new RAR5HeaderReader(fs);
            while (fs.Position < fs.Length)
            {
                var block = reader.ReadBlock();
                if (block == null) break;

                if (block.BlockType == RAR5BlockType.File)
                {
                    if (block.FileInfo?.IsSplitBefore == true)
                        throw new ArgumentException("You must start with the first volume from a RAR set.");
                    return; // first file found, not split — OK
                }

                reader.SkipBlock(block);
            }
        }
        else
        {
            var reader = new RARHeaderReader(fs);
            while (fs.Position < fs.Length)
            {
                var block = reader.ReadBlock(parseContents: true);
                if (block == null) break;

                if (block.BlockType == RAR4BlockType.FileHeader)
                {
                    if (block.FileHeader?.IsSplitBefore == true)
                        throw new ArgumentException("You must start with the first volume from a RAR set.");
                    return; // first file found, not split — OK
                }

                // Skip past the block
                long target = block.BlockPosition + block.HeaderSize;
                if (block.BlockType != RAR4BlockType.FileHeader)
                    target += block.AddSize;
                fs.Position = Math.Min(target, fs.Length);
            }
        }
    }

    /// <summary>
    /// Processes a single RAR volume, adding volume entries for the target file.
    /// Returns the detected naming style (true = old-style).
    /// </summary>
    private bool ProcessVolume(string volumePath, ref string? packedFileName, bool? previousNamingStyle)
    {
        bool isOldNaming = previousNamingStyle ?? false;

        using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool isRar5 = IsRar5(fs);
        fs.Position = isRar5 ? 8 : 7; // skip marker

        if (isRar5)
        {
            ProcessRar5Volume(fs, volumePath, ref packedFileName);
            // RAR5 always uses new-style naming
            isOldNaming = false;
        }
        else
        {
            isOldNaming = ProcessRar4Volume(fs, volumePath, ref packedFileName, previousNamingStyle);
        }

        return isOldNaming;
    }

    private void ProcessRar5Volume(FileStream fs, string volumePath, ref string? packedFileName)
    {
        var reader = new RAR5HeaderReader(fs);

        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock();
            if (block == null) break;

            if (block.BlockType == RAR5BlockType.File && block.FileInfo != null)
            {
                var fileInfo = block.FileInfo;
                string fileName = NormalizePathSeparator(fileInfo.FileName);

                packedFileName ??= fileName;

                if (string.Equals(fileName, NormalizePathSeparator(packedFileName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    // Data starts after the header
                    long dataOffset = block.BlockPosition + (long)block.HeaderSize;
                    long packedSize = (long)block.DataSize;

                    var volume = new RarVolume(
                        volumePath,
                        _length,
                        _length + packedSize - 1,
                        dataOffset);

                    _volumes.Add(volume);
                    _length += packedSize;
                }
            }

            reader.SkipBlock(block);
        }
    }

    private bool ProcessRar4Volume(FileStream fs, string volumePath,
        ref string? packedFileName, bool? previousNamingStyle)
    {
        bool isOldNaming = previousNamingStyle ?? false;
        var reader = new RARHeaderReader(fs);

        while (fs.Position < fs.Length)
        {
            var block = reader.ReadBlock(parseContents: true);
            if (block == null) break;

            if (block.BlockType == RAR4BlockType.ArchiveHeader && block.ArchiveHeader != null)
            {
                isOldNaming = !block.ArchiveHeader.HasNewVolumeNaming;
            }

            if (block.BlockType == RAR4BlockType.FileHeader && block.FileHeader != null)
            {
                var fileHeader = block.FileHeader;
                string fileName = NormalizePathSeparator(fileHeader.FileName);

                packedFileName ??= fileName;

                if (string.Equals(fileName, NormalizePathSeparator(packedFileName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    long dataOffset = block.BlockPosition + block.HeaderSize;
                    long packedSize = (long)fileHeader.PackedSize;

                    var volume = new RarVolume(
                        volumePath,
                        _length,
                        _length + packedSize - 1,
                        dataOffset);

                    _volumes.Add(volume);
                    _length += packedSize;
                }
            }

            // Skip past the block (header + data)
            long target = block.BlockPosition + block.HeaderSize;
            if (block.BlockType == RAR4BlockType.FileHeader || block.BlockType == RAR4BlockType.Service)
                target += block.AddSize;
            else if ((block.Flags & (ushort)RARFileFlags.LongBlock) != 0)
                target += block.AddSize;
            fs.Position = Math.Min(target, fs.Length);
        }

        return isOldNaming;
    }

    #endregion

    #region Volume Naming

    /// <summary>
    /// Computes the path of the next volume in the set.
    /// </summary>
    private static string? GetNextVolumePath(string currentPath, bool isOldNaming)
    {
        if (isOldNaming)
            return GetNextOldStyleVolume(currentPath);
        else
            return GetNextNewStyleVolume(currentPath);
    }

    /// <summary>
    /// Old-style naming: .rar -> .r00 -> .r01 -> ... -> .r99 -> .s00 -> ...
    /// Also handles: .001 -> .002 -> ...
    /// </summary>
    private static string? GetNextOldStyleVolume(string currentPath)
    {
        string ext = Path.GetExtension(currentPath);

        if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            // First volume .rar -> .r00
            string basePath = currentPath[..^ext.Length];
            char prefix = char.IsUpper(ext[1]) ? 'R' : 'r';
            return basePath + "." + prefix + "00";
        }

        if (ext.Length == 4 && (ext[1] == 'r' || ext[1] == 'R' || ext[1] == 's' || ext[1] == 'S' ||
                                ext[1] == 't' || ext[1] == 'T' || ext[1] == 'u' || ext[1] == 'U' ||
                                ext[1] == 'v' || ext[1] == 'V' || ext[1] == 'w' || ext[1] == 'W' ||
                                ext[1] == 'x' || ext[1] == 'X' || ext[1] == 'y' || ext[1] == 'Y' ||
                                ext[1] == 'z' || ext[1] == 'Z') &&
            char.IsDigit(ext[2]) && char.IsDigit(ext[3]))
        {
            // .r00 -> .r01 -> ... -> .r99 -> .s00 -> ...
            char prefix = ext[1];
            int num = (ext[2] - '0') * 10 + (ext[3] - '0');
            num++;

            if (num > 99)
            {
                // Roll over to next letter: r->s, s->t, ...
                num = 0;
                prefix = char.IsUpper(prefix) ? (char)(prefix + 1) : (char)(prefix + 1);
            }

            string basePath = currentPath[..^ext.Length];
            return basePath + "." + prefix + num.ToString("D2");
        }

        // Handle numeric extensions like .001 -> .002
        if (ext.Length >= 2 && ext[1..].All(char.IsDigit))
        {
            int num = int.Parse(ext[1..]);
            num++;
            string basePath = currentPath[..^ext.Length];
            return basePath + "." + num.ToString($"D{ext.Length - 1}");
        }

        return null;
    }

    /// <summary>
    /// New-style naming: .part1.rar -> .part2.rar, .part01.rar -> .part02.rar, etc.
    /// </summary>
    private static string? GetNextNewStyleVolume(string currentPath)
    {
        // Match patterns like .part1.rar, .part01.rar, .part001.rar
        var match = NewStylePartRegex().Match(currentPath);
        if (match.Success)
        {
            string numStr = match.Groups[1].Value;
            string suffix = match.Groups[2].Value; // e.g. ".rar"

            int num = int.Parse(numStr) + 1;
            string newNumStr = num.ToString($"D{numStr.Length}");
            return currentPath[..match.Groups[1].Index] + newNumStr + suffix;
        }

        return null;
    }

    [GeneratedRegex(@"\.part(\d+)(\.rar)$", RegexOptions.IgnoreCase)]
    private static partial Regex NewStylePartRegex();

    #endregion

    #region Helpers

    /// <summary>
    /// Checks whether the stream starts with the RAR5 signature.
    /// </summary>
    private static bool IsRar5(Stream stream)
    {
        if (stream.Length < 8)
            return false;

        long pos = stream.Position;
        stream.Position = 0;
        Span<byte> marker = stackalloc byte[8];
        stream.ReadExactly(marker);
        stream.Position = pos;

        return marker.SequenceEqual(Rar5Marker);
    }

    /// <summary>
    /// Updates _currentVolume to the volume containing _position.
    /// </summary>
    private void UpdateCurrentVolume()
    {
        // Fast path: still in the same volume
        if (_currentVolume != null &&
            _position >= _currentVolume.LogicalStart &&
            _position <= _currentVolume.LogicalEnd)
        {
            return;
        }

        _currentVolume = null;

        foreach (var vol in _volumes)
        {
            if (_position >= vol.LogicalStart && _position <= vol.LogicalEnd)
            {
                _currentVolume = vol;
                return;
            }
        }
    }

    /// <summary>
    /// Gets or opens a FileStream for the given archive path.
    /// </summary>
    private FileStream GetOrOpenStream(string archivePath)
    {
        if (!_openStreams.TryGetValue(archivePath, out var stream))
        {
            stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _openStreams[archivePath] = stream;
        }
        return stream;
    }

    /// <summary>
    /// Normalizes path separators to backslash (RAR internal format).
    /// </summary>
    private static string NormalizePathSeparator(string path)
        => path.Replace('/', '\\');

    #endregion
}
