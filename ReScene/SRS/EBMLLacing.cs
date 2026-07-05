namespace ReScene.SRS;

/// <summary>
/// Parses lacing headers from MKV Block/SimpleBlock elements to determine
/// individual frame sizes within a laced block.
/// </summary>
internal static class EBMLLacing
{
    /// <summary>
    /// Xiph lacing continuation sentinel: a frame-size byte equal to this value means
    /// "add 255 to the running sum and read the next byte".
    /// </summary>
    public const int XiphContinuation = 0xFF;
    /// <summary>
    /// Parses the lacing information from block data to get individual frame sizes.
    /// </summary>
    /// <param name="data">
    /// Block data starting at the lacing header (after track number, timecode, and flags byte).
    /// For <see cref="EBMLLaceType.None"/>, this parameter is unused.
    /// </param>
    /// <param name="laceType">
    /// The lacing type extracted from the block flags byte.
    /// </param>
    /// <param name="totalDataLength">
    /// Total length of the frame data area (block data size minus the block header: track VINT + 2 timecode + 1 flags).
    /// </param>
    /// <returns>
    /// Array of frame sizes and the number of bytes consumed by the lacing header.
    /// </returns>
    public static (int[] frameSizes, int bytesConsumed) GetFrameLengths(
        ReadOnlySpan<byte> data, EBMLLaceType laceType, int totalDataLength)
    {
        int bytesConsumed = 0;
        int frameCount = 1;

        if (laceType != EBMLLaceType.None)
        {
            if (data.Length < 1)
            {
                return ([totalDataLength], 0);
            }

            frameCount = data[0] + 1;
            bytesConsumed = 1;
        }

        int[] frameSizes = new int[frameCount];

        switch (laceType)
        {
            case EBMLLaceType.None:
                frameSizes[0] = totalDataLength;
                break;

            case EBMLLaceType.Fixed:
                int fixedSize = totalDataLength / frameCount;
                for (int i = 0; i < frameCount; i++)
                {
                    frameSizes[i] = fixedSize;
                }

                break;

            case EBMLLaceType.Xiph:
                for (int i = 0; i < frameCount; i++)
                {
                    if (i < frameCount - 1)
                    {
                        // Read 0xFF bytes, summing them, until a non-0xFF byte
                        int size = 0;
                        while (bytesConsumed < data.Length)
                        {
                            byte b = data[bytesConsumed];
                            bytesConsumed++;
                            size += b;
                            if (b != XiphContinuation)
                            {
                                break;
                            }
                        }

                        frameSizes[i] = size;
                    }
                    else
                    {
                        // Last frame: remaining bytes after lacing header and previous frames
                        int usedByFrames = 0;
                        for (int j = 0; j < i; j++)
                        {
                            usedByFrames += frameSizes[j];
                        }

                        frameSizes[i] = totalDataLength - bytesConsumed - usedByFrames;
                    }
                }

                break;

            case EBMLLaceType.EBML:
                for (int i = 0; i < frameCount; i++)
                {
                    if (i == 0)
                    {
                        // First frame: read as unsigned EBML VINT
                        (long value, int vintLen) = EBMLVInt.ReadUnsigned(data[bytesConsumed..]);
                        frameSizes[0] = (int)value;
                        bytesConsumed += vintLen;
                    }
                    else if (i < frameCount - 1)
                    {
                        // Subsequent frames (not last): read signed EBML VINT delta
                        (long delta, int vintLen) = EBMLVInt.ReadSigned(data[bytesConsumed..]);
                        frameSizes[i] = frameSizes[i - 1] + (int)delta;
                        bytesConsumed += vintLen;
                    }
                    else
                    {
                        // Last frame: remaining bytes
                        int usedByFrames = 0;
                        for (int j = 0; j < i; j++)
                        {
                            usedByFrames += frameSizes[j];
                        }

                        frameSizes[i] = totalDataLength - bytesConsumed - usedByFrames;
                    }
                }

                break;
        }

        return (frameSizes, bytesConsumed);
    }

    /// <summary>
    /// Reads the lacing header (if any) that follows the base block header and returns the number of
    /// bytes it occupies. Reads directly from the stream with no fixed cap, so lacing headers larger
    /// than any peek buffer are measured correctly. Leaves the stream position indeterminate. This is
    /// the single source of truth shared by SRS creation and rebuild so the two always agree on where
    /// the frame data (and thus the track signature) begins.
    /// </summary>
    public static int ReadLacingHeaderSize(Stream fs, long blockStart, int blockHeaderBase, int flagsByte)
    {
        var laceType = (EBMLLaceType)(flagsByte & MkvBlockFlags.LacingMask);
        if (laceType == EBMLLaceType.None)
        {
            return 0;
        }

        fs.Position = blockStart + blockHeaderBase;
        int laceCount = fs.ReadByte();
        if (laceCount < 0)
        {
            return 0;
        }

        int lacingHeaderSize = 1;

        if (laceType == EBMLLaceType.Xiph)
        {
            for (int i = 0; i < laceCount; i++)
            {
                int b;
                do
                {
                    b = fs.ReadByte();
                    if (b < 0)
                    {
                        return lacingHeaderSize;
                    }

                    lacingHeaderSize++;
                } while (b == XiphContinuation);
            }
        }
        else if (laceType == EBMLLaceType.EBML)
        {
            if (EBMLReader.TryReadSize(fs, out _, out int firstSizeLen))
            {
                lacingHeaderSize += firstSizeLen;
                for (int i = 1; i < laceCount; i++)
                {
                    if (EBMLReader.TryReadSize(fs, out _, out int deltaLen))
                    {
                        lacingHeaderSize += deltaLen;
                    }
                }
            }
        }
        // Fixed-size lacing (EBMLLaceType.Fixed) has only the lace count byte — no extra size data

        return lacingHeaderSize;
    }
}
