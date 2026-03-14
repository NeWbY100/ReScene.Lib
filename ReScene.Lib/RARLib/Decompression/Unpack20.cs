namespace RARLib.Decompression;

/// <summary>
/// RAR 2.0 decompressor.
/// Ported from unrar unpack20.cpp - LZ compression only (no audio mode for comments).
/// </summary>
public class Unpack20
{
    // Distance decode tables for RAR 2.0 (48 entries) - unique to RAR 2.0
    private static readonly int[] DDecode =
    [
        0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192,
        256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288,
        16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 327680,
        393216, 458752, 524288, 589824, 655360, 720896, 786432, 851968, 917504, 983040
    ];

    private static readonly byte[] DBits =
    [
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
        15, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16
    ];

    private readonly BitInput _inp;
    private readonly DecodeTable _tableLD = new();  // Literal/Length
    private readonly DecodeTable _tableDD = new();  // Distance
    private readonly DecodeTable _tableRD = new();  // Repeat
    private readonly DecodeTable _tableBD = new();  // Bit length

    private readonly byte[] _unpOldTable = new byte[PackDef.NC20 + PackDef.DC20 + PackDef.RC20];

    private byte[] _window = null!;
    private int _winSize;
    private int _winMask;
    private int _unpPtr;

    private int _lastDist;
    private int _lastLength;
    private readonly int[] _oldDist = new int[4];
    private int _oldDistPtr;

    /// <summary>
    /// Creates a new RAR 2.0 unpacker.
    /// </summary>
    public Unpack20()
    {
        _inp = new BitInput();
    }

    /// <summary>
    /// Decompresses RAR 2.0 compressed data.
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

        // Initialize state
        Array.Clear(_oldDist);
        _oldDistPtr = 0;
        _lastDist = 0;
        _lastLength = 0;
        Array.Clear(_unpOldTable);

        // Set up input buffer
        _inp.SetBuffer(srcData);

        // Read Huffman tables
        if (!ReadTables20())
            return null;

        // Decompress
        byte[] result = new byte[destSize];
        int destPtr = 0;

        while (destPtr < destSize)
        {
            if (_inp.InAddr >= srcData.Length)
                break;

            uint number = HuffmanDecoder.DecodeNumber(_inp, _tableLD);

            if (number < 256)
            {
                // Literal byte
                _window[_unpPtr++] = (byte)number;
                _unpPtr &= _winMask;
                result[destPtr++] = (byte)number;
                continue;
            }

            if (number > 269)
            {
                // Length-distance pair
                int length = PackDef.LDecode[number - 270] + 3;
                int bits = PackDef.LBits[number - 270];
                if (bits > 0)
                {
                    length += (int)(_inp.GetBits() >> (16 - bits));
                    _inp.AddBits(bits);
                }

                uint distNumber = HuffmanDecoder.DecodeNumber(_inp, _tableDD);
                int distance = DDecode[distNumber] + 1;
                bits = DBits[distNumber];
                if (bits > 0)
                {
                    distance += (int)(_inp.GetBits() >> (16 - bits));
                    _inp.AddBits(bits);
                }

                // Length adjustment for long distances
                if (distance >= 0x2000)
                {
                    length++;
                    if (distance >= 0x40000)
                        length++;
                }

                destPtr = CopyString20(result, destPtr, destSize, length, distance);
                continue;
            }

            if (number == 269)
            {
                // Read new tables
                if (!ReadTables20())
                    break;
                continue;
            }

            if (number == 256)
            {
                // Repeat last length/distance
                destPtr = CopyString20(result, destPtr, destSize, _lastLength, _lastDist);
                continue;
            }

            if (number < 261)
            {
                // Use old distance
                int distance = _oldDist[(_oldDistPtr - (int)(number - 256)) & 3];
                uint lengthNumber = HuffmanDecoder.DecodeNumber(_inp, _tableRD);
                int length = PackDef.LDecode[lengthNumber] + 2;
                int bits = PackDef.LBits[lengthNumber];
                if (bits > 0)
                {
                    length += (int)(_inp.GetBits() >> (16 - bits));
                    _inp.AddBits(bits);
                }

                // Length adjustment for distances
                if (distance >= 0x101)
                {
                    length++;
                    if (distance >= 0x2000)
                    {
                        length++;
                        if (distance >= 0x40000)
                            length++;
                    }
                }

                destPtr = CopyString20(result, destPtr, destSize, length, distance);
                continue;
            }

            if (number < 270)
            {
                // Short distance
                int distance = PackDef.SDDecode[number - 261] + 1;
                int bits = PackDef.SDBits[number - 261];
                if (bits > 0)
                {
                    distance += (int)(_inp.GetBits() >> (16 - bits));
                    _inp.AddBits(bits);
                }

                destPtr = CopyString20(result, destPtr, destSize, 2, distance);
                continue;
            }
        }

