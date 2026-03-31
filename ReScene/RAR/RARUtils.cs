using System.Globalization;
using System.Text;
using Force.Crc32;

namespace ReScene.RAR;

/// <summary>
/// Utility methods for RAR format handling.
/// </summary>
public static class RARUtils
{
    private static readonly Encoding RarNameEncoding = GetRarNameEncoding();

    /// <summary>
    /// Dictionary sizes in KB indexed by the 3-bit flag value.
    /// </summary>
    public static readonly int[] DictionarySizes = [64, 128, 256, 512, 1024, 2048, 4096, 0];

    #region CRC Validation

    /// <summary>
    /// Calculates the RAR 4.x header CRC (lower 16 bits of CRC-32).
    /// The CRC is calculated over the header bytes starting from the type field (skipping the 2-byte CRC).
    /// </summary>
    /// <param name="headerBytes">Complete header bytes including CRC field</param>
    /// <returns>16-bit CRC value</returns>
    public static ushort CalculateHeaderCrc(byte[] headerBytes)
    {
        if (headerBytes.Length < 3)
        {
            return 0;
        }

        // Skip the first 2 bytes (CRC field) and calculate CRC over the rest
        uint crc32 = Crc32Algorithm.Compute(headerBytes, 2, headerBytes.Length - 2);
        return (ushort)(crc32 & 0xFFFF);
    }

    /// <summary>
    /// Validates a RAR 4.x header CRC.
    /// </summary>
    /// <param name="storedCrc">CRC value stored in the header</param>
    /// <param name="headerBytes">Complete header bytes including CRC field</param>
    /// <returns>True if CRC matches, false otherwise</returns>
    public static bool ValidateHeaderCrc(ushort storedCrc, byte[] headerBytes)
    {
        return storedCrc == CalculateHeaderCrc(headerBytes);
    }

    #endregion

    #region DOS Date/Time Conversion

