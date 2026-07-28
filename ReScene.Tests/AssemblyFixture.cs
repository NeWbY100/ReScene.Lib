namespace ReScene.Tests;

/// <summary>A complete synthetic reconstruction scenario on disk.</summary>
internal sealed record AssemblyFixture(
    string SrrPath,
    IReadOnlyList<string> OriginalVolumePaths,   // byte-identity reference
    IReadOnlyList<string> OriginalVolumeNames,   // set selector (qualified where built so)
    string ProducedFirstVolumePath,              // the "rar output" carrier set
    IReadOnlyDictionary<string, string> ExpectedVolumeCrcs); // name -> CRC32 (as Manager expects)
