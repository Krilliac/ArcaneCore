# Twisting Nether and reference relog behavior — 2026-10-04

This active-goal tranche extends the local Reincarnation/reagent candidate on
`codex/server-continue-20261004`, based on `7313b9eb089197b253dc51bd8bb5ceb803adcc6b`.
Earlier work is preserved and remains local/uncommitted.

## Implemented

The death selector now considers each actually applied Dummy aura effect.
Twisting Nether passive `23701` receives a uniform ten-percent roll and
selects internal resurrection `23700`. Existing selected offers are preserved
without rerolling. Passive Nether holders survive ordinary death cleanup.
The existing normal self-resurrection cast uses the imported effect's vitals,
clears its offer after an attempt, removes corpse/ghost state and saves life.

Both pinned vanilla cores use a priority/ordering rule in which a successful
Nether roll can override Soulstone, even though their comments label Soulstone
as priority three. This implementation preserves their actual branch behavior,
including either aura insertion order. Failed Nether rolls leave Soulstone or
eligible Reincarnation available. Nether does not consume an Ankh; the
Reincarnation fallback pays its normal imported reagent cost.

## Relog policy correction

The earlier handoff incorrectly listed pending self-resurrection offer
persistence as unfinished vanilla behavior. The reference `SaveToDB` lists
omit the private `PLAYER_SELF_RES_SPELL`; loading creates fresh fields, and
the dead-player loader restores ghost/body state without restoring this offer.
The source-grounded default is preserved. `PlayerLife.ApplyVitals` now explicitly
clears the transient field even when a loading adapter reuses an object.

A ghost relog regression and an actual SQLite cold-start regression prove
that the body and ghost survive while the previous offer does not reappear
or cause a new death roll. A persistent-offer feature would be a separately
documented server policy rather than an assertion of vanilla fidelity.

## Grounding and added sources

- vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`,
  `Player.cpp:19889-19939` for selection and `16347-16536` for the save list;
  `LoadCorpse` at `15418-15440` for ghost/body restoration.
- cmangos `8ec338a1704e7dcb1c0213eb7ed58f9231ade40f`,
  `Player.cpp:18746-18787` corroborates selection and `SaveToDB` omits the offer.
- The developer requested [WoWWiki's patch 1.12.1 archive](https://wowwiki-archive.fandom.com/wiki/Patch_1.12.1)
  and [Wowhead Classic](https://www.wowhead.com/classic) on 2026-10-04. They are
  now in charter §4 and the roadmap's reference catalog, with patch/version,
  date and vanilla-corroboration rules.
- [Wowhead's Classic item 19290](https://www.wowhead.com/classic/item=19290/darkmoon-card-twisting-nether)
  corroborates the item's ten-percent resurrection tooltip. Its comments
  contain later-expansion observations, so they are not treated as protocol
  or original-client authority. The exact 1.12.1 wiki page was discoverable
  through search; direct access was blocked by the site's robots policy.

No upstream code or proprietary asset is copied into the project. No schema
migration or new configuration option is introduced.

## Evidence

The initial Game baseline reproduced nine failures and passed five guards.
Focused validation passes 40 Game cases, 14 World cases and three persistent
loopback cases. Fourteen new Game cases, three World cases and one SQLite
restart case belong to this tranche. The integrated Release build has zero
warnings/errors. Final full-suite validation passes 14,311 tests with six
fixture skips and no failures. Cryptography 8,017; Data 797 (five skips);
Game 3,910; MockClient 196; Realm 37; World 1,354 (one skip). The standalone
mock passes 59/59 build-5875 checks and receives 148 frames. One final targeted
read-only reviewer found no actionable blockers in the lifecycle or source
policy changes. Real-client and unconfigured external providers remain
unverified.

The first relog test asserted an asynchronous stored life immediately after
`SMSG_LOGOUT_COMPLETE`. The fixture now waits for the actual saved ghost/body
snapshot before checking it; no production save behavior was weakened.

Actual client UI, remaining content and external provider qualification are
still pending. Broader goal work includes database-backed pet recovery,
cast-item/owner context, the remaining roadmap and M15 clustering. The active
goal is not declared complete by this milestone.
