# Task 3 golden fixtures — pyrescene byte-equality harness

Non-circular oracle for `SRRWriter.CreateFromInputsAsync` (multi-set SRR creation): the local
[pyrescene](https://bitbucket.org/Gfy/pyrescene) checkout builds an SRR for each synthetic release
tree below, and `GoldenFixtureTests.cs` asserts our own writer produces byte-identical output for
the same tree (after normalizing the header's app-name field — see below). Design:
`docs/superpowers/specs/2026-07-18-multiset-srr-creation-design.md` §6. Normative classification
rules: `docs/superpowers/specs/pyrescene-rules-excerpt.txt`.

## Task 9: full-pipeline golden (`tree-fullpipeline/`) — divergence found, adjudicated, FIXED

`FullPipelineGoldenTests.cs` (`golden-fullpipeline.srr`) is Task 9's "samples + subs" golden —
`tree-fullpipeline/` run through pyrescene's `--vobsub-srr --no-isdb` (NO `--no-srs`), the ONE
combination that exercises SRS generation for a sample AND real subtitle-RAR extraction via a real
`unrar.exe` (see "UnRAR provenance" below). **This harness found a real divergence** (below), which
was NOT hand-tweaked away — per the escalation rule (this file's own precedent above), it was
reported and the team lead/user adjudicated a fix. `FullRelease_MatchesPyresceneGoldenBytes`
**PASSES** (byte-identical after deep normalization) with the fix applied.

**What was checked first (and is NOT the divergence):** the KNOWN RISK flagged before this golden
even ran — whether pyrescene's SRS (`.srs`) app-name field (a DIFFERENT format than the `.srr`
header block, `SRSF` chunk: `flags(2)+appNameLen(2)+appName+fileNameLen(2)+fileName+sampleSize(8)+
crc32(4)`, `resample/main.py`'s `FileData.serialize_as_stream`/`serialize_as_mp3`) would differ from
ours. It DOES differ ("pyReSample 0.7" vs "ReScene.NET") — confirmed by hand-decoding both sides'
raw bytes byte-by-byte — but it normalizes away completely: `FullPipelineGoldenTests.
NormalizeSrsfAppName` (own hand-built vectors, same trust-anchor rigor as `GoldenFixtureTests.
NormalizeAppName`) rewrites it the same way, and after that EVERY other SRS byte (flags,
fileName, sampleSize, crc32, and the entire `SRST` track block incl. all 256 signature bytes) is
byte-identical between pyrescene's SRS writer and ours for a plain "stream"-type sample. The
nested `Subs/subs.srr`'s OWN header app-name field ALSO normalizes away cleanly via the same
`GoldenFixtureTests.NormalizeAppName`, applied recursively (it's a full SRR byte-for-byte, embedded
verbatim as a stored file's payload). **Neither app-name field is the divergence.**

**The actual divergence:** after ALL app-name normalization, the two SRRs still differ — first
differing byte at **offset 607** (in the deep-normalized 1220/1260-byte buffers; see
`FullPipelineGoldenTests.NormalizeDeep`), the `Subs/subs.srr` stored-file block's own declared
length field (`0x69`=105 golden vs `0x91`=145 ours — a 40-byte difference). Isolated precisely by
building three variants and diffing: (1) our writer's nested-SRR generation WITHOUT the subtitle
SFV stored inside it → **byte-identical** to the golden (1220 bytes both, 0 differences); (2) the
SAME, but WITH the SFV stored inside (== what `CreatorViewModel.BuildNestedSubtitleStoredFiles`
ACTUALLY does in production, pre-existing since before this multi-set feature/Task 9) → the 40-byte
divergence, exactly the size of the stored `subs.sfv` entry (19 bytes + block-header overhead).

**Root cause:** `CreatorViewModel.GenerateNestedSubtitleSrrsCoreAsync` (via
`BuildNestedSubtitleStoredFiles`) stored the subtitle SFV itself (and any sibling `.nfo` files —
none exist in this tree) INSIDE the nested SRR it creates. pyrescene's real `--vobsub-srr` nested
SRR (`resample`'s `extract_and_create_srr`, via real `unrar.exe` extraction) contains ONLY the
extracted RAR volume block(s) — nothing else. This behavior predated Task 9 (it was the
pre-existing Advanced-tab/wizard `GenerateNestedSRRFileAsync`'s behavior, shared via the
`BuildNestedSubtitleStoredFiles` helper Task 9 only refactored, not changed). No existing committed
test locked in "the nested SRR contains the SFV", so the fix required no other test updates.

**Adjudicated fix (landed, D0 in `task-9-delivered-fix-findings.md`):** the user chose (a) — strict
pyrescene byte-identity, parallelling the RECOVERY_BLOCKS_REMOVED "fix globally" precedent (Task
3). `BuildNestedSubtitleStoredFiles` now returns no additional files at all (`null`) — a nested
subtitle SRR is RAR-blocks-only, for BOTH the folder-mode staging path and the wizard/Advanced-tab
path (the SAME shared helper). The subtitle SFV's own bytes are still stored, just only once, in
the OUTER SRR (the scanner's pass-10 already stores every SFV — no redundant re-add either, a
separate finding closed in the same round). See `task-9-report.md` for the full writeup.

## UnRAR provenance (Task 9)

`--vobsub-srr` needs a real `unrar` on PATH (`rescene/unrar.py`'s `locate_unrar()` → on Windows,
`locate_windows()` checks `%ProgramW6432%\WinRAR`/`%ProgramFiles(x86)%\WinRAR`/the registry App
Paths key before falling back to `locate_in_path()` → `shutil.which("unrar")`) — without it,
`bin/pyrescene.py` silently sets `options.vobsub_srr = False` with only a warning, and the nested
SRR is never created at all (the subtitle SFV would just be stored plain, like `tree-2disc/`'s
`--no-srs` run). This machine has no WinRAR/unrar install anywhere on the standard search paths
(confirmed via exhaustive filesystem search); `generate-golden.py`'s `UNRAR_DIR` constant instead
prepends a pinned, already-present extraction to `PATH` for the `tree-fullpipeline/` run only:
**UnRAR 7.01 (freeware "UnRAR for Windows"), `G:\winrar\extracted\winrar-x32-701\UnRAR.exe`**
(`Rar.exe` also present in the same directory, unused). `generate-golden.py`'s
`assert_unrar_available()` aborts if this path ever stops existing — update `UNRAR_DIR` if unrar
moves. Real `unrar.exe` extraction also means the RAR volumes it touches must carry their REAL
payload CRC32 in the FILE_CRC header field (unlike `tree-2disc/`'s/`tree-storageonly/`'s volumes,
which use a placeholder `0xDEADBEEF` — never extracted, so it never mattered): `build-tree.cs`'s
`WriteStoreModeRarSet`/`WriteVolume`/`WriteFileHeader` gained an opt-in `useRealCrc` parameter
(default `false`, preserving `tree-2disc/`'s and `tree-storageonly/`'s COMMITTED, dual-reviewed
bytes exactly — verified via `git status` showing zero changes to either after rebuilding both with
the updated tool); only `tree-fullpipeline/`'s `BuildRarSet` calls pass `useRealCrc: true`.

Python 3.13 also removed `nntplib` from the stdlib; `rescene/main.py`'s `_rarreader_usenet` (used
internally by `create_srr_for_subs`/`extract_and_create_srr` when reading an extracted RAR's
blocks — nothing usenet-specific about this local, offline use of it) does a bare `import nntplib`
and calls `exit(1)` on failure, a hard crash unrelated to `--no-isdb`. `compat/nntplib.py` is a
minimal shim (the handful of exception classes referenced in an `except` clause, never actually
raised here) — same pattern as the existing `compat/imghdr.py` shim, same rationale.

## Fixed divergence (found by this harness, adjudicated and fixed)

This harness originally found that `golden-2disc.srr`'s four `SrrRarFile` (0x71) reference blocks
all carry flag bit `0x0001` (`RECOVERY_BLOCKS_REMOVED` — pyrescene's own comment: *"we always set
this flag, even if there aren't RR"*, `rescene/rar.py` `SrrRarFileBlock.__init__`), while
`SRRWriter.WriteRARFileBlock` (`ReScene/SRR/SRRWriter.cs`) always wrote `SRRBlockFlags.None`
(`0x0000`) for these blocks — the ORIGINAL single-SFV/single-RAR `CreateAsync` path's pre-existing
behavior, shared verbatim by `CreateFromInputsAsync`'s `WriteVolumesAsync`/`ProcessRARVolume`;
nothing Task 1/2 of this feature introduced it. `SRRRARFileBlock` (the reader) does parse and
populate this flag on every read (it inherits `SRRBlock.Flags`), but no consumer branches on its
value for RAR-file blocks, so the bit was functionally inert — this was never observed before this
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
Confirmed safe: `SRRRARFileBlock` does parse/populate this flag on read, but no consumer branches
on its value, so reconstruction/round-trip is unaffected, and the flag is semantically accurate
(this writer's SRRs are always header-only / recovery-stripped). `GoldenFixtureTests.TwoDiscTree_MatchesPyresceneGoldenBytes` and
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
- RAR volumes: `../tools/build-tree.cs` (`ReScene.Tests/tools/build-tree.cs` — a SIBLING of
  `TestData/`, deliberately NOT under it; see "Why build-tree.cs lives outside TestData/" below),
  a .NET 10 **file-based app** (`dotnet run build-tree.cs -- <outDir>`, no `.csproj`; not part of
  `ReScene.Lib.slnx` or `ReScene.Manager.slnx`, so it never affects the forced-rebuild gate). It is
  a byte-for-byte port of `ReScene.Tests/RarFixtures.cs`'s `WriteStoreModeRarSet` (same RAR4
  marker/archive-header/file-header/end-block layout and flag values, inlined as literals since
  those enums are `internal` to the `ReScene` assembly) — chosen over a from-scratch Python RAR4
  writer so the golden's RAR bytes are produced by the exact layout our own writer/tests already
  rely on, rather than a second, independently-fallible implementation. Each generated volume's
  SFV line carries the file's REAL CRC32 (`Force.Crc32`, `Crc32Algorithm.Compute` over the whole
  file), independently cross-checked against Python's `zlib.crc32` while building these fixtures.
- Exact commands: see `generate-golden.py` (`python generate-golden.py`, optionally
  `--pyrescene-dir <path>`); it (1) asserts the pinned hash, (2) runs
  `dotnet run build-tree.cs -- <this dir>` (cwd `../tools/`) to (re)build
  `tree-2disc/`/`tree-storageonly/`, (3) runs `python bin/pyrescene.py --no-srs --no-isdb --output
  <tmp> <tree>` with `PYTHONPATH=<this dir>/compat` for each tree, (4) copies the result to
  `golden-<name>.srr`.

### Why build-tree.cs lives outside TestData/

`build-tree.cs` (a file-based app with its own `#:package` directive) originally lived at
`TestData/multiset/tools/build-tree.cs`. `ReScene.Tests.csproj`'s
`<None Include="TestData\**\*" CopyToOutputDirectory="PreserveNewest" />` copied it into `bin/`
alongside the real test data. A forced rebuild that redirects `BaseOutputPath` (the
`dotnet build -t:Rebuild -p:BaseOutputPath=bin2/` gate) shifts the SDK's default compile-exclude
away from the conventional `bin/`, so a stale `bin/…/build-tree.cs` copy left by an earlier normal
build was no longer excluded and got swept into `Compile` by the default `**/*.cs` glob →
`CS9298` (`#:` directives are file-based-app only). Excluding `.cs` from the copy glob and adding
`Compile Remove` entries for `bin/`, `obj/`, and `bin2*` closed each instance of this as it was
found, but a codex review flagged that enumerating output roots is inherently incomplete — a build
retargeted to yet another custom output root would reopen the same hole. The root-cause fix:
`build-tree.cs` now lives in `ReScene.Tests/tools/`, a directory no `None`/`Content` glob in this
project ever copies to ANY output directory — so it is structurally impossible for it to land in
`bin/`, `bin2/`, or any other build output, and no output-root enumeration is needed at all.

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

### `tree-fullpipeline/` (Task 9)

```
tree-fullpipeline/
  release.nfo
  CD1/{a.rar, a.r00, a.sfv}      2-volume store-mode RAR set, correct-CRC SFV, REAL payload CRC32
  CD2/{b.rar, b.r00, b.sfv}      2-volume store-mode RAR set, correct-CRC SFV, REAL payload CRC32
  Subs/{subs.rar, subs.sfv}      1-volume set, REAL payload CRC32 (extracted by real unrar.exe —
                                 see "UnRAR provenance" above; tree-2disc's Subs/ never needs this
                                 since that tree only ever runs --no-srs, no --vobsub-srr)
  Sample/clip.ts                 a plain "stream"-type sample (2048 bytes, deterministic
                                 Random(42) content) — deliberately NOT .vob, sidestepping the
                                 excerpt's RAR-backed-vob special case entirely (separately
                                 unit-tested in ReScene.App.Core.Tests/CreatorViewModelArtifactTests.cs
                                 with fakes). stream_profile_sample (resample/main.py) only needs
                                 the file's own size + first 256 bytes (SIG_SIZE) + whole-file
                                 CRC32 — no container structure to get subtly wrong on either
                                 implementation's side.
```

Run via `python bin/pyrescene.py --vobsub-srr --no-isdb --output <tmp> tree-fullpipeline` (NO
`--no-srs`) with `UNRAR_DIR` prepended to `PATH`. Verified (manually, byte-decoding both sides):
SRS creation succeeds for `Sample/clip.ts` (pyrescene console output: `File Details: Size 2,048 CRC
547D2660`, matching our own writer's independently-computed CRC for the same file); `Subs/subs.rar`
is genuinely extracted (console: `Processing file: subs.rar`) and a nested `Subs/subs.srr` is
produced — confirming `--vobsub-srr` is ACTUALLY active, not silently self-disabled. See the OPEN
divergence section above for the one byte-level finding this tree surfaced.

## App-name normalization

pyrescene's header records `pyReScene Auto <version>` as its app name; our writer's default is
`ReScene.Lib` (`ReScene.NET` when these fixtures were generated). Before any byte comparison, `GoldenFixtureTests.NormalizeAppName` — an independent,
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
`PYTHONPATH` support for the vendored `compat/imghdr.py`/`compat/nntplib.py` shims (any 3.x works;
3.13+ needs both), the .NET 10 SDK (`dotnet run` file-based-app support) on `PATH`, and (Task 9) a
real `unrar.exe`/`UnRAR.exe` at the path recorded in `generate-golden.py`'s `UNRAR_DIR` constant
(see "UnRAR provenance" above — `assert_unrar_available()` aborts with a clear message if it's
missing). Re-running overwrites `tree-2disc/`, `tree-storageonly/`, `tree-fullpipeline/`,
`golden-2disc.srr`, `golden-storageonly.srr`, and `golden-fullpipeline.srr` in place; the process is
fully deterministic (re-verified byte-for-byte across repeated runs while authoring this harness —
`tree-2disc/`'s and `tree-storageonly/`'s bytes are additionally verified UNCHANGED from their
originally committed (Task 3) versions after every Task 9 regeneration, via `git status`).
