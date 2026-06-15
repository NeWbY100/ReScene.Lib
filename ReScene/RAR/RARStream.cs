namespace ReScene.RAR;

/// <summary>
/// Represents a logical byte range within a single RAR volume for a packed file.
/// </summary>
/// <param name="ArchivePath">
/// Full path to the RAR volume file.
/// </param>
/// <param name="LogicalStart">
/// Start byte position in the logical file (inclusive).
/// </param>
/// <param name="LogicalEnd">
/// End byte position in the logical file (inclusive).
/// </param>
/// <param name="DataOffset">
/// Byte offset within the physical RAR file where this segment's data begins.
/// </param>
internal record RARVolume(string ArchivePath, long LogicalStart, long LogicalEnd, long DataOffset);

/// <summary>
/// Provides transparent read-only streaming access to a file packed across
/// one or more RAR archive volumes. Supports both RAR4 and RAR5 formats,
/// and both old-style (.rar/.r00/.r01) and new-style (.partNN.rar) volume naming.
/// Only stored (m0) files are guaranteed to return correct content; compressed
/// files will return the raw compressed bytes.
/// </summary>
internal class RARStream : Stream
{
    private static readonly byte[] _rar5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    private readonly List<RARVolume> _volumes = [];
    private readonly Dictionary<string, FileStream> _openStreams = new(StringComparer.OrdinalIgnoreCase);
    private long _length;
    private long _position;
    private RARVolume? _currentVolume;
    private bool _disposed;

    /// <summary>
    /// The name of the packed file being streamed.
    /// </summary>
    public string? PackedFileName
    {
        get; private set;
    }

    /// <summary>
    /// Opens a packed file from a RAR archive set for streaming.
    /// </summary>
    /// <param name="firstRarPath">
    /// Path to the first RAR volume.
    /// </param>
    /// <param name="packedFileName">
    /// Name of the file inside the archive to stream. If null, the first file found is used.
    /// </param>
    /// <exception cref="FileNotFoundException">The specified RAR file does not exist.</exception>
    /// <exception cref="ArgumentException">
    /// The specified file is not the first volume, the archive contains no files,
    /// or the requested file was not found in the archive.
    /// </exception>
    public RARStream(string firstRarPath, string? packedFileName = null)
    {
        if (!File.Exists(firstRarPath))
        {
            throw new FileNotFoundException("RAR archive not found.", firstRarPath);
        }

        // Validate this is the first volume
        ValidateFirstVolume(firstRarPath);

        // Scan all volumes and build the logical map
        string? currentPath = firstRarPath;
        bool? isOldNaming = null;

        while (currentPath != null && File.Exists(currentPath))
        {
            isOldNaming = ProcessVolume(currentPath, ref packedFileName, isOldNaming);
            currentPath = RARVolumeNaming.GetNextVolumePath(currentPath, isOldNaming.Value);
        }

        PackedFileName = packedFileName;

        if (_volumes.Count == 0)
        {
            throw new ArgumentException("File not found in the archive.", nameof(packedFileName));
        }

        _currentVolume = _volumes[0];
    }

