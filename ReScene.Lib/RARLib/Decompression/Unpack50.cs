namespace RARLib.Decompression;

/// <summary>
/// RAR 5.x LZSS decompressor.
/// Ported from unrar unpack50.cpp - simplified for comment decompression.
/// Does not support filters (not needed for comments).
/// </summary>
public class Unpack50
{
    private readonly BitInput _inp;
    private readonly UnpackBlockTables _tables = new();

    private byte[] _window = null!;
    private int _winSize;
    private int _winMask;
    private int _unpPtr;

    private int _lastLength;
    private readonly long[] _oldDist = new long[4];

    private bool _tablesRead;

    /// <summary>
    /// Creates a new RAR 5.x unpacker.
    /// </summary>
    public Unpack50()
    {
        _inp = new BitInput();
    }

    /// <summary>
    /// Decompresses RAR 5.x compressed data.
    /// </summary>
    /// <param name="srcData">Compressed data</param>
    /// <param name="destSize">Expected decompressed size</param>
    /// <returns>Decompressed data, or null on failure</returns>
    public byte[]? Decompress(byte[] srcData, int destSize)
    {
        if (srcData == null || srcData.Length == 0 || destSize <= 0)
            return null;

        // Initialize window (64KB for comments)
        _winSize = 0x10000; // 64KB
        _winMask = _winSize - 1;
        _window = new byte[_winSize];
        _unpPtr = 0;

        // Initialize old distances
        Array.Clear(_oldDist);
        _lastLength = 0;
        _tablesRead = false;

        // Set up input buffer
        _inp.SetBuffer(srcData);

        // Read block header and tables
        if (!ReadBlockHeader())
            return null;

        if (!ReadTables())
            return null;

        // Decompress
        byte[] result = new byte[destSize];
        int destPtr = 0;

        while (destPtr < destSize)
        {
            // Check for buffer overflow
            if (_inp.InAddr >= srcData.Length)
                break;

            uint mainSlot = HuffmanDecoder.DecodeNumber(_inp, _tables.LD);

            if (mainSlot < 256)
            {
                // Literal byte
                _window[_unpPtr++] = (byte)mainSlot;
                _unpPtr &= _winMask;
                result[destPtr++] = (byte)mainSlot;
                continue;
            }

            if (mainSlot >= 262)
            {
                // Length-distance pair
                int length = SlotToLength((int)mainSlot - 262);

                uint distSlot = HuffmanDecoder.DecodeNumber(_inp, _tables.DD);
                long distance = DecodeDistance(distSlot);

                // Adjust length for long distances
                if (distance > 0x100)
                {
                    length++;
                    if (distance > 0x2000)
                    {
                        length++;
                        if (distance > 0x40000)
                            length++;
                    }
                }

                InsertOldDist(distance);
                _lastLength = length;

                // Copy string
                destPtr = CopyString(result, destPtr, destSize, length, distance);
                continue;
            }

            if (mainSlot == 256)
            {
                // Filter - skip for comments
                SkipFilter();
                continue;
            }

            if (mainSlot == 257)
            {
                // Repeat last match
                if (_lastLength != 0)
                    destPtr = CopyString(result, destPtr, destSize, _lastLength, _oldDist[0]);
                continue;
            }

            if (mainSlot < 262)
            {
                // Use old distance
                int distNum = (int)(mainSlot - 258);
                long distance = _oldDist[distNum];

                // Shift old distances
                for (int i = distNum; i > 0; i--)
                    _oldDist[i] = _oldDist[i - 1];
                _oldDist[0] = distance;

                uint lengthSlot = HuffmanDecoder.DecodeNumber(_inp, _tables.RD);
                int length = SlotToLength((int)lengthSlot);

                _lastLength = length;
                destPtr = CopyString(result, destPtr, destSize, length, distance);
                continue;
            }
        }

        return result;
    }

    private int SlotToLength(int slot)
    {
        int length = 2;
        int lBits;

        if (slot < 8)
        {
            lBits = 0;
            length += slot;
        }
        else
        {
            lBits = slot / 4 - 1;
            length += (4 | (slot & 3)) << lBits;
        }

        if (lBits > 0)
        {
            length += (int)(_inp.GetBits() >> (16 - lBits));
            _inp.AddBits(lBits);
        }

        return length;
    }

    private long DecodeDistance(uint distSlot)
    {
        long distance = 1;
        int dBits;

        if (distSlot < 4)
        {
            dBits = 0;
            distance += distSlot;
        }
        else
        {
            dBits = (int)(distSlot / 2 - 1);
            distance += (long)(2 | (distSlot & 1)) << dBits;
        }

        if (dBits > 0)
        {
            if (dBits >= 4)
            {
                if (dBits > 4)
                {
                    // For large distances
                    if (dBits > 36)
                    {
                        distance += ((long)_inp.GetBits32() >> (68 - dBits)) << 4;
                    }
                    else
                    {
                        distance += ((long)_inp.GetBits32() >> (36 - dBits)) << 4;
                    }
                    _inp.AddBits(dBits - 4);
                }

                uint lowDist = HuffmanDecoder.DecodeNumber(_inp, _tables.LDD);
                distance += lowDist;
            }
            else
            {
                distance += _inp.GetBits() >> (16 - dBits);
                _inp.AddBits(dBits);
            }
        }

        return distance;
    }

    private void InsertOldDist(long distance)
    {
        _oldDist[3] = _oldDist[2];
        _oldDist[2] = _oldDist[1];
        _oldDist[1] = _oldDist[0];
        _oldDist[0] = distance;
    }

