using System.Buffers.Binary;
using System.Text;
using ReScene.Core.Comparison;

namespace ReScene.Tests;

/// <summary>
/// Tests parsing of MKV/WebM files into a full EBML element tree (<see cref="MKVFileData"/>) and the
/// element-tree comparison (<see cref="FileComparer.CompareMKVFiles"/>).
/// </summary>
public class MKVFileDataTests : TempDirTestBase
{
    #region EBML encoding helpers

    /// <summary>
    /// Encodes a master element: its ID, a size VINT, then the concatenated child bytes.
    /// </summary>
    private static byte[] Master(byte[] id, params byte[][] children)
    {
        byte[] body = Concat(children);
        return Concat(id, EncodeSize(body.Length), body);
    }

    /// <summary>
    /// Encodes a leaf element: its ID, a size VINT, then the raw payload.
    /// </summary>
    private static byte[] Leaf(byte[] id, byte[] payload) => Concat(id, EncodeSize(payload.Length), payload);

    private static byte[] Uint(byte[] id, ulong value)
    {
        byte[] bytes = value switch
        {
            <= 0xFF => [(byte)value],
            <= 0xFFFF => [(byte)(value >> 8), (byte)value],
            <= 0xFFFFFF => [(byte)(value >> 16), (byte)(value >> 8), (byte)value],
            _ => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]
        };
        return Leaf(id, bytes);
    }

    private static byte[] Str(byte[] id, string value) => Leaf(id, Encoding.UTF8.GetBytes(value));

    private static byte[] FloatLeaf(byte[] id, double value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
        return Leaf(id, bytes);
    }

    /// <summary>
    /// Encodes an EBML size as a VINT using the smallest 1-4 byte form with the marker bit set.
    /// </summary>
    private static byte[] EncodeSize(long size)
    {
        if (size < 0x7F)
        {
            return [(byte)(0x80 | size)];
        }

        if (size < 0x3FFF)
        {
            return [(byte)(0x40 | (size >> 8)), (byte)size];
        }

        if (size < 0x1FFFFF)
        {
            return [(byte)(0x20 | (size >> 16)), (byte)(size >> 8), (byte)size];
        }

        return [(byte)(0x10 | (size >> 24)), (byte)(size >> 16), (byte)(size >> 8), (byte)size];
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (byte[] p in parts)
        {
            total += p.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }

        return result;
    }

    // Element IDs (marker bit preserved, as stored on disk).
    private static byte[] IdEbml => [0x1A, 0x45, 0xDF, 0xA3];
    private static byte[] IdDocType => [0x42, 0x82];
    private static byte[] IdSegment => [0x18, 0x53, 0x80, 0x67];
    private static byte[] IdInfo => [0x15, 0x49, 0xA9, 0x66];
    private static byte[] IdDateUtc => [0x44, 0x61];
    private static byte[] IdTimestampScale => [0x2A, 0xD7, 0xB1];
    private static byte[] IdMuxingApp => [0x4D, 0x80];
    private static byte[] IdDuration => [0x44, 0x89];
    private static byte[] IdTracks => [0x16, 0x54, 0xAE, 0x6B];
    private static byte[] IdTrackEntry => [0xAE];
    private static byte[] IdTrackNumber => [0xD7];
    private static byte[] IdTrackType => [0x83];
    private static byte[] IdCodecId => [0x86];
    private static byte[] IdCluster => [0x1F, 0x43, 0xB6, 0x75];
    private static byte[] IdClusterTimestamp => [0xE7];
    private static byte[] IdSimpleBlock => [0xA3];

    #endregion

    /// <summary>
    /// Builds a small but structurally complete MKV byte array.
    /// </summary>
    private static byte[] BuildSampleMkv(string muxingApp = "libebml", bool includeTrack = true)
    {
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));

        byte[] info = Master(IdInfo,
            Uint(IdTimestampScale, 1000000),
            Str(IdMuxingApp, muxingApp),
            FloatLeaf(IdDuration, 1234.5));

        var segmentChildren = new List<byte[]> { info };

        if (includeTrack)
        {
            byte[] trackEntry = Master(IdTrackEntry,
                Uint(IdTrackNumber, 1),
                Uint(IdTrackType, 1),
                Str(IdCodecId, "V_MPEGH/ISO/HEVC"));
            byte[] tracks = Master(IdTracks, trackEntry);
            segmentChildren.Add(tracks);
        }

        byte[] simpleBlock = Leaf(IdSimpleBlock, [0x81, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0xCC, 0xDD]);
        byte[] cluster = Master(IdCluster, Uint(IdClusterTimestamp, 0), simpleBlock);
        segmentChildren.Add(cluster);

        byte[] segment = Master(IdSegment, [.. segmentChildren]);

        return Concat(ebml, segment);
    }

    private string WriteMkv(string name, byte[] bytes)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Load_ParsesElementTreeWithNamesAndValues()
    {
        string path = WriteMkv("sample.mkv", BuildSampleMkv());

        MKVFileData data = MKVFileData.Load(path);

        // Top level: EBML header + Segment.
        Assert.Equal(2, data.Elements.Count);
        Assert.Equal("EBML", data.Elements[0].Name);
        Assert.Equal("Segment", data.Elements[1].Name);

        EBMLElement segment = data.Elements[1];
        EBMLElement info = segment.Children.First(c => c.Name == "Info");

        EBMLElement muxingApp = info.Children.First(c => c.Name == "MuxingApp");
        Assert.Equal("libebml", muxingApp.Value);

        EBMLElement timestampScale = info.Children.First(c => c.Name == "TimestampScale");
        Assert.Equal("1000000", timestampScale.Value);

        EBMLElement docType = data.Elements[0].Children.First(c => c.Name == "DocType");
        Assert.Equal("matroska", docType.Value);
    }

    [Fact]
    public void Load_CountsTrackEntries()
    {
        string path = WriteMkv("tracks.mkv", BuildSampleMkv());

        MKVFileData data = MKVFileData.Load(path);

        Assert.Equal(1, data.TrackCount);

        EBMLElement segment = data.Elements[1];
        EBMLElement tracks = segment.Children.First(c => c.Name == "Tracks");
        EBMLElement trackEntry = tracks.Children.First(c => c.Name == "TrackEntry");
        Assert.Equal("1", trackEntry.Children.First(c => c.Name == "TrackNumber").Value);
        Assert.Equal("V_MPEGH/ISO/HEVC", trackEntry.Children.First(c => c.Name == "CodecID").Value);
    }

    [Fact]
    public void Load_DoesNotRecurseIntoClusterChildren()
    {
        string path = WriteMkv("cluster.mkv", BuildSampleMkv());

        MKVFileData data = MKVFileData.Load(path);
        EBMLElement segment = data.Elements[1];
        EBMLElement cluster = segment.Children.First(c => c.Name == "Cluster");

        // Cluster bodies are not enumerated: no SimpleBlock/Timestamp child elements.
        Assert.Empty(cluster.Children);
        // But the first Timestamp is surfaced as a hint value.
        Assert.NotNull(cluster.Value);
        Assert.Contains("Timestamp", cluster.Value!, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_IdenticalFiles_NoFileDifferences()
    {
        byte[] bytes = BuildSampleMkv();
        MKVFileData left = MKVFileData.Load(WriteMkv("left.mkv", bytes));
        MKVFileData right = MKVFileData.Load(WriteMkv("right.mkv", bytes));

        var result = new CompareResult();
        FileComparer.CompareMKVFiles(left, right, result,
            new ByteArrayDataSource(bytes), new ByteArrayDataSource(bytes));

        Assert.Empty(result.FileDifferences);
        // Identical files must not report any difference, or the UI can never say "identical".
        Assert.Empty(result.ArchiveDifferences);
    }

    private sealed class ByteArrayDataSource(byte[] bytes) : ReScene.Hex.IHexDataSource
    {
        public long Length => bytes.Length;

        public int Read(long position, byte[] buffer, int offset, int count)
        {
            int available = (int)Math.Max(0, Math.Min(count, bytes.Length - position));
            Array.Copy(bytes, position, buffer, offset, available);
            return available;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    private static byte[] BuildMkvWithClusterPayload(byte fill, int payloadLength)
    {
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        byte[] info = Master(IdInfo, Str(IdMuxingApp, "libebml"));
        byte[] payload = new byte[payloadLength];
        Array.Fill(payload, fill);
        byte[] cluster = Master(IdCluster, Uint(IdClusterTimestamp, 0), Leaf(IdSimpleBlock, payload));
        byte[] segment = Master(IdSegment, info, cluster);
        return Concat(ebml, segment);
    }

    [Fact]
    public void Compare_ClusterSizeDiffers_ReportsModifiedWithoutSources()
    {
        // Different payload lengths change the cluster's data size — detectable from the
        // parsed trees alone, no byte-level sources needed.
        MKVFileData left = MKVFileData.Load(WriteMkv("left.mkv", BuildMkvWithClusterPayload(0xAA, 64)));
        MKVFileData right = MKVFileData.Load(WriteMkv("right.mkv", BuildMkvWithClusterPayload(0xAA, 96)));

        var result = new CompareResult();
        FileComparer.CompareMKVFiles(left, right, result);

        FileDifference diff = Assert.Single(result.FileDifferences);
        Assert.Equal("/Segment/Cluster", diff.FileName);
        Assert.Equal(DifferenceType.Modified, diff.Type);
        Assert.Equal("Data Size", Assert.Single(diff.PropertyDifferences).PropertyName);
    }

    [Fact]
    public void Compare_ClusterContentDiffers_WithSources_ReportsModified()
    {
        // Same structure and sizes; only the A/V payload bytes inside the cluster differ.
        // This is the typical "original sample vs rebuilt sample" comparison.
        byte[] leftBytes = BuildMkvWithClusterPayload(0xAA, 64);
        byte[] rightBytes = BuildMkvWithClusterPayload(0xBB, 64);
        MKVFileData left = MKVFileData.Load(WriteMkv("left.mkv", leftBytes));
        MKVFileData right = MKVFileData.Load(WriteMkv("right.mkv", rightBytes));

        var result = new CompareResult();
        FileComparer.CompareMKVFiles(left, right, result,
            new ByteArrayDataSource(leftBytes), new ByteArrayDataSource(rightBytes));

        FileDifference diff = Assert.Single(result.FileDifferences);
        Assert.Equal("/Segment/Cluster", diff.FileName);
        Assert.Equal(DifferenceType.Modified, diff.Type);
        Assert.Equal("Data", Assert.Single(diff.PropertyDifferences).PropertyName);
    }

    [Fact]
    public void Compare_ClusterContentDiffers_WithoutSources_NotReported()
    {
        // Without byte-level sources the comparer cannot see payload-only changes;
        // it must stay silent rather than guess.
        MKVFileData left = MKVFileData.Load(WriteMkv("left.mkv", BuildMkvWithClusterPayload(0xAA, 64)));
        MKVFileData right = MKVFileData.Load(WriteMkv("right.mkv", BuildMkvWithClusterPayload(0xBB, 64)));

        var result = new CompareResult();
        FileComparer.CompareMKVFiles(left, right, result);

        Assert.Empty(result.FileDifferences);
    }

    [Fact]
    public void Compare_ChangedMuxingApp_ReportsModifiedAtPath()
    {
        MKVFileData left = MKVFileData.Load(WriteMkv("left.mkv", BuildSampleMkv("libebml")));
        MKVFileData right = MKVFileData.Load(WriteMkv("right.mkv", BuildSampleMkv("mkvmerge")));

        var result = new CompareResult();
        FileComparer.CompareMKVFiles(left, right, result);

        FileDifference diff = Assert.Single(result.FileDifferences);
        Assert.Equal(DifferenceType.Modified, diff.Type);
        Assert.Equal("/Segment/Info/MuxingApp", diff.FileName);

        PropertyDifference prop = Assert.Single(diff.PropertyDifferences);
        Assert.Equal("Value", prop.PropertyName);
        Assert.Equal("libebml", prop.LeftValue);
        Assert.Equal("mkvmerge", prop.RightValue);
    }

    [Fact]
    public void Compare_RemovedTrackEntry_ReportsRemoved()
    {
        // Left has the Tracks/TrackEntry; right does not.
        MKVFileData left = MKVFileData.Load(WriteMkv("left.mkv", BuildSampleMkv(includeTrack: true)));
        MKVFileData right = MKVFileData.Load(WriteMkv("right.mkv", BuildSampleMkv(includeTrack: false)));

        var result = new CompareResult();
        FileComparer.CompareMKVFiles(left, right, result);

        // The entire Tracks subtree is missing on the right → a Removed difference at the Tracks path.
        Assert.Contains(result.FileDifferences,
            d => d.Type == DifferenceType.Removed && d.FileName == "/Segment/Tracks");
    }

    [Fact]
    public void Compare_AddedTrackEntry_ReportsAdded()
    {
        // Left lacks the Tracks subtree; right has it → an Added difference.
        MKVFileData left = MKVFileData.Load(WriteMkv("left.mkv", BuildSampleMkv(includeTrack: false)));
        MKVFileData right = MKVFileData.Load(WriteMkv("right.mkv", BuildSampleMkv(includeTrack: true)));

        var result = new CompareResult();
        FileComparer.CompareMKVFiles(left, right, result);

        Assert.Contains(result.FileDifferences,
            d => d.Type == DifferenceType.Added && d.FileName == "/Segment/Tracks");
    }

    [Fact]
    public void Load_MalformedFile_DoesNotThrow_ReturnsPartialTree()
    {
        // Valid EBML header followed by garbage; Load must not throw.
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        byte[] garbage = [0xFF, 0xFF, 0xFF, 0x12, 0x34, 0x56];
        string path = WriteMkv("bad.mkv", Concat(ebml, garbage));

        MKVFileData data = MKVFileData.Load(path);

        Assert.NotEmpty(data.Elements);
        Assert.Equal("EBML", data.Elements[0].Name);
    }

    [Fact]
    public void Load_UnknownSizeSegment_ParsesChildrenToEndOfFile()
    {
        // Real-world MKVs almost always store the Segment with an unknown size (the 1-byte all-ones
        // form 0xFF, or the 8-byte 0x01 FF…FF form) so the muxer can stream without backtracking.
        // The parser must treat the body as running to EOF and still enumerate the children.
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        byte[] info = Master(IdInfo, Str(IdMuxingApp, "libebml"));
        byte[] cluster = Master(IdCluster, Uint(IdClusterTimestamp, 0));
        // Segment ID + 1-byte unknown-size VINT (0xFF) + body that runs to the end of the file.
        byte[] segment = Concat(IdSegment, [0xFF], info, cluster);
        string path = WriteMkv("unknown.mkv", Concat(ebml, segment));

        MKVFileData data = MKVFileData.Load(path);

        Assert.Equal(2, data.Elements.Count);
        EBMLElement seg = data.Elements[1];
        Assert.Equal("Segment", seg.Name);
        Assert.Contains(seg.Children, c => c.Name == "Info");
        Assert.Contains(seg.Children, c => c.Name == "Cluster");
        Assert.Equal("libebml",
            seg.Children.First(c => c.Name == "Info").Children.First(c => c.Name == "MuxingApp").Value);
    }

    [Fact]
    public void Load_FormatsFloatAndDateValues()
    {
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        // DateUTC is nanoseconds relative to 2001-01-01T00:00:00 UTC; all-zero bytes == the epoch.
        byte[] info = Master(IdInfo, FloatLeaf(IdDuration, 1234.5), Leaf(IdDateUtc, new byte[8]));
        byte[] segment = Master(IdSegment, info);
        string path = WriteMkv("floatdate.mkv", Concat(ebml, segment));

        MKVFileData data = MKVFileData.Load(path);
        EBMLElement info2 = data.Elements[1].Children.First(c => c.Name == "Info");

        Assert.Equal("1234.5", info2.Children.First(c => c.Name == "Duration").Value);
        Assert.Equal("2001-01-01 00:00:00 UTC", info2.Children.First(c => c.Name == "DateUTC").Value);
    }

    [Fact]
    public void Compare_MultipleTrackEntries_UsesOccurrenceIndexPaths()
    {
        static byte[] BuildTwoTracks(string codecId2)
        {
            byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
            byte[] track1 = Master(IdTrackEntry, Uint(IdTrackNumber, 1), Str(IdCodecId, "V_MPEGH/ISO/HEVC"));
            byte[] track2 = Master(IdTrackEntry, Uint(IdTrackNumber, 2), Str(IdCodecId, codecId2));
            byte[] tracks = Master(IdTracks, track1, track2);
            byte[] segment = Master(IdSegment, tracks);
            return Concat(ebml, segment);
        }

        MKVFileData left = MKVFileData.Load(WriteMkv("l.mkv", BuildTwoTracks("A_FLAC")));
        MKVFileData right = MKVFileData.Load(WriteMkv("r.mkv", BuildTwoTracks("A_AAC")));

        var result = new CompareResult();
        FileComparer.CompareMKVFiles(left, right, result);

        // Only the second TrackEntry's CodecID differs → keyed with the [1] occurrence suffix.
        Assert.Contains(result.FileDifferences,
            d => d.Type == DifferenceType.Modified && d.FileName == "/Segment/Tracks/TrackEntry[1]/CodecID");
        // The first TrackEntry (no suffix) is identical → must not be reported.
        Assert.DoesNotContain(result.FileDifferences,
            d => d.FileName == "/Segment/Tracks/TrackEntry/CodecID");
    }

    [Fact]
    public void Load_OversizedNumericField_FallsBackToBinary()
    {
        // A field the registry types as an unsigned int but whose payload exceeds 8 bytes cannot be a
        // number — it must surface as raw bytes rather than overflow into a misleading value.
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        byte[] oversizedScale = Leaf(IdTimestampScale, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        byte[] info = Master(IdInfo, oversizedScale);
        byte[] segment = Master(IdSegment, info);
        string path = WriteMkv("oversize.mkv", Concat(ebml, segment));

        MKVFileData data = MKVFileData.Load(path);
        EBMLElement scale = data.Elements[1].Children
            .First(c => c.Name == "Info").Children
            .First(c => c.Name == "TimestampScale");

        Assert.NotNull(scale.Value);
        Assert.Contains("bytes", scale.Value!, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_FlagLacing_IsNamed()
    {
        // FlagLacing (0x9C) is a standard TrackEntry child; it must be named, not "Unknown (0x9C)".
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        byte[] trackEntry = Master(IdTrackEntry,
            Uint(IdTrackNumber, 1),
            Leaf([0x9C], [0x00]), // FlagLacing = 0 (lacing disabled)
            Str(IdCodecId, "S_TEXT/UTF8"));
        byte[] tracks = Master(IdTracks, trackEntry);
        byte[] segment = Master(IdSegment, tracks);
        string path = WriteMkv("lacing.mkv", Concat(ebml, segment));

        MKVFileData data = MKVFileData.Load(path);
        EBMLElement track = data.Elements[1].Children
            .First(c => c.Name == "Tracks").Children
            .First(c => c.Name == "TrackEntry");

        EBMLElement flagLacing = track.Children.First(c => c.Name == "FlagLacing");
        Assert.Equal("0", flagLacing.Value);
    }

    [Fact]
    public void Load_UnknownElementId_NamedUnknownWithHexId()
    {
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        // 0x4DAB is a valid 2-byte EBML ID that is not in the element registry.
        byte[] unknown = Leaf([0x4D, 0xAB], [0xDE, 0xAD]);
        byte[] segment = Master(IdSegment, unknown);
        string path = WriteMkv("unknownid.mkv", Concat(ebml, segment));

        MKVFileData data = MKVFileData.Load(path);
        EBMLElement element = Assert.Single(data.Elements[1].Children);

        Assert.Equal("Unknown (0x4DAB)", element.Name);
        Assert.Equal(EBMLValueType.Binary, element.ValueType);
        Assert.Equal("2 bytes: DE AD", element.Value);
    }

    [Fact]
    public void Load_ElementCapExceeded_AppendsTruncationMarkerAndStops()
    {
        // A Segment stuffed with more tiny Void leaves (2 bytes each: ID 0xEC + size 0x80) than the
        // cap must stop parsing at the cap and surface an explicit truncation marker instead of looping on.
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        byte[] voids = new byte[(MKVFileData.DefaultMaxElements + 100) * 2];
        for (int i = 0; i < voids.Length; i += 2)
        {
            voids[i] = 0xEC;
            voids[i + 1] = 0x80;
        }

        byte[] segment = Master(IdSegment, voids);
        string path = WriteMkv("cap.mkv", Concat(ebml, segment));

        MKVFileData data = MKVFileData.Load(path);
        EBMLElement seg = data.Elements[1];

        EBMLElement marker = seg.Children[^1];
        Assert.Equal("… (truncated)", marker.Name);
        Assert.Contains(MKVFileData.DefaultMaxElements.ToString(System.Globalization.CultureInfo.InvariantCulture),
            marker.Value, StringComparison.Ordinal);
        // Children = parsed Voids + the marker; never more than the cap allows.
        Assert.True(seg.Children.Count <= MKVFileData.DefaultMaxElements + 1);
    }

    [Fact]
    public void Load_CustomElementCap_IsHonored()
    {
        // The cap is user-configurable; a small explicit value must truncate accordingly.
        byte[] ebml = Master(IdEbml, Str(IdDocType, "matroska"));
        byte[] voids = new byte[50 * 2];
        for (int i = 0; i < voids.Length; i += 2)
        {
            voids[i] = 0xEC;
            voids[i + 1] = 0x80;
        }

        byte[] segment = Master(IdSegment, voids);
        string path = WriteMkv("smallcap.mkv", Concat(ebml, segment));

        MKVFileData data = MKVFileData.Load(path, maxElements: 10);

        EBMLElement seg = data.Elements[^1];
        Assert.Equal("… (truncated)", seg.Children[^1].Name);
        Assert.True(seg.Children.Count <= 11);
    }
}
