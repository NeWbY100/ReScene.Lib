namespace ReScene.SRR;

/// <summary>A stored/volume logical-name violation (spec §1a): source outside the release
/// root, an SFV entry escaping its directory, or a logical-name collision.</summary>
public sealed class SrrNameException : Exception
{
    public SrrNameException()
    {
    }

    public SrrNameException(string message) : base(message)
    {
    }

    public SrrNameException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
