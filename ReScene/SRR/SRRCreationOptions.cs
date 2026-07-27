namespace ReScene.SRR;

/// <summary>
/// Options for SRR file creation.
/// </summary>
public class SRRCreationOptions
{
    /// <summary>
    /// Application name to embed in the SRR header. Defaults to the library's own name;
    /// applications should pass their own (ReScene Manager does).
    /// </summary>
    public string? AppName { get; set; } = "ReScene.Lib";

    /// <summary>
    /// If false, reject compressed RAR volumes (method != Store).
    /// </summary>
    public bool AllowCompressed { get; set; } = true;

    /// <summary>
    /// Whether to compute and store OSO hashes for archived files.
    /// </summary>
    public bool ComputeOSOHashes
    {
        get; set;
    }

    /// <summary>
    /// Whether to generate a languages.diz stored file from VobSub .idx files in the archive.
    /// </summary>
    public bool GenerateLanguagesDiz
    {
        get; set;
    }
}
