using ReScene.SRR;

namespace ReScene.Tests;

public class SRRFileParserTests : TempDirTestBase
{
    [Fact]
    public void Load_TruncatedRarFileBlockAtEof_DoesNotThrow()
    {
        // Regression: ParseRarFileBlock read the 2-byte name length with no bounds
        // check. A RARFile block (0x71) with headerSize 7 and no data at the very
        // end of the file passes all of Load's guards, then ReadUInt16 ran past EOF
        // and threw EndOfStreamException out of SRRFile.Load. Truncated / incomplete
        // SRR downloads are an explicitly supported scenario, so Load must degrade
        // gracefully rather than crash.
        byte[] header = new SRRTestDataBuilder().AddSRRHeader().Build();
        byte[] truncatedRarBlock =
        [
            0x71, 0x71, // CRC sentinel
            0x71,       // RARFile type
            0x00, 0x00, // flags (no LongBlock)
            0x07, 0x00, // headerSize = 7 (base only, no name-length field follows)
        ];
        byte[] data = [.. header, .. truncatedRarBlock];
        string path = Path.Combine(TempDir, "truncated.srr");
        File.WriteAllBytes(path, data);

        SRRFile? srr = null;
        Exception? ex = Record.Exception(() => srr = SRRFile.Load(path));

        Assert.Null(ex);
        Assert.NotNull(srr);
        Assert.NotNull(srr!.HeaderBlock);
        Assert.Empty(srr.RARFiles);
    }
}
