using System.Text;
using Force.Crc32;
using ReScene.Core.Cryptography;

namespace ReScene.Tests;

/// <summary>
/// Known-answer and edge-case tests for the file-hashing primitives in
/// <see cref="ReScene.Core.Cryptography"/>. These pin the exact digest format
/// (lowercase hex) and prove that streaming/block accumulation, cancellation,
/// missing-file handling, and shared-instance reset all behave correctly.
/// </summary>
public class CryptoTests : TempDirTestBase
{
    private string WriteBytes(string name, byte[] content)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // CRC-32 standard check value: the 9 ASCII bytes "123456789" hash to 0xCBF43926.
    // Pins both the algorithm and the lowercase "x8" formatting the source uses.
    [Fact]
    public void CRC32_StandardCheckVector_Returns_cbf43926()
    {
        string path = WriteBytes("check.bin", Encoding.ASCII.GetBytes("123456789"));

        string result = CRC32.Calculate(path);

        Assert.Equal("cbf43926", result);
    }

    // An empty file produces a zero CRC, formatted as a full 8-char lowercase hex string.
    [Fact]
    public void CRC32_EmptyFile_Returns_00000000()
    {
        string path = WriteBytes("empty.bin", Array.Empty<byte>());

        string result = CRC32.Calculate(path);

        Assert.Equal("00000000", result);
    }

    // A multi-megabyte file spans several 1 MiB read blocks. The streamed result must
    // equal a single-shot Crc32 over the identical buffer, proving block accumulation
    // (Crc32Algorithm.Append across reads) is buffer-size independent.
    [Fact]
    public void CRC32_MultiBlockFile_MatchesSingleShotOverSameBuffer()
    {
        // ~3.5 MiB of a repeating, non-trivial pattern that straddles the 1 MiB boundary.
        byte[] buffer = new byte[(1024 * 1024 * 3) + (1024 * 512) + 7];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)((i * 31) + (i >> 8));
        }

        string path = WriteBytes("big.bin", buffer);

        string streamed = CRC32.Calculate(path);
        string oneShot = Crc32Algorithm.Compute(buffer).ToString("x8");

        Assert.Equal(oneShot, streamed);
    }

    // A path that does not exist must throw FileNotFoundException (checked before opening).
    [Fact]
    public void CRC32_NonExistentPath_ThrowsFileNotFound()
    {
        string missing = Path.Combine(TempDir, "does_not_exist.bin");

        Assert.Throws<FileNotFoundException>(() => CRC32.Calculate(missing));
    }

    // The cancellation check is the first statement inside the read loop, so a pre-cancelled
    // token throws OperationCanceledException on the first iteration of any NON-EMPTY file
    // (size is irrelevant; an empty file would skip the loop body and not throw).
    [Fact]
    public void CRC32_PreCancelledToken_OnNonEmptyFile_ThrowsOperationCanceled()
    {
        string path = WriteBytes("cancel.bin", new byte[64]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => CRC32.Calculate(path, null, cts.Token));
    }

    // SHA-1 standard check vector: "abc" hashes to a9993e364706816aba3e25717850c26c9cd0d89d.
    // Pins the lowercase 40-char hex output produced via ByteArrayToHexViaLookup32.
    [Fact]
    public void SHA1_StandardCheckVector_Returns_abc_Digest()
    {
        string path = WriteBytes("abc.bin", Encoding.ASCII.GetBytes("abc"));

        string result = SHA1.Calculate(path);

        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", result);
    }

    // The canonical SHA-1 of an empty input.
    [Fact]
    public void SHA1_EmptyFile_Returns_EmptyDigest()
    {
        string path = WriteBytes("empty.sha1.bin", Array.Empty<byte>());

        string result = SHA1.Calculate(path);

        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", result);
    }

    // SHA1.Calculate uses a single shared HashAlgorithm instance guarded by a lock.
    // Two sequential calls over different files must each yield the correct digest,
    // proving ComputeHash resets the shared instance between calls (no carry-over).
    [Fact]
    public void SHA1_SequentialCalls_OnDifferentFiles_EachReturnCorrectDigest()
    {
        string abcPath = WriteBytes("seq_abc.bin", Encoding.ASCII.GetBytes("abc"));
        string emptyPath = WriteBytes("seq_empty.bin", Array.Empty<byte>());

        string first = SHA1.Calculate(abcPath);
        string second = SHA1.Calculate(emptyPath);
        string third = SHA1.Calculate(abcPath);

        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", first);
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", second);
        // Re-hashing the first file after a different one confirms no residual state.
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", third);
    }

    // A path that does not exist must throw FileNotFoundException before any stream is opened.
    [Fact]
    public void SHA1_NonExistentPath_ThrowsFileNotFound()
    {
        string missing = Path.Combine(TempDir, "no_sha1_here.bin");

        Assert.Throws<FileNotFoundException>(() => SHA1.Calculate(missing));
    }
}
