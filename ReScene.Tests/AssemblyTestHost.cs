using ReScene.Core;
using ReScene.Core.Cryptography;
using ReScene.Core.Diagnostics;

namespace ReScene.Tests;

/// <summary>
/// One fake rar "version" directory plus a <see cref="Manager"/> wired to a <see cref="FakeRunner"/>
/// and a <see cref="RecordingLogger"/>, over a per-instance temp directory. Shared harness for every
/// producer-lifecycle and guided-assembly test that needs a real <see cref="Manager"/> run without a
/// real rar binary.
/// </summary>
internal sealed class AssemblyTestHost : IDisposable
{
    /// <summary>This host's unique subdirectory under the caller's temp directory.</summary>
    public string Root { get; }

    /// <summary>Contains one fake WinRAR version directory ("rar100") with a dummy binary.</summary>
    public string VersionsDir { get; }

    /// <summary>The run's working/output directory (<see cref="BruteForceOptions.OutputDirectoryPath"/>).</summary>
    public string WorkDir { get; }

    /// <summary>The release directory (unpacked inputs); seeded with one small file.</summary>
    public string ReleaseDir { get; }

    public FakeRunner Runner { get; } = new();
    public RecordingLogger Log { get; } = new();
    public Manager Manager { get; }

    public AssemblyTestHost(string tempDir)
    {
        Root = Path.Combine(tempDir, Guid.NewGuid().ToString("N")[..8]);
        VersionsDir = Path.Combine(Root, "versions");
        // "rar100" parses via RARVersionSelector's regex ("rar" + digits -> 100); "fake100" would
        // match nothing and zero candidates would launch (codex plan-rev-4 B1).
        string fakeVersion = Path.Combine(VersionsDir, "rar100");
        WorkDir = Path.Combine(Root, "work");
        ReleaseDir = Path.Combine(Root, "release");
        Directory.CreateDirectory(fakeVersion);
        Directory.CreateDirectory(WorkDir);
        Directory.CreateDirectory(ReleaseDir);
        File.WriteAllBytes(Path.Combine(ReleaseDir, "a.bin"), new byte[16]);
        // Platform-correct binary name (rar.exe on Windows, rar elsewhere) — RarExecutable.ResolveIn
        // only needs the file to exist; it is never actually run (FakeRunner never launches anything).
        File.WriteAllBytes(Path.Combine(fakeVersion, RarExecutable.FileName), []);
        Manager = new Manager(Log, Runner);
    }

    /// <summary>Adds a second candidate version ("rar200") for multi-candidate tests.</summary>
    public void AddSecondVersion()
    {
        string v = Path.Combine(VersionsDir, "rar200");
        Directory.CreateDirectory(v);
        File.WriteAllBytes(Path.Combine(v, RarExecutable.FileName), []);
    }

    /// <summary>
    /// Builds <see cref="BruteForceOptions"/> over this host's directories. When <paramref
    /// name="fixture"/> is <see langword="null"/> (the legacy-path tests), <see
    /// cref="BruteForceOptions.Hashes"/> and <see cref="BruteForceOptions.ExpectedVolumeCrcs"/> are
    /// left for the caller to populate.
    /// </summary>
    public BruteForceOptions Options(AssemblyFixture? fixture, bool completeAllVolumes,
        bool deleteRarFiles = true, bool deleteDuplicates = false, bool renameToOriginal = true)
    {
        var options = new BruteForceOptions(VersionsDir, ReleaseDir, WorkDir)
        {
            HashType = HashType.CRC32,
            RAROptions = new RAROptions
            {
                CompleteAllVolumes = completeAllVolumes,
                DeleteRARFiles = deleteRarFiles,
                DeleteDuplicateCRCFiles = deleteDuplicates,
                StopOnFirstMatch = true,
                RenameToOriginalNames = renameToOriginal,
                SRRFilePath = fixture?.SrrPath,
                OriginalRARFileNames = fixture?.OriginalVolumeNames ?? [],
                // EMPTY ranges reject every directory (RARVersionSelector.GetValidRARDirectories) and
                // EMPTY combinations iterate zero candidates (Manager's CommandLineArguments loop) —
                // both must be non-empty (codex plan-rev-4 B1):
                RARVersions = [new VersionRange(100, 299)],
                CommandLineArguments = [Array.Empty<RARCommandLineArgument>()],
            },
        };

        if (fixture is not null)
        {
            options.Hashes.Add(HashCalculator.Calculate(HashType.CRC32, fixture.OriginalVolumePaths[0]));
            foreach (KeyValuePair<string, string> kv in fixture.ExpectedVolumeCrcs)
            {
                options.ExpectedVolumeCrcs[kv.Key] = kv.Value;
            }
        }

        return options;
    }

    public void Dispose() => Manager.Dispose();
}