        return result;
    }

    private int CopyString20(byte[] result, int destPtr, int destSize, int length, int distance)
    {
        _lastDist = distance;
        _oldDist[_oldDistPtr++] = distance;
        _oldDistPtr &= 3;
        _lastLength = length;

        // Copy from window
        int srcPos = (_unpPtr - distance) & _winMask;
        while (length-- > 0 && destPtr < destSize)
        {
            byte b = _window[srcPos++];
            srcPos &= _winMask;
            _window[_unpPtr++] = b;
            _unpPtr &= _winMask;
            result[destPtr++] = b;
        }

        return destPtr;
    }

    private bool ReadTables20()
    {
        // Align to byte boundary
        _inp.AddBits((8 - _inp.InBit) & 7);

        uint bitField = _inp.GetBits();

        // Check for audio block (bit 15) - we skip audio mode for comments
        bool audioBlock = (bitField & 0x8000) != 0;
        if (audioBlock)
        {
            // Audio mode not supported for comments
            return false;
        }

        // Check if we should reset old table (bit 14)
        if ((bitField & 0x4000) == 0)
            Array.Clear(_unpOldTable);

        _inp.AddBits(2);

        int tableSize = PackDef.NC20 + PackDef.DC20 + PackDef.RC20;

        // Read bit lengths for the bit length alphabet
        byte[] bitLength = new byte[PackDef.BC20];
        for (int i = 0; i < PackDef.BC20; i++)
        {
            bitLength[i] = (byte)(_inp.GetBits() >> 12);
            _inp.AddBits(4);
        }

        HuffmanDecoder.MakeDecodeTables(bitLength, _tableBD, PackDef.BC20);

        // Read the main table
        byte[] table = new byte[tableSize];
        for (int i = 0; i < tableSize;)
        {
            uint number = HuffmanDecoder.DecodeNumber(_inp, _tableBD);

            if (number < 16)
            {
                table[i] = (byte)((number + _unpOldTable[i]) & 0xF);
                i++;
            }
            else if (number == 16)
            {
                // Repeat previous
                int n = (int)((_inp.GetBits() >> 14) + 3);
                _inp.AddBits(2);

                if (i == 0)
                    return false; // Cannot repeat at first position

                while (n-- > 0 && i < tableSize)
                {
                    table[i] = table[i - 1];
                    i++;
                }
            }
            else if (number == 17)
            {
                // Zero run (short)
                int n = (int)((_inp.GetBits() >> 13) + 3);
                _inp.AddBits(3);

                while (n-- > 0 && i < tableSize)
                    table[i++] = 0;
            }
            else
            {
                // Zero run (long)
                int n = (int)((_inp.GetBits() >> 9) + 11);
                _inp.AddBits(7);

                while (n-- > 0 && i < tableSize)
                    table[i++] = 0;
            }
        }

        // Build decode tables
        HuffmanDecoder.MakeDecodeTables(table, _tableLD, PackDef.NC20);
        HuffmanDecoder.MakeDecodeTables(table.AsSpan(PackDef.NC20).ToArray(), _tableDD, PackDef.DC20);
        HuffmanDecoder.MakeDecodeTables(table.AsSpan(PackDef.NC20 + PackDef.DC20).ToArray(), _tableRD, PackDef.RC20);

        // Save old table
        Array.Copy(table, _unpOldTable, tableSize);

        return true;
    }
}
