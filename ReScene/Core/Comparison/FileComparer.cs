using ReScene.Hex;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.Core.Comparison;

/// <summary>
/// Comparison algorithms for SRR and RAR files.
/// </summary>
public static class FileComparer
{
    /// <summary>
    /// Compares two parsed file data objects (SRR, SRS, or RAR) and returns all differences found.
    /// </summary>
    /// <param name="leftData">
    /// The left (original) parsed file data.
    /// </param>
    /// <param name="rightData">
    /// The right (comparison) parsed file data.
    /// </param>
    /// <param name="leftBlocks">
    /// Optional detailed RAR blocks for the left file.
    /// </param>
    /// <param name="rightBlocks">
    /// Optional detailed RAR blocks for the right file.
    /// </param>
    /// <param name="leftSource">
    /// Optional byte-level data source for the left file, used to compare block payloads.
    /// </param>
    /// <param name="rightSource">
    /// Optional byte-level data source for the right file, used to compare block payloads.
    /// </param>
    /// <returns>
    /// A <see cref="CompareResult"/> containing all detected differences.
    /// </returns>
    public static CompareResult Compare(object? leftData, object? rightData,
        List<RARDetailedBlock>? leftBlocks = null, List<RARDetailedBlock>? rightBlocks = null,
        IHexDataSource? leftSource = null, IHexDataSource? rightSource = null)
    {
        var result = new CompareResult();

        if (leftData is SRRFileData leftSRR && rightData is SRRFileData rightSRR)
        {
            CompareSRRFiles(leftSRR.SRRFile, rightSRR.SRRFile, result);
        }
        else if (leftData is SRSFile leftSRS && rightData is SRSFile rightSRS)
        {
            CompareSRSFiles(leftSRS, rightSRS, result);
        }
        else if (leftData is MKVFileData leftMkv && rightData is MKVFileData rightMkv)
        {
            CompareMKVFiles(leftMkv, rightMkv, result, leftSource, rightSource);
        }
        else if (leftData is RARFileData leftRar && rightData is RARFileData rightRar)
        {
            CompareRARFiles(leftRar, rightRar, result, leftBlocks, rightBlocks, leftSource, rightSource);
        }
        else
        {
            result.ArchiveDifferences.Add(new PropertyDifference
            {
                PropertyName = "File Type",
                LeftValue = GetFileTypeName(leftData),
                RightValue = GetFileTypeName(rightData)
            });
        }

        return result;
    }

    /// <summary>
    /// Returns a display name for the given parsed file data type (e.g., "SRR File", "RAR 4.x").
    /// </summary>
    /// <param name="data">
    /// The parsed file data object.
    /// </param>
    /// <returns>
    /// A human-readable file type name.
    /// </returns>
    public static string GetFileTypeName(object? data) => data switch
    {
        SRRFileData => "SRR File",
        SRSFile => "SRS File",
        MKVFileData => "MKV File",
        RARFileData r => r.IsRAR5 ? "RAR 5.x" : "RAR 4.x",
        _ => "Unknown"
    };

