namespace ReScene.RAR;

/// <summary>
/// Represents a single field within a RAR header, with its offset and raw/formatted values.
/// </summary>
public class RARHeaderField
{
    /// <summary>
    /// Field name (e.g., "Header CRC", "Flags", "Packed Size").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Byte offset from the start of the file.
    /// </summary>
    public long Offset
    {
        get; set;
    }

    /// <summary>
    /// Length in bytes.
    /// </summary>
    public int Length
    {
        get; set;
    }

    /// <summary>
    /// Raw bytes of this field.
    /// </summary>
    public ReadOnlyMemory<byte> RawBytes { get; set; }

    /// <summary>
    /// Formatted display value.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Additional description or decoded meaning.
    /// </summary>
    public string? Description
    {
        get; set;
    }

    /// <summary>
    /// Child fields (for nested structures like flags).
    /// </summary>
    public IList<RARHeaderField> Children { get; } = [];

    public override string ToString() => $"{Name}: {Value}";
}
