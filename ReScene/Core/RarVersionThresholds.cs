namespace ReScene.Core;

/// <summary>
/// Integer version-number boundaries that separate the major RAR archive formats.
/// The version numbers match the WinRAR installer directory naming convention (e.g., "winrar-700").
/// </summary>
internal static class RarVersionThresholds
{
    /// <summary>The first WinRAR version that generates RAR5 archives by default (500 = 5.00).</summary>
    public const int Rar5FormatMinimum = 500;

    /// <summary>The first WinRAR version that generates RAR7 archives by default (700 = 7.00).</summary>
    public const int Rar7FormatMinimum = 700;
}
