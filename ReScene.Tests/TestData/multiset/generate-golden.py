#!/usr/bin/env python
"""Regenerates the Task 3/9 golden-fixture SRRs from the local pyReScene checkout.

Run MANUALLY from this directory (`python generate-golden.py`) — the xUnit suite never invokes
Python. See README.md for full provenance (pinned pyrescene commit, Python version, imghdr/nntplib
shims, unrar provenance, regeneration steps) and for the KNOWN DIVERGENCE this harness surfaced
(Task 3's RAR-block-flags finding, and Task 9's nested-SRR-content finding below).

Steps (docs/superpowers/specs/2026-07-18-multiset-srr-creation-design.md §6):
  1. Assert the local pyrescene checkout is pinned to the recorded commit.
  2. (Re)build tree-2disc/, tree-storageonly/, and tree-fullpipeline/ via the file-based C# helper
     (../tools/build-tree.cs — deliberately OUTSIDE this TestData/ tree, see README.md's I2
     writeup), which ports ReScene.Tests/RarFixtures.cs's WriteStoreModeRarSet byte-for-byte so
     the RAR volumes are produced by the same layout our own writer/tests already exercise.
  3. Run `bin/pyrescene.py --no-srs --no-isdb` over tree-2disc/tree-storageonly, and
     `bin/pyrescene.py --vobsub-srr --no-isdb` (no --no-srs) over tree-fullpipeline/ — the latter
     needs unrar.exe on PATH (Task 9: prepends UNRAR_DIR, see README.md's "UnRAR provenance") to
     actually extract Subs/ instead of silently self-disabling --vobsub-srr. Both runs use
     PYTHONPATH pointed at the vendored imghdr/nntplib shims (compat/ — Python 3.13+ dropped both
     from the stdlib; nntplib is unrelated to usenet in this offline use — see compat/nntplib.py).
  4. Copy the resulting .srr next to each tree as golden-<name>.srr.
"""

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PINNED_PYRESCENE_COMMIT = "04da213cef6765ed98e0d1735683822a41ea0103"

# Task 9: UnRAR 7.01 (freeware "UnRAR for Windows"), needed for tree-fullpipeline's --vobsub-srr
# run to actually extract Subs/ (see README.md's "UnRAR provenance"). Update this if the pinned
# unrar location on this machine ever moves.
UNRAR_DIR = r"G:\winrar\extracted\winrar-x32-701"

HERE = Path(__file__).resolve().parent


def assert_pinned_pyrescene(pyrescene_dir: Path) -> None:
    result = subprocess.run(
        ["git", "-C", str(pyrescene_dir), "rev-parse", "HEAD"],
        capture_output=True, text=True, check=True,
    )
    actual = result.stdout.strip()
    if actual != PINNED_PYRESCENE_COMMIT:
        print(
            f"ABORT: {pyrescene_dir} is at {actual}, expected pinned commit "
            f"{PINNED_PYRESCENE_COMMIT}. Golden fixtures must be regenerated from the exact "
            "pinned pyrescene revision recorded in README.md.",
            file=sys.stderr,
        )
        sys.exit(1)
    print(f"pyrescene pinned-hash check OK: {actual}")


def build_trees() -> None:
    # ReScene.Tests/tools/, a sibling of TestData/ — deliberately NOT under TestData/ so
    # build-tree.cs is never swept up by TestData's None/copy-to-output glob (I2, README.md).
    # Writes tree-2disc/, tree-storageonly/, AND tree-fullpipeline/ (Task 9) in one run.
    tools_dir = HERE.parent.parent / "tools"
    subprocess.run(
        ["dotnet", "run", "build-tree.cs", "--", str(HERE)],
        cwd=str(tools_dir), check=True,
    )


