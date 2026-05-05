namespace ReScene.SRR;

/// <summary>
/// Validates the structural integrity of an SRR file. The verifier walks each block,
/// checks header sanity, CRC sentinels, and block sizes against the file length, and
/// returns a structured <see cref="SrrVerifyResult"/>.
/// </summary>
public static class SRRVerifier
{
    private const int BaseHeaderSize = 7;
    private const int AddSizeFieldLength = 4;
    private const ushort HeaderSentinel = 0x6969;
    private const ushort StoredFileSentinel = 0x6A6A;
    private const ushort OsoSentinel = 0x6B6B;
    private const ushort RarPaddingSentinel = 0x6C6C;
    private const ushort RarFileSentinel = 0x7171;

    /// <summary>
    /// Verifies the structural integrity of the SRR file at the given path.
    /// </summary>
    /// <param name="srrFilePath">
    /// Absolute path to the SRR file to verify.
    /// </param>
    /// <returns>
    /// A <see cref="SrrVerifyResult"/> describing the outcome.
    /// </returns>
    public static SrrVerifyResult Verify(string srrFilePath)
    {
        if (string.IsNullOrWhiteSpace(srrFilePath))
        {
            throw new ArgumentException("SRR file path is required.", nameof(srrFilePath));
        }

        if (!File.Exists(srrFilePath))
        {
            throw new FileNotFoundException("SRR file not found.", srrFilePath);
        }

        List<SrrVerifyIssue> issues = [];
        int blocksScanned = 0;
        bool sawHeader = false;

        using FileStream fs = new(srrFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using BinaryReader reader = new(fs);
        long fileSize = fs.Length;

        while (fs.Position < fileSize)
        {
            long blockStart = fs.Position;

            if (blockStart + BaseHeaderSize > fileSize)
            {
                issues.Add(new SrrVerifyIssue
                {
                    Severity = SrrVerifyIssueSeverity.Error,
                    Message = $"Truncated header at offset 0x{blockStart:X}.",
                    Offset = blockStart
                });
                break;
            }

            ushort crc = reader.ReadUInt16();
            byte typeRaw = reader.ReadByte();
            ushort flags = reader.ReadUInt16();
            ushort headerSize = reader.ReadUInt16();

            if (headerSize < BaseHeaderSize)
            {
                issues.Add(new SrrVerifyIssue
                {
                    Severity = SrrVerifyIssueSeverity.Error,
                    Message = $"Block at 0x{blockStart:X} reports header size {headerSize}; must be >= {BaseHeaderSize}.",
                    Offset = blockStart,
                    BlockType = typeRaw
                });
                break;
            }

            if (!CrcSentinelMatches(crc, typeRaw))
            {
                issues.Add(new SrrVerifyIssue
                {
                    Severity = SrrVerifyIssueSeverity.Warning,
                    Message = $"Unexpected CRC sentinel 0x{crc:X4} for block type 0x{typeRaw:X2} at 0x{blockStart:X}.",
                    Offset = blockStart,
                    BlockType = typeRaw
                });
            }

            uint addSize = 0;
            bool hasAddSize = (flags & (ushort)SRRBlockFlags.LongBlock) != 0
                              || typeRaw == (byte)SRRBlockType.StoredFile;

            if (hasAddSize)
            {
                // Strict guard (verifier reports truncation as an error; SRREditor reads-or-skips silently).
                if (fs.Position + AddSizeFieldLength > fileSize)
                {
                    issues.Add(new SrrVerifyIssue
                    {
                        Severity = SrrVerifyIssueSeverity.Error,
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
                issues.Add(new SrrVerifyIssue
                {
                    Severity = SrrVerifyIssueSeverity.Error,
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
            issues.Add(new SrrVerifyIssue
            {
                Severity = SrrVerifyIssueSeverity.Error,
                Message = "Missing SRR header block (0x69).",
                Offset = 0
            });
        }

        bool isValid = !issues.Any(i => i.Severity == SrrVerifyIssueSeverity.Error);

        return new SrrVerifyResult
        {
            IsValid = isValid,
            Issues = issues,
            BlocksScanned = blocksScanned,
            FileSize = fileSize
        };
    }

    private static bool CrcSentinelMatches(ushort crc, byte typeRaw)
        => typeRaw switch
        {
            (byte)SRRBlockType.Header => crc == HeaderSentinel,
            (byte)SRRBlockType.StoredFile => crc == StoredFileSentinel,
            (byte)SRRBlockType.OsoHash => crc == OsoSentinel,
            (byte)SRRBlockType.RarPadding => crc == RarPaddingSentinel,
            (byte)SRRBlockType.RarFile => crc == RarFileSentinel,
            _ => true
        };
}
