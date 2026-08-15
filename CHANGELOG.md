# Changelog

All notable changes to ReScene.Lib are documented here. Releases follow [SemVer](https://semver.org/) and this file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Earlier releases (v1.0.0 – v1.2.7) are recorded in the Git tags.

## [Unreleased]

### Added

- `SRRFile.ExtractStoredFiles` — bulk stored-file extraction that recreates each entry's
  relative directory structure (the single-file `ExtractStoredFile` keeps flattening to the
  base name). Everything is validated before the first write, so a hostile or inconsistent SRR
  extracts nothing at all: rooted or `.`/`..` names, duplicate names after normalization
  (case-insensitively, on every host), names needed as both a file and a directory, output
  paths that pre-existing links inside the output directory would redirect or collide, and
  out-of-bounds data ranges are all refused loudly.

### Fixed

- The legacy (non-assembly) reconstruction path now matches the assembly path in two places where
  the two had silently diverged. A carrier whose hash duplicates an earlier candidate's is now
  deleted on a per-volume verification mismatch when "delete duplicate CRC files" is enabled — it
  previously survived, because the duplicate flag was computed in a block that had already closed.
  And a placement that cannot complete is now reported as a failed combination, so it appears as an
  error in the version grid instead of only in the log.

### Changed

- The brute-force candidate loop is decomposed into named phases — a per-candidate context record,
  a progress-row factory, both first-volume gates, both win paths, and a producer handle carrying
  the observation invariant as named operations. `TryProcessCommandLinesAsync` drops from 656 lines
  at nesting depth 7 to 330 (183 of them code) at depth 5, with every rationale comment preserved.
  Behavior is unchanged throughout; the assembly and legacy paths keep their deliberate
  differences in retention, log ordering and failure reporting.
- The two per-volume verification blocks — previously duplicated and kept in step by nothing but a
  comment stating they were identical — now share one implementation. The volume list is supplied
  lazily so the legacy path still performs no enumeration and no write when verification is not
  configured.
- `Manager.BruteForceRARVersionAsync` is now explicitly single-run-at-a-time per instance: a
  second call while a run is executing throws `InvalidOperationException` immediately instead
  of silently corrupting the running run's cancellation source and per-run state. `Stop()`
  remains safe to call concurrently. The `@listfile` fallback write also moved out of the
  input-operand composition step to its call site — pure refactor, same file, same timing.
- The test suite now executes on net8.0 as well as net10.0 — previously the library's net8.0
  target was only ever compile-verified, so a net8.0-only runtime regression could not be caught.
- The test project now builds with the library's own analyzer regime (`AnalysisLevel=latest-All`
  + `EnforceCodeStyleInBuild`), warning-free; test-only conventions (underscore test names,
  seeded `Random`, Action-based recorder hooks) are exempted with justifications in
  `.editorconfig` rather than in code.

### Fixed

- **Security:** dangling links are no longer mistaken for "not yet materialized" paths in the
  canonicalizer's path walks (`Exists` follows links, so a link with a missing target looked
  absent) — a dangling link inside an SFV directory or an extraction output directory could
  route a later write outside the containment root. Dangling links are now resolved like any
  other link and contained accordingly.
- Brute-force setup failures now fail the run properly on every path: a missing or unreadable
  WinRAR installations directory no longer throws out of `BruteForceRARVersionAsync`, and the
  empty-installations-directory and input-file-validation early exits now fire the terminal
  status event (previously a caller keying its busy state off `BruteForceStatusChanged` was
  stranded at Running).

## [1.9.0] — 2026-07-05

### Changed

- **Public identifiers now spell format acronyms in all-caps** — `SRR`, `SRS`, `SRST`, `SRSF`,
  `RAR`, `MP3`, `MP4`, `MKV`, `ASF`, `WMV`, `EBML`, `OSO` (e.g. `Srr*`→`SRR*`, `RarFileName`→
  `RARFileName`, `Mp4AtomTypes`→`MP4AtomTypes`). `Flac`, `Riff`, and `VobSub` keep PascalCase.
  This renames public types and members, so consumers referencing the old casing must update.
