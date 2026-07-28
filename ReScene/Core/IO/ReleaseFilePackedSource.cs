namespace ReScene.Core.IO;

/// <summary>
/// Custom-packer data source: the archived file's bytes ARE its packed bytes (store
/// method), read from the release input directory. Extracted verbatim from the
/// pre-seam <see cref="SRRReconstructor"/> source handling.
/// </summary>
internal sealed class ReleaseFilePackedSource(string inputDirectory) : IPackedSource
{
    public Stream OpenPackedStream(string archivedFileName)
    {
        string sourcePath = SRRReconstructor.FindSourceFile(inputDirectory, archivedFileName);
        return new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void Dispose()
    {
        // Stateless: streams are owned and disposed by the reconstructor.
    }
}
