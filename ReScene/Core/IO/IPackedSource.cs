namespace ReScene.Core.IO;

/// <summary>
/// Supplies one archived file's packed byte stream to <see cref="SRRReconstructor"/>.
/// Called once per archived file, in SRR order; the returned stream is positioned at
/// the file's packed byte 0 and is disposed by the reconstructor after the file's last
/// split piece is copied.
/// </summary>
internal interface IPackedSource : IDisposable
{
    public Stream OpenPackedStream(string archivedFileName);
}