- Source layout: one top-level type per file throughout, and magic numbers replaced with named
  constants/enums (e.g. `RARFileFlags`, `RAR4BlockType`, `RAR5ArchiveFlags`, `EBMLLaceType`,
  `RAR4HeaderLayout`, `SRSBlockLayout`, `SRSFourCC`). Behavior is unchanged.

### Added

- `AllowedVersionFolders` — the reconstruction engine now honors a caller-supplied set of WinRAR
  version folders, so only the selected versions are tried during the brute-force.

### Fixed

- **Data loss:** the SRR editor/verifier/commit path could mis-parse embedded RAR headers as SRR
  blocks; SRR editing now preserves data correctly.
- **RAR byte-exactness:** EXT_TIME remainder byte order, Unicode filename patching, and 64-bit pack
  sizes are handled correctly; RAR4 AV/SIGN block-type display labels corrected.
- **Verification & orchestration:** correct SHA-1/CRC-32 verification, a resilient brute-force loop
  that no longer aborts the whole run on a bad candidate, support for releases with more than 101
  volumes, and directory-entry handling.
- **Sample rebuild / SRS:** MP3 samples with stacked tags rebuild correctly and no longer hang on a
  zero-size SRS block; FLAC samples carrying a leading ID3v2 tag rebuild; MKV lacing headers larger
  than 256 bytes are measured correctly; fragmented (multi-`mdat`) MP4 is refused cleanly on both
  sides; MP4 `tkhd` track-ID offset and WMV profile total size corrected.
- **Comparison:** correct stored-file content comparison, MKV element-cap tail handling, RAR4 data
  skipping, and block alignment.
- **Decompression robustness:** fail cleanly (rather than returning a partial/garbage buffer) on
  truncated LZ input and on malformed PPMd/window/RAR5-block data; PPMd allocator sizing fix.
- **Security:** closed path-traversal (Zip-Slip) sinks in SRR reconstruction.
- Brute-force completion status reporting and case-insensitive hash matching corrected.

## [1.8.0] — 2026-07-02

### Added

- `Manager.TryParseRARVersion(name, out version)` — public, non-throwing WinRAR version-folder
  parse (e.g. `winrar-560` → `560`, names `< 100` normalised `×10`). `ParseRARVersion` now
  delegates to it, so the two cannot diverge.
- `Manager.TryParseRARVersion(name, out version, out variantTag)` — also returns the **variant
  tag**: the remainder of the folder name after the version digits, trimmed of leading separators
  (`winrar-250-beta1` → `250` + `"beta1"`, `wrar25b2` → `250` + `"b2"`). Lets callers distinguish
  folders that parse to the same version (betas, locale builds).

### Fixed

- A WinRAR version folder containing `rar.exe` but with no parseable version in its name (e.g.
  `winrar-beta/`) made the brute-force throw `FormatException` and abort the whole run. Such
  folders are now skipped with a log line.

## [1.7.1] — 2026-06-29

### Added

- `RARDetailedParser` now decodes **every** header flag, set and unset: each flags field lists all of
  its flags (the description when the bit is set, `"Not set"` when clear), `LONG_BLOCK` is always
  emitted, and the RAR5 main-archive/file/end flag blocks are routed through the shared `EmitFlags`
  helper. This lets a diff align and highlight exactly which flag differs.
- RAR4 End-of-Archive blocks surface the `EARC_REVSPACE` trailing bytes as a `Reserved Space` field,
  so a 20-byte terminator is fully accounted for instead of looking identical to a 13-byte one.

No public API change (`RARHeaderField`/`RARDetailedBlock` unchanged; the flag tables and `EmitFlags`
are private).

## [1.7.0] — 2026-06-28

This release adds per-archive-set reconstruction and **changes one public signature**: `Manager.BruteForceRARVersionAsync` now returns `BruteForceRunResult` instead of `bool` (see Changed). (The lib jumps 1.5.1 → 1.7.0; the 1.6.x line was app-only releases with no library changes.)

