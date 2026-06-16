namespace ReScene.RAR.Decompression;

/// <summary>
/// Shared "read tables" helpers for RAR 3.x and 5.x Huffman table decoding.
/// These are behavior-identical mechanical extractions from
/// <see cref="Unpack29"/> and <see cref="Unpack50"/>; they read the same bits
/// in the same order and produce the same arrays as the original inline code.
/// </summary>
internal static class HuffmanTableReader
{
    /// <summary>
    /// Reads the bit lengths for the bit length alphabet (Loop 1).
    /// Byte-identical between RAR 3.x and 5.x: <c>count</c> is 20 (BC30/BC) in
    /// both cases. Reads 4 bits per entry with a length==15 RLE escape for runs
    /// of zeros.
    /// </summary>
    /// <param name="inp">Bit input stream.</param>
    /// <param name="count">Number of bit-length codes (BC30 / BC).</param>
    /// <returns>The populated bit-length table.</returns>
    public static byte[] ReadBitLengthTable(BitInput inp, int count)
    {
        byte[] bitLength = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int length = (int)(inp.GetBits() >> 12);
            inp.AddBits(4);

            if (length == 15)
            {
                int zeroCount = (int)(inp.GetBits() >> 12);
                inp.AddBits(4);

                if (zeroCount == 0)
                {
                    bitLength[i] = 15;
                }
                else
                {
                    zeroCount += 2;
                    while (zeroCount-- > 0 && i < bitLength.Length)
                    {
                        bitLength[i++] = 0;
                    }

                    i--;
                }
            }
            else
            {
                bitLength[i] = (byte)length;
            }
        }

        return bitLength;
    }

    /// <summary>
    /// Builds the per-alphabet decode tables from the decoded main table (Loop 3).
    /// Structurally identical between RAR 3.x and 5.x; only the alphabet sizes
    /// differ, so they are passed as parameters. Slices the main <paramref name="table"/>
    /// at offsets <c>nc</c>, <c>nc+dc</c>, <c>nc+dc+ldc</c> and builds the LD/DD/LDD/RD
    /// decode tables.
    /// </summary>
    /// <param name="table">The fully decoded main bit-length table.</param>
    /// <param name="tables">The decode tables to populate.</param>
    /// <param name="nc">Literal/length alphabet size (NC30 / NC).</param>
    /// <param name="dc">Distance alphabet size (DC30 / DCB).</param>
    /// <param name="ldc">Low distance alphabet size (LDC30 / LDC).</param>
    /// <param name="rc">Repeat alphabet size (RC30 / RC).</param>
    public static void BuildDecodeTables(byte[] table, UnpackBlockTables tables, int nc, int dc, int ldc, int rc)
    {
        HuffmanDecoder.MakeDecodeTables(table, tables.LD, nc);

        byte[] ddTable = new byte[dc];
        Array.Copy(table, nc, ddTable, 0, dc);
        HuffmanDecoder.MakeDecodeTables(ddTable, tables.DD, dc);

        byte[] lddTable = new byte[ldc];
        Array.Copy(table, nc + dc, lddTable, 0, ldc);
        HuffmanDecoder.MakeDecodeTables(lddTable, tables.LDD, ldc);

        byte[] rdTable = new byte[rc];
        Array.Copy(table, nc + dc + ldc, rdTable, 0, rc);
        HuffmanDecoder.MakeDecodeTables(rdTable, tables.RD, rc);
    }
}
