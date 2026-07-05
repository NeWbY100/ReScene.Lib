namespace ReScene.Core.Comparison;

/// <summary>
/// The interpreted value type of an EBML element, used to format leaf values for display.
/// </summary>
public enum EBMLValueType
{
    /// <summary>
    /// A container element whose payload is a sequence of child elements.
    /// </summary>
    Master,

    /// <summary>
    /// A big-endian unsigned integer.
    /// </summary>
    UnsignedInt,

    /// <summary>
    /// A big-endian two's-complement signed integer.
    /// </summary>
    SignedInt,

    /// <summary>
    /// A 4- or 8-byte IEEE 754 floating-point value.
    /// </summary>
    Float,

    /// <summary>
    /// A printable ASCII string.
    /// </summary>
    String,

    /// <summary>
    /// A UTF-8 string.
    /// </summary>
    Utf8,

    /// <summary>
    /// A date, stored as nanoseconds relative to 2001-01-01T00:00:00 UTC.
    /// </summary>
    Date,

    /// <summary>
    /// Raw binary data.
    /// </summary>
    Binary,

    /// <summary>
    /// An element of unknown semantics (treated as binary).
    /// </summary>
    Unknown
}
