# Changelog

All notable changes to ReScene.Lib are documented here.
Releases follow [SemVer](https://semver.org/) and this file follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Earlier releases (v1.0.0 – v1.2.7) are recorded in the Git tags.

## [2.0.1] — 2026-06-18

### Fixed

- **Decompression of compressed entries larger than 32 KB.** `BitInput` used a fixed 32 KB buffer (and `SetBuffer` capped the copy), silently truncating any packed payload over 32 KB and producing wrong output; the buffer now grows to hold the full payload.
- **WMV/ASF sample reconstruction.** The rebuilder copied the Data Object body verbatim from the SRS (where the packet payload had been stripped) and never read the media file, and the parser skipped the Data Object by its original declared size — so WMV rebuilds could never byte-match. Packets are now restored from the media file and the parser walks only the retained data header.
- **File timestamps on archives larger than 2 GB.** `RARPatcher` read the file name from a fixed header offset that is wrong for LARGE-flag headers, so per-file mtime patching silently no-opped on >2 GB files (the analyze/preview path was corrected too).
- **Crash on corrupt RAR5 metadata.** `RARDetailedParser` let `DateTime.FromFileTime` throw out of `Parse` on an out-of-range value; the RAR5 block loop now has a per-block guard and the FILETIME is formatted defensively.
- **Crash on truncated SRR files.** `SRRFileParser` read a block's name length without a bounds check, throwing past EOF on a truncated RARFile block; now guarded.
- **SHA-1 hash files with blank/whitespace lines** are no longer rejected.

### Changed

- `SHA1.Calculate` uses the stateless `SHA1.HashData` instead of a process-wide `HashAlgorithm` behind a global lock, so SHA-1 file hashing is no longer serialized across the whole process.
- `OSOHashCalculator` surfaces a warning for any entry it has to skip (unreadable or corrupt) instead of swallowing the failure, and narrows its exception handling so genuinely fatal errors propagate.
- Substantial new unit-test coverage and behaviour-preserving best-practice cleanups across the library; no supported-API changes.

## [2.0.0] — 2026-06-16

This is a **breaking** release: the public API surface was deliberately narrowed,
so external consumers may need to adjust (see Changed). Internals were also
decomposed and hardened with no behavioural change to the supported API.

### Changed

- **Breaking — public API narrowed.** Types that were public only by accident are
  now `internal`: the RAR decompression engine, the low-level format readers
  (MP3 / FLAC / EBML), the RAR parsing / process / patch internals
  (`RARHeaderReader`, `RAR5HeaderReader`, `RARArchive`, `RARPatcher`,
  `RARProcess`, `RARStream`, `SRRReconstructor`, …) and assorted helpers. Only the
  intended format/model and creation / reconstruction / comparison API remains
  public, now guarded by a public-API snapshot test.
- **Breaking — public member shapes tightened** for a library: `List<T>`
  properties and returns are now `IReadOnlyList<T>` (or `IList<T>` where mutation
  is intended), settable collection properties are `init` / get-only, and
  `byte[]` properties are `ReadOnlyMemory<byte>`.
- **Breaking — `Manager.BruteForceRARVersionAsync` now takes a
  `CancellationToken`**; the internal source is linked to it so a caller's cancel
  reaches the running RAR processes (previously only `Stop()` did).
- `Manager` was split into focused internal collaborators (process-log
  management, RAR-version selection, progress calculation, output writing, input
  preparation, comment-phase brute-force) behind its unchanged public facade.

### Added

- `Manager` implements `IDisposable` (disposes its per-run linked cancellation
  source).

### Fixed

- **Data race** on the per-process log writers — they were mutated from CliWrap's
  thread-pool callbacks via a plain `Dictionary`; now a `ConcurrentDictionary`
  with a per-writer lock.
- **Lost cancellation** — the UI's cancel did not reach the library; the threaded
  token (above) fixes it.
- A matched volume that cannot be written (destination already occupied) and a
  systemic Phase-1 failure (every comment-phase test erroring) are now logged as
  warnings instead of being silently swallowed.
- `DiscoverRarVolumes` no longer truncates an old-style volume set at the first
  gap in the numbering.

### Internal

- `ConfigureAwait(false)` on all library awaits; `static ReadOnlySpan<byte>`
  markers, a `FrozenDictionary` lookup, and a right-sized copy buffer; expanded
  unit coverage of the brute-force helpers and direct SRR reconstruction.

## [1.3.0] — 2026-06-14

### Added

- **MKV/WebM comparison** (`Core.Comparison.MKVFileData`): parses the EBML
  element tree so two MKV/WebM files can be compared side by side. Leaf-like
  elements — including cluster payloads — are compared down to their bytes
  (chunked, with early-exit), and the number of elements parsed is capped
  (configurable) so very large files load quickly.

### Changed

- SRR stored files are now passed as an ordered
  `IReadOnlyList<StoredFileEntry>` instead of an
  `IReadOnlyDictionary<string, string>` (`SRRWriter.CreateAsync` /
  `CreateFromSFVAsync`). This guarantees the on-disk write order of stored-file
  blocks and skips duplicate stored names deterministically.
- RAR reconstruction writes matched archives to an `output/` subdirectory of the
  chosen output folder (instead of its root) and can rename them to the
  release's original names — sourced from the SRR or, when none is loaded, the
  verification `.sfv`. `RARVolumeIdentifier` is now public to support this.

### Fixed

- MKV track signatures grow past 256 bytes when needed so x265 samples match
  pyrescene output (`RebuildAsync` / SRS track signature sizing).
