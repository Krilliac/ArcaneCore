# Verified continuation: hunter abandon, weapon procs and combat health

Coordinator verification on 2026-10-04: Release zero warnings/errors; one
uninterrupted six-project suite 14,422 passed / six existing fixture skips /
zero failures; native mock self-test 59 checks. Focused Game26 / World18 /
SQLite cold-host1 checks pass. The initial failed full gate is retained as
development evidence, separate from the corrected candidate's passing gate.

- Hunter abandon queues deletion behind prior current/detached saves, invalidates
  cached reads before database awaits, and retains the delete tail for flush and
  shutdown. The socket test persists a pet and two cooldown rows before proving
  deletion, cold login without a pet, and Call Pet failure reason 7.
- White-hit trigger-2 weapon procs run after damage with a surviving victim,
  source-qualified hit/GCD/weapon/PPM rules, retained item GUID and cooldown
  metadata, and no charge or power consumption. Extra-attack, enchantment,
  ranged and special-attack producers remain pending.
- Aura116 enables combat spirit health regeneration using summed percentages.
  Food and rage decay remain out of combat. The socket fixture verifies a live
  100% modifier with an explicit duration and continued combat state.
- Quest visibility fixtures use a targeted per-viewer visibility veto so map
  updates cannot restore the nearby questgiver during the socket round trip.
  Runtime checks and test deadlines are unchanged.

Characters schema remains23. Source is local, unstaged and uncommitted on
`codex/server-continue-20261004`, base
`7313b9eb089197b253dc51bd8bb5ceb803adcc6b`. Original-client acceptance,
proprietary fixtures and external database providers remain pending. The charter
continues to include version-checked WoWWiki 1.12.1 and Wowhead Classic research.

Pinned behavioral references: vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`,
mangos-classic `8ec338a1704e7dcb1c0213eb7ed58f9231ade40f`; packet cross-checks
use wow_messages `70abb9deff0bb63440d8aeb4386b820653e8a176`.
