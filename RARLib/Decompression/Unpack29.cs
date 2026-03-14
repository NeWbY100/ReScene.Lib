using RARLib.Decompression.PPMd;

namespace RARLib.Decompression;

/// <summary>
/// RAR 2.9/3.x decompressor.
/// Ported from unrar unpack30.cpp - supports both LZSS and PPMd.
/// </summary>
public class Unpack29
{
    private enum BlockType { LZ, PPM }

    // Distance decode tables (initialized on first use) - unique to RAR 2.9/3.x
    private static readonly int[] DDecode = new int[PackDef.DC30];
    private static readonly byte[] DBits = new byte[PackDef.DC30];
    private static readonly int[] DBitLengthCounts = [4, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 14, 0, 12];

    private static bool _tablesInitialized;

    private readonly BitInput _inp;
    private readonly UnpackBlockTables _tables = new();
    private readonly byte[] _unpOldTable = new byte[PackDef.HuffTableSize30];

    private byte[] _window = null!;
    private int _winSize;
    private int _winMask;
    private int _unpPtr;

    private int _prevLowDist;
    private int _lowDistRepCount;
    private int _lastLength;
    private readonly int[] _oldDist = new int[4];

    private BlockType _blockType;
    private ModelPPM? _ppm;
    private int _ppmEscChar;

    static Unpack29()
    {
        InitStaticTables();
    }

    private static void InitStaticTables()
    {
        if (_tablesInitialized)
            return;

        int dist = 0, bitLength = 0, slot = 0;
        for (int i = 0; i < DBitLengthCounts.Length; i++, bitLength++)
        {
            for (int j = 0; j < DBitLengthCounts[i]; j++, slot++, dist += (1 << bitLength))
            {
                if (slot < DDecode.Length)
                {
                    DDecode[slot] = dist;
                    DBits[slot] = (byte)bitLength;
                }
            }
        }

        _tablesInitialized = true;
    }

    /// <summary>
    /// Creates a new RAR 2.9/3.x unpacker.
    /// </summary>
    public Unpack29()
    {
        _inp = new BitInput();
    }

    /// <summary>
    /// Decompresses RAR 2.9/3.x compressed data.
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
        _prevLowDist = 0;
        _lowDistRepCount = 0;

        // Clear old table
        Array.Clear(_unpOldTable);

        // Set up input buffer
        _inp.SetBuffer(srcData);

        // Read Huffman tables (determines LZ or PPM mode)
        if (!ReadTables30())
            return null;

        // Decompress
        byte[] result = new byte[destSize];
        int destPtr = 0;

        while (destPtr < destSize)
        {
            // Check for buffer exhaustion - use actual length, not 30-byte margin
            // BitInput.GetBits() handles bounds checking and returns 0 if past end
            if (_inp.InAddr >= srcData.Length)
                break;

            if (_blockType == BlockType.PPM)
            {
                // PPM decompression
                destPtr = DecompressPPM(result, destPtr, destSize);
                if (destPtr < 0)
                    return null;
            }
            else
            {
                // LZ decompression
                destPtr = DecompressLZ(result, destPtr, destSize, srcData.Length);
                if (destPtr < 0)
                    return null;
            }
        }

