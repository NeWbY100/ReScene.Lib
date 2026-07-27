namespace ReScene.SRS;

/// <summary>
/// Options for SRS file creation.
/// </summary>
public class SRSCreationOptions
{
    /// <summary>
    /// Application name to embed in the SRS file. Defaults to the library's own name;
    /// applications should pass their own (ReScene Manager does).
    /// </summary>
    public string AppName { get; set; } = "ReScene.Lib";

    /// <summary>
    /// Optional path to the full "main" media file (e.g. the unpacked full
    /// movie) to verify the sample against. When set, the writer locates each
    /// track's signature inside this file and records the offset as
    /// <c>MatchOffset</c> in the SRS — mirroring the scene-tool behaviour
    /// (pyrescene's <c>-c</c> flag). When unset, MatchOffset stays at 0.
    /// </summary>
    public string? MainFilePath
    {
        get; set;
    }
}
