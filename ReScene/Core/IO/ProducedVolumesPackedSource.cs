using ReScene.RAR;

namespace ReScene.Core.IO;

/// <summary>
/// Packed-byte source over a brute-forced rar output set: each archived file's stream
/// is a <see cref="RARStream"/> over the produced volumes. Snapshot semantics: EACH
/// OPENED RARStream enumerates the volume list when IT is constructed (inside
/// OpenPackedStream) and never discovers volumes created later — so callers create a
/// fresh source (and thus fresh streams) per assembly attempt and never reuse one
/// across a producer state change (spec §4).
/// </summary>
internal sealed class ProducedVolumesPackedSource(string producedFirstVolumePath) : IPackedSource
{
    public Stream OpenPackedStream(string archivedFileName) =>
        new RARStream(producedFirstVolumePath, archivedFileName);

    public void Dispose()
    {
        // Streams are owned and disposed by the reconstructor.
    }
}
