namespace RARLib.Decompression;

/// <summary>
/// Huffman decoding functions for RAR decompression.
/// Ported from unrar unpack.cpp and unpackinline.cpp.
/// </summary>
public static class HuffmanDecoder
{
    /// <summary>
    /// Builds decode tables from a bit length table.
    /// Ported from Unpack::MakeDecodeTables in unpack.cpp.
    /// </summary>
    /// <param name="lengthTable">Bit lengths for each symbol</param>
    /// <param name="dec">Decode table to populate</param>
    /// <param name="size">Size of the alphabet</param>
    public static void MakeDecodeTables(byte[] lengthTable, DecodeTable dec, int size)
    {
        // Size of alphabet and DecodePos array
        dec.MaxNum = size;

        // Calculate how many entries for every bit length
        int[] lengthCount = new int[16];
        for (int i = 0; i < size; i++)
        {
            lengthCount[lengthTable[i] & 0xF]++;
        }

        // We must not count zero length codes
        lengthCount[0] = 0;

        // Clear the DecodeNum array
        Array.Clear(dec.DecodeNum, 0, size);

        // Initialize entry for zero length code
        dec.DecodePos[0] = 0;

        // Start code for bit length 1 is 0
        dec.DecodeLen[0] = 0;

        // Right aligned upper limit code for current bit length
        uint upperLimit = 0;

        for (int i = 1; i < 16; i++)
        {
            // Adjust the upper limit code
            upperLimit += (uint)lengthCount[i];

            // Left aligned upper limit code
            uint leftAligned = upperLimit << (16 - i);

            // Prepare the upper limit code for next bit length
            upperLimit *= 2;

            // Store the left aligned upper limit code
            dec.DecodeLen[i] = leftAligned;

            // Every item contains the sum of all preceding items
            dec.DecodePos[i] = dec.DecodePos[i - 1] + (uint)lengthCount[i - 1];
        }

        // Prepare copy of DecodePos for modification
        uint[] copyDecodePos = new uint[16];
        Array.Copy(dec.DecodePos, copyDecodePos, 16);

        // For every bit length in the table
        for (int i = 0; i < size; i++)
        {
            byte curBitLength = (byte)(lengthTable[i] & 0xF);
            if (curBitLength != 0)
            {
                // Last position in code list for current bit length
                uint lastPos = copyDecodePos[curBitLength];

                // Prepare the decode table
                dec.DecodeNum[lastPos] = (ushort)i;

                // Move to next position for this bit length
                copyDecodePos[curBitLength]++;
            }
        }

        // Define the number of bits to process in quick mode
        dec.QuickBits = size switch
        {
            PackDef.NC or PackDef.NC20 or PackDef.NC30 => PackDef.MaxQuickDecodeBits,
            _ => PackDef.MaxQuickDecodeBits > 3 ? PackDef.MaxQuickDecodeBits - 3 : 0
        };

        // Size of tables for quick mode
        int quickDataSize = 1 << dec.QuickBits;

        // Current bit length, start from 1
        int curBitLen = 1;

        // For every right aligned bit string supporting quick decoding
        for (int code = 0; code < quickDataSize; code++)
        {
            // Left align the current code
            uint bitField = (uint)code << (16 - dec.QuickBits);

            // Find upper limit and adjust bit length
            while (curBitLen < dec.DecodeLen.Length && bitField >= dec.DecodeLen[curBitLen])
            {
                curBitLen++;
            }

            // Translation of right aligned bit string to bit length
            dec.QuickLen[code] = (byte)curBitLen;

            // Calculate distance from start code for current bit length
            uint dist = bitField - dec.DecodeLen[curBitLen - 1];

            // Right align the distance
            dist >>= (16 - curBitLen);

            // Calculate position in code list
            uint pos;
            if (curBitLen < dec.DecodePos.Length &&
                (pos = dec.DecodePos[curBitLen] + dist) < (uint)size)
            {
                // Define code to alphabet number translation
                dec.QuickNum[code] = dec.DecodeNum[pos];
            }
            else
            {
                // For empty or corrupt tables
                dec.QuickNum[code] = 0;
            }
        }
    }

    /// <summary>
    /// Decodes a Huffman-encoded number.
    /// Ported from Unpack::DecodeNumber in unpackinline.cpp.
    /// </summary>
    /// <param name="inp">Bit input stream</param>
    /// <param name="dec">Decode table to use</param>
    /// <returns>Decoded symbol number</returns>
    public static uint DecodeNumber(BitInput inp, DecodeTable dec)
    {
        // Left aligned 15 bit length raw bit field
        uint bitField = inp.GetBits() & 0xFFFE;

        // Try quick decode first
        if (bitField < dec.DecodeLen[dec.QuickBits])
        {
            uint code = bitField >> (16 - dec.QuickBits);
            inp.AddBits(dec.QuickLen[code]);
            return dec.QuickNum[code];
        }

        // Detect the real bit length for current code
        int bits = 15;
        for (int i = dec.QuickBits + 1; i < 15; i++)
        {
            if (bitField < dec.DecodeLen[i])
            {
                bits = i;
                break;
            }
        }

        inp.AddBits(bits);

        // Calculate distance from start code for current bit length
        uint dist = bitField - dec.DecodeLen[bits - 1];

        // Right align the distance
        dist >>= (16 - bits);

        // Calculate position in code list
        uint pos = dec.DecodePos[bits] + dist;

        // Out of bounds safety check
        if (pos >= dec.MaxNum)
        {
            pos = 0;
        }

        // Convert position in code list to position in alphabet
        return dec.DecodeNum[pos];
    }
}