    /// <summary>
    /// Compares two SRR files and populates the result with archive, file, and stored file differences.
    /// </summary>
    /// <param name="left">
    /// The left SRR file.
    /// </param>
    /// <param name="right">
    /// The right SRR file.
    /// </param>
    /// <param name="result">
    /// The result to populate with differences.
    /// </param>
    public static void CompareSRRFiles(SRRFile left, SRRFile right, CompareResult result)
    {
        CompareProperty(result.ArchiveDifferences, "App Name", left.HeaderBlock?.AppName, right.HeaderBlock?.AppName);
        CompareProperty(result.ArchiveDifferences, "RAR Version", FormatRARVersion(left.RARVersion), FormatRARVersion(right.RARVersion));
        CompareProperty(result.ArchiveDifferences, "Compression Method", GetCompressionMethodName(left.CompressionMethod), GetCompressionMethodName(right.CompressionMethod));
        CompareProperty(result.ArchiveDifferences, "Dictionary Size", FormatDictionarySize(left.DictionarySize), FormatDictionarySize(right.DictionarySize));
        CompareProperty(result.ArchiveDifferences, "Solid Archive", FormatBool(left.IsSolidArchive), FormatBool(right.IsSolidArchive));
        CompareProperty(result.ArchiveDifferences, "Volume Archive", FormatBool(left.IsVolumeArchive), FormatBool(right.IsVolumeArchive));
        CompareProperty(result.ArchiveDifferences, "Recovery Record", FormatBool(left.HasRecoveryRecord), FormatBool(right.HasRecoveryRecord));
        CompareProperty(result.ArchiveDifferences, "Encrypted Headers", FormatBool(left.HasEncryptedHeaders), FormatBool(right.HasEncryptedHeaders));
        CompareProperty(result.ArchiveDifferences, "Has Comment", FormatBool(!string.IsNullOrEmpty(left.ArchiveComment)), FormatBool(!string.IsNullOrEmpty(right.ArchiveComment)));
        CompareProperty(result.ArchiveDifferences, "RAR Volumes Count", left.RARFiles.Count.ToString(), right.RARFiles.Count.ToString());
        CompareProperty(result.ArchiveDifferences, "Stored Files Count", left.StoredFiles.Count.ToString(), right.StoredFiles.Count.ToString());
        CompareProperty(result.ArchiveDifferences, "Archived Files Count", left.ArchivedFiles.Count.ToString(), right.ArchivedFiles.Count.ToString());
        CompareProperty(result.ArchiveDifferences, "Header CRC Errors", left.HeaderCRCMismatches.ToString(), right.HeaderCRCMismatches.ToString());

        // Compare archived files
        var leftFiles = new HashSet<string>(left.ArchivedFiles, StringComparer.OrdinalIgnoreCase);
        var rightFiles = new HashSet<string>(right.ArchivedFiles, StringComparer.OrdinalIgnoreCase);

        foreach (string? file in leftFiles.Union(rightFiles).OrderBy(f => f))
        {
            bool inLeft = leftFiles.Contains(file);
            bool inRight = rightFiles.Contains(file);

            var fileDiff = new FileDifference { FileName = file };

            if (inLeft && !inRight)
            {
                fileDiff.Type = DifferenceType.Removed;
            }
            else if (!inLeft && inRight)
            {
                fileDiff.Type = DifferenceType.Added;
            }
            else
            {
                left.ArchivedFileCrcs.TryGetValue(file, out string? leftCRC);
                right.ArchivedFileCrcs.TryGetValue(file, out string? rightCRC);

                if (!string.Equals(leftCRC, rightCRC, StringComparison.OrdinalIgnoreCase))
                {
                    fileDiff.Type = DifferenceType.Modified;
                    fileDiff.PropertyDifferences.Add(new PropertyDifference
                    {
                        PropertyName = "CRC",
                        LeftValue = leftCRC ?? "N/A",
                        RightValue = rightCRC ?? "N/A"
                    });
                }

                left.ArchivedFileTimestamps.TryGetValue(file, out DateTime leftTime);
                right.ArchivedFileTimestamps.TryGetValue(file, out DateTime rightTime);

                if (leftTime != rightTime)
                {
                    fileDiff.Type = DifferenceType.Modified;
                    fileDiff.PropertyDifferences.Add(new PropertyDifference
                    {
                        PropertyName = "Modified Time",
                        LeftValue = leftTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        RightValue = rightTime.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                }
            }

            if (fileDiff.Type != DifferenceType.None)
            {
                result.FileDifferences.Add(fileDiff);
            }
        }

        // Compare stored files (normalize path separators for cross-platform compatibility)
        var leftStored = left.StoredFiles.Select(s => s.FileName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightStored = right.StoredFiles.Select(s => s.FileName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string? file in leftStored.Union(rightStored).OrderBy(f => f))
        {
            bool inLeft = leftStored.Contains(file);
            bool inRight = rightStored.Contains(file);

            if (inLeft != inRight)
            {
                result.StoredFileDifferences.Add(new FileDifference
                {
                    FileName = file,
                    Type = inLeft ? DifferenceType.Removed : DifferenceType.Added
                });
            }
        }
    }

    /// <summary>
    /// Compares two SRS files and populates the result with file data and track differences.
    /// </summary>
    /// <param name="left">
    /// The left SRS file.
    /// </param>
    /// <param name="right">
    /// The right SRS file.
    /// </param>
    /// <param name="result">
    /// The result to populate with differences.
    /// </param>
    public static void CompareSRSFiles(SRSFile left, SRSFile right, CompareResult result)
    {
        if (left.FileData is { } leftFd && right.FileData is { } rightFd)
        {
            CompareProperty(result.ArchiveDifferences, "App Name", leftFd.AppName, rightFd.AppName);
            CompareProperty(result.ArchiveDifferences, "File Name", leftFd.FileName, rightFd.FileName);
            CompareProperty(result.ArchiveDifferences, "Sample Size",
                $"{leftFd.SampleSize:N0} bytes", $"{rightFd.SampleSize:N0} bytes");
            CompareProperty(result.ArchiveDifferences, "CRC32",
                leftFd.CRC32.ToString("X8"), rightFd.CRC32.ToString("X8"));
            CompareProperty(result.ArchiveDifferences, "Flags",
                $"0x{leftFd.Flags:X4}", $"0x{rightFd.Flags:X4}");
        }

        // Compare tracks
        int trackCount = Math.Max(left.Tracks.Count, right.Tracks.Count);
        for (int i = 0; i < trackCount; i++)
        {
            SRSTrackDataBlock? lt = i < left.Tracks.Count ? left.Tracks[i] : null;
            SRSTrackDataBlock? rt = i < right.Tracks.Count ? right.Tracks[i] : null;
            string trackName = $"Track {lt?.TrackNumber ?? rt?.TrackNumber ?? (uint)i}";

            if (lt is null || rt is null)
            {
                result.FileDifferences.Add(new FileDifference
                {
                    FileName = trackName,
                    Type = lt is null ? DifferenceType.Added : DifferenceType.Removed
                });
                continue;
            }

            var fileDiff = new FileDifference { FileName = trackName };

            CompareProperty(fileDiff.PropertyDifferences, "Data Length",
                $"{lt.DataLength:N0}", $"{rt.DataLength:N0}");
            CompareProperty(fileDiff.PropertyDifferences, "Match Offset",
                $"0x{lt.MatchOffset:X}", $"0x{rt.MatchOffset:X}");
            CompareProperty(fileDiff.PropertyDifferences, "Signature Size",
                lt.SignatureSize.ToString(), rt.SignatureSize.ToString());
            CompareProperty(fileDiff.PropertyDifferences, "Signature",
                Convert.ToHexString(lt.Signature), Convert.ToHexString(rt.Signature));
            CompareProperty(fileDiff.PropertyDifferences, "Flags",
                $"0x{lt.Flags:X4}", $"0x{rt.Flags:X4}");
            CompareProperty(fileDiff.PropertyDifferences, "Block Size",
                $"{lt.BlockSize:N0}", $"{rt.BlockSize:N0}");

            if (fileDiff.PropertyDifferences.Count > 0)
            {
                fileDiff.Type = DifferenceType.Modified;
                result.FileDifferences.Add(fileDiff);
            }
        }
    }

    /// <summary>
    /// Compares two MKV files element-by-element across their full EBML trees, populating the result
    /// with per-element differences keyed by element path.
    /// </summary>
    /// <param name="left">
    /// The left MKV file data.
    /// </param>
    /// <param name="right">
    /// The right MKV file data.
    /// </param>
    /// <param name="result">
    /// The result to populate with differences.
    /// </param>
    /// <param name="leftSource">
    /// Optional byte-level data source for the left file, used to detect content changes that the
    /// formatted element values cannot show (cluster payloads, truncated binary previews).
    /// </param>
    /// <param name="rightSource">
    /// Optional byte-level data source for the right file.
    /// </param>
    public static void CompareMKVFiles(MKVFileData left, MKVFileData right, CompareResult result,
        IHexDataSource? leftSource = null, IHexDataSource? rightSource = null) =>
        CompareEBMLChildren("", left.Elements, right.Elements, result, leftSource, rightSource);

    /// <summary>
    /// Pairs and compares two lists of sibling EBML elements under the same parent path. Pairing is by
    /// (Name, occurrence index among same-named siblings); recursion descends into master elements.
    /// </summary>
    private static void CompareEBMLChildren(string parentPath, List<EBMLElement> left, List<EBMLElement> right,
        CompareResult result, IHexDataSource? leftSource, IHexDataSource? rightSource)
    {
        Dictionary<string, List<EBMLElement>> leftByName = GroupByName(left);
        Dictionary<string, List<EBMLElement>> rightByName = GroupByName(right);

        var allNames = new HashSet<string>(leftByName.Keys, StringComparer.Ordinal);
        allNames.UnionWith(rightByName.Keys);

        foreach (string name in allNames)
        {
            leftByName.TryGetValue(name, out List<EBMLElement>? leftGroup);
            rightByName.TryGetValue(name, out List<EBMLElement>? rightGroup);

            int leftN = leftGroup?.Count ?? 0;
            int rightN = rightGroup?.Count ?? 0;
            int max = Math.Max(leftN, rightN);

            for (int i = 0; i < max; i++)
            {
                EBMLElement? le = i < leftN ? leftGroup![i] : null;
                EBMLElement? re = i < rightN ? rightGroup![i] : null;

                if (le is not null && re is not null)
                {
                    string path = ElementPath(parentPath, le, i);

                    // Pairs without child nodes are compared directly: true leaves, and non-recursed
                    // masters (Clusters), whose Value is only the first-Timestamp hint.
                    bool leafLike = le.Children.Count == 0 && re.Children.Count == 0;

                    if (!leafLike)
                    {
                        CompareEBMLChildren(path, le.Children, re.Children, result, leftSource, rightSource);
                    }
                    else if (!string.Equals(le.Value ?? "", re.Value ?? "", StringComparison.Ordinal))
                    {
                        AddElementDifference(result, path, "Value", le.Value ?? "", re.Value ?? "");
                    }
                    else if (le.DataSize != re.DataSize)
                    {
                        // Formatted values can collide while the payloads differ in length
                        // (trimmed strings, cluster timestamp hints, truncated binary previews).
                        AddElementDifference(result, path, "Data Size",
                            $"{le.DataSize:N0} bytes", $"{re.DataSize:N0} bytes");
                    }
                    else if (leftSource is not null && rightSource is not null && le.DataSize > 0
                        && !BlockDataMatches(leftSource, le.Position + le.HeaderSize,
                                             rightSource, re.Position + re.HeaderSize, le.DataSize))
                    {
                        // Same size and same formatted value, but the raw bytes differ — typical for
                        // cluster A/V payloads and binary fields longer than the hex preview.
                        AddElementDifference(result, path, "Data", "(content differs)", "(content differs)");
                    }
                }
                else if (le is not null)
                {
                    // Present on left, missing on right → Removed.
                    result.FileDifferences.Add(new FileDifference
                    {
                        FileName = ElementPath(parentPath, le, i),
                        Type = DifferenceType.Removed
                    });
                }
                else if (re is not null)
                {
                    // Present on right, missing on left → Added.
                    result.FileDifferences.Add(new FileDifference
                    {
                        FileName = ElementPath(parentPath, re, i),
                        Type = DifferenceType.Added
                    });
                }
            }
        }
    }

    private static void AddElementDifference(CompareResult result, string path, string propertyName,
        string leftValue, string rightValue)
    {
        result.FileDifferences.Add(new FileDifference
        {
            FileName = path,
            Type = DifferenceType.Modified,
            PropertyDifferences =
            [
                new PropertyDifference
                {
                    PropertyName = propertyName,
                    LeftValue = leftValue,
                    RightValue = rightValue
                }
            ]
        });
    }

    private static Dictionary<string, List<EBMLElement>> GroupByName(List<EBMLElement> elements)
    {
        var map = new Dictionary<string, List<EBMLElement>>(StringComparer.Ordinal);
        foreach (EBMLElement e in elements)
        {
            if (!map.TryGetValue(e.Name, out List<EBMLElement>? list))
            {
                list = [];
                map[e.Name] = list;
            }

            list.Add(e);
        }

        return map;
    }

    /// <summary>
    /// Builds the path key for an EBML element: <c>parentPath + "/" + Name</c>, plus a
    /// <c>[index]</c> suffix when the element is not the first occurrence among same-named siblings.
    /// Root elements use <paramref name="parentPath"/> = "". The ViewModel's tree population uses the
    /// identical scheme so tree nodes align with the diff keys.
    /// </summary>
    /// <param name="parentPath">
    /// The path of the parent element (empty for root elements).
    /// </param>
    /// <param name="element">
    /// The element to build a path for.
    /// </param>
    /// <param name="occurrenceIndex">
    /// The element's zero-based index among same-named siblings under the parent.
    /// </param>
    /// <returns>
    /// The element's path key.
    /// </returns>
    public static string ElementPath(string parentPath, EBMLElement element, int occurrenceIndex)
    {
        string suffix = occurrenceIndex > 0 ? $"[{occurrenceIndex}]" : "";
        return $"{parentPath}/{element.Name}{suffix}";
    }

    /// <summary>
    /// Compares two RAR files using detailed block data if available, otherwise compares archive-level properties.
    /// </summary>
    /// <param name="left">
    /// The left RAR file data.
    /// </param>
    /// <param name="right">
    /// The right RAR file data.
    /// </param>
    /// <param name="result">
    /// The result to populate with differences.
    /// </param>
    /// <param name="leftBlocks">
    /// Optional detailed blocks for the left file.
    /// </param>
    /// <param name="rightBlocks">
    /// Optional detailed blocks for the right file.
    /// </param>
    /// <param name="leftSource">
    /// Optional byte-level data source for the left file, used to compare block payloads.
    /// </param>
    /// <param name="rightSource">
    /// Optional byte-level data source for the right file, used to compare block payloads.
    /// </param>
    public static void CompareRARFiles(RARFileData left, RARFileData right, CompareResult result,
        List<RARDetailedBlock>? leftBlocks, List<RARDetailedBlock>? rightBlocks,
        IHexDataSource? leftSource = null, IHexDataSource? rightSource = null)
    {
        if (leftBlocks != null && rightBlocks != null)
        {
            CompareDetailedBlocks(leftBlocks, rightBlocks, result, leftSource, rightSource);
            return;
        }

        CompareProperty(result.ArchiveDifferences, "Format", left.IsRAR5 ? "RAR 5.x" : "RAR 4.x", right.IsRAR5 ? "RAR 5.x" : "RAR 4.x");
    }

    /// <summary>
    /// Compares two lists of detailed RAR blocks field by field, populating the result with differences.
    /// </summary>
    /// <param name="leftBlocks">
    /// The left list of detailed RAR blocks.
    /// </param>
    /// <param name="rightBlocks">
    /// The right list of detailed RAR blocks.
    /// </param>
    /// <param name="result">
    /// The result to populate with differences.
    /// </param>
    /// <param name="leftSource">
    /// Optional byte-level data source for the left file, used to compare block payloads.
    /// </param>
    /// <param name="rightSource">
    /// Optional byte-level data source for the right file, used to compare block payloads.
    /// </param>
    public static void CompareDetailedBlocks(List<RARDetailedBlock> leftBlocks, List<RARDetailedBlock> rightBlocks, CompareResult result,
        IHexDataSource? leftSource = null, IHexDataSource? rightSource = null)
    {
        if (leftBlocks.Count != rightBlocks.Count)
        {
            result.ArchiveDifferences.Add(new PropertyDifference
            {
                PropertyName = "Block Count",
                LeftValue = leftBlocks.Count.ToString(),
                RightValue = rightBlocks.Count.ToString()
            });
        }

        int count = Math.Min(leftBlocks.Count, rightBlocks.Count);
        for (int i = 0; i < count; i++)
        {
            RARDetailedBlock lb = leftBlocks[i];
            RARDetailedBlock rb = rightBlocks[i];

            bool isItemBlock = lb.BlockType.Contains("File", StringComparison.Ordinal) || lb.BlockType.Contains("Service", StringComparison.Ordinal);

            FileDifference? fileDiff = null;
            if (isItemBlock)
            {
                fileDiff = new FileDifference
                {
                    FileName = lb.ItemName ?? rb.ItemName ?? $"Block [{i}]"
                };
            }

            int fieldCount = Math.Max(lb.Fields.Count, rb.Fields.Count);
            for (int f = 0; f < fieldCount; f++)
            {
                RARHeaderField? lf = f < lb.Fields.Count ? lb.Fields[f] : null;
                RARHeaderField? rf = f < rb.Fields.Count ? rb.Fields[f] : null;

                string name = lf?.Name ?? rf?.Name ?? $"Field {f}";

                // Skip Header CRC - it's a consequence of other field changes
                if (name is "Header CRC" or "CRC32")
                {
                    continue;
                }

                string leftVal = lf?.Value ?? "N/A";
                string rightVal = rf?.Value ?? "N/A";

                if (leftVal != rightVal)
                {
                    var propDiff = new PropertyDifference
                    {
                        PropertyName = name,
                        LeftValue = leftVal,
                        RightValue = rightVal
                    };

                    if (fileDiff != null)
                    {
                        fileDiff.Type = DifferenceType.Modified;
                        fileDiff.PropertyDifferences.Add(propDiff);
                    }
                    else
                    {
                        result.ArchiveDifferences.Add(propDiff);
                    }
                }
            }

            // Byte-compare the data payload when sources are available and both blocks have data of equal size.
            // Different sizes are already surfaced via the Pack Size / Data Size header field comparison above.
            if (leftSource is not null && rightSource is not null
                && lb.HasData && rb.HasData
                && lb.DataSize > 0 && lb.DataSize == rb.DataSize)
            {
                if (!BlockDataMatches(leftSource, lb.StartOffset + lb.HeaderSize,
                                      rightSource, rb.StartOffset + rb.HeaderSize, lb.DataSize))
                {
                    var propDiff = new PropertyDifference
                    {
                        PropertyName = "Block Data",
                        LeftValue = $"{lb.DataSize:N0} bytes (different)",
                        RightValue = $"{rb.DataSize:N0} bytes (different)"
                    };

                    if (fileDiff != null)
                    {
                        fileDiff.Type = DifferenceType.Modified;
                        fileDiff.PropertyDifferences.Add(propDiff);
                    }
                    else
                    {
                        result.ArchiveDifferences.Add(propDiff);
                    }
                }
            }

            if (fileDiff != null && fileDiff.Type != DifferenceType.None)
            {
                result.FileDifferences.Add(fileDiff);
            }
        }
    }

    private const int BlockDataCompareBufferSize = 64 * 1024;

    /// <summary>
    /// Returns whether the byte ranges at the given offsets in the two sources are identical.
    /// </summary>
    /// <param name="leftSource">
    /// The left data source.
    /// </param>
    /// <param name="leftOffset">
    /// Start offset in the left source.
    /// </param>
    /// <param name="rightSource">
    /// The right data source.
    /// </param>
    /// <param name="rightOffset">
    /// Start offset in the right source.
    /// </param>
    /// <param name="length">
    /// Number of bytes to compare.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if all bytes match; <see langword="false"/> on first mismatch
    /// or if a source returns fewer bytes than requested.
    /// </returns>
    public static bool BlockDataMatches(IHexDataSource leftSource, long leftOffset,
        IHexDataSource rightSource, long rightOffset, long length)
    {
        if (length <= 0)
        {
            return true;
        }

        if (leftOffset < 0 || rightOffset < 0
            || leftOffset + length > leftSource.Length
            || rightOffset + length > rightSource.Length)
        {
            return false;
        }

        byte[] leftBuf = new byte[BlockDataCompareBufferSize];
        byte[] rightBuf = new byte[BlockDataCompareBufferSize];
        long remaining = length;
        long lPos = leftOffset;
        long rPos = rightOffset;

        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, BlockDataCompareBufferSize);
            int leftRead = leftSource.Read(lPos, leftBuf, 0, chunk);
            int rightRead = rightSource.Read(rPos, rightBuf, 0, chunk);

            if (leftRead != chunk || rightRead != chunk)
            {
                return false;
            }

            if (!leftBuf.AsSpan(0, chunk).SequenceEqual(rightBuf.AsSpan(0, chunk)))
            {
                return false;
            }

            remaining -= chunk;
            lPos += chunk;
            rPos += chunk;
        }

        return true;
    }

    /// <summary>
    /// Returns whether two RAR detailed blocks have any field value or data size differences.
    /// </summary>
    /// <param name="left">
    /// The left detailed block.
    /// </param>
    /// <param name="right">
    /// The right detailed block.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if any field values or data sizes differ.
    /// </returns>
    public static bool HasFieldDifferences(RARDetailedBlock left, RARDetailedBlock right)
    {
        if (left.DataSize != right.DataSize)
        {
            return true;
        }

        int count = Math.Max(left.Fields.Count, right.Fields.Count);
        for (int f = 0; f < count; f++)
        {
            RARHeaderField? lf = f < left.Fields.Count ? left.Fields[f] : null;
            RARHeaderField? rf = f < right.Fields.Count ? right.Fields[f] : null;
            if ((lf?.Value ?? "") != (rf?.Value ?? ""))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether two RAR detailed blocks differ in either header fields or, when data sources
    /// are provided, in their data payload bytes.
    /// </summary>
    /// <param name="left">
    /// The left detailed block.
    /// </param>
    /// <param name="right">
    /// The right detailed block.
    /// </param>
    /// <param name="leftSource">
    /// Optional byte-level data source for the left file.
    /// </param>
    /// <param name="rightSource">
    /// Optional byte-level data source for the right file.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if any header field, data size, or payload byte differs.
    /// </returns>
    public static bool HasBlockDifferences(RARDetailedBlock left, RARDetailedBlock right,
        IHexDataSource? leftSource, IHexDataSource? rightSource)
    {
        if (HasFieldDifferences(left, right))
        {
            return true;
        }

        if (leftSource is not null && rightSource is not null
            && left.HasData && right.HasData
            && left.DataSize > 0 && left.DataSize == right.DataSize)
        {
            if (!BlockDataMatches(leftSource, left.StartOffset + left.HeaderSize,
                                  rightSource, right.StartOffset + right.HeaderSize, left.DataSize))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds a property difference to the list if the left and right values differ.
    /// </summary>
    /// <param name="diffs">
    /// The list to add the difference to.
    /// </param>
    /// <param name="name">
    /// The property name.
    /// </param>
    /// <param name="leftValue">
    /// The left value.
    /// </param>
    /// <param name="rightValue">
    /// The right value.
    /// </param>
    public static void CompareProperty(List<PropertyDifference> diffs, string name, string? leftValue, string? rightValue)
    {
        if (!string.Equals(leftValue ?? "", rightValue ?? "", StringComparison.Ordinal))
        {
            diffs.Add(new PropertyDifference
            {
                PropertyName = name,
                LeftValue = leftValue ?? "N/A",
                RightValue = rightValue ?? "N/A"
            });
        }
    }

    /// <summary>
    /// Formats a RAR version number (e.g., 29) as a display string (e.g., "RAR 2.9").
    /// </summary>
    /// <param name="version">
    /// The RAR version number.
    /// </param>
    /// <returns>
    /// A formatted version string.
    /// </returns>
    public static string FormatRARVersion(int? version) => version switch
    {
        null => "Unknown",
        50 => "RAR 5.0",
        _ => $"RAR {version / 10}.{version % 10}"
    };

    /// <summary>
    /// Returns the display name for a RAR compression method byte (e.g., 0x33 = "Normal").
    /// </summary>
    /// <param name="method">
    /// The compression method value.
    /// </param>
    /// <returns>
    /// A human-readable compression method name.
    /// </returns>
    public static string GetCompressionMethodName(int? method) => method switch
    {
        null => "Unknown",
        0x00 or 0x30 => "Store",
        0x01 or 0x31 => "Fastest",
        0x02 or 0x32 => "Fast",
        0x03 or 0x33 => "Normal",
        0x04 or 0x34 => "Good",
        0x05 or 0x35 => "Best",
        _ => $"Unknown ({method})"
    };

    /// <summary>
    /// Returns the display name for a RAR compression method byte value.
    /// </summary>
    /// <param name="method">
    /// The compression method byte.
    /// </param>
    /// <returns>
    /// A human-readable compression method name.
    /// </returns>
    public static string GetCompressionMethodName(byte method) => GetCompressionMethodName((int?)method);

    /// <summary>
    /// Formats a dictionary size in KB as a display string.
    /// </summary>
    /// <param name="size">
    /// The dictionary size in KB.
    /// </param>
    /// <returns>
    /// A formatted dictionary size string.
    /// </returns>
    public static string FormatDictionarySize(int? size) => size switch
    {
        null => "Unknown",
        _ => $"{size} KB"
    };

    /// <summary>
    /// Formats a nullable boolean as "Yes", "No", or "Unknown".
    /// </summary>
    /// <param name="value">
    /// The boolean value to format.
    /// </param>
    /// <returns>
    /// "Yes", "No", or "Unknown".
    /// </returns>
    public static string FormatBool(bool? value) => value switch
    {
        null => "Unknown",
        true => "Yes",
        false => "No"
    };
}
