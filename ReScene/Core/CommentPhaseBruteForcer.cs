using System.Text;
using ReScene.Core.Diagnostics;

namespace ReScene.Core;

/// <summary>
/// Phase 1 of the brute force: narrows the candidate RAR versions by reproducing only the archive
/// comment (CMT) block. For each version it creates a tiny test archive with the SRR's comment and
/// compares the resulting compressed CMT bytes against the expected ones, returning the versions
/// that matched. Extracted from <see cref="Manager"/>.
/// </summary>
/// <remarks>
/// Cross-cutting concerns are supplied explicitly: the logger and original log source (so log
/// events are indistinguishable from when Manager logged them), the run's cancellation token, and
/// the progress callback that re-raises Manager's <c>BruteForceProgress</c> event.
/// </remarks>
internal sealed class CommentPhaseBruteForcer(
    IReSceneLogger logger,
    object logSource,
    Action<BruteForceProgressEventArgs> fireBruteForceProgress,
    CancellationToken cancellationToken)
{
    private readonly IReSceneLogger _logger = logger;
    private readonly object _logSource = logSource;
    private readonly Action<BruteForceProgressEventArgs> _fireBruteForceProgress = fireBruteForceProgress;
    private readonly CancellationToken _cancellationToken = cancellationToken;

    /// <summary>
    /// Phase 1: Brute-force RAR versions using only the comment block.
    /// Returns a list of (directoryPath, version) tuples that produced matching CMT blocks.
    /// </summary>
    public async Task<List<(string Path, int Version)>> BruteForceCommentPhaseAsync(
        BruteForceOptions options,
        List<(string Path, int Version)> allRarDirectories)
    {
        var matchedVersions = new List<(string Path, int Version)>();
        byte[]? expectedCmtData = options.RAROptions.CmtCompressedData?.ToArray();

        if (expectedCmtData == null || expectedCmtData.Length == 0)
        {
            _logger.Information(_logSource, "Phase 1 skipped: No CMT compressed data available", LogTarget.Phase1);
            return allRarDirectories; // Return all versions if no CMT data
        }

        _logger.Information(_logSource, $"=== PHASE 1: Comment Block Brute-Force ===", LogTarget.Phase1);
        _logger.Information(_logSource, $"Expected CMT compressed data: {expectedCmtData.Length} bytes", LogTarget.Phase1);

        // Create a temporary directory for Phase 1 testing
        string phase1Dir = Path.Combine(options.OutputDirectoryPath, "phase1_temp");
        if (Directory.Exists(phase1Dir))
        {
            Directory.Delete(phase1Dir, true);
        }

        Directory.CreateDirectory(phase1Dir);

        // Create a small dummy file for testing
        string dummyInputDir = Path.Combine(phase1Dir, "input");
        Directory.CreateDirectory(dummyInputDir);
        string dummyFilePath = Path.Combine(dummyInputDir, "dummy.txt");
        File.WriteAllText(dummyFilePath, "dummy");

        // Create comment file
        string commentFilePath = Path.Combine(phase1Dir, "comment.txt");
        if (options.RAROptions.ArchiveCommentBytes is { } phase1CommentBytes)
        {
            File.WriteAllBytes(commentFilePath, phase1CommentBytes.ToArray());
        }
        else if (!string.IsNullOrEmpty(options.RAROptions.ArchiveComment))
        {
            File.WriteAllText(commentFilePath, options.RAROptions.ArchiveComment, new UTF8Encoding(false));
        }

        string outputDir = Path.Combine(phase1Dir, "output");
        Directory.CreateDirectory(outputDir);

        int totalTests = allRarDirectories.Count;
        int currentTest = 0;
        int matchCount = 0;
        int errorCount = 0;
        string? lastErrorMessage = null;
        DateTime phase1StartTime = DateTime.Now;

        // For Phase 1, use the CMT compression method (not file compression method)
        // CMT CompressionMethod is stored as raw 0x30-0x35, convert to 0-5 for -m flag
        string cmtMethodArg;
        if (options.RAROptions.CmtCompressionMethod.HasValue)
        {
            byte rawMethod = options.RAROptions.CmtCompressionMethod.Value;
            int cmtMethod = rawMethod >= 0x30 ? rawMethod - 0x30 : rawMethod;
            if (cmtMethod is >= 0 and <= 5)
            {
                cmtMethodArg = $"-m{cmtMethod}";
            }
            else
            {
                cmtMethodArg = "-m3"; // Default to normal
            }
        }
        else
        {
            cmtMethodArg = "-m3"; // Default to normal compression
        }

        // For Phase 1, always use -md64k (comments always use 64KB dictionary window)
        string cmtDictArg = "-md64k";

        _logger.Information(_logSource, $"CMT compression method: {cmtMethodArg}", LogTarget.Phase1);
        _logger.Information(_logSource, $"CMT dictionary size: {cmtDictArg}", LogTarget.Phase1);

        foreach ((string? rarVersionDir, int version) in allRarDirectories)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                break;
            }

            string rarExePath = Path.Combine(rarVersionDir, "rar.exe");
            string versionName = Path.GetFileName(rarVersionDir);

            currentTest++;

            // Fire progress event for Phase 1
            _fireBruteForceProgress(new(options.ReleaseDirectoryPath, rarVersionDir, $"{cmtMethodArg} {cmtDictArg}", totalTests, currentTest, phase1StartTime)
            {
                PhaseDescription = "Phase 1: Comment Block Filtering"
            });

            // Build arguments using CMT-specific compression method and dictionary
            List<string> args = ["a", "-r", cmtMethodArg, cmtDictArg, $"-z{commentFilePath}"];

            // Add -ma4 for RAR 5.50-6.x to create RAR4 format (RAR 7.x doesn't accept -ma4)
            if (version is >= 550 and < 700)
            {
                args.Add("-ma4");
            }

            // Add timestamp options if RAR version supports them (3.20+)
            // For CMT blocks, we typically disable ctime and atime
            if (version >= 320)
            {
                args.Add("-tsc-");
                args.Add("-tsa-");
            }

            string testRarPath = Path.Combine(outputDir, $"test_{versionName}_{cmtMethodArg}_{cmtDictArg}.rar"
                .Replace("-", "", StringComparison.Ordinal).Replace("/", "", StringComparison.Ordinal));

            try
            {
                // Create the test RAR
                var process = new RARProcess(rarExePath, dummyInputDir, testRarPath, args, _logger)
                {
                    LogTarget = LogTarget.Phase1
                };
                await process.RunAsync(_cancellationToken);

                if (!File.Exists(testRarPath))
                {
                    continue;
                }

                // Extract CMT data from generated RAR
                byte[]? generatedCmtData = ExtractCmtCompressedData(testRarPath);

                // Clean up test file
                try
                {
                    File.Delete(testRarPath);
                }
                catch { }

                if (generatedCmtData == null)
                {
                    continue;
                }

                // Compare CMT compressed data
                if (generatedCmtData.SequenceEqual(expectedCmtData))
                {
                    matchCount++;
                    if (!matchedVersions.Any(v => v.Path == rarVersionDir))
                    {
                        matchedVersions.Add((rarVersionDir, version));
                        _logger.Information(_logSource, $"Phase 1 MATCH: {versionName} {cmtMethodArg} {cmtDictArg}", LogTarget.Phase1);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                errorCount++;
                lastErrorMessage = ex.Message;
                _logger.Debug(_logSource, $"Phase 1 test failed for {versionName}: {ex.Message}", LogTarget.Phase1);
            }
        }

        // Clean up Phase 1 temp directory
        try
        {
            Directory.Delete(phase1Dir, true);
        }
        catch { }

        _logger.Information(_logSource, $"Phase 1 complete: {totalTests} tests, {matchCount} matches, {matchedVersions.Count} unique versions", LogTarget.Phase1);

        if (matchedVersions.Count == 0)
        {
            // Every test erroring (rather than simply not matching) points at a systemic problem
            // — rar.exe failing to launch, no disk space, an unreadable comment file — that would
            // otherwise be buried in Debug logs while Phase 2 silently retries the whole set.
            if (errorCount == totalTests && totalTests > 0)
            {
                _logger.Warning(_logSource, $"Phase 1 errored on all {totalTests} test(s) — likely a configuration or environment problem (last error: {lastErrorMessage}). Falling back to all versions for Phase 2.", LogTarget.System);
            }
            else if (errorCount > 0)
            {
                _logger.Warning(_logSource, $"Phase 1 found no matches ({errorCount} of {totalTests} test(s) errored; last error: {lastErrorMessage}) - falling back to all versions for Phase 2", LogTarget.Phase1);
            }
            else
            {
                _logger.Warning(_logSource, "Phase 1 found no matches - falling back to all versions for Phase 2", LogTarget.Phase1);
            }

            return allRarDirectories;
        }

        return matchedVersions;
    }

    /// <summary>
    /// Extracts the CMT block compressed data from a RAR file.
    /// </summary>
    private static byte[]? ExtractCmtCompressedData(string rarFilePath)
    {
        try
        {
            using var fs = new FileStream(rarFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            // Skip RAR signature (7 bytes for RAR 4.x)
            if (fs.Length < 7)
            {
                return null;
            }

            fs.Position = 7;

            while (fs.Position + 7 <= fs.Length)
            {
                long blockStart = fs.Position;

                // Read base header
                ushort crc = reader.ReadUInt16();
                byte blockType = reader.ReadByte();
                ushort flags = reader.ReadUInt16();
                ushort headerSize = reader.ReadUInt16();

                if (headerSize < 7 || blockStart + headerSize > fs.Length)
                {
                    break;
                }

                // Check if this is a service block (0x7A)
                if (blockType == 0x7A && headerSize >= 32)
                {
                    // Read ADD_SIZE (4 bytes at offset 7)
                    uint addSize = reader.ReadUInt32();

                    // Read to get the sub-type name
                    // Skip: UnpSize(4), HostOS(1), FileCRC(4), FileTime(4), UnpVer(1), Method(1), NameSize(2), Attr(4) = 21 bytes
                    fs.Position = blockStart + 7 + 4 + 4 + 1 + 4 + 4 + 1 + 1;
                    ushort nameSize = reader.ReadUInt16();

                    // Skip Attr (4 bytes)
                    fs.Position += 4;

                    // Read the sub-type name
                    if (nameSize > 0 && fs.Position + nameSize <= fs.Length)
                    {
                        byte[] nameBytes = reader.ReadBytes(nameSize);
                        string subType = Encoding.ASCII.GetString(nameBytes);

                        if (string.Equals(subType, "CMT", StringComparison.OrdinalIgnoreCase))
                        {
                            // Found CMT block - read the compressed data
                            long dataStart = blockStart + headerSize;
                            if (dataStart + addSize <= fs.Length && addSize > 0)
                            {
                                fs.Position = dataStart;
                                byte[] data = reader.ReadBytes((int)addSize);
                                return data;
                            }
                        }
                    }

                    // Skip this block
                    fs.Position = blockStart + headerSize + addSize;
                }
                else
                {
                    // Skip this block
                    bool hasAddSize = (flags & 0x8000) != 0 || blockType == 0x74 || blockType == 0x7A;
                    uint addSize = 0;
                    if (hasAddSize)
                    {
                        fs.Position = blockStart + 7;
                        if (fs.Position + 4 <= fs.Length)
                        {
                            addSize = reader.ReadUInt32();
                        }
                    }

                    fs.Position = blockStart + headerSize + addSize;
                }

                // Safety check
                if (fs.Position <= blockStart)
                {
                    break;
                }
            }
        }
        catch
        {
            // Failed to extract CMT data
        }

        return null;
    }
}
