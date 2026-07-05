using System.Collections.Frozen;

namespace ReScene.Core.Comparison;

/// <summary>
/// Maps known Matroska/EBML element IDs to a display name and interpreted value type.
/// </summary>
internal static class EBMLElementRegistry
{
    private static readonly FrozenDictionary<ulong, (string Name, EBMLValueType Type)> _map = new Dictionary<ulong, (string Name, EBMLValueType Type)>
    {
        // EBML header
        [0x1A45DFA3] = ("EBML", EBMLValueType.Master),
        [0x4286] = ("EBMLVersion", EBMLValueType.UnsignedInt),
        [0x42F7] = ("EBMLReadVersion", EBMLValueType.UnsignedInt),
        [0x42F2] = ("EBMLMaxIDLength", EBMLValueType.UnsignedInt),
        [0x42F3] = ("EBMLMaxSizeLength", EBMLValueType.UnsignedInt),
        [0x4282] = ("DocType", EBMLValueType.String),
        [0x4287] = ("DocTypeVersion", EBMLValueType.UnsignedInt),
        [0x4285] = ("DocTypeReadVersion", EBMLValueType.UnsignedInt),

        // Segment
        [0x18538067] = ("Segment", EBMLValueType.Master),

        // SeekHead
        [0x114D9B74] = ("SeekHead", EBMLValueType.Master),
        [0x4DBB] = ("Seek", EBMLValueType.Master),
        [0x53AB] = ("SeekID", EBMLValueType.Binary),
        [0x53AC] = ("SeekPosition", EBMLValueType.UnsignedInt),

        // Info
        [0x1549A966] = ("Info", EBMLValueType.Master),
        [0x2AD7B1] = ("TimestampScale", EBMLValueType.UnsignedInt),
        [0x4489] = ("Duration", EBMLValueType.Float),
        [0x4D80] = ("MuxingApp", EBMLValueType.Utf8),
        [0x5741] = ("WritingApp", EBMLValueType.Utf8),
        [0x73A4] = ("SegmentUUID", EBMLValueType.Binary),
        [0x7384] = ("SegmentFilename", EBMLValueType.Utf8),
        [0x7BA9] = ("Title", EBMLValueType.Utf8),
        [0x4461] = ("DateUTC", EBMLValueType.Date),

        // Tracks
        [0x1654AE6B] = ("Tracks", EBMLValueType.Master),
        [0xAE] = ("TrackEntry", EBMLValueType.Master),
        [0xD7] = ("TrackNumber", EBMLValueType.UnsignedInt),
        [0x73C5] = ("TrackUID", EBMLValueType.UnsignedInt),
        [0x83] = ("TrackType", EBMLValueType.UnsignedInt),
        [0xB9] = ("FlagEnabled", EBMLValueType.UnsignedInt),
        [0x88] = ("FlagDefault", EBMLValueType.UnsignedInt),
        [0x55AA] = ("FlagForced", EBMLValueType.UnsignedInt),
        [0x9C] = ("FlagLacing", EBMLValueType.UnsignedInt),
        [0x86] = ("CodecID", EBMLValueType.String),
        [0x258688] = ("CodecName", EBMLValueType.Utf8),
        [0x22B59C] = ("Language", EBMLValueType.String),
        [0x22B59D] = ("LanguageBCP47", EBMLValueType.String),
        [0x23E383] = ("DefaultDuration", EBMLValueType.UnsignedInt),
        [0x536E] = ("Name", EBMLValueType.Utf8),
        [0x63A2] = ("CodecPrivate", EBMLValueType.Binary),
        [0xAA] = ("CodecDecodeAll", EBMLValueType.UnsignedInt),
        [0x6DE7] = ("MinCache", EBMLValueType.UnsignedInt),
        [0x6DF8] = ("MaxCache", EBMLValueType.UnsignedInt),
        [0x55EE] = ("MaxBlockAdditionID", EBMLValueType.UnsignedInt),
        [0x56AA] = ("CodecDelay", EBMLValueType.UnsignedInt),
        [0x56BB] = ("SeekPreRoll", EBMLValueType.UnsignedInt),
        [0x23314F] = ("TrackTimestampScale", EBMLValueType.Float),
        [0x234E7A] = ("DefaultDecodedFieldDuration", EBMLValueType.UnsignedInt),
        [0x6FAB] = ("TrackOverlay", EBMLValueType.UnsignedInt),
        [0x7446] = ("AttachmentLink", EBMLValueType.UnsignedInt),
        [0x55AB] = ("FlagHearingImpaired", EBMLValueType.UnsignedInt),
        [0x55AC] = ("FlagVisualImpaired", EBMLValueType.UnsignedInt),
        [0x55AD] = ("FlagTextDescriptions", EBMLValueType.UnsignedInt),
        [0x55AE] = ("FlagOriginal", EBMLValueType.UnsignedInt),
        [0x55AF] = ("FlagCommentary", EBMLValueType.UnsignedInt),

        // Video
        [0xE0] = ("Video", EBMLValueType.Master),
        [0xB0] = ("PixelWidth", EBMLValueType.UnsignedInt),
        [0xBA] = ("PixelHeight", EBMLValueType.UnsignedInt),
        [0x54B0] = ("DisplayWidth", EBMLValueType.UnsignedInt),
        [0x54BA] = ("DisplayHeight", EBMLValueType.UnsignedInt),
        [0x9A] = ("FlagInterlaced", EBMLValueType.UnsignedInt),
        [0x9D] = ("FieldOrder", EBMLValueType.UnsignedInt),
        [0x53B8] = ("StereoMode", EBMLValueType.UnsignedInt),
        [0x53C0] = ("AlphaMode", EBMLValueType.UnsignedInt),
        [0x54B2] = ("DisplayUnit", EBMLValueType.UnsignedInt),
        [0x54B3] = ("AspectRatioType", EBMLValueType.UnsignedInt),

        // Video > Colour (incl. HDR mastering metadata)
        [0x55B0] = ("Colour", EBMLValueType.Master),
        [0x55B1] = ("MatrixCoefficients", EBMLValueType.UnsignedInt),
        [0x55B2] = ("BitsPerChannel", EBMLValueType.UnsignedInt),
        [0x55B3] = ("ChromaSubsamplingHorz", EBMLValueType.UnsignedInt),
        [0x55B4] = ("ChromaSubsamplingVert", EBMLValueType.UnsignedInt),
        [0x55B5] = ("CbSubsamplingHorz", EBMLValueType.UnsignedInt),
        [0x55B6] = ("CbSubsamplingVert", EBMLValueType.UnsignedInt),
        [0x55B7] = ("ChromaSitingHorz", EBMLValueType.UnsignedInt),
        [0x55B8] = ("ChromaSitingVert", EBMLValueType.UnsignedInt),
        [0x55B9] = ("Range", EBMLValueType.UnsignedInt),
        [0x55BA] = ("TransferCharacteristics", EBMLValueType.UnsignedInt),
        [0x55BB] = ("Primaries", EBMLValueType.UnsignedInt),
        [0x55BC] = ("MaxCLL", EBMLValueType.UnsignedInt),
        [0x55BD] = ("MaxFALL", EBMLValueType.UnsignedInt),
        [0x55D0] = ("MasteringMetadata", EBMLValueType.Master),
        [0x55D1] = ("PrimaryRChromaticityX", EBMLValueType.Float),
        [0x55D2] = ("PrimaryRChromaticityY", EBMLValueType.Float),
        [0x55D3] = ("PrimaryGChromaticityX", EBMLValueType.Float),
        [0x55D4] = ("PrimaryGChromaticityY", EBMLValueType.Float),
        [0x55D5] = ("PrimaryBChromaticityX", EBMLValueType.Float),
        [0x55D6] = ("PrimaryBChromaticityY", EBMLValueType.Float),
        [0x55D7] = ("WhitePointChromaticityX", EBMLValueType.Float),
        [0x55D8] = ("WhitePointChromaticityY", EBMLValueType.Float),
        [0x55D9] = ("LuminanceMax", EBMLValueType.Float),
        [0x55DA] = ("LuminanceMin", EBMLValueType.Float),

        // Audio
        [0xE1] = ("Audio", EBMLValueType.Master),
        [0xB5] = ("SamplingFrequency", EBMLValueType.Float),
        [0x78B5] = ("OutputSamplingFrequency", EBMLValueType.Float),
        [0x9F] = ("Channels", EBMLValueType.UnsignedInt),
        [0x6264] = ("BitDepth", EBMLValueType.UnsignedInt),

        // Content encodings
        [0x6D80] = ("ContentEncodings", EBMLValueType.Master),
        [0x6240] = ("ContentEncoding", EBMLValueType.Master),
        [0x5031] = ("ContentEncodingOrder", EBMLValueType.UnsignedInt),
        [0x5032] = ("ContentEncodingScope", EBMLValueType.UnsignedInt),
        [0x5033] = ("ContentEncodingType", EBMLValueType.UnsignedInt),
        [0x5034] = ("ContentCompression", EBMLValueType.Master),
        [0x4254] = ("ContentCompAlgo", EBMLValueType.UnsignedInt),
        [0x4255] = ("ContentCompSettings", EBMLValueType.Binary),
        [0x5035] = ("ContentEncryption", EBMLValueType.Master),
        [0x47E1] = ("ContentEncAlgo", EBMLValueType.UnsignedInt),
        [0x47E2] = ("ContentEncKeyID", EBMLValueType.Binary),

        // Cluster
        [0x1F43B675] = ("Cluster", EBMLValueType.Master),
        [0xE7] = ("Timestamp", EBMLValueType.UnsignedInt),
        [0xA3] = ("SimpleBlock", EBMLValueType.Binary),
        [0xA0] = ("BlockGroup", EBMLValueType.Master),
        [0xA1] = ("Block", EBMLValueType.Binary),
        [0x9B] = ("BlockDuration", EBMLValueType.UnsignedInt),
        [0xFB] = ("ReferenceBlock", EBMLValueType.SignedInt),
        [0xA4] = ("CodecState", EBMLValueType.Binary),
        [0x75A2] = ("DiscardPadding", EBMLValueType.SignedInt),
        [0x75A1] = ("BlockAdditions", EBMLValueType.Master),
        [0xA6] = ("BlockMore", EBMLValueType.Master),
        [0xEE] = ("BlockAddID", EBMLValueType.UnsignedInt),
        [0xA5] = ("BlockAdditional", EBMLValueType.Binary),

        // Cues
        [0x1C53BB6B] = ("Cues", EBMLValueType.Master),
        [0xBB] = ("CuePoint", EBMLValueType.Master),
        [0xB3] = ("CueTime", EBMLValueType.UnsignedInt),
        [0xB7] = ("CueTrackPositions", EBMLValueType.Master),
        [0xF7] = ("CueTrack", EBMLValueType.UnsignedInt),
        [0xF1] = ("CueClusterPosition", EBMLValueType.UnsignedInt),
        [0xF0] = ("CueRelativePosition", EBMLValueType.UnsignedInt),
        [0xB2] = ("CueDuration", EBMLValueType.UnsignedInt),
        [0x5378] = ("CueBlockNumber", EBMLValueType.UnsignedInt),

        // Chapters
        [0x1043A770] = ("Chapters", EBMLValueType.Master),
        [0x45B9] = ("EditionEntry", EBMLValueType.Master),
        [0x45BC] = ("EditionUID", EBMLValueType.UnsignedInt),
        [0x45BD] = ("EditionFlagHidden", EBMLValueType.UnsignedInt),
        [0x45DB] = ("EditionFlagDefault", EBMLValueType.UnsignedInt),
        [0x45DD] = ("EditionFlagOrdered", EBMLValueType.UnsignedInt),
        [0xB6] = ("ChapterAtom", EBMLValueType.Master),
        [0x73C4] = ("ChapterUID", EBMLValueType.UnsignedInt),
        [0x91] = ("ChapterTimeStart", EBMLValueType.UnsignedInt),
        [0x92] = ("ChapterTimeEnd", EBMLValueType.UnsignedInt),
        [0x98] = ("ChapterFlagHidden", EBMLValueType.UnsignedInt),
        [0x4598] = ("ChapterFlagEnabled", EBMLValueType.UnsignedInt),
        [0x80] = ("ChapterDisplay", EBMLValueType.Master),
        [0x85] = ("ChapString", EBMLValueType.Utf8),
        [0x437C] = ("ChapLanguage", EBMLValueType.String),
        [0x437E] = ("ChapCountry", EBMLValueType.String),

        // Tags
        [0x1254C367] = ("Tags", EBMLValueType.Master),
        [0x7373] = ("Tag", EBMLValueType.Master),
        [0x63C0] = ("Targets", EBMLValueType.Master),
        [0x68CA] = ("TargetTypeValue", EBMLValueType.UnsignedInt),
        [0x63CA] = ("TargetType", EBMLValueType.String),
        [0x67C8] = ("SimpleTag", EBMLValueType.Master),
        [0x45A3] = ("TagName", EBMLValueType.Utf8),
        [0x4487] = ("TagString", EBMLValueType.Utf8),
        [0x447A] = ("TagLanguage", EBMLValueType.String),

        // Attachments
        [0x1941A469] = ("Attachments", EBMLValueType.Master),
        [0x61A7] = ("AttachedFile", EBMLValueType.Master),
        [0x466E] = ("FileName", EBMLValueType.Utf8),
        [0x4660] = ("FileMimeType", EBMLValueType.String),
        [0x465C] = ("FileData", EBMLValueType.Binary),
        [0x46AE] = ("FileUID", EBMLValueType.UnsignedInt),

        // Misc
        [0xEC] = ("Void", EBMLValueType.Binary),
        [0xBF] = ("CRC-32", EBMLValueType.Binary),
    }.ToFrozenDictionary();

    /// <summary>
    /// Looks up the display name and value type for an EBML element ID. Unknown IDs return a
    /// generated name and the <see cref="EBMLValueType.Binary"/> type.
    /// </summary>
    /// <param name="id">
    /// The EBML element ID (with marker bit preserved).
    /// </param>
    /// <returns>
    /// The element's name and interpreted value type.
    /// </returns>
    public static (string Name, EBMLValueType Type) Lookup(ulong id)
    {
        if (_map.TryGetValue(id, out (string Name, EBMLValueType Type) entry))
        {
            return entry;
        }

        return ($"Unknown (0x{id:X})", EBMLValueType.Binary);
    }
}
