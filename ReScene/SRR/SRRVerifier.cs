namespace ReScene.SRR;

/// <summary>
/// Validates the structural integrity of an SRR file. The verifier walks each block,
/// checks header sanity, CRC sentinels, and block sizes against the file length, and
/// returns a structured <see cref="SRRVerifyResult"/>.
/// </summary>
public static class SRRVerifier
{
    /// <summary>
    /// Verifies the structural integrity of the SRR file at the given path.
    /// </summary>
    /// <param name="srrFilePath">
    /// Absolute path to the SRR file to verify.
    /// </param>
    /// <returns>
    /// A <see cref="SRRVerifyResult"/> describing the outcome.
    /// </returns>
    public static SRRVerifyResult Verify(string srrFilePath)
    {
        if (string.IsNullOrWhiteSpace(srrFilePath))
        {
            throw new ArgumentException("SRR file path is required.", nameof(srrFilePath));
        }

        if (!File.Exists(srrFilePath))
        {
            throw new FileNotFoundException("SRR file not found.", srrFilePath);
        }

        List<SRRVerifyIssue> issues = [];
        int blocksScanned = 0;
        bool sawHeader = false;

        using FileStream fs = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using BinaryReader reader = new(fs);
        long fileSize = fs.Length;

        while (fs.Position < fileSize)
        {
            long blockStart = fs.Position;

            if (blockStart + SRRBlockLayout.BaseHeaderSize > fileSize)
            {
                issues.Add(new SRRVerifyIssue
                {
                    Severity = SRRVerifyIssueSeverity.Error,
                    Message = $"Truncated header at offset 0x{blockStart:X}.",
                    Offset = blockStart
                });
                break;
            }

            ushort crc = reader.ReadUInt16();
            byte typeRaw = reader.ReadByte();
            ushort flags = reader.ReadUInt16();
            ushort headerSize = reader.ReadUInt16();

            if (headerSize < SRRBlockLayout.BaseHeaderSize)
            {
                issues.Add(new SRRVerifyIssue
                {
                    Severity = SRRVerifyIssueSeverity.Error,
                    Message = $"Block at 0x{blockStart:X} reports header size {headerSize}; must be >= {SRRBlockLayout.BaseHeaderSize}.",
                    Offset = blockStart,
                    BlockType = typeRaw
                });
                break;
            }

            if (!CRCSentinelMatches(crc, typeRaw))
            {
                issues.Add(new SRRVerifyIssue
                {
                    Severity = SRRVerifyIssueSeverity.Warning,
                    Message = $"Unexpected CRC sentinel 0x{crc:X4} for block type 0x{typeRaw:X2} at 0x{blockStart:X}.",
                    Offset = blockStart,
                    BlockType = typeRaw
                });
            }

            if (typeRaw == (byte)SRRBlockType.RARFile)
            {
                // The first RAR volume block begins the embedded RAR-header region, which is NOT
                // SRR-framed. Validate the SRR structure up to here and stop — applying SRR
                // ADD_SIZE framing to the embedded RAR4 file headers (LONG_BLOCK with a phantom
                // packed-size ADD_SIZE) always false-errored "extends past end of file" on every
                // real SRR that contains archived files.
                blocksScanned++;
                break;
            }

            uint addSize = 0;
            bool hasAddSize = (flags & (ushort)SRRBlockFlags.LongBlock) != 0
                              || typeRaw == (byte)SRRBlockType.StoredFile;

            if (hasAddSize)
            {
                // Strict guard (verifier reports truncation as an error; SRREditor reads-or-skips silently).
                if (fs.Position + SRRBlockLayout.AddSizeFieldLength > fileSize)
                {
                    issues.Add(new SRRVerifyIssue
                    {
                        Severity = SRRVerifyIssueSeverity.Error,
                        Message = $"Truncated addSize at offset 0x{fs.Position:X}.",
                        Offset = blockStart,
                        BlockType = typeRaw
                    });
                    break;
                }

                addSize = reader.ReadUInt32();
            }

            long totalBlockSize = headerSize + addSize;
            long blockEnd = blockStart + totalBlockSize;

            if (blockEnd > fileSize)
            {
                issues.Add(new SRRVerifyIssue
                {
                    Severity = SRRVerifyIssueSeverity.Error,
                    Message = $"Block at 0x{blockStart:X} extends past end of file (size {totalBlockSize:N0}, file {fileSize:N0}).",
                    Offset = blockStart,
                    BlockType = typeRaw
                });
                break;
            }

            if (typeRaw == (byte)SRRBlockType.Header)
            {
                sawHeader = true;
            }

            blocksScanned++;
            fs.Position = blockEnd;
        }

        if (!sawHeader)
        {
            issues.Add(new SRRVerifyIssue
            {
                Severity = SRRVerifyIssueSeverity.Error,
                Message = "Missing SRR header block (0x69).",
                Offset = 0
            });
        }

        bool isValid = !issues.Any(i => i.Severity == SRRVerifyIssueSeverity.Error);

        return new SRRVerifyResult
        {
            IsValid = isValid,
            Issues = issues,
            BlocksScanned = blocksScanned,
            FileSize = fileSize
        };
    }

    private static bool CRCSentinelMatches(ushort crc, byte typeRaw)
        => typeRaw switch
        {
            (byte)SRRBlockType.Header => crc == SRRBlockLayout.HeaderSentinel,
            (byte)SRRBlockType.StoredFile => crc == SRRBlockLayout.StoredFileSentinel,
            (byte)SRRBlockType.OSOHash => crc == SRRBlockLayout.OSOSentinel,
            (byte)SRRBlockType.RARPadding => crc == SRRBlockLayout.RARPaddingSentinel,
            (byte)SRRBlockType.RARFile => crc == SRRBlockLayout.RARFileSentinel,
            _ => true
        };
}
