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
    public static CompareResult Compare(object? leftData, object? rightData,
        List<RARDetailedBlock>? leftBlocks = null, List<RARDetailedBlock>? rightBlocks = null)
    {
        var result = new CompareResult();

        if (leftData is SRRFileData leftSrr && rightData is SRRFileData rightSrr)
        {
            CompareSRRFiles(leftSrr.SrrFile, rightSrr.SrrFile, result);
        }
        else if (leftData is SRSFile leftSrs && rightData is SRSFile rightSrs)
        {
            CompareSRSFiles(leftSrs, rightSrs, result);
        }
        else if (leftData is RARFileData leftRar && rightData is RARFileData rightRar)
        {
            CompareRARFiles(leftRar, rightRar, result, leftBlocks, rightBlocks);
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
    public static string GetFileTypeName(object? data) => data switch
    {
        SRRFileData => "SRR File",
        SRSFile => "SRS File",
        RARFileData r => r.IsRAR5 ? "RAR 5.x" : "RAR 4.x",
        _ => "Unknown"
    };

    /// <summary>
    /// Compares two SRR files and populates the result with archive, file, and stored file differences.
    /// </summary>
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
        CompareProperty(result.ArchiveDifferences, "RAR Volumes Count", left.RarFiles.Count.ToString(), right.RarFiles.Count.ToString());
        CompareProperty(result.ArchiveDifferences, "Stored Files Count", left.StoredFiles.Count.ToString(), right.StoredFiles.Count.ToString());
        CompareProperty(result.ArchiveDifferences, "Archived Files Count", left.ArchivedFiles.Count.ToString(), right.ArchivedFiles.Count.ToString());
        CompareProperty(result.ArchiveDifferences, "Header CRC Errors", left.HeaderCrcMismatches.ToString(), right.HeaderCrcMismatches.ToString());

        // Compare archived files
        var leftFiles = new HashSet<string>(left.ArchivedFiles, StringComparer.OrdinalIgnoreCase);
        var rightFiles = new HashSet<string>(right.ArchivedFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var file in leftFiles.Union(rightFiles).OrderBy(f => f))
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
                left.ArchivedFileCrcs.TryGetValue(file, out var leftCrc);
                right.ArchivedFileCrcs.TryGetValue(file, out var rightCrc);

                if (!string.Equals(leftCrc, rightCrc, StringComparison.OrdinalIgnoreCase))
                {
                    fileDiff.Type = DifferenceType.Modified;
                    fileDiff.PropertyDifferences.Add(new PropertyDifference
                    {
                        PropertyName = "CRC",
                        LeftValue = leftCrc ?? "N/A",
                        RightValue = rightCrc ?? "N/A"
                    });
                }

                left.ArchivedFileTimestamps.TryGetValue(file, out var leftTime);
                right.ArchivedFileTimestamps.TryGetValue(file, out var rightTime);

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
                result.FileDifferences.Add(fileDiff);
        }

        // Compare stored files (normalize path separators for cross-platform compatibility)
        var leftStored = left.StoredFiles.Select(s => s.FileName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightStored = right.StoredFiles.Select(s => s.FileName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in leftStored.Union(rightStored).OrderBy(f => f))
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
    public static void CompareSRSFiles(SRSFile left, SRSFile right, CompareResult result)
    {
        if (left.FileData is { } leftFd && right.FileData is { } rightFd)
        {
            CompareProperty(result.ArchiveDifferences, "App Name", leftFd.AppName, rightFd.AppName);
            CompareProperty(result.ArchiveDifferences, "File Name", leftFd.FileName, rightFd.FileName);
            CompareProperty(result.ArchiveDifferences, "Sample Size",
                $"{leftFd.SampleSize:N0} bytes", $"{rightFd.SampleSize:N0} bytes");
            CompareProperty(result.ArchiveDifferences, "CRC32",
                leftFd.Crc32.ToString("X8"), rightFd.Crc32.ToString("X8"));
            CompareProperty(result.ArchiveDifferences, "Flags",
                $"0x{leftFd.Flags:X4}", $"0x{rightFd.Flags:X4}");
        }

        // Compare tracks
        int trackCount = Math.Max(left.Tracks.Count, right.Tracks.Count);
        for (int i = 0; i < trackCount; i++)
        {
            var lt = i < left.Tracks.Count ? left.Tracks[i] : null;
            var rt = i < right.Tracks.Count ? right.Tracks[i] : null;
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
            CompareProperty(fileDiff.PropertyDifferences, "Signature",
                Convert.ToHexString(lt.Signature), Convert.ToHexString(rt.Signature));
            CompareProperty(fileDiff.PropertyDifferences, "Flags",
                $"0x{lt.Flags:X4}", $"0x{rt.Flags:X4}");

            if (fileDiff.PropertyDifferences.Count > 0)
            {
                fileDiff.Type = DifferenceType.Modified;
                result.FileDifferences.Add(fileDiff);
            }
        }
    }

    /// <summary>
    /// Compares two RAR files using detailed block data if available, otherwise compares archive-level properties.
    /// </summary>
    public static void CompareRARFiles(RARFileData left, RARFileData right, CompareResult result,
        List<RARDetailedBlock>? leftBlocks, List<RARDetailedBlock>? rightBlocks)
    {
        if (leftBlocks != null && rightBlocks != null)
        {
            CompareDetailedBlocks(leftBlocks, rightBlocks, result);
            return;
        }

        CompareProperty(result.ArchiveDifferences, "Format", left.IsRAR5 ? "RAR 5.x" : "RAR 4.x", right.IsRAR5 ? "RAR 5.x" : "RAR 4.x");
    }

    /// <summary>
    /// Compares two lists of detailed RAR blocks field by field, populating the result with differences.
    /// </summary>
    public static void CompareDetailedBlocks(List<RARDetailedBlock> leftBlocks, List<RARDetailedBlock> rightBlocks, CompareResult result)
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
            var lb = leftBlocks[i];
            var rb = rightBlocks[i];

            bool isItemBlock = lb.BlockType.Contains("File") || lb.BlockType.Contains("Service");

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
                var lf = f < lb.Fields.Count ? lb.Fields[f] : null;
                var rf = f < rb.Fields.Count ? rb.Fields[f] : null;

                string name = lf?.Name ?? rf?.Name ?? $"Field {f}";

                // Skip Header CRC - it's a consequence of other field changes
                if (name is "Header CRC" or "CRC32")
                    continue;

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

            if (fileDiff != null && fileDiff.Type != DifferenceType.None)
                result.FileDifferences.Add(fileDiff);
        }
    }

    /// <summary>
    /// Returns whether two RAR detailed blocks have any field value or data size differences.
    /// </summary>
    public static bool HasFieldDifferences(RARDetailedBlock left, RARDetailedBlock right)
    {
        if (left.DataSize != right.DataSize) return true;

        int count = Math.Max(left.Fields.Count, right.Fields.Count);
        for (int f = 0; f < count; f++)
        {
            var lf = f < left.Fields.Count ? left.Fields[f] : null;
            var rf = f < right.Fields.Count ? right.Fields[f] : null;
            if ((lf?.Value ?? "") != (rf?.Value ?? ""))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Adds a property difference to the list if the left and right values differ.
    /// </summary>
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
    public static string FormatRARVersion(int? version) => version switch
    {
        null => "Unknown",
        50 => "RAR 5.0",
        _ => $"RAR {version / 10}.{version % 10}"
    };

    /// <summary>
    /// Returns the display name for a RAR compression method byte (e.g., 0x33 = "Normal").
    /// </summary>
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
    public static string GetCompressionMethodName(byte method) => GetCompressionMethodName((int?)method);

    /// <summary>
    /// Formats a dictionary size in KB as a display string.
    /// </summary>
    public static string FormatDictionarySize(int? size) => size switch
    {
        null => "Unknown",
        _ => $"{size} KB"
    };

    /// <summary>
    /// Formats a nullable boolean as "Yes", "No", or "Unknown".
    /// </summary>
    public static string FormatBool(bool? value) => value switch
    {
        null => "Unknown",
        true => "Yes",
        false => "No"
    };
}
