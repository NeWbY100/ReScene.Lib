using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using ReScene.Core.IO;

namespace ReScene.Core.Diagnostics;

/// <summary>
/// Wraps a RAR command-line process, parsing its output for compression progress and status events.
/// </summary>
public sealed partial class RARProcess
{
    /// <summary>
    /// Occurs when the RAR process writes output (stdout or stderr).
    /// </summary>
    public event EventHandler<ProcessDataEventArgs>? ProcessOutput;

    /// <summary>
    /// Occurs when the RAR process status changes (running, completed, etc.).
    /// </summary>
    public event EventHandler<OperationStatusChangedEventArgs>? ProcessStatusChanged;

    /// <summary>
    /// Occurs when compression progress updates for a file being archived.
    /// </summary>
    public event EventHandler<FileCompressionOperationProgressEventArgs>? CompressionProgress;

    /// <summary>
    /// Occurs when compression status changes for a file being archived.
    /// </summary>
    public event EventHandler<FileCompressionOperationStatusChangedEventArgs>? CompressionStatusChanged;

    /// <summary>
    /// Gets the path to the RAR executable.
    /// </summary>
    public string ProcessFilePath
    {
        get; private set;
    }

    /// <summary>
    /// Gets the input directory containing files to compress.
    /// </summary>
    public string InputDirectory
    {
        get; private set;
    }

    /// <summary>
    /// Gets the output RAR file path.
    /// </summary>
    public string OutputFilePath
    {
        get; private set;
    }

    /// <summary>
    /// Gets the command-line options passed to the RAR process.
    /// </summary>
    public string[] CommandLineOptions
    {
        get; private set;
    }

    /// <summary>
    /// The log target for this process (Phase1, Phase2, or System).
    /// </summary>
    public LogTarget LogTarget { get; set; } = LogTarget.System;

    private readonly IReSceneLogger _logger;

    private struct ArchiveItem
    {
        public string FileName
        {
            get; set;
        }

        public int Progress
        {
            get; set;
        }

        public bool Done
        {
            get; set;
        }
    };

    private struct Archive
    {
        public string ArchiveFileName
        {
            get; set;
        }

        public List<ArchiveItem> ArchiveItems
        {
            get; set;
        }

        public Archive()
        {
            ArchiveFileName = string.Empty;
            ArchiveItems = [];
        }
    }

    private Archive _archiveFile = new();

    private static readonly Encoding _outputEncoding = GetOutputEncoding();

    // Matches lines with filename and percentage, language-independent
    [GeneratedRegex(@"^\s*\S+\s+(?<filename>.+?)\s{2,}(?<progress>\d+)%", RegexOptions.Compiled)]
    private static partial Regex GeneratedProgressRegex();
    private static readonly Regex _progressRegex = GeneratedProgressRegex();

    [GeneratedRegex(@"^\s*\S+\s+(?<filename>.+?)\s{2,}OK\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedOkRegex();
    private static readonly Regex _okRegex = GeneratedOkRegex();

    /// <summary>
    /// Initializes a new RAR process with the specified executable, directories, and command-line options.
    /// </summary>
    /// <param name="processFilePath">The path to the RAR executable.</param>
    /// <param name="inputDirectory">The input directory containing files to compress.</param>
    /// <param name="outputFilePath">The output RAR archive path.</param>
    /// <param name="commandLineOptions">The command-line options to pass to RAR.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public RARProcess(string processFilePath, string inputDirectory, string outputFilePath, IEnumerable<string> commandLineOptions, IReSceneLogger? logger = null)
    {
        if (!File.Exists(processFilePath))
        {
            throw new FileNotFoundException(processFilePath);
        }

        _logger = logger ?? NullReSceneLogger.Instance;
        ProcessFilePath = processFilePath;
        InputDirectory = inputDirectory;
        OutputFilePath = outputFilePath;

        List<string> options =
        [
            .. commandLineOptions,
                // Output file
                outputFilePath,

                // Input directory/files
                // RAR 2.00 does not like just '.' as input directory
                ".\\*"
        ];

        // Save new options
        CommandLineOptions = [.. options];
    }

    /// <summary>
    /// Executes the RAR process asynchronously and returns the exit code.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the process.</param>
    /// <returns>The process exit code.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // Start process
        try
        {
            _logger.Debug(this, $"Starting RAR process: {ProcessFilePath}", LogTarget);
            _logger.Debug(this, $"Working Directory: {InputDirectory}", LogTarget);
            _logger.Debug(this, $"Output File: {OutputFilePath}", LogTarget);
            _logger.Debug(this, $"Arguments: {string.Join(" ", CommandLineOptions)}", LogTarget);

            FireProcessStatusChanged(new(OperationStatus.Running));
            FireCompressionStatusChanged(new(OperationStatus.Running, OutputFilePath));

            DateTime startTime = DateTime.Now;

            // Create custom streams to capture output as it arrives (including \r updates)
            var stdOutStream = new OutStream(data => HandleOutputData(data, startTime), _outputEncoding);
            var stdErrStream = new OutStream(HandleErrorData, _outputEncoding);

            CommandResult result = await Cli.Wrap(ProcessFilePath)
                .WithArguments(CommandLineOptions)
                .WithWorkingDirectory(InputDirectory)
                .WithStandardOutputPipe(PipeTarget.ToStream(stdOutStream))
                .WithStandardErrorPipe(PipeTarget.ToStream(stdErrStream))
                .WithValidation(CommandResultValidation.None) // Manually handle exit codes
                .ExecuteAsync(cancellationToken);

            int exitCode = result.ExitCode;
            TimeSpan elapsed = DateTime.Now - startTime;

            _logger.Information(this, $"RAR process completed. Exit Code: {exitCode}, Duration: {elapsed.TotalSeconds:F2}s, Output: {OutputFilePath}", LogTarget);

            OperationCompletionStatus completionStatus = exitCode == 0 ? OperationCompletionStatus.Success : OperationCompletionStatus.Error;

            FireProcessStatusChanged(new(OperationStatus.Running, OperationStatus.Completed, completionStatus));
            FireCompressionStatusChanged(new(OperationStatus.Running, OperationStatus.Completed, completionStatus, OutputFilePath));

            return exitCode;
        }
        catch (OperationCanceledException)
        {
            _logger.Warning(this, $"RAR process cancelled: {OutputFilePath}", LogTarget);
            FireProcessStatusChanged(new(OperationStatus.Running, OperationStatus.Completed, OperationCompletionStatus.Cancelled));
            FireCompressionStatusChanged(new(OperationStatus.Running, OperationStatus.Completed, OperationCompletionStatus.Cancelled, OutputFilePath));
        }
        catch (Exception ex)
        {
            _logger.Error(this, ex, $"RAR process failed: {OutputFilePath}", LogTarget);
            FireCompressionStatusChanged(new(OperationStatus.Running, OperationStatus.Completed, OperationCompletionStatus.Error, OutputFilePath));
            throw;
        }

        return 1;
    }

    // Custom stream class to capture data as it arrives (byte-by-byte)
    private class OutStream(Action<string> onData, Encoding encoding) : Stream
    {
        private readonly StringBuilder _buffer = new();
        private readonly Action<string> _onData = onData;
        private readonly Encoding _encoding = encoding;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Decode bytes to string
            string data = _encoding.GetString(buffer, offset, count);

            // Process each character
            foreach (char c in data)
            {
                if (c == '\n')
                {
                    _onData(_buffer.ToString());
                    _buffer.Clear();
                }
                else if (c == '\r')
                {
                    _onData(_buffer.ToString());
                    _buffer.Clear();
                }
                else
                {
                    _buffer.Append(c);
                }
            }
        }
    }