### Added

- `SrrArchiveSet` and `SRRFile.ArchiveSets` group an SRR's RAR volumes into independent archive sets, keyed by directory + volume base name (e.g. `DVD1/aln-re4a`). Each set carries its own archived files, per-volume CRCs, timestamps, and header-derived metadata. The flat `SRRFile` properties are unchanged and remain the union across all sets, so single-set SRRs behave exactly as before.
- `RARVolumeIdentifier.GetArchiveSetKey(volumePath)` computes a volume's archive-set key (handles old-style `.rNN` and new-style `.partNN.rar`, with or without a directory prefix).
- `VolumeMatchEvaluator.Evaluate(...)` — a pure, `rar.exe`-free helper that compares a produced multi-volume set against expected per-volume CRCs (positional assignment, per-position CRC verification, first-mismatch and count-mismatch reporting).
- `BruteForceOptions.ExpectedVolumeCrcs` (per-volume `name → CRC`) drives full-set verification; `WinningCombo` and `BruteForceRunResult` carry the winning version + arguments so callers can seed a later set's search.
- `SFVFile.ParseBytes(bytes, tolerant)` and `SFVFile.ParseLines(lines, tolerant)` parse SFV content from memory (tolerant mode skips malformed lines instead of throwing); `ReadFile` now delegates to the strict path.

### Changed

- **`Manager.BruteForceRARVersionAsync` now returns `BruteForceRunResult` (was `bool`).** Read `.Success` for the previous boolean result and `.Combo` for the winning version + arguments.
- When recreating a whole volume set, the engine now verifies **every** produced volume against its expected CRC (not just the first) and keeps brute-forcing on a near-miss instead of committing a mismatched rename. The CRC-based rename creates output subdirectories as needed and no longer requires `StopOnFirstMatch`.

## [1.5.1] — 2026-06-21

### Added

- The MKV/Matroska inspector and comparer now name many more EBML elements that previously showed as "Unknown": `FlagLacing` and other `TrackEntry` fields (MinCache, MaxCache, MaxBlockAdditionID, CodecDelay, SeekPreRoll, the accessibility flags, …), the `Video` flags, the HDR `Colour` / `MasteringMetadata` set, content-encryption elements, the `BlockGroup` children, the `Cues` subtree, and `SegmentFilename`. IDs cross-checked against the IETF normative `ebml_matroska.xml` and matroska.org.

## [1.5.0] — 2026-06-20

### Added

- `SRRFile.ReadStoredFile(srrFilePath, match)` reads an embedded stored file into memory — the in-memory counterpart of `ExtractStoredFile`, with the same bounds checks. It backs the app's new embedded-image preview and Inspector text view.

## [1.4.0] — 2026-06-18

This release **narrows the public API** — types that were public only by accident are now internal, so external consumers may need to adjust (see Changed). Internals were also decomposed and hardened with no behavioural change to the supported API, alongside a batch of correctness fixes.

### Changed

- **Breaking — public API narrowed.** Types that were public only by accident are now `internal`: the RAR decompression engine, the low-level format readers (MP3 / FLAC / EBML), the RAR parsing / process / patch internals (`RARHeaderReader`, `RAR5HeaderReader`, `RARArchive`, `RARPatcher`, `RARProcess`, `RARStream`, `SRRReconstructor`, …) and assorted helpers. Only the intended format/model and creation / reconstruction / comparison API remains public, now guarded by a public-API snapshot test.
- **Breaking — public member shapes tightened** for a library: `List<T>` properties and returns are now `IReadOnlyList<T>` (or `IList<T>` where mutation is intended), settable collection properties are `init` / get-only, and `byte[]` properties are `ReadOnlyMemory<byte>`.
- **Breaking — `Manager.BruteForceRARVersionAsync` now takes a `CancellationToken`**; the internal source is linked to it so a caller's cancel reaches the running RAR processes (previously only `Stop()` did).
- `Manager` was split into focused internal collaborators (process-log management, RAR-version selection, progress calculation, output writing, input preparation, comment-phase brute-force) behind its unchanged public facade.
- `SHA1.Calculate` uses the stateless `SHA1.HashData` instead of a process-wide `HashAlgorithm` behind a global lock, so SHA-1 file hashing is no longer serialized across the whole process.
- `OSOHashCalculator` surfaces a warning for any entry it has to skip (unreadable or corrupt) instead of swallowing the failure, and narrows its exception handling so genuinely fatal errors propagate.

