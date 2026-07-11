using ReScene.Core.Comparison;

namespace ReScene.Tests;

/// <summary>
/// Covers <see cref="RARFileData.Load"/> against real RAR archives from the test data set. The focus is
/// that a real (data-bearing) multi-file RAR4 archive yields every embedded file header, which requires
/// the loader to seek past each file's packed data rather than parsing media bytes as the next header.
/// </summary>
public class RARFileDataTests
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    [Fact]
    public void Load_RealMultiFileRAR4_ReturnsAllFileHeaders()
    {
        // [audit #5] store_split_folder.rar is a single-volume RAR4 archive holding three stored files
        // (empty_file.txt, little_file.txt, users_manual4.00.txt). Before the fix the loader stopped
        // after little_file.txt because the packed data of a FileHeader block was never skipped, so the
        // packed bytes were misparsed as the next header and users_manual4.00.txt was lost.
        string path = Path.Combine(TestDataPath, "store_split_folder_old_srrsfv_windows", "store_split_folder.rar");

        var data = RARFileData.Load(path);

        Assert.False(data.IsRAR5);
        Assert.Equal(3, data.FileHeaders.Count);

        List<string> names = [.. data.FileHeaders.Select(f => f.FileName)];
        Assert.Contains(names, n => n.EndsWith("empty_file.txt", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("little_file.txt", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("users_manual4.00.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RAR4WithCommentThenFiles_ReturnsAllFileHeaders()
    {
        // store_utf8_comment.rar places a CMT service block before three file headers; skipping both the
        // service data and each file's packed data is required to reach every file header.
        string path = Path.Combine(TestDataPath, "store_utf8_comment", "store_utf8_comment.rar");

        var data = RARFileData.Load(path);

        Assert.False(data.IsRAR5);
        Assert.Equal(3, data.FileHeaders.Count);
        Assert.Contains(data.FileHeaders, f => f.FileName.EndsWith("little_file.txt", StringComparison.Ordinal));
        Assert.Contains(data.FileHeaders, f => f.FileName.EndsWith("empty_file.txt", StringComparison.Ordinal));
    }
}