    private void HandleOutputData(string data, DateTime startTime)
    {
        ProcessOutput?.Invoke(this, new(data));

        if (!string.IsNullOrEmpty(data))
        {
            ParseProcessOutputData(data, startTime);
        }
    }

    private void HandleErrorData(string data) => ProcessOutput?.Invoke(this, new(data, true));

    private void ParseProcessOutputData(string output, DateTime startDateTime)
    {
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        if (string.IsNullOrEmpty(_archiveFile.ArchiveFileName))
        {
            _archiveFile.ArchiveFileName = OutputFilePath;
        }

        Match lineMatch = _progressRegex.Match(output);
        if (lineMatch.Success)
        {
            string filename = NormalizeOutputFileName(lineMatch.Groups["filename"].Value);
            string progressStr = lineMatch.Groups["progress"].Value;

            if (!string.IsNullOrEmpty(filename) && int.TryParse(progressStr, out int progress))
            {
                int itemIndex = _archiveFile.ArchiveItems.FindIndex(item => item.FileName == filename);

                if (itemIndex >= 0)
                {
                    ArchiveItem item = _archiveFile.ArchiveItems[itemIndex];
                    item.Progress = progress;
                    item.Done = progress >= 100;
                    _archiveFile.ArchiveItems[itemIndex] = item;
                }
                else
                {
                    _archiveFile.ArchiveItems.Add(new ArchiveItem
                    {
                        FileName = filename,
                        Progress = progress,
                        Done = progress >= 100
                    });
                }

                string filePath = Path.IsPathRooted(filename) ? filename : Path.Combine(InputDirectory, filename);
                FireCompressionProgress(new(100, progress, startDateTime, filePath));
            }
        }
        else if (output.Contains("OK", StringComparison.OrdinalIgnoreCase))
        {
            Match okMatch = _okRegex.Match(output);
            if (okMatch.Success)
            {
                string filename = NormalizeOutputFileName(okMatch.Groups["filename"].Value);

                int itemIndex = _archiveFile.ArchiveItems.FindIndex(item => item.FileName == filename);

                if (itemIndex >= 0)
                {
                    ArchiveItem item = _archiveFile.ArchiveItems[itemIndex];
                    item.Progress = 100;
                    item.Done = true;
                    _archiveFile.ArchiveItems[itemIndex] = item;
                }
                else
                {
                    _archiveFile.ArchiveItems.Add(new ArchiveItem
                    {
                        FileName = filename,
                        Progress = 100,
                        Done = true
                    });
                }

                string filePath = Path.IsPathRooted(filename) ? filename : Path.Combine(InputDirectory, filename);
                FireCompressionProgress(new(100, 100, startDateTime, filePath));
            }
        }
    }

    private static string NormalizeOutputFileName(string filename)
    {
        string trimmed = filename.Trim();
        if (trimmed.StartsWith(".\\", StringComparison.Ordinal) || trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            return trimmed[2..];
        }

        return trimmed;
    }

    private static Encoding GetOutputEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
        catch (NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private void FireProcessStatusChanged(OperationStatusChangedEventArgs e) => ProcessStatusChanged?.Invoke(this, e);

    private void FireCompressionStatusChanged(FileCompressionOperationStatusChangedEventArgs e) => CompressionStatusChanged?.Invoke(this, e);

    private void FireCompressionProgress(FileCompressionOperationProgressEventArgs e) => CompressionProgress?.Invoke(this, e);
}
