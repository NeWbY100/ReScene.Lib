# Task 3 golden fixtures — pyrescene byte-equality harness

Non-circular oracle for `SRRWriter.CreateFromInputsAsync` (multi-set SRR creation): the local
[pyrescene](https://bitbucket.org/Gfy/pyrescene) checkout builds an SRR for each synthetic release
tree below, and `GoldenFixtureTests.cs` asserts our own writer produces byte-identical output for
the same tree (after normalizing the header's app-name field — see below). Design:
`docs/superpowers/specs/2026-07-18-multiset-srr-creation-design.md` §6. Normative classification
rules: `docs/superpowers/specs/pyrescene-rules-excerpt.txt`.

## Fixed divergence (found by this harness, adjudicated and fixed)

This harness originally found that `golden-2disc.srr`'s four `SrrRarFile` (0x71) reference blocks
all carry flag bit `0x0001` (`RECOVERY_BLOCKS_REMOVED` — pyrescene's own comment: *"we always set
this flag, even if there aren't RR"*, `rescene/rar.py` `SrrRarFileBlock.__init__`), while
`SRRWriter.WriteRARFileBlock` (`ReScene/SRR/SRRWriter.cs`) always wrote `SRRBlockFlags.None`
(`0x0000`) for these blocks — the ORIGINAL single-SFV/single-RAR `CreateAsync` path's pre-existing
behavior, shared verbatim by `CreateFromInputsAsync`'s `WriteVolumesAsync`/`ProcessRARVolume`;
nothing Task 1/2 of this feature introduced it. `SRRRARFileBlock` (the reader) has no flags
property at all, so the bit is read-side-inert — this was never functionally observed before this
harness did a real, byte-exact comparison against genuine pyrescene output.

**First byte offset (in the raw, non-normalized `golden-2disc.srr`): 379** (block starts at 376;
offset 379 is the 2-byte little-endian flags field). All four occurrences, one per RAR volume:

| Block (raw golden offset) | golden flags | writer's OLD flags | name |
|---|---|---|---|
| 376 | `0x0001` | `0x0000` (at raw offset 369 in our output) | `CD1/a.rar` |
| 460 | `0x0001` | `0x0000` (at 453) | `CD1/a.r00` |
| 544 | `0x0001` | `0x0000` (at 537) | `CD2/b.rar` |
| 628 | `0x0001` | `0x0000` (at 621) | `CD2/b.r00` |

After app-name normalization every OTHER byte in the 704-byte normalized comparison was already
identical — stored-file order (`release.nfo`, `Subs/subs.sfv`, `CD1/a.sfv`, `CD2/b.sfv`),
forward-slash-normalized names, RAR volume order (`CD1/a.rar`, `CD1/a.r00`, `CD2/b.rar`,
`CD2/b.r00`), and every copied RAR header/payload byte all matched exactly. `golden-storageonly.srr`
(no RAR blocks) was byte-identical to our output from the start (zero differences).

**Adjudicated fix (landed):** `SRRBlockFlags` gained a `RecoveryBlocksRemoved = 0x0001` member
(`ReScene/SRR/SRRBlockFlags.cs`), and `WriteRARFileBlock` now sets it unconditionally, matching
pyReScene for both this multi-input path and the pre-existing single-input `CreateAsync` path.
Confirmed safe: `SRRRARFileBlock` never reads this flag, so reconstruction/round-trip is
unaffected, and the flag is semantically accurate (this writer's SRRs are always header-only /
recovery-stripped). `GoldenFixtureTests.TwoDiscTree_MatchesPyresceneGoldenBytes` and
`StorageOnlyTree_MatchesPyresceneGoldenBytes` both pass; the only other test the fix touched was
`PublicApiSnapshotTests` (its self-regenerating public-API-surface baseline, `PublicApi.ReScene.approved.txt`
— one line added for the new enum member; not a byte-content test).

## Provenance

- pyrescene checkout: `E:\git\extern\pyrescene`, pinned commit `04da213cef6765ed98e0d1735683822a41ea0103`
  (`generate-golden.py` aborts if the checkout isn't at this exact commit).
- Python: 3.14.0. Python 3.13 removed `imghdr` from the stdlib; pyrescene's `bin/pyrescene.py`
  still does `import imghdr` (used only by its `fixed_resolution_cover` image-type sniffing, never
  exercised by these image-free trees). `compat/imghdr.py` is a from-scratch reconstruction of
  CPython's `Lib/imghdr.py` (PSF License 2.0) — the same public `tests` list + `what(file, h=None)`
  API, format-detection bodies for jpeg/png/gif/tiff/rgb/pbm/pgm/ppm/rast/xbm/bmp/webp/exr — wired
  in via `PYTHONPATH=compat`.
- RAR volumes: `tools/build-tree.cs`, a .NET 10 **file-based app** (`dotnet run build-tree.cs --
  <outDir>`, no `.csproj`; not part of `ReScene.Lib.slnx` or `ReScene.Manager.slnx`, so it never
  affects the forced-rebuild gate). It is a byte-for-byte port of
  `ReScene.Tests/RarFixtures.cs`'s `WriteStoreModeRarSet` (same RAR4 marker/archive-header/
  file-header/end-block layout and flag values, inlined as literals since those enums are
  `internal` to the `ReScene` assembly) — chosen over a from-scratch Python RAR4 writer so the
  golden's RAR bytes are produced by the exact layout our own writer/tests already rely on, rather
  than a second, independently-fallible implementation. Each generated volume's SFV line carries
  the file's REAL CRC32 (`Force.Crc32`, `Crc32Algorithm.Compute` over the whole file), independently
  cross-checked against Python's `zlib.crc32` while building these fixtures.
- Exact commands: see `generate-golden.py` (`python generate-golden.py`, optionally
  `--pyrescene-dir <path>`); it (1) asserts the pinned hash, (2) runs
  `dotnet run build-tree.cs -- <this dir>` to (re)build `tree-2disc/`/`tree-storageonly/`,
  (3) runs `python bin/pyrescene.py --no-srs --no-isdb --output <tmp> <tree>` with
  `PYTHONPATH=<this dir>/compat` for each tree, (4) copies the result to `golden-<name>.srr`.

## Fixture trees

### `tree-2disc/`

```
tree-2disc/
  release.nfo
  CD1/{a.rar, a.r00, a.sfv}      2-volume store-mode RAR set, correct-CRC SFV
  CD2/{b.rar, b.r00, b.sfv}      2-volume store-mode RAR set, correct-CRC SFV
  Subs/{subs.rar, subs.sfv}      1-volume set; subs.sfv is EXCLUDED from main sets (pyrescene
                                 remove_unwanted_sfvs: name contains "subs", no subpack/subfix/
                                 vobsub/subtitle release-name override) — stored as a raw stored
                                 file, same as pyrescene's own `copied_files` handling; subs.rar
                                 itself is never opened by either side for this tree.
```

No `Sample/` directory — `--no-srs` disables all sample/SRS handling on the pyrescene side, and
samples join the golden fixtures once the full pipeline lands in Task 9
(`FullPipelineGoldenTests`, disabled here).

Because `pyrescene.py` is invoked WITHOUT `-r`/`--recursive`, `tree-2disc` itself is `generate_srr`'s
`reldir` directly — pyrescene's `is_release`/`get_release_directories`/`RELEASE_FOLDERS`
auto-detection never runs; only `remove_unwanted_sfvs`'s classification (ported to
`docs/superpowers/specs/pyrescene-rules-excerpt.txt`) applies to the three SFVs `get_files` finds
recursively under the tree.

Expected stored-file order (verified against the actual golden bytes, matching
`generate_srr`'s `copied_files` construction: nfo → not-yet-handled/excluded SFVs → main SFVs,
each appended in that relative order): `release.nfo`, `Subs/subs.sfv`, `CD1/a.sfv`, `CD2/b.sfv`.
RAR volume block order: `CD1/a.rar`, `CD1/a.r00`, `CD2/b.rar`, `CD2/b.r00` (main-SFV order, then
within-chain volume order). All names are forward-slash-normalized in the actual block bytes
regardless of the OS path separator used internally by pyrescene's `os.path.relpath` (verified:
`SrrRarFileBlock`/`SrrStoredFileBlock` construction normalizes `\` to `/` before serializing, even
though pyrescene's own progress/log messages print the raw OS-separated path).

`GoldenFixtureTests.BuildStoredListInTraversalOrder` hardcodes this exact stored-file list (as
`additionalFiles`: `release.nfo`, `Subs/subs.sfv`) — `CreateFromInputsAsync` auto-appends the two
main-set SFVs (`CD1/a.sfv`, `CD2/b.sfv`) from `inputFiles` in the same order, per its documented
"`additionalFiles` first, then each `.sfv` input not already present" contract
(`ReScene/SRR/SRRWriter.cs`, `ResolveStoredFiles`).

### `tree-storageonly/`

```
tree-storageonly/
  release.nfo
```

No SFV, no RAR — pyrescene's zero-SFV/zero-RAR branch (`generate_srr`'s
`len(main_sfvs) or (not len(main_sfvs) and not len(main_rars))` condition) produces a header +
single stored-file (`release.nfo`) SRR, matching `CreateFromInputsAsync`'s zero-input,
stored-files-only ("storage-only / fix-release") mode. Verified byte-identical (0 differences)
after app-name normalization.

## App-name normalization

pyrescene's header records `pyReScene Auto <version>` as its app name; our writer's default is
`ReScene.NET`. Before any byte comparison, `GoldenFixtureTests.NormalizeAppName` — an independent,
from-scratch header-block splitter (NOT the production `SRRFileParser`) — rewrites only the
header's app-name field (both files) to the fixed string `NORMALIZED`, leaving every other byte
(including the RAR-block-flags divergence above) untouched. Its own correctness is validated first
against hand-built byte vectors: a header with no app-name flag, headers with differing name
lengths, a truncated header, and trailing-bytes-preserved — see `GoldenFixtureTests.cs`.

## Regeneration

```
cd ReScene.Lib/ReScene.Tests/TestData/multiset
python generate-golden.py
```

Requires: the `E:\git\extern\pyrescene` checkout at the pinned commit above, Python 3.x with
`PYTHONPATH` support for the vendored `compat/imghdr.py` shim (any 3.x works; 3.13+ needs the
shim), and the .NET 10 SDK (`dotnet run` file-based-app support) on `PATH`. Re-running
overwrites `tree-2disc/`, `tree-storageonly/`, `golden-2disc.srr`, and `golden-storageonly.srr`
in place; the process is fully deterministic (re-verified byte-for-byte across repeated runs
while authoring this harness).