    /// <summary>
    /// Internal constructor for testing with pre-built volumes.
    /// </summary>
    /// <param name="volumes">
    /// The pre-built volume list.
    /// </param>
    /// <param name="length">
    /// The total logical length of the stream.
    /// </param>
    internal RARStream(List<RARVolume> volumes, long length)
    {
        _volumes = volumes;
        _length = length;
        if (_volumes.Count > 0)
        {
            _currentVolume = _volumes[0];
        }
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
        {
            throw new ArgumentException("The sum of offset and count is greater than the buffer length.");
        }

        if (_currentVolume == null || count == 0)
        {
            return 0;
        }

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
            {
                break;
            }

            totalBytesRead += bytesRead;
            offset += bytesRead;
            count -= bytesRead;
            _position += bytesRead;

            // If we've moved past this volume, update to the next
            if (_position > _currentVolume.LogicalEnd)
            {
                UpdateCurrentVolume();
            }

            // If we've reached the end of the logical file, stop
            if (_position >= _length)
            {
                break;
            }
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
        throw new NotSupportedException("RARStream is read-only.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("RARStream is read-only.");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                foreach (FileStream stream in _openStreams.Values)
                {
                    try
                    {
                        stream.Dispose();
                    }
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
                RAR5BlockReadResult? block = reader.ReadBlock();
                if (block == null)
                {
                    break;
                }

                if (block.BlockType == RAR5BlockType.File)
                {
                    if (block.FileInfo?.IsSplitBefore == true)
                    {
                        throw new ArgumentException("You must start with the first volume from a RAR set.");
                    }

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
                RARBlockReadResult? block = reader.ReadBlock(parseContents: true);
                if (block == null)
                {
                    break;
                }

                if (block.BlockType == RAR4BlockType.FileHeader)
                {
                    if (block.FileHeader?.IsSplitBefore == true)
                    {
                        throw new ArgumentException("You must start with the first volume from a RAR set.");
                    }

                    return; // first file found, not split — OK
                }

                // Skip past the block
                long target = block.BlockPosition + block.HeaderSize;
                if (block.BlockType != RAR4BlockType.FileHeader)
                {
                    target += block.AddSize;
                }

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
        using var fs = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool isRar5 = IsRar5(fs);
        fs.Position = isRar5 ? 8 : 7; // skip marker

        if (isRar5)
        {
            ProcessRar5Volume(fs, volumePath, ref packedFileName);
            // RAR5 always uses new-style naming
            return false;
        }

        return ProcessRar4Volume(fs, volumePath, ref packedFileName, previousNamingStyle);
    }

    private void ProcessRar5Volume(FileStream fs, string volumePath, ref string? packedFileName)
    {
        var reader = new RAR5HeaderReader(fs);

        while (fs.Position < fs.Length)
        {
            RAR5BlockReadResult? block = reader.ReadBlock();
            if (block == null)
            {
                break;
            }

            if (block.BlockType == RAR5BlockType.File && block.FileInfo != null)
            {
                RAR5FileInfo fileInfo = block.FileInfo;
                string fileName = NormalizePathSeparator(fileInfo.FileName);

                packedFileName ??= fileName;

                if (string.Equals(fileName, NormalizePathSeparator(packedFileName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    // Data starts after the header
                    long dataOffset = block.BlockPosition + (long)block.HeaderSize;
                    long packedSize = (long)block.DataSize;

                    var volume = new RARVolume(
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
            RARBlockReadResult? block = reader.ReadBlock(parseContents: true);
            if (block == null)
            {
                break;
            }

            if (block.BlockType == RAR4BlockType.ArchiveHeader && block.ArchiveHeader != null)
            {
                isOldNaming = !block.ArchiveHeader.HasNewVolumeNaming;
            }

            if (block.BlockType == RAR4BlockType.FileHeader && block.FileHeader != null)
            {
                RARFileHeader fileHeader = block.FileHeader;
                string fileName = NormalizePathSeparator(fileHeader.FileName);

                packedFileName ??= fileName;

                if (string.Equals(fileName, NormalizePathSeparator(packedFileName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    long dataOffset = block.BlockPosition + block.HeaderSize;
                    long packedSize = (long)fileHeader.PackedSize;

                    var volume = new RARVolume(
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
            if (block.BlockType is RAR4BlockType.FileHeader or RAR4BlockType.Service)
            {
                target += block.AddSize;
            }
            else if ((block.Flags & (ushort)RARFileFlags.LongBlock) != 0)
            {
                target += block.AddSize;
            }

            fs.Position = Math.Min(target, fs.Length);
        }

        return isOldNaming;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Checks whether the stream starts with the RAR5 signature.
    /// </summary>
    private static bool IsRar5(Stream stream)
    {
        if (stream.Length < 8)
        {
            return false;
        }

        long pos = stream.Position;
        stream.Position = 0;
        Span<byte> marker = stackalloc byte[8];
        stream.ReadExactly(marker);
        stream.Position = pos;

        return marker.SequenceEqual(_rar5Marker);
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

        foreach (RARVolume vol in _volumes)
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
        if (!_openStreams.TryGetValue(archivePath, out FileStream? stream))
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