def assert_unrar_available() -> None:
    unrar_exe = Path(UNRAR_DIR) / "UnRAR.exe"
    if not unrar_exe.is_file():
        print(
            f"ABORT: expected UnRAR.exe at {unrar_exe} (UNRAR_DIR at the top of this script). "
            "tree-fullpipeline's --vobsub-srr run needs it to actually extract Subs/ — without "
            "it pyrescene silently self-disables --vobsub-srr and the golden would not exercise "
            "the nested-SRR path at all. Update UNRAR_DIR if unrar moved.",
            file=sys.stderr,
        )
        sys.exit(1)
    print(f"unrar check OK: {unrar_exe}")


def run_pyrescene(pyrescene_dir: Path, tree_dir: Path, out_dir: Path, extra_args: list[str], prepend_unrar: bool = False) -> Path:
    compat_dir = HERE / "compat"
    full_env = dict(os.environ)
    full_env["PYTHONPATH"] = str(compat_dir)
    if prepend_unrar:
        # Task 9: makes rescene/unrar.py's locate_in_path() -> shutil.which("unrar") find
        # UnRAR.exe, so --vobsub-srr actually extracts instead of self-disabling with a warning
        # (bin/pyrescene.py: "if options.vobsub_srr and not unrar_is_available(): ... = False").
        full_env["PATH"] = UNRAR_DIR + os.pathsep + full_env.get("PATH", "")

    subprocess.run(
        [
            sys.executable, str(pyrescene_dir / "bin" / "pyrescene.py"),
            *extra_args,
            "--output", str(out_dir),
            str(tree_dir),
        ],
        check=True, env=full_env,
    )

    produced = out_dir / (tree_dir.name + ".srr")
    if not produced.is_file():
        print(f"ABORT: expected pyrescene output not found: {produced}", file=sys.stderr)
        sys.exit(1)
    return produced


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--pyrescene-dir", default=r"E:\git\extern\pyrescene",
        help="Path to the local pyrescene checkout (default: E:\\git\\extern\\pyrescene).")
    args = parser.parse_args()

    pyrescene_dir = Path(args.pyrescene_dir)
    assert_pinned_pyrescene(pyrescene_dir)
    assert_unrar_available()

    print("Building tree-2disc/, tree-storageonly/, and tree-fullpipeline/ via ../tools/build-tree.cs ...")
    build_trees()

    with tempfile.TemporaryDirectory(prefix="pyrescene-golden-") as tmp:
        tmp_dir = Path(tmp)

        print("Running pyrescene over tree-2disc/ (--no-srs --no-isdb) ...")
        srr_2disc = run_pyrescene(pyrescene_dir, HERE / "tree-2disc", tmp_dir, ["--no-srs", "--no-isdb"])
        shutil.copyfile(srr_2disc, HERE / "golden-2disc.srr")
        print(f"  -> {HERE / 'golden-2disc.srr'}")

        print("Running pyrescene over tree-storageonly/ (--no-srs --no-isdb) ...")
        srr_storageonly = run_pyrescene(pyrescene_dir, HERE / "tree-storageonly", tmp_dir, ["--no-srs", "--no-isdb"])
        shutil.copyfile(srr_storageonly, HERE / "golden-storageonly.srr")
        print(f"  -> {HERE / 'golden-storageonly.srr'}")

        print("Running pyrescene over tree-fullpipeline/ (--vobsub-srr --no-isdb, unrar on PATH) ...")
        srr_fullpipeline = run_pyrescene(
            pyrescene_dir, HERE / "tree-fullpipeline", tmp_dir,
            ["--vobsub-srr", "--no-isdb"], prepend_unrar=True)
        shutil.copyfile(srr_fullpipeline, HERE / "golden-fullpipeline.srr")
        print(f"  -> {HERE / 'golden-fullpipeline.srr'}")

    print("Done. Review README.md for the KNOWN DIVERGENCEs this harness found (Task 3's RAR-block"
          "-flags finding; Task 9's nested-SRR-content finding) before trusting a green test run.")


if __name__ == "__main__":
    main()
