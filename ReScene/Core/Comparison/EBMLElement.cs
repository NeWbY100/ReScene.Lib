namespace ReScene.Core.Comparison;

/// <summary>
/// A single parsed EBML element from an MKV/WebM file, with its position, sizes, and (for leaves)
/// a formatted value.
/// </summary>
public sealed class EBMLElement
{
    /// <summary>
    /// Gets the EBML element ID (marker bit preserved), e.g. <c>0x1A45DFA3</c>.
    /// </summary>
    public ulong ElementId
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the human-readable element name (e.g. "Segment", "TrackNumber").
    /// </summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>
    /// Gets the file offset of the first byte of the element ID.
    /// </summary>
    public long Position
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the number of bytes occupied by the element ID and the size VINT.
    /// </summary>
    public int HeaderSize
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the number of bytes of element data (excluding the header).
    /// </summary>
    public long DataSize
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the total size of the element including its header.
    /// </summary>
    public long TotalSize
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the interpreted value type of the element.
    /// </summary>
    public EBMLValueType ValueType
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the formatted leaf value, or <see langword="null"/> for master elements.
    /// </summary>
    public string? Value
    {
        get; internal set;
    }

    /// <summary>
    /// Gets the child elements (populated only for master elements).
    /// </summary>
    public IReadOnlyList<EBMLElement> Children => _children;

    internal List<EBMLElement> _children { get; } = [];
}
