# Local source recovery and Soulstone requests, 2026-10-04

Development resumes on `codex/server-continue-20261004` in
`D:\ArcaneCore-continue-20261004`, based on current GitHub `main` at
`7313b9eb089197b253dc51bd8bb5ceb803adcc6b`. The older local acceptance
checkout remains pinned to `f8ae6e8` and was preserved.

## Recovered source

The previous cloud chat left five completed tranches uncommitted. GitHub does
not contain them. Their saved parent and worker edit records were read, then
reconstructed into this isolated checkout without executing archived commands.
Recovery applied 289 recorded changes with no unresolved patches, recovered
four literal source-file blocks, and restored 14 files containing additional
reviewed static edits. The complete original tree had no recorded digest;
this is verified reconstruction, not a claim of byte-for-byte identity.

Recovered behavior includes escrow/loot cleanup, bounded ban searches and chat
gates, active totems and skill auras, resurrection and Grounding flows, ghost
and aura lifecycle cleanup, existing-pet revival, and death/spell durability.
The individual earlier handoffs are preserved, including their historical
cloud paths and validation claims. This document records the current local run.

The recovered Release build had zero warnings/errors. Fresh full validation
passed 14,242 tests with six skips and no failures. Two content checks that
were skipped in the cloud ran here against available developer reference data.

## New Soulstone slice

All five vanilla Soulstone ranks now select the release-dialog spell before
death removes the nonpersistent Dummy aura. Selection verifies visual 99 and
icon 92 and preserves a previously selected server offer. Normal death
cleanup removes the consumed Soulstone buff while retaining its private
`PLAYER_SELF_RES_SPELL` value for the current death.

The production World handler accepts the empty build-5875 `CMSG_SELF_RES`.
The server selects its effect from the private field, so learning the internal
resurrection effect is unnecessary. Casts use normal cooldown and cast checks.
A completed attempt consumes the offer even if the effect is absent or the
cast is rejected. Settlement holds and map transit defer consumption. Any
other resurrection clears the old offer, and zero-health Alive state cannot
replay a death offer. Extra payload bytes do not select a spell.

Successful revival reuses the existing corpse/ghost cleanup and character
snapshot save. Socket regressions cover body and ghost states, malformed and
repeated requests, stored life, logout and relog. Pending-offer persistence
across dead relog is a separate remaining requirement.

References were read locally at their pinned revisions:

- vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`,
  `Player.cpp:1538-1570,19866-19939`, `SpellHandler.cpp:461-473`.
- wow_messages `70abb9deff0bb63440d8aeb4386b820653e8a176`,
  `wow_message_parser/wowm/world/resurrect/cmsg_self_res.wowm`:
  opcode `0x02B3`, empty payload.

No upstream implementation or proprietary asset is copied into the source.
No schema change or new configuration option is introduced.

## Verification

The preimplementation Game baseline reproduced six missing-behavior failures
and passed five guards. The final focused checks passed 65 Game cases and
18 World cases, including the new request path. The final Release build
has zero warnings/errors, using .NET SDK 10.0.401 under the repository's
`latestFeature` roll-forward policy. One final targeted read-only reviewer
found no blocking lifecycle defects. The final full suite passed 14,262 tests,
with six skips and zero failures; the two new Soulstone socket cases both passed.
The independent mock client passed all 59 checks for build 5875, receiving 148
frames. These results add 18 Game and two World cases to the recovered baseline.

Commands:

```powershell
dotnet build ArcaneCore.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test ArcaneCore.slnx -c Release --no-build --no-restore -m:1 --verbosity minimal
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
git diff --check
```

Local logs and recovery records live under
`C:\Users\Nathan\Documents\Codex\2026-10-04\c\work`.
The handoff and portable source patch are in the sibling `outputs` directory.
All source changes remain local and uncommitted; no push, merge, deployment,
database upgrade, or real-client session occurred.

## Remaining acceptance and development

- Reincarnation availability, Ankh consumption and reagent/cooldown behavior.
- Twisting Nether death selection and proc behavior.
- Pending self-resurrection offers across dead logout/relog and restart.
- Database-backed hunter-pet recovery and effect 109.
- Actual build-5875 client UI acceptance for the server's implemented flows.
- Full MariaDB/PostgreSQL provider qualification and remaining proprietary
  DBC/world-content checks; local skips and omitted provider theory members
  are not passes.
- M15 clustering and the broader roadmap remain separate work.