### Added

- `Manager` implements `IDisposable` (disposes its per-run linked cancellation source).

### Fixed

- **Decompression of compressed entries larger than 32 KB.** `BitInput` used a fixed 32 KB buffer (and `SetBuffer` capped the copy), silently truncating any packed payload over 32 KB and producing wrong output; the buffer now grows to hold the full payload.
- **WMV/ASF sample reconstruction.** The rebuilder copied the Data Object body verbatim from the SRS (where the packet payload had been stripped) and never read the media file, and the parser skipped the Data Object by its original declared size — so WMV rebuilds could never byte-match. Packets are now restored from the media file and the parser walks only the retained data header.
- **File timestamps on archives larger than 2 GB.** `RARPatcher` read the file name from a fixed header offset that is wrong for LARGE-flag headers, so per-file mtime patching silently no-opped on >2 GB files (the analyze/preview path was corrected too).
- **Crash on corrupt RAR5 metadata.** `RARDetailedParser` let `DateTime.FromFileTime` throw out of `Parse` on an out-of-range value; the RAR5 block loop now has a per-block guard and the FILETIME is formatted defensively.
- **Crash on truncated SRR files.** `SRRFileParser` read a block's name length without a bounds check, throwing past EOF on a truncated RARFile block; now guarded.
- **SHA-1 hash files with blank/whitespace lines** are no longer rejected.
- **Data race** on the per-process log writers — they were mutated from CliWrap's thread-pool callbacks via a plain `Dictionary`; now a `ConcurrentDictionary` with a per-writer lock.
- **Lost cancellation** — the UI's cancel did not reach the library; the threaded token (above) fixes it.
- A matched volume that cannot be written (destination already occupied) and a systemic Phase-1 failure (every comment-phase test erroring) are now logged as warnings instead of being silently swallowed.
- `DiscoverRarVolumes` no longer truncates an old-style volume set at the first gap in the numbering.

### Internal

- `ConfigureAwait(false)` on all library awaits; `static ReadOnlySpan<byte>` markers, a `FrozenDictionary` lookup, and a right-sized copy buffer.
- Large additions to unit-test coverage (the brute-force helpers, direct SRR reconstruction, and the formats / comparison / IO subsystems) plus behaviour-preserving best-practice cleanups across the library; no supported-API changes.

## [1.3.0] — 2026-06-14

### Added

- **MKV/WebM comparison** (`Core.Comparison.MKVFileData`): parses the EBML element tree so two MKV/WebM files can be compared side by side. Leaf-like elements — including cluster payloads — are compared down to their bytes (chunked, with early-exit), and the number of elements parsed is capped (configurable) so very large files load quickly.

### Changed

- SRR stored files are now passed as an ordered `IReadOnlyList<StoredFileEntry>` instead of an `IReadOnlyDictionary<string, string>` (`SRRWriter.CreateAsync` / `CreateFromSFVAsync`). This guarantees the on-disk write order of stored-file blocks and skips duplicate stored names deterministically.
- RAR reconstruction writes matched archives to an `output/` subdirectory of the chosen output folder (instead of its root) and can rename them to the release's original names — sourced from the SRR or, when none is loaded, the verification `.sfv`. `RARVolumeIdentifier` is now public to support this.

### Fixed

- MKV track signatures grow past 256 bytes when needed so x265 samples match pyrescene output (`RebuildAsync` / SRS track signature sizing).
