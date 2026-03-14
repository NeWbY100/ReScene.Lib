namespace RARLib.Decompression.PPMd;

/// <summary>
/// Carryless range coder for PPMd decompression.
/// Ported from unrar coder.cpp/hpp by Dmitry Subbotin.
/// </summary>
public class RangeCoder
{
    public const uint TOP = 1u << 24;
    public const uint BOT = 1u << 15;

    public uint Low;
    public uint Code;
    public uint Range;

    public uint LowCount;
    public uint HighCount;
    public uint Scale;

    private byte[]? _buffer;
    private int _bufPos;

    /// <summary>
    /// Initializes the decoder with input data.
    /// </summary>
    public void InitDecoder(byte[] buffer, int offset = 0)
    {
        _buffer = buffer;
        _bufPos = offset;

        Low = 0;
        Code = 0;
        Range = 0xFFFFFFFF;

        for (int i = 0; i < 4; i++)
            Code = (Code << 8) | GetChar();
    }

    /// <summary>
    /// Gets a byte from the input buffer.
    /// </summary>
    public byte GetChar()
    {
        if (_buffer == null || _bufPos >= _buffer.Length)
            return 0;
        return _buffer[_bufPos++];
    }

    /// <summary>
    /// Gets the current count for arithmetic decoding.
    /// Note: This modifies Range as a side effect (range /= scale).
    /// </summary>
    public int GetCurrentCount()
    {
        Range /= Scale;
        return (int)((Code - Low) / Range);
    }

    /// <summary>
    /// Gets the current shifted count.
    /// Note: This modifies Range as a side effect (range >>= shift).
    /// </summary>
    public uint GetCurrentShiftCount(int shift)
    {
        Range >>= shift;
        return (Code - Low) / Range;
    }

    /// <summary>
    /// Decodes the current symbol.
    /// </summary>
    public void Decode()
    {
        Low += Range * LowCount;
        Range *= HighCount - LowCount;
    }

    /// <summary>
    /// Normalizes the range coder state.
    /// This is the ARI_DEC_NORMALIZE macro from unrar.
    /// </summary>
    public void Normalize()
    {
        // ARI_DEC_NORMALIZE(code,low,range,read):
        // while ((low^(low+range))<TOP || range<BOT && ((range=-(int)low&(BOT-1)),1))
        // {
        //   code=(code << 8) | read->GetChar();
        //   range <<= 8;
        //   low <<= 8;
        // }

        while (true)
        {
            // Check if low and low+range are in the same TOP block
            if ((Low ^ (Low + Range)) >= TOP)
            {
                // Check if range is too small
                if (Range >= BOT)
                    break;

                // Range is too small, adjust it
                // range = -(int)low & (BOT-1)
                Range = (uint)(-(int)Low) & (BOT - 1);
            }

            // Shift in a new byte
            Code = (Code << 8) | GetChar();
            Range <<= 8;
            Low <<= 8;
        }
    }
}
