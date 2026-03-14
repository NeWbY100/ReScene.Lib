namespace RARLib.Decompression;

/// <summary>
/// Bit-level input stream reader for RAR decompression.
/// Ported from unrar getbits.hpp.
/// </summary>
public class BitInput
{
    /// <summary>
    /// Size of input buffer (32KB).
    /// </summary>
    public const int MaxSize = 0x8000;

    /// <summary>
    /// Input buffer containing compressed data.
    /// </summary>
    public byte[] InBuf { get; private set; }

    /// <summary>
    /// Current byte position in the buffer.
    /// </summary>
    public int InAddr { get; set; }

    /// <summary>
    /// Current bit position within the current byte (0-7).
    /// </summary>
    public int InBit { get; set; }

    /// <summary>
    /// Creates a new BitInput with an allocated buffer.
    /// </summary>
    public BitInput()
    {
        InBuf = new byte[MaxSize];
        InAddr = 0;
        InBit = 0;
    }

    /// <summary>
    /// Creates a BitInput using an external buffer.
    /// </summary>
    /// <param name="buffer">External buffer to use</param>
    public BitInput(byte[] buffer)
    {
        InBuf = buffer;
        InAddr = 0;
        InBit = 0;
    }

    /// <summary>
    /// Initializes bit input position to the beginning.
    /// </summary>
    public void InitBitInput()
    {
        InAddr = 0;
        InBit = 0;
    }

    /// <summary>
    /// Moves forward by the specified number of bits.
    /// </summary>
    /// <param name="bits">Number of bits to advance</param>
    public void AddBits(int bits)
    {
        bits += InBit;
        InAddr += bits >> 3;
        InBit = bits & 7;
    }

    /// <summary>
    /// Returns 16 bits from current position in the buffer.
    /// Bit at (InAddr, InBit) has the highest position in returned data.
    /// </summary>
    /// <returns>16-bit value</returns>
    public uint GetBits()
    {
        // Ensure we don't read past buffer
        if (InAddr + 2 >= InBuf.Length)
        {
            return 0;
        }

        // Read 3 bytes and combine (big-endian style)
        uint bitField = (uint)InBuf[InAddr] << 16;
        bitField |= (uint)InBuf[InAddr + 1] << 8;
        bitField |= InBuf[InAddr + 2];

        // Shift by (8 - InBit) to align
        bitField >>= (8 - InBit);

        return bitField & 0xFFFF;
    }

    /// <summary>
    /// Returns 32 bits from current position in the buffer.
    /// </summary>
    /// <returns>32-bit value</returns>
    public uint GetBits32()
    {
        if (InAddr + 4 >= InBuf.Length)
        {
            return 0;
        }

        // Read 4 bytes big-endian
        uint bitField = (uint)InBuf[InAddr] << 24;
        bitField |= (uint)InBuf[InAddr + 1] << 16;
        bitField |= (uint)InBuf[InAddr + 2] << 8;
        bitField |= InBuf[InAddr + 3];

        // Shift left by InBit, then add remaining bits
        bitField <<= InBit;
        if (InAddr + 4 < InBuf.Length)
        {
            bitField |= (uint)InBuf[InAddr + 4] >> (8 - InBit);
        }

        return bitField;
    }

    /// <summary>
    /// Checks if buffer would overflow with the specified increment.
    /// </summary>
    /// <param name="incPtr">Number of bytes to check</param>
    /// <returns>True if would overflow</returns>
    public bool Overflow(int incPtr)
    {
        return InAddr + incPtr >= MaxSize;
    }

    /// <summary>
    /// Sets the buffer from external data.
    /// </summary>
    /// <param name="data">Data to copy into buffer</param>
    /// <param name="offset">Offset in source data</param>
    /// <param name="length">Number of bytes to copy</param>
    public void SetBuffer(byte[] data, int offset = 0, int length = -1)
    {
        if (length < 0)
        {
            length = Math.Min(data.Length - offset, MaxSize);
        }

        Array.Copy(data, offset, InBuf, 0, Math.Min(length, MaxSize));
        InAddr = 0;
        InBit = 0;
    }

    /// <summary>
    /// Gets a single byte from the current position and advances.
    /// </summary>
    /// <returns>Byte value</returns>
    public byte GetChar()
    {
        if (InAddr >= InBuf.Length)
        {
            return 0;
        }
        return InBuf[InAddr++];
    }
}
