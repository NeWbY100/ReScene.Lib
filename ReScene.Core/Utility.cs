namespace ReScene.Core;

/// <summary>
/// Provides general utility methods for the application.
/// </summary>
public static class Utility
{
    private static readonly string[] SizeUnits = ["b", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"];

    /// <summary>
    /// Converts a byte count to a human-readable size string using binary (IEC) units.
    /// </summary>
    /// <param name="bytes">The number of bytes to format.</param>
    /// <returns>A formatted string like "1.5 MiB" or "256 KiB".</returns>
    public static string GetUserFriendlySizeString(long bytes)
    {
        int index = 0;
        double value = bytes;
        while (value > 1024)
        {
            value /= 1024;
            index++;
        }

        return $"{value} {SizeUnits[index]}";
    }
}