    /// <summary>
    /// Converts a DOS date/time value to DateTime.
    /// </summary>
    /// <param name="dosDate">DOS date/time packed value</param>
    /// <returns>DateTime or null if invalid</returns>
    public static DateTime? DosDateToDateTime(uint dosDate)
    {
        if (dosDate == 0)
        {
            return null;
        }

        uint datePart = (dosDate >> 16) & 0xFFFF;
        uint timePart = dosDate & 0xFFFF;

        int day = (int)(datePart & 0x1F);
        int month = (int)((datePart >> 5) & 0x0F);
        const int dosEpochYear = 1980;
        int year = (int)((datePart >> 9) & 0x7F) + dosEpochYear;

        int second = (int)((timePart & 0x1F) * 2);
        int minute = (int)((timePart >> 5) & 0x3F);
        int hour = (int)((timePart >> 11) & 0x1F);

        try
        {
            return new DateTime(year, month, day, hour, minute, second);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Filename Decoding

    /// <summary>
    /// Decodes a RAR filename from raw bytes, handling Unicode encoding if present.
    /// </summary>
    /// <param name="nameBytes">Raw filename bytes</param>
    /// <param name="hasUnicode">True if LHD_UNICODE flag is set</param>
    /// <returns>Decoded filename or null if invalid</returns>
    public static string? DecodeFileName(byte[] nameBytes, bool hasUnicode)
    {
        if (nameBytes.Length == 0)
        {
            return null;
        }

        if (hasUnicode)
        {
            int nullIndex = Array.IndexOf(nameBytes, (byte)0);
            if (nullIndex >= 0)
            {
                byte[] stdName = nameBytes[..nullIndex];
                byte[] encData = nameBytes[(nullIndex + 1)..];
                if (encData.Length > 0)
                {
                    return DecodeRarUnicode(stdName, encData);
                }

                if (stdName.Length > 0)
                {
                    return RarNameEncoding.GetString(stdName);
                }
            }

            try
            {
                return Encoding.UTF8.GetString(nameBytes);
            }
            catch (DecoderFallbackException)
            {
                return RarNameEncoding.GetString(nameBytes);
            }
        }

        return RarNameEncoding.GetString(nameBytes);
    }

    /// <summary>
    /// Decodes RAR's custom Unicode encoding for filenames.
    /// </summary>
    private static string DecodeRarUnicode(byte[] stdName, byte[] encData)
    {
        if (encData.Length == 0)
        {
            return RarNameEncoding.GetString(stdName);
        }

        List<byte> output = new(encData.Length * 2);
        int pos = 0;
        int encPos = 0;
        byte hi = encData[encPos++];
        int flagBits = 0;
        byte flags = 0;

        while (encPos < encData.Length)
        {
            if (flagBits == 0)
            {
                flags = encData[encPos++];
                flagBits = 8;
            }

            flagBits -= 2;
            int t = (flags >> flagBits) & 3;

            switch (t)
            {
                case 0:
                    Put(output, EncByte(encData, ref encPos), 0, ref pos);
                    break;
                case 1:
                    Put(output, EncByte(encData, ref encPos), hi, ref pos);
                    break;
                case 2:
                    Put(output, EncByte(encData, ref encPos), EncByte(encData, ref encPos), ref pos);
                    break;
                default:
                    byte n = EncByte(encData, ref encPos);
                    if ((n & 0x80) != 0)
                    {
                        byte c = EncByte(encData, ref encPos);
                        int count = (n & 0x7f) + 2;
                        for (int i = 0; i < count; i++)
                        {
                            byte lo = (byte)((StdByte(stdName, pos) + c) & 0xFF);
                            Put(output, lo, hi, ref pos);
                        }
                    }
                    else
                    {
                        int count = n + 2;
                        for (int i = 0; i < count; i++)
                        {
                            byte lo = StdByte(stdName, pos);
                            Put(output, lo, 0, ref pos);
                        }
                    }

                    break;
            }
        }

        return Encoding.Unicode.GetString(output.ToArray());
    }

    private static byte EncByte(byte[] data, ref int index)
    {
        if (index >= data.Length)
        {
            return 0;
        }

        return data[index++];
    }

    private static byte StdByte(byte[] data, int index)
    {
        if (index >= data.Length)
        {
            return 0;
        }

        return data[index];
    }

    private static void Put(List<byte> output, byte lo, byte hi, ref int pos)
    {
        output.Add(lo);
        output.Add(hi);
        pos++;
    }

    private static Encoding GetRarNameEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
        catch (NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    #endregion

    #region Dictionary Size

    /// <summary>
    /// Gets the dictionary size in KB from file flags.
    /// </summary>
    /// <param name="flags">File header flags</param>
    /// <returns>Dictionary size in KB, or 0 if directory entry</returns>
    public static int GetDictionarySize(RARFileFlags flags)
    {
        int dictIndex = ((ushort)flags & RARFlagMasks.DictionarySizeMask) >> RARFlagMasks.DictionarySizeShift;
        return dictIndex < DictionarySizes.Length ? DictionarySizes[dictIndex] : 0;
    }

    /// <summary>
    /// Checks if the flags indicate a directory entry.
    /// </summary>
    /// <param name="flags">File header flags</param>
    /// <returns>True if directory</returns>
    public static bool IsDirectory(RARFileFlags flags)
    {
        return ((ushort)flags & RARFlagMasks.DictionarySizeMask) == (ushort)RARFileFlags.Directory;
    }

    #endregion

    #region SFX Support

    private static readonly byte[] Rar4Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] Rar5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    /// <summary>
    /// Scans a stream for the RAR marker signature (RAR4 or RAR5).
    /// Used for finding the RAR archive within SFX (self-extracting) executables.
    /// </summary>
    /// <param name="stream">The stream to scan.</param>
    /// <param name="maxScanSize">Maximum number of bytes to scan (default 1 MB).</param>
    /// <returns>The byte offset of the RAR marker, or -1 if not found.</returns>
    private const long DefaultMaxScanSize = 1024 * 1024; // 1 MB

    public static long FindRarMarkerOffset(Stream stream, long maxScanSize = DefaultMaxScanSize)
    {
        long savedPos = stream.Position;
        stream.Position = 0;

        int toRead = (int)Math.Min(maxScanSize, stream.Length);
        byte[] buffer = new byte[toRead];
        int bytesRead = stream.ReadAtLeast(buffer, toRead, throwOnEndOfStream: false);
        stream.Position = savedPos;

        // Scan for RAR5 marker first (8 bytes, superset of RAR4's first 7)
        for (int i = 0; i <= bytesRead - 8; i++)
        {
            if (buffer[i] == 0x52 && buffer[i + 1] == 0x61 &&
                buffer[i + 2] == 0x72 && buffer[i + 3] == 0x21 &&
                buffer[i + 4] == 0x1A && buffer[i + 5] == 0x07)
            {
                // Check RAR5 (byte 6 = 0x01, byte 7 = 0x00)
                if (buffer[i + 6] == 0x01 && buffer[i + 7] == 0x00)
                {
                    return i;
                }

                // Check RAR4 (byte 6 = 0x00)
                if (buffer[i + 6] == 0x00)
                {
                    return i;
                }
            }
        }

        // Check the last position that could be RAR4 but not RAR5
        if (bytesRead >= 7 && bytesRead - 7 > bytesRead - 8)
        {
            int i = bytesRead - 7;
            if (buffer[i] == 0x52 && buffer[i + 1] == 0x61 &&
                buffer[i + 2] == 0x72 && buffer[i + 3] == 0x21 &&
                buffer[i + 4] == 0x1A && buffer[i + 5] == 0x07 &&
                buffer[i + 6] == 0x00)
                return i;
        }

        return -1;
    }

    #endregion
}
