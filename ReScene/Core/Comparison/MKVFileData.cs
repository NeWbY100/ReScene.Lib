using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ReScene.SRS;

namespace ReScene.Core.Comparison;

/// <summary>
/// The interpreted value type of an EBML element, used to format leaf values for display.
/// </summary>
public enum EBMLValueType
{
    /// <summary>
    /// A container element whose payload is a sequence of child elements.
    /// </summary>
    Master,

    /// <summary>
    /// A big-endian unsigned integer.
    /// </summary>
    UnsignedInt,

    /// <summary>
    /// A big-endian two's-complement signed integer.
    /// </summary>
    SignedInt,

    /// <summary>
    /// A 4- or 8-byte IEEE 754 floating-point value.
    /// </summary>
    Float,

    /// <summary>
    /// A printable ASCII string.
    /// </summary>
    String,

    /// <summary>
    /// A UTF-8 string.
    /// </summary>
    Utf8,

    /// <summary>
    /// A date, stored as nanoseconds relative to 2001-01-01T00:00:00 UTC.
    /// </summary>
    Date,

    /// <summary>
    /// Raw binary data.
    /// </summary>
    Binary,

    /// <summary>
    /// An element of unknown semantics (treated as binary).
    /// </summary>
    Unknown
}

/// <summary>
/// A single parsed EBML element from an MKV/WebM file, with its position, sizes, and (for leaves)
/// a formatted value.
/// </summary>
public sealed class EBMLElement
{
    /// <summary>
    /// Gets or sets the EBML element ID (marker bit preserved), e.g. <c>0x1A45DFA3</c>.
    /// </summary>
    public ulong ElementId
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the human-readable element name (e.g. "Segment", "TrackNumber").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file offset of the first byte of the element ID.
    /// </summary>
    public long Position
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the number of bytes occupied by the element ID and the size VINT.
    /// </summary>
    public int HeaderSize
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the number of bytes of element data (excluding the header).
    /// </summary>
    public long DataSize
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the total size of the element including its header.
    /// </summary>
    public long TotalSize
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the interpreted value type of the element.
    /// </summary>
    public EBMLValueType ValueType
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the formatted leaf value, or <see langword="null"/> for master elements.
    /// </summary>
    public string? Value
    {
        get; set;
    }

    /// <summary>
    /// Gets the child elements (populated only for master elements).
    /// </summary>
    public IReadOnlyList<EBMLElement> Children => _children;

    internal List<EBMLElement> _children { get; } = [];
}

/// <summary>
/// Parsed representation of an MKV/WebM file as a full EBML element tree, used for comparison.
/// </summary>
public sealed class MKVFileData
{
    /// <summary>
    /// The default maximum number of EBML elements parsed before truncation, to bound malformed or
    /// huge files. Beyond this point a movie-sized MKV is just more clusters and cue points —
    /// parsing (and rendering) them adds load time without adding information.
    /// </summary>
    public const int DefaultMaxElements = 1000;

    /// <summary>
    /// The maximum number of data bytes read into memory to format a single leaf value.
    /// Larger leaves are shown as a size only.
    /// </summary>
    public const long MaxLeafValueBytes = 1L * 1024 * 1024;

    private const int BinaryPreviewBytes = 16;

    private const ulong EbmlIdCluster = 0x1F43B675;
    private const ulong EbmlIdTimestamp = 0xE7;
    private const ulong EbmlIdTrackEntry = 0xAE;

    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets the top-level EBML elements (typically the EBML header and the Segment).
    /// </summary>
    public IReadOnlyList<EBMLElement> Elements => _elements;

    internal List<EBMLElement> _elements { get; } = [];

    /// <summary>
    /// Gets or sets the number of TrackEntry elements found, used for the root label.
    /// </summary>
    public int TrackCount
    {
        get; set;
    }

