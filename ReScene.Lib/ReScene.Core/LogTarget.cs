namespace ReScene.Core;

/// <summary>
/// Specifies the target log panel for displaying log messages in the UI.
/// </summary>
public enum LogTarget
{
    /// <summary>
    /// General system log messages.
    /// </summary>
    System,

    /// <summary>
    /// Log messages related to Phase 1 (comment block brute-force).
    /// </summary>
    Phase1,

    /// <summary>
    /// Log messages related to Phase 2 (full RAR brute-force).
    /// </summary>
    Phase2
}
