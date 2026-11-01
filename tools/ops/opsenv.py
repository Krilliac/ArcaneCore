"""Environment lookups shared by the Python operator tools (tools/ops/**, tools/ops/llm).

Every host-specific location comes from an environment variable, so nothing in the repository names a machine or a user. A tool that
needs a value which is not set stops with a message that names the variable; it never guesses a path.

  ARCANECORE_LIVE_PROFILE  the live server profile directory (the one that holds appsettings.json)
  ARCANECORE_LANES_ROOT    the directory that holds the lane worktrees and the ops output (_deploy, _diag, _wf, _plan)
  ARCANECORE_DATA_ROOT     extracted client data: terrain-5875, client-dbc-5875
  ARCANECORE_REFS_ROOT     reference checkouts and dumps: mangos-classic, vmangos, classic-db
  ARCANECORE_DEPLOY_ROOT   deploy artifacts and run logs (default <ARCANECORE_LANES_ROOT>/_deploy)

Documented in docs/ops/tooling.md.
"""
from __future__ import annotations

import os
import pathlib
import sys

LIVE_PROFILE = "ARCANECORE_LIVE_PROFILE"
LIVE_ROOT = "ARCANECORE_LIVE_ROOT"
LANES_ROOT = "ARCANECORE_LANES_ROOT"
DATA_ROOT = "ARCANECORE_DATA_ROOT"
TERRAIN_DIR = "ARCANECORE_TERRAIN_DIR"
REFS_ROOT = "ARCANECORE_REFS_ROOT"
VMANGOS_REF = "ARCANECORE_VMANGOS_REF"
WOTLK_REF = "ARCANECORE_WOTLK_REF"
DEPLOY_ROOT = "ARCANECORE_DEPLOY_ROOT"
SONDER_MCP_URL = "ARCANECORE_SONDER_MCP_URL"
GH_REPO = "ARCANECORE_GH_REPO"


def optional(name: str) -> str | None:
    value = os.environ.get(name, "").strip()
    return value or None


def require(name: str, purpose: str, option: str | None = None) -> str:
    """The value of an environment variable, or exit with a message naming it and what it is for (and the command-line option that supplies it, when given)."""
    value = optional(name)
    if value is None:
        instead = f"Set it, or pass {option}" if option else "Set it"
        sys.exit(f"{name} is not set ({purpose}). {instead}; see docs/ops/tooling.md.")
    return value


def lanes_root() -> pathlib.Path:
    return pathlib.Path(require(LANES_ROOT, "the directory that holds the lane worktrees and _deploy/_diag/_wf"))


def data_root() -> pathlib.Path:
    return pathlib.Path(require(DATA_ROOT, "the directory that holds terrain-5875 and client-dbc-5875"))


def refs_root() -> pathlib.Path:
    return pathlib.Path(require(REFS_ROOT, "the directory that holds mangos-classic, vmangos and classic-db"))


def live_profile() -> pathlib.Path:
    explicit = optional(LIVE_PROFILE) or optional(LIVE_ROOT)
    if explicit:
        return pathlib.Path(explicit)
    return pathlib.Path(require(LIVE_PROFILE, "the live server profile directory that holds appsettings.json"))


def deploy_root() -> pathlib.Path:
    explicit = optional(DEPLOY_ROOT)
    return pathlib.Path(explicit) if explicit else lanes_root() / "_deploy"


def repo_root() -> pathlib.Path:
    """The checkout this file lives in (tools/ops/opsenv.py is two levels below the root)."""
    return pathlib.Path(__file__).resolve().parents[2]


if __name__ == "__main__":
    import argparse

    ap = argparse.ArgumentParser(description="Shared environment lookups for ArcaneCore operator tools.")
    ap.parse_args()