    /// <summary>
    /// Parses an MKV/WebM file into a bounded EBML element tree. Never throws on a malformed file;
    /// returns whatever could be parsed.
    /// </summary>
    /// <param name="path">
    /// Absolute path to the .mkv/.webm file.
    /// </param>
    /// <param name="maxElements">
    /// The maximum number of elements to parse before appending a truncation marker;
    /// values below 1 are treated as 1.
    /// </param>
    /// <returns>
    /// The parsed <see cref="MKVFileData"/>.
    /// </returns>
    public static MKVFileData Load(string path, int maxElements = DefaultMaxElements)
    {
        var data = new MKVFileData { FilePath = path };
        int elementCount = 0;
        int trackCount = 0;
        maxElements = Math.Max(1, maxElements);

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ParseElements(fs, fs.Length, data._elements, maxElements, ref elementCount, ref trackCount);
        }
        catch (IOException)
        {
            // Return whatever parsed so far.
        }
        catch (UnauthorizedAccessException)
        {
            // Return whatever parsed so far.
        }

        data.TrackCount = trackCount;
        return data;
    }

    /// <summary>
    /// Recursively parses EBML elements from <paramref name="stream"/> between the current position
    /// and <paramref name="parentEnd"/>, appending them to <paramref name="target"/>.
    /// </summary>
    private static void ParseElements(Stream stream, long parentEnd, List<EBMLElement> target,
        int maxElements, ref int elementCount, ref int trackCount)
    {
        while (stream.Position < parentEnd)
        {
            if (elementCount >= maxElements)
            {
                target.Add(new EBMLElement
                {
                    Name = "… (truncated)",
                    Position = stream.Position,
                    ValueType = EBMLValueType.Unknown,
                    Value = $"element cap of {maxElements} reached"
                });
                return;
            }

            long idPos = stream.Position;
            if (!EBMLReader.TryReadId(stream, out ulong id, out int idLen))
            {
                return;
            }

            if (!EBMLReader.TryReadSize(stream, out ulong size, out int sizeLen))
            {
                return;
            }

            int headerSize = idLen + sizeLen;
            long dataPos = stream.Position;

            // Unknown-size (all-ones VINT) elements stream to the parent's end.
            bool unknownSize = IsUnknownSize(size, sizeLen);
            long dataSize = unknownSize ? Math.Max(0, parentEnd - dataPos) : (long)size;

            // Clamp to the parent so malformed sizes cannot escape the bounds.
            if (dataPos + dataSize > parentEnd)
            {
                dataSize = Math.Max(0, parentEnd - dataPos);
            }

            (string name, EBMLValueType type) = EbmlElementRegistry.Lookup(id);

            var element = new EBMLElement
            {
                ElementId = id,
                Name = name,
                Position = idPos,
                HeaderSize = headerSize,
                DataSize = dataSize,
                TotalSize = headerSize + dataSize,
                ValueType = type
            };

            elementCount++;

            if (id == EbmlIdTrackEntry)
            {
                trackCount++;
            }

            long dataEnd = dataPos + dataSize;

            if (type == EBMLValueType.Master)
            {
                if (id == EbmlIdCluster)
                {
                    // Do not recurse into cluster bodies; surface the first Timestamp child as a hint.
                    element.Value = ReadClusterTimestampHint(stream, dataPos, dataEnd);
                }
                else
                {
                    ParseElements(stream, dataEnd, element._children, maxElements, ref elementCount, ref trackCount);
                }
            }
            else
            {
                element.Value = FormatLeafValue(stream, type, dataPos, dataSize);
            }

            target.Add(element);

            // Always continue from the element's declared end regardless of how far parsing read.
            if (stream.Position != dataEnd)
            {
                stream.Position = dataEnd;
            }
        }
    }

    /// <summary>
    /// Reads only the first <c>Timestamp</c> child of a cluster as a hint, without enumerating blocks.
    /// </summary>
    private static string? ReadClusterTimestampHint(Stream stream, long clusterDataStart, long clusterEnd)
    {
        long saved = stream.Position;
        try
        {
            stream.Position = clusterDataStart;
            while (stream.Position < clusterEnd)
            {
                if (!EBMLReader.TryReadId(stream, out ulong childId, out int _))
                {
                    return null;
                }

                if (!EBMLReader.TryReadSize(stream, out ulong childSize, out int childSizeLen))
                {
                    return null;
                }

                long childDataPos = stream.Position;
                bool unknownSize = IsUnknownSize(childSize, childSizeLen);
                long childDataSize = unknownSize ? Math.Max(0, clusterEnd - childDataPos) : (long)childSize;
                if (childDataPos + childDataSize > clusterEnd)
                {
                    childDataSize = Math.Max(0, clusterEnd - childDataPos);
                }

                if (childId == EbmlIdTimestamp)
                {
                    string? ts = FormatLeafValue(stream, EBMLValueType.UnsignedInt, childDataPos, childDataSize);
                    return ts is null ? null : $"Cluster @ Timestamp {ts}";
                }

                stream.Position = childDataPos + childDataSize;
            }
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            stream.Position = saved;
        }

        return null;
    }

    /// <summary>
    /// Reads and formats a leaf element's value according to its <paramref name="type"/>.
    /// </summary>
    private static string? FormatLeafValue(Stream stream, EBMLValueType type, long dataPos, long dataSize)
    {
        if (dataSize <= 0)
        {
            return type switch
            {
                EBMLValueType.UnsignedInt or EBMLValueType.SignedInt => "0",
                EBMLValueType.String or EBMLValueType.Utf8 => string.Empty,
                EBMLValueType.Binary or EBMLValueType.Unknown => "0 bytes",
                _ => string.Empty
            };
        }

        // Never read a huge payload into memory just to format a value.
        if (dataSize > MaxLeafValueBytes)
        {
            return $"{dataSize:N0} bytes";
        }

        byte[] buffer = new byte[dataSize];
        stream.Position = dataPos;
        int read = ReadFully(stream, buffer, (int)dataSize);
        if (read < dataSize)
        {
            Array.Resize(ref buffer, read);
        }

        if (buffer.Length == 0)
        {
            return type is EBMLValueType.Binary or EBMLValueType.Unknown ? "0 bytes" : string.Empty;
        }

        // A truncated read, or a field longer than its type allows, can't be safely interpreted as a
        // number / float / date — fall back to the raw bytes rather than show a misleading value (this
        // also avoids integer overflow when a malformed element claims an over-long numeric field).
        bool complete = read == dataSize;
        return type switch
        {
            EBMLValueType.UnsignedInt when complete && buffer.Length <= 8 => ReadUnsignedInt(buffer).ToString(CultureInfo.InvariantCulture),
            EBMLValueType.SignedInt when complete && buffer.Length <= 8 => ReadSignedInt(buffer).ToString(CultureInfo.InvariantCulture),
            EBMLValueType.Float when complete && buffer.Length is 4 or 8 => FormatFloat(buffer),
            EBMLValueType.Date when complete && buffer.Length <= 8 => FormatDate(buffer),
            EBMLValueType.String or EBMLValueType.Utf8 => FormatText(buffer),
            _ => FormatBinary(buffer)
        };
    }

    private static ulong ReadUnsignedInt(byte[] buffer)
    {
        ulong value = 0;
        foreach (byte b in buffer)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static long ReadSignedInt(byte[] buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        long value = (buffer[0] & 0x80) != 0 ? -1L : 0L;
        foreach (byte b in buffer)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static string FormatFloat(byte[] buffer)
    {
        double value = buffer.Length switch
        {
            4 => BinaryPrimitives.ReadSingleBigEndian(buffer),
            8 => BinaryPrimitives.ReadDoubleBigEndian(buffer),
            _ => double.NaN
        };

        if (double.IsNaN(value) && buffer.Length is not (4 or 8))
        {
            return FormatBinary(buffer);
        }

        return value.ToString("G", CultureInfo.InvariantCulture);
    }

    private static string FormatText(byte[] buffer)
    {
        string text = Encoding.UTF8.GetString(buffer);
        return text.TrimEnd('\0');
    }

    private static string FormatDate(byte[] buffer)
    {
        long ns = ReadSignedInt(buffer);
        try
        {
            var epoch = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime date = epoch.AddTicks(ns / 100);
            return date.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            // An extreme/malformed timestamp can fall outside DateTime's range.
            return $"{ns} ns (out of range)";
        }
    }

    private static string FormatBinary(byte[] buffer)
    {
        int previewLen = Math.Min(BinaryPreviewBytes, buffer.Length);
        string hex = Convert.ToHexString(buffer.AsSpan(0, previewLen));
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{buffer.Length} bytes");
        if (previewLen > 0)
        {
            sb.Append(": ");
            for (int i = 0; i < previewLen; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(hex.AsSpan(i * 2, 2));
            }

            if (buffer.Length > previewLen)
            {
                sb.Append(" …");
            }
        }

        return sb.ToString();
    }

    private static bool IsUnknownSize(ulong size, int sizeLen)
    {
        // All value bits set (after the marker bit was masked off) means "unknown size".
        if (sizeLen is < 1 or > 8)
        {
            return false;
        }

        int valueBits = (sizeLen * 7);
        ulong allOnes = valueBits >= 64 ? ulong.MaxValue : (1UL << valueBits) - 1;
        return size == allOnes;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

/// <summary>
/// Maps known Matroska/EBML element IDs to a display name and interpreted value type.
/// </summary>
internal static class EbmlElementRegistry
{
    private static readonly Dictionary<ulong, (string Name, EBMLValueType Type)> _map = new()
    {
        // EBML header
        [0x1A45DFA3] = ("EBML", EBMLValueType.Master),
        [0x4286] = ("EBMLVersion", EBMLValueType.UnsignedInt),
        [0x42F7] = ("EBMLReadVersion", EBMLValueType.UnsignedInt),
        [0x42F2] = ("EBMLMaxIDLength", EBMLValueType.UnsignedInt),
        [0x42F3] = ("EBMLMaxSizeLength", EBMLValueType.UnsignedInt),
        [0x4282] = ("DocType", EBMLValueType.String),
        [0x4287] = ("DocTypeVersion", EBMLValueType.UnsignedInt),
        [0x4285] = ("DocTypeReadVersion", EBMLValueType.UnsignedInt),

        // Segment
        [0x18538067] = ("Segment", EBMLValueType.Master),

        // SeekHead
        [0x114D9B74] = ("SeekHead", EBMLValueType.Master),
        [0x4DBB] = ("Seek", EBMLValueType.Master),
        [0x53AB] = ("SeekID", EBMLValueType.Binary),
        [0x53AC] = ("SeekPosition", EBMLValueType.UnsignedInt),

        // Info
        [0x1549A966] = ("Info", EBMLValueType.Master),
        [0x2AD7B1] = ("TimestampScale", EBMLValueType.UnsignedInt),
        [0x4489] = ("Duration", EBMLValueType.Float),
        [0x4D80] = ("MuxingApp", EBMLValueType.Utf8),
        [0x5741] = ("WritingApp", EBMLValueType.Utf8),
        [0x73A4] = ("SegmentUUID", EBMLValueType.Binary),
        [0x7BA9] = ("Title", EBMLValueType.Utf8),
        [0x4461] = ("DateUTC", EBMLValueType.Date),

        // Tracks
        [0x1654AE6B] = ("Tracks", EBMLValueType.Master),
        [0xAE] = ("TrackEntry", EBMLValueType.Master),
        [0xD7] = ("TrackNumber", EBMLValueType.UnsignedInt),
        [0x73C5] = ("TrackUID", EBMLValueType.UnsignedInt),
        [0x83] = ("TrackType", EBMLValueType.UnsignedInt),
        [0xB9] = ("FlagEnabled", EBMLValueType.UnsignedInt),
        [0x88] = ("FlagDefault", EBMLValueType.UnsignedInt),
        [0x55AA] = ("FlagForced", EBMLValueType.UnsignedInt),
        [0x86] = ("CodecID", EBMLValueType.String),
        [0x258688] = ("CodecName", EBMLValueType.Utf8),
        [0x22B59C] = ("Language", EBMLValueType.String),
        [0x22B59D] = ("LanguageBCP47", EBMLValueType.String),
        [0x23E383] = ("DefaultDuration", EBMLValueType.UnsignedInt),
        [0x536E] = ("Name", EBMLValueType.Utf8),
        [0x63A2] = ("CodecPrivate", EBMLValueType.Binary),

        // Video
        [0xE0] = ("Video", EBMLValueType.Master),
        [0xB0] = ("PixelWidth", EBMLValueType.UnsignedInt),
        [0xBA] = ("PixelHeight", EBMLValueType.UnsignedInt),
        [0x54B0] = ("DisplayWidth", EBMLValueType.UnsignedInt),
        [0x54BA] = ("DisplayHeight", EBMLValueType.UnsignedInt),

        // Audio
        [0xE1] = ("Audio", EBMLValueType.Master),
        [0xB5] = ("SamplingFrequency", EBMLValueType.Float),
        [0x9F] = ("Channels", EBMLValueType.UnsignedInt),
        [0x6264] = ("BitDepth", EBMLValueType.UnsignedInt),

        // Content encodings
        [0x6D80] = ("ContentEncodings", EBMLValueType.Master),
        [0x6240] = ("ContentEncoding", EBMLValueType.Master),
        [0x5034] = ("ContentCompression", EBMLValueType.Master),
        [0x4254] = ("ContentCompAlgo", EBMLValueType.UnsignedInt),
        [0x4255] = ("ContentCompSettings", EBMLValueType.Binary),

        // Cluster
        [0x1F43B675] = ("Cluster", EBMLValueType.Master),
        [0xE7] = ("Timestamp", EBMLValueType.UnsignedInt),
        [0xA3] = ("SimpleBlock", EBMLValueType.Binary),
        [0xA0] = ("BlockGroup", EBMLValueType.Master),
        [0xA1] = ("Block", EBMLValueType.Binary),

        // Other top-level sections
        [0x1C53BB6B] = ("Cues", EBMLValueType.Master),

        // Chapters
        [0x1043A770] = ("Chapters", EBMLValueType.Master),
        [0x45B9] = ("EditionEntry", EBMLValueType.Master),
        [0x45BC] = ("EditionUID", EBMLValueType.UnsignedInt),
        [0x45BD] = ("EditionFlagHidden", EBMLValueType.UnsignedInt),
        [0x45DB] = ("EditionFlagDefault", EBMLValueType.UnsignedInt),
        [0x45DD] = ("EditionFlagOrdered", EBMLValueType.UnsignedInt),
        [0xB6] = ("ChapterAtom", EBMLValueType.Master),
        [0x73C4] = ("ChapterUID", EBMLValueType.UnsignedInt),
        [0x91] = ("ChapterTimeStart", EBMLValueType.UnsignedInt),
        [0x92] = ("ChapterTimeEnd", EBMLValueType.UnsignedInt),
        [0x98] = ("ChapterFlagHidden", EBMLValueType.UnsignedInt),
        [0x4598] = ("ChapterFlagEnabled", EBMLValueType.UnsignedInt),
        [0x80] = ("ChapterDisplay", EBMLValueType.Master),
        [0x85] = ("ChapString", EBMLValueType.Utf8),
        [0x437C] = ("ChapLanguage", EBMLValueType.String),
        [0x437E] = ("ChapCountry", EBMLValueType.String),

        // Tags
        [0x1254C367] = ("Tags", EBMLValueType.Master),
        [0x7373] = ("Tag", EBMLValueType.Master),
        [0x63C0] = ("Targets", EBMLValueType.Master),
        [0x68CA] = ("TargetTypeValue", EBMLValueType.UnsignedInt),
        [0x63CA] = ("TargetType", EBMLValueType.String),
        [0x67C8] = ("SimpleTag", EBMLValueType.Master),
        [0x45A3] = ("TagName", EBMLValueType.Utf8),
        [0x4487] = ("TagString", EBMLValueType.Utf8),
        [0x447A] = ("TagLanguage", EBMLValueType.String),

        // Attachments
        [0x1941A469] = ("Attachments", EBMLValueType.Master),
        [0x61A7] = ("AttachedFile", EBMLValueType.Master),
        [0x466E] = ("FileName", EBMLValueType.Utf8),
        [0x4660] = ("FileMimeType", EBMLValueType.String),
        [0x465C] = ("FileData", EBMLValueType.Binary),
        [0x46AE] = ("FileUID", EBMLValueType.UnsignedInt),

        // Misc
        [0xEC] = ("Void", EBMLValueType.Binary),
        [0xBF] = ("CRC-32", EBMLValueType.Binary),
    };

    /// <summary>
    /// Looks up the display name and value type for an EBML element ID. Unknown IDs return a
    /// generated name and the <see cref="EBMLValueType.Binary"/> type.
    /// </summary>
    /// <param name="id">
    /// The EBML element ID (with marker bit preserved).
    /// </param>
    /// <returns>
    /// The element's name and interpreted value type.
    /// </returns>
    public static (string Name, EBMLValueType Type) Lookup(ulong id)
    {
        if (_map.TryGetValue(id, out (string Name, EBMLValueType Type) entry))
        {
            return entry;
        }

        return ($"Unknown (0x{id:X})", EBMLValueType.Binary);
    }
}
