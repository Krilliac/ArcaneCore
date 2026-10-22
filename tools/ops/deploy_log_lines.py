"""The world-log lines a deploy must see before it may call a started World healthy.

Shared by tools/ops/deploy/deploy-live.py and tools/ops/deploy/deploy-post-checks.py so both gate on the same list. It sits directly in
tools/ops (not tools/ops/deploy) because both scripts put tools/ops on sys.path, which also holds under `python -I`.

Each line is matched as a prefix pattern: `{n}` stands for any run of digits, so a content update (more maps, areas, item sets ...) does
not turn a healthy deploy into a failed one. Every line listed here is blocking: a missing line stops the deploy before it is marked healthy.
"""
from __future__ import annotations

import re

_COUNT = "{n}"

# Required of every deploy.
_ALWAYS = (
    "Terrain: reading .map files from",
    "Collision: vmaps from",
    "Collision: navmeshes from",
    "loaded {n} maps, {n} areas",
    "loaded {n} graveyards and {n} graveyard-zone links",
)

# The bot-start line is not listed: it is logged once per started bot, so it is absent when none is expected (no enabled bots or MaxBots=0);
# both deploy scripts already require the started-bot count to reach min(enabled, MaxBots).

# Required unless the deploy leaves GameObjects:TransportAnimationDbcPath unset (--no-elevators).
_ELEVATORS = ("elevators and trams:",)

# Required unless the deploy leaves World:Transports:Enabled alone (--no-transports).
_TRANSPORTS = ("Transports: {n} routes, {n} ships sailing",)

# Required unless the deploy leaves the optional client data keys alone (--no-optional-data).
_OPTIONAL_DATA = (
    "Loaded {n} item sets",
    "Loaded {n} random properties and {n} item_enchantment_template entries",
    "Loaded {n} page texts",
    "Enchanting: {n} enchantments",
    "appearance is checked against {n} CharSections and {n} CharacterFacialHairStyles rows",
)


def expected_lines(no_transports: bool, no_optional_data: bool, no_elevators: bool) -> list[str]:
    """The line templates the world log must contain for a deploy with these options."""
    lines = list(_ALWAYS)
    if not no_elevators:
        lines += _ELEVATORS
    if not no_transports:
        lines += _TRANSPORTS
    if not no_optional_data:
        lines += _OPTIONAL_DATA
    return lines


def _pattern(template: str) -> re.Pattern[str]:
    return re.compile(r"\d+".join(re.escape(part) for part in template.split(_COUNT)))


def check_log(text: str, no_transports: bool, no_optional_data: bool, no_elevators: bool) -> dict[str, bool]:
    """Each expected line template mapped to whether the log text contains a line matching it."""
    return {t: _pattern(t).search(text) is not None for t in expected_lines(no_transports, no_optional_data, no_elevators)}
