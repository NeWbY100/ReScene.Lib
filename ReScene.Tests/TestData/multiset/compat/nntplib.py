"""Minimal shim for the stdlib `nntplib` module (deprecated Python 3.11, REMOVED in 3.13).

Task 9: `rescene/main.py`'s `_rarreader_usenet` (used internally by `create_srr_for_subs` ->
`extract_and_create_srr` when reading an extracted subs RAR's blocks — nothing usenet-specific
about our local, offline use of it) does `import nntplib` and references
`nntplib.NNTPTemporaryError` in an `except` clause; on Python 3.13+ the bare `import nntplib`
raises `ModuleNotFoundError`, which pyrescene's own `except: exit(1)` turns into a hard, opaque
crash regardless of `--no-isdb` (unrelated to ISDB; this is a SEPARATE stdlib removal). We never
actually hit a real NNTP/usenet error in this local, no-network golden-generation workflow, so
only the class NAMES need to exist for the import and the `except (..., nntplib.NNTPTemporaryError)`
type check to succeed — none of these are ever constructed or raised here. Mirrors the real
module's exception hierarchy (`NNTPError` base; `Reply`/`Temporary`/`Permanent`/`Protocol`/`Data`
subclasses) closely enough for that purpose; `NNTP`/`LONGRESP`/`_LONGRESP` are referenced only by
`usenet/srr_usenet.py`, which `bin/pyrescene.py`'s local `--vobsub-srr` path never imports, but are
included for a complete, drop-in-shaped shim.
"""


class NNTPError(Exception):
    pass


class NNTPReplyError(NNTPError):
    pass


class NNTPTemporaryError(NNTPError):
    pass


class NNTPPermanentError(NNTPError):
    pass


class NNTPProtocolError(NNTPError):
    pass


class NNTPDataError(NNTPError):
    pass


LONGRESP = ()
_LONGRESP = ()


class NNTP:
    pass
