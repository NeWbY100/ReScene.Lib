using ReScene.SRS;

namespace ReScene.Tests;

/// <summary>
/// Tests for <see cref="SignatureScanner"/>, the byte-signature engine that
/// computes MatchOffset for non-MKV SRS containers. Exercises the 64 KiB
/// sliding-window boundary logic, edge cases, cancellation, and the
/// <c>MatchesAt</c> exact-offset check.
/// </summary>
public class SignatureScannerTests
{
    // Builds a MemoryStream of the requested length and copies the signature
    // bytes into it at the given offset (rest stays zero-filled).
    private static MemoryStream BuildStreamWithSignature(int length, int offset, byte[] signature)
    {
        byte[] data = new byte[length];
        Array.Copy(signature, 0, data, offset, signature.Length);
        return new MemoryStream(data, writable: false);
    }

    // A distinctive 16-byte pattern that will not appear by accident in
    // zero-filled fill bytes.
    private static byte[] DistinctSignature16() =>
        [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22,
         0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0x10];

    // Pins that the sliding window correctly finds a signature that straddles
    // the 64 KiB read-buffer boundary. With a 16-byte sig the buffer size is
    // max(65536, 32) = 65536, so the first read covers bytes 0..65535. Placing
    // the signature at 65530 means it spans 65530..65545, crossing the 65536
    // boundary and forcing the carry/overlap logic to match it on the 2nd read.
    [Fact]
    public void Scan_SignatureAcross64KiBBoundary_ReturnsExactOffset()
    {
        const int Length = 130 * 1024;
        const int Offset = 65530;
        byte[] signature = DistinctSignature16();
        using MemoryStream stream = BuildStreamWithSignature(Length, Offset, signature);

        long result = SignatureScanner.Scan(stream, signature, regionStart: 0, regionEnd: Length);

        Assert.Equal(Offset, result);
    }

    // Pins that a signature located at the very start of the region is found
    // at offset 0 (no carry has accumulated yet).
    [Fact]
    public void Scan_SignatureAtOffsetZero_ReturnsZero()
    {
        const int Length = 100 * 1024;
        byte[] signature = DistinctSignature16();
        using MemoryStream stream = BuildStreamWithSignature(Length, offset: 0, signature);

        long result = SignatureScanner.Scan(stream, signature, regionStart: 0, regionEnd: Length);

        Assert.Equal(0, result);
    }

    // Pins that a signature ending exactly at the region end (offset =
    // length - signature.Length) is still found by the final buffer pass.
    [Fact]
    public void Scan_SignatureAtRegionEnd_ReturnsLengthMinusSignature()
    {
        const int Length = 100 * 1024;
        byte[] signature = DistinctSignature16();
        int offset = Length - signature.Length;
        using MemoryStream stream = BuildStreamWithSignature(Length, offset, signature);

        long result = SignatureScanner.Scan(stream, signature, regionStart: 0, regionEnd: Length);

        Assert.Equal(offset, result);
    }

    // Pins that an absent signature returns -1 after scanning the whole region.
    [Fact]
    public void Scan_SignatureAbsent_ReturnsMinusOne()
    {
        const int Length = 100 * 1024;
        byte[] present = DistinctSignature16();
        // Search for a different pattern that is not in the (zero-filled) stream.
        byte[] missing = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        using MemoryStream stream = BuildStreamWithSignature(Length, offset: 0, present);

        long result = SignatureScanner.Scan(stream, missing, regionStart: 0, regionEnd: Length);

        Assert.Equal(-1, result);
    }

    // Pins the early-out: when the search region is smaller than the signature
    // there is nowhere for it to fit, so Scan returns -1 without reading.
    [Fact]
    public void Scan_RegionSmallerThanSignature_ReturnsMinusOne()
    {
        byte[] signature = DistinctSignature16(); // 16 bytes
        // Region is only 10 bytes wide (0..10), smaller than the 16-byte sig.
        using MemoryStream stream = new(new byte[64]);

        long result = SignatureScanner.Scan(stream, signature, regionStart: 0, regionEnd: 10);

        Assert.Equal(-1, result);
    }

    // Pins that an already-cancelled token aborts the scan via
    // ThrowIfCancellationRequested at the top of the loop.
    [Fact]
    public void Scan_AlreadyCancelledToken_ThrowsOperationCanceled()
    {
        const int Length = 100 * 1024;
        byte[] signature = DistinctSignature16();
        using MemoryStream stream = BuildStreamWithSignature(Length, offset: 50000, signature);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SignatureScanner.Scan(stream, signature, regionStart: 0, regionEnd: Length, ct: cts.Token));
    }

    // Pins that an empty signature is trivially considered a match anywhere.
    [Fact]
    public void MatchesAt_EmptySignature_ReturnsTrue()
    {
        using MemoryStream stream = new([0x01, 0x02, 0x03, 0x04]);

        bool result = SignatureScanner.MatchesAt(stream, [], offset: 2);

        Assert.True(result);
    }

    // Pins that an offset where the signature would run past the end of the
    // stream is rejected (bounds check before reading).
    [Fact]
    public void MatchesAt_OffsetPlusLengthPastEnd_ReturnsFalse()
    {
        byte[] data = [0x10, 0x20, 0x30, 0x40];
        using MemoryStream stream = new(data);
        // Signature length 3 at offset 2 would need bytes 2,3,4 but length is 4.
        byte[] signature = [0x30, 0x40, 0x50];

        bool result = SignatureScanner.MatchesAt(stream, signature, offset: 2);

        Assert.False(result);
    }

    // Pins that MatchesAt returns true when the signature bytes equal the
    // stream content at the exact offset.
    [Fact]
    public void MatchesAt_CorrectOffset_ReturnsTrue()
    {
        byte[] data = [0x10, 0x20, 0x30, 0x40, 0x50];
        using MemoryStream stream = new(data);
        byte[] signature = [0x30, 0x40];

        bool result = SignatureScanner.MatchesAt(stream, signature, offset: 2);

        Assert.True(result);
    }

    // Pins that MatchesAt returns false when the bytes at the offset differ
    // from the signature (it does not scan, only checks the exact spot).
    [Fact]
    public void MatchesAt_WrongOffset_ReturnsFalse()
    {
        byte[] data = [0x10, 0x20, 0x30, 0x40, 0x50];
        using MemoryStream stream = new(data);
        // 0x30,0x40 actually lives at offset 2, so offset 1 must not match.
        byte[] signature = [0x30, 0x40];

        bool result = SignatureScanner.MatchesAt(stream, signature, offset: 1);

        Assert.False(result);
    }

    // Pins that a negative offset is rejected by the bounds check.
    [Fact]
    public void MatchesAt_NegativeOffset_ReturnsFalse()
    {
        byte[] data = [0x10, 0x20, 0x30];
        using MemoryStream stream = new(data);

        bool result = SignatureScanner.MatchesAt(stream, [0x10], offset: -1);

        Assert.False(result);
    }
}