    private int CopyString(byte[] result, int destPtr, int destSize, int length, long distance)
    {
        int srcPos = (int)((_unpPtr - distance) & _winMask);

        while (length-- > 0 && destPtr < destSize)
        {
            byte b = _window[srcPos];
            srcPos = (srcPos + 1) & _winMask;

            _window[_unpPtr++] = b;
            _unpPtr &= _winMask;

            result[destPtr++] = b;
        }

        return destPtr;
    }

    private bool ReadBlockHeader()
    {
        // Align to byte boundary
        _inp.AddBits((8 - _inp.InBit) & 7);

        byte blockFlags = (byte)(_inp.GetBits() >> 8);
        _inp.AddBits(8);

        int byteCount = ((blockFlags >> 3) & 3) + 1;
        if (byteCount == 4)
            return false;

        byte savedCheckSum = (byte)(_inp.GetBits() >> 8);
        _inp.AddBits(8);

        int blockSize = 0;
        for (int i = 0; i < byteCount; i++)
        {
            blockSize += (int)((_inp.GetBits() >> 8) << (i * 8));
            _inp.AddBits(8);
        }

        byte checkSum = (byte)(0x5A ^ blockFlags ^ blockSize ^ (blockSize >> 8) ^ (blockSize >> 16));
        if (checkSum != savedCheckSum)
            return false;

        // Check table present flag
        return (blockFlags & 0x80) != 0 || _tablesRead;
    }

    private bool ReadTables()
    {
        // Read bit lengths for the bit length alphabet
        byte[] bitLength = new byte[PackDef.BC];
        for (int i = 0; i < PackDef.BC; i++)
        {
            int length = (int)(_inp.GetBits() >> 12);
            _inp.AddBits(4);

            if (length == 15)
            {
                int zeroCount = (int)(_inp.GetBits() >> 12);
                _inp.AddBits(4);

                if (zeroCount == 0)
                {
                    bitLength[i] = 15;
                }
                else
                {
                    zeroCount += 2;
                    while (zeroCount-- > 0 && i < bitLength.Length)
                        bitLength[i++] = 0;
                    i--;
                }
            }
            else
            {
                bitLength[i] = (byte)length;
            }
        }

        HuffmanDecoder.MakeDecodeTables(bitLength, _tables.BD, PackDef.BC);

        // Read the main table (use base table size for comments - no extra dist)
        byte[] table = new byte[PackDef.HuffTableSizeB];
        for (int i = 0; i < PackDef.HuffTableSizeB;)
        {
            uint number = HuffmanDecoder.DecodeNumber(_inp, _tables.BD);

            if (number < 16)
            {
                table[i] = (byte)number;
                i++;
            }
            else if (number < 18)
            {
                int n;
                if (number == 16)
                {
                    n = (int)((_inp.GetBits() >> 13) + 3);
                    _inp.AddBits(3);
                }
                else
                {
                    n = (int)((_inp.GetBits() >> 9) + 11);
                    _inp.AddBits(7);
                }

                if (i == 0)
                    return false; // Cannot repeat at position 0

                while (n-- > 0 && i < PackDef.HuffTableSizeB)
                {
                    table[i] = table[i - 1];
                    i++;
                }
            }
            else
            {
                int n;
                if (number == 18)
                {
                    n = (int)((_inp.GetBits() >> 13) + 3);
                    _inp.AddBits(3);
                }
                else
                {
                    n = (int)((_inp.GetBits() >> 9) + 11);
                    _inp.AddBits(7);
                }

                while (n-- > 0 && i < PackDef.HuffTableSizeB)
                    table[i++] = 0;
            }
        }

        _tablesRead = true;

        // Build decode tables for each alphabet
        HuffmanDecoder.MakeDecodeTables(table, _tables.LD, PackDef.NC);

        byte[] ddTable = new byte[PackDef.DCB];
        Array.Copy(table, PackDef.NC, ddTable, 0, PackDef.DCB);
        HuffmanDecoder.MakeDecodeTables(ddTable, _tables.DD, PackDef.DCB);

        byte[] lddTable = new byte[PackDef.LDC];
        Array.Copy(table, PackDef.NC + PackDef.DCB, lddTable, 0, PackDef.LDC);
        HuffmanDecoder.MakeDecodeTables(lddTable, _tables.LDD, PackDef.LDC);

        byte[] rdTable = new byte[PackDef.RC];
        Array.Copy(table, PackDef.NC + PackDef.DCB + PackDef.LDC, rdTable, 0, PackDef.RC);
        HuffmanDecoder.MakeDecodeTables(rdTable, _tables.RD, PackDef.RC);

        return true;
    }

    private void SkipFilter()
    {
        // Read and discard filter data - not needed for comments
        // Filter block start
        ReadFilterData();
        // Filter block length
        ReadFilterData();
        // Filter type (3 bits)
        int filterType = (int)(_inp.GetBits() >> 13);
        _inp.AddBits(3);

        // Delta filter has extra channel data
        if (filterType == 3) // FILTER_DELTA
        {
            _inp.AddBits(5); // channels
        }
    }

    private uint ReadFilterData()
    {
        int byteCount = (int)((_inp.GetBits() >> 14) + 1);
        _inp.AddBits(2);

        uint data = 0;
        for (int i = 0; i < byteCount; i++)
        {
            data += (uint)((_inp.GetBits() >> 8) << (i * 8));
            _inp.AddBits(8);
        }
        return data;
    }
}
