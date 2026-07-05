namespace ReScene.SRS;

/// <summary>SRS-injected block type identifiers embedded inside a FLAC stream.</summary>
internal enum FlacSrsBlockType : byte
{
    Srsf = 0x73,        // 's' — file-data block
    Srst = 0x74,        // 't' — track-data block
    Fingerprint = 0x75, // 'u' — fingerprint block
}
