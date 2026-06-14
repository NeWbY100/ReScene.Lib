# Changelog

All notable changes to ReScene.Lib are documented here.
Releases follow [SemVer](https://semver.org/) and this file follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Earlier releases (v1.0.0 – v1.2.7) are recorded in the Git tags.

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
