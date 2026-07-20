#!/usr/bin/env python
"""Regenerates the Task 3 golden-fixture SRRs from the local pyReScene checkout.

Run MANUALLY from this directory (`python generate-golden.py`) — the xUnit suite never invokes
Python. See README.md for full provenance (pinned pyrescene commit, Python version, imghdr shim,
regeneration steps) and for the KNOWN DIVERGENCE this harness surfaced.

Steps (docs/superpowers/specs/2026-07-18-multiset-srr-creation-design.md §6):
  1. Assert the local pyrescene checkout is pinned to the recorded commit.
  2. (Re)build tree-2disc/ and tree-storageonly/ via the file-based C# helper
     (../tools/build-tree.cs — deliberately OUTSIDE this TestData/ tree, see README.md's I2
     writeup), which ports ReScene.Tests/RarFixtures.cs's WriteStoreModeRarSet byte-for-byte so
     the RAR volumes are produced by the same layout our own writer/tests already exercise.
  3. Run `bin/pyrescene.py --no-srs --no-isdb` over each tree with PYTHONPATH pointed at the
     vendored imghdr shim (compat/imghdr.py — Python 3.13+ dropped imghdr from the stdlib).
  4. Copy the resulting .srr next to each tree as golden-<name>.srr.
"""

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PINNED_PYRESCENE_COMMIT = "04da213cef6765ed98e0d1735683822a41ea0103"

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
    tools_dir = HERE.parent.parent / "tools"
    subprocess.run(
        ["dotnet", "run", "build-tree.cs", "--", str(HERE)],
        cwd=str(tools_dir), check=True,
    )


def run_pyrescene(pyrescene_dir: Path, tree_dir: Path, out_dir: Path) -> Path:
    compat_dir = HERE / "compat"
    env = {"PYTHONPATH": str(compat_dir)}
    import os
    full_env = dict(os.environ)
    full_env.update(env)

    subprocess.run(
        [
            sys.executable, str(pyrescene_dir / "bin" / "pyrescene.py"),
            "--no-srs", "--no-isdb",
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

    print("Building tree-2disc/ and tree-storageonly/ via ../tools/build-tree.cs ...")
    build_trees()

    with tempfile.TemporaryDirectory(prefix="pyrescene-golden-") as tmp:
        tmp_dir = Path(tmp)

        print("Running pyrescene over tree-2disc/ ...")
        srr_2disc = run_pyrescene(pyrescene_dir, HERE / "tree-2disc", tmp_dir)
        shutil.copyfile(srr_2disc, HERE / "golden-2disc.srr")
        print(f"  -> {HERE / 'golden-2disc.srr'}")

        print("Running pyrescene over tree-storageonly/ ...")
        srr_storageonly = run_pyrescene(pyrescene_dir, HERE / "tree-storageonly", tmp_dir)
        shutil.copyfile(srr_storageonly, HERE / "golden-storageonly.srr")
        print(f"  -> {HERE / 'golden-storageonly.srr'}")

    print("Done. Review README.md for the KNOWN DIVERGENCE this harness found before trusting a "
          "green GoldenFixtureTests run.")


if __name__ == "__main__":
    main()