        return result;
    }

    private int DecompressPPM(byte[] result, int destPtr, int destSize)
    {
        if (_ppm == null)
            return -1;

        while (destPtr < destSize)
        {
            int ch = _ppm.DecodeChar();
            if (ch < 0)
            {
                // Corrupt PPM data - switch to LZ mode
                _ppm.CleanUp();
                _blockType = BlockType.LZ;
                break;
            }

            if (ch == _ppmEscChar)
            {
                int nextCh = _ppm.DecodeChar();
                if (nextCh < 0)
                    return -1;

                if (nextCh == 0)
                {
                    // End of PPM encoding - read new tables
                    if (!ReadTables30())
                        return -1;
                    break;
                }
                if (nextCh == 2)
                {
                    // End of file
                    return destPtr;
                }
                if (nextCh == 3)
                {
                    // VM code - skip (not needed for comments)
                    continue;
                }
                if (nextCh == 4)
                {
                    // LZ inside PPM
                    int distance = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        int b = _ppm.DecodeChar();
                        if (b < 0) return -1;
                        distance = (distance << 8) | b;
                    }
                    int lengthByte = _ppm.DecodeChar();
                    if (lengthByte < 0) return -1;
                    int length = lengthByte + 32;
                    destPtr = CopyString(result, destPtr, destSize, length, distance + 2);
                    continue;
                }
                if (nextCh == 5)
                {
                    // RLE inside PPM
                    int length = _ppm.DecodeChar();
                    if (length < 0) return -1;
                    destPtr = CopyString(result, destPtr, destSize, length + 4, 1);
                    continue;
                }
                // nextCh == 1 means escape char itself
            }

            _window[_unpPtr++] = (byte)ch;
            _unpPtr &= _winMask;
            result[destPtr++] = (byte)ch;
        }

        return destPtr;
    }

    private int DecompressLZ(byte[] result, int destPtr, int destSize, int srcLength)
    {
        while (destPtr < destSize)
        {
            // Check for buffer exhaustion - BitInput handles bounds checking
            if (_inp.InAddr >= srcLength)
                break;

            uint number = HuffmanDecoder.DecodeNumber(_inp, _tables.LD);

            if (number < 256)
            {
                // Literal byte
                _window[_unpPtr++] = (byte)number;
                _unpPtr &= _winMask;
                result[destPtr++] = (byte)number;
                continue;
            }

            if (number >= 271)
            {
                // Length-distance pair
                int length = PackDef.LDecode[number - 271] + 3;
                int bits = PackDef.LBits[number - 271];
                if (bits > 0)
                {
                    length += (int)(_inp.GetBits() >> (16 - bits));
                    _inp.AddBits(bits);
                }

                uint distNumber = HuffmanDecoder.DecodeNumber(_inp, _tables.DD);
                int distance = DDecode[distNumber] + 1;
                bits = DBits[distNumber];

                if (bits > 0)
                {
                    if (distNumber > 9)
                    {
                        if (bits > 4)
                        {
                            distance += (int)((_inp.GetBits() >> (20 - bits)) << 4);
                            _inp.AddBits(bits - 4);
                        }
                        if (_lowDistRepCount > 0)
                        {
                            _lowDistRepCount--;
                            distance += _prevLowDist;
                        }
                        else
                        {
                            uint lowDist = HuffmanDecoder.DecodeNumber(_inp, _tables.LDD);
                            if (lowDist == 16)
                            {
                                _lowDistRepCount = PackDef.LowDistRepCount - 1;
                                distance += _prevLowDist;
                            }
                            else
                            {
                                distance += (int)lowDist;
                                _prevLowDist = (int)lowDist;
                            }
                        }
                    }
                    else
                    {
                        distance += (int)(_inp.GetBits() >> (16 - bits));
                        _inp.AddBits(bits);
                    }
                }

                // Adjust length for long distances
                if (distance >= 0x2000)
                {
                    length++;
                    if (distance >= 0x40000)
                        length++;
                }

                InsertOldDist(distance);
                _lastLength = length;

                // Copy string
                destPtr = CopyString(result, destPtr, destSize, length, distance);
                continue;
            }

            if (number == 256)
            {
                // End of block - read new tables or end
                if (!ReadEndOfBlock())
                    return destPtr;
                continue;
            }

            if (number == 257)
            {
                // VM code - skip for comments
                SkipVMCode();
                continue;
            }

            if (number == 258)
            {
                // Repeat last match
                if (_lastLength != 0)
                    destPtr = CopyString(result, destPtr, destSize, _lastLength, _oldDist[0]);
                continue;
            }

            if (number < 263)
            {
                // Use old distance
                int distNum = (int)(number - 259);
                int distance = _oldDist[distNum];

                // Shift old distances
                for (int i = distNum; i > 0; i--)
                    _oldDist[i] = _oldDist[i - 1];
                _oldDist[0] = distance;

                uint lengthNumber = HuffmanDecoder.DecodeNumber(_inp, _tables.RD);
                int length = PackDef.LDecode[lengthNumber] + 2;
                int bits = PackDef.LBits[lengthNumber];
                if (bits > 0)
                {
                    length += (int)(_inp.GetBits() >> (16 - bits));
                    _inp.AddBits(bits);
                }

                _lastLength = length;
                destPtr = CopyString(result, destPtr, destSize, length, distance);
                continue;
            }

            if (number < 272)
            {
                // Short distance match
                int distance = PackDef.SDDecode[number - 263] + 1;
                int bits = PackDef.SDBits[number - 263];
                if (bits > 0)
                {
                    distance += (int)(_inp.GetBits() >> (16 - bits));
                    _inp.AddBits(bits);
                }

                InsertOldDist(distance);
                _lastLength = 2;
                destPtr = CopyString(result, destPtr, destSize, 2, distance);
                continue;
            }
        }

        return destPtr;
    }

    private void InsertOldDist(int distance)
    {
        _oldDist[3] = _oldDist[2];
        _oldDist[2] = _oldDist[1];
        _oldDist[1] = _oldDist[0];
        _oldDist[0] = distance;
    }

    private int CopyString(byte[] result, int destPtr, int destSize, int length, int distance)
    {
        int srcPos = (_unpPtr - distance) & _winMask;

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

    private bool ReadTables30()
    {
        // Align to byte boundary
        _inp.AddBits((8 - _inp.InBit) & 7);

        uint bitField = _inp.GetBits();

        // Check for PPM mode
        if ((bitField & 0x8000) != 0)
        {
            _blockType = BlockType.PPM;
            _ppm ??= new ModelPPM();

            // Initialize PPM decoder
            byte[] remainingData = new byte[_inp.InBuf.Length - _inp.InAddr];
            Array.Copy(_inp.InBuf, _inp.InAddr, remainingData, 0, remainingData.Length);

            int offset = 0;
            int escChar = _ppmEscChar;
            if (!_ppm.DecodeInit(remainingData, ref offset, ref escChar))
                return false;

            _ppmEscChar = escChar;
            _inp.InAddr += offset;
            return true;
        }

        // LZ mode
        _blockType = BlockType.LZ;
        _prevLowDist = 0;
        _lowDistRepCount = 0;

        // Check if we should reset old table
        if ((bitField & 0x4000) == 0)
            Array.Clear(_unpOldTable);

        _inp.AddBits(2);

        // Read bit lengths for the bit length alphabet
        byte[] bitLength = new byte[PackDef.BC30];
        for (int i = 0; i < PackDef.BC30; i++)
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

        HuffmanDecoder.MakeDecodeTables(bitLength, _tables.BD, PackDef.BC30);

        // Read the main table
        byte[] table = new byte[PackDef.HuffTableSize30];
        for (int i = 0; i < PackDef.HuffTableSize30;)
        {
            uint number = HuffmanDecoder.DecodeNumber(_inp, _tables.BD);

            if (number < 16)
            {
                table[i] = (byte)((number + _unpOldTable[i]) & 0xF);
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

                while (n-- > 0 && i < PackDef.HuffTableSize30)
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

                while (n-- > 0 && i < PackDef.HuffTableSize30)
                    table[i++] = 0;
            }
        }

        // Build decode tables for each alphabet
        HuffmanDecoder.MakeDecodeTables(table, _tables.LD, PackDef.NC30);

        byte[] ddTable = new byte[PackDef.DC30];
        Array.Copy(table, PackDef.NC30, ddTable, 0, PackDef.DC30);
        HuffmanDecoder.MakeDecodeTables(ddTable, _tables.DD, PackDef.DC30);

        byte[] lddTable = new byte[PackDef.LDC30];
        Array.Copy(table, PackDef.NC30 + PackDef.DC30, lddTable, 0, PackDef.LDC30);
        HuffmanDecoder.MakeDecodeTables(lddTable, _tables.LDD, PackDef.LDC30);

        byte[] rdTable = new byte[PackDef.RC30];
        Array.Copy(table, PackDef.NC30 + PackDef.DC30 + PackDef.LDC30, rdTable, 0, PackDef.RC30);
        HuffmanDecoder.MakeDecodeTables(rdTable, _tables.RD, PackDef.RC30);

        // Save old table for delta encoding
        Array.Copy(table, _unpOldTable, PackDef.HuffTableSize30);

        return true;
    }

    private bool ReadEndOfBlock()
    {
        uint bitField = _inp.GetBits();

        if ((bitField & 0x8000) != 0)
        {
            // New table follows
            _inp.AddBits(1);
            return ReadTables30();
        }
        else
        {
            // New file or end
            _inp.AddBits(2);
            return false;
        }
    }

    private void SkipVMCode()
    {
        // Skip VM code - not needed for comments
        uint firstByte = _inp.GetBits() >> 8;
        _inp.AddBits(8);

        int length = ((int)firstByte & 7) + 1;
        if (length == 7)
        {
            length = (int)((_inp.GetBits() >> 8) + 7);
            _inp.AddBits(8);
        }
        else if (length == 8)
        {
            length = (int)_inp.GetBits();
            _inp.AddBits(16);
        }

        // Skip the VM code bytes
        for (int i = 0; i < length; i++)
        {
            _inp.GetBits();
            _inp.AddBits(8);
        }
    }
}
