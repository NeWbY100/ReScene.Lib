using System.Collections.Concurrent;
using ReScene.Core.Diagnostics;

namespace ReScene.Core;

/// <summary>
/// Owns the per-process log writers used during brute-force RAR creation: opening a streaming
/// log file for a process, writing process output to it, and closing it when the process
/// completes. Extracted from <see cref="Manager"/> to isolate the concurrency-safe writer
/// bookkeeping.
/// </summary>
/// <remarks>
/// The writer map is written from the orchestration thread and read/closed from CliWrap's
/// process-output callbacks (thread-pool threads), so it is concurrency-safe; each writer is also
/// guarded by its own gate because stdout/stderr can be delivered on separate threads.
/// </remarks>
internal sealed class ProcessLogManager(IReSceneLogger logger, object logSource)
{
    private readonly IReSceneLogger _logger = logger;

    // Used as the log <c>sender</c> so the emitted log events are indistinguishable from when
    // Manager logged these lines directly.
    private readonly object _logSource = logSource;

    private readonly ConcurrentDictionary<RARProcess, ProcessLog> _processLogs = new();

    /// <summary>A per-process log writer paired with the gate that serializes access to it.</summary>
    private sealed class ProcessLog(StreamWriter writer)
    {
        public StreamWriter Writer { get; } = writer;

        // A dedicated gate object — never lock on the StreamWriter itself (CA2002: it derives
        // from MarshalByRefObject and has weak identity).
        public object Gate { get; } = new();
    }

    /// <summary>
    /// Opens a streaming, auto-flushing log file for the given process under
    /// <paramref name="outputDirectoryPath"/>'s <c>logs</c> subdirectory and registers it.
    /// </summary>
    public void OpenLog(RARProcess process, string outputDirectoryPath, string outputFilePath)
    {
        // Create log file path
        string logsDir = Path.Combine(outputDirectoryPath, "logs");
        Directory.CreateDirectory(logsDir);

        string logFileName = $"{Path.GetFileNameWithoutExtension(outputFilePath)}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        string logFilePath = Path.Combine(logsDir, logFileName);

        // Open StreamWriter with AutoFlush enabled for immediate writes
        StreamWriter writer = new(logFilePath, append: false)
        {
            AutoFlush = true
        };
        _processLogs[process] = new ProcessLog(writer);

        _logger.Information(_logSource, $"Opened log file for streaming: {logFilePath}", LogTarget.Phase2);
    }

    /// <summary>
    /// Writes a line of process output to the process's log file, if one is open.
    /// </summary>
    public void WriteOutput(RARProcess process, string? data)
    {
        // Stream output directly to log file (auto-flushed). The per-writer gate serializes
        // concurrent stdout/stderr callbacks; a writer closed concurrently is handled by the
        // catch (a caught ObjectDisposedException, never a crash or a corrupt dictionary).
        if (_processLogs.TryGetValue(process, out ProcessLog? log))
        {
            try
            {
                lock (log.Gate)
                {
                    log.Writer.WriteLine(data);
                    // AutoFlush is enabled, so data is immediately written to disk
                }
            }
            catch (Exception ex)
            {
                _logger.Information(_logSource, $"Failed to write to process log: {ex.Message}", LogTarget.Phase2);
            }
        }
    }

    /// <summary>
    /// Closes and disposes the log writer for the given completed process.
    /// </summary>
    public void CloseLog(RARProcess process)
    {
        // TryRemove is atomic, so a late WriteOutput call can no longer find the writer once it
        // has been closed.
        if (_processLogs.TryRemove(process, out ProcessLog? log))
        {
            try
            {
                lock (log.Gate)
                {
                    log.Writer.Close();
                    log.Writer.Dispose();
                }

                _logger.Information(_logSource, $"Process log file closed", LogTarget.Phase2);
            }
            catch (Exception ex)
            {
                _logger.Information(_logSource, $"Failed to close process log writer: {ex.Message}", LogTarget.Phase2);
            }
        }
    }
}
