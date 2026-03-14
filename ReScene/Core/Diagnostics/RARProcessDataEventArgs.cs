namespace ReScene.Core.Diagnostics;

public class RARProcessDataEventArgs : ProcessDataEventArgs
{
    public RARProcess Process { get; private set; }

    public RARProcessDataEventArgs(RARProcess process, string? data) : base(data)
    {
        Process = process;
    }

    public RARProcessDataEventArgs(RARProcess process, string? data, bool error) : base(data, error)
    {
        Process = process;
    }
}
