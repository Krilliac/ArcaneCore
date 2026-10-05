# Server spell flow continuation, 2026-10-04

Continues the [gameplay and content work](server-gameplay-20261004.md) in
`/workspace/arcanecore-work`, branch `codex/wave3-escrow-loot-cleanup`, based on
`7313b9eb089197b253dc51bd8bb5ceb803adcc6b`. The user requested continued server
development in goal mode with agent fanout. This tranche completes three
bounded spell flows using existing runtime seams. Changes remain local and
uncommitted, alongside the earlier continuation work.

## Implemented behavior

Player resurrection effects 18 and 113 send an offer instead of immediately
reviving the target. A response must name the offer's caster. Player-origin
acceptance uses the existing near or far teleport handshake, and life restoration
waits for acknowledgement and arrival at the captured destination. Restoration
clears ghost/root state, removes the body, restores bounded health and powers,
and queues the normal character snapshot. Requests are transient. Logout,
another revival and a new death invalidate them; duplicate responses cannot
replay restoration. Corpse targeting can resolve its released online owner
on another map during an ordinary cast bar. See
[player resurrection evidence](player-resurrection-20261004.md).

Intrinsic totem immunity removes foreign healing, energize, taunt, negative
aura and periodic regeneration effects. Self casts and the Shaman regeneration
family retain their source exceptions. Mixed spells still apply their eligible
effects; a target with no remaining effects is reported as `IMMUNE2` in
`SMSG_SPELL_GO`. The filter is shared by packet outcomes and effect application.
See [totem immunity evidence](totem-immunity-20261004.md).

Grounding's spell-magnet aura redirects eligible hostile magic spells to its
live caster, consumes a protection charge before hit resolution, and reports
the selected target in spell-go packets. All explicit effects share the
selection and redirected chains terminate there. The final charge removes
the source protection and party children. Damage follows normal combat and
can kill the totem; misses and non-damaging spells consume protection without
creating a death. Channels clean up their redirected target on cancellation
or target disappearance. See [Grounding evidence](grounding-totem-20261004.md).

No schema migration or new configuration switch is introduced. Retail defaults
remain in effect; the existing developer immunity toggle still controls immunity
enforcement. Source behavior was independently implemented in C# from pinned
vmangos commit `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`; upstream code and
content assets are not vendored.

## Regression and review evidence

Before implementation, both initial resurrection cases failed because no offer
was sent. Sixteen totem rule cases demonstrated missing intrinsic exclusions,
and the fully blocked spell packet case reported a hit instead of an immune
miss. The mixed immunity case and the held party-child cleanup boundary already
passed and remain regression coverage.

Independent read-only review caught resurrection offers being cleared during
the following corpse transition, normal cast bars losing a cross-map ghost,
the resurrection `LocationCasterDest` exception, Grounding's creature-type
exception, mixed-effect redirection, and redirected channel cleanup. These
were corrected with regressions before final combined validation.

The first World run exposed two fixture assumptions: combined unit/corpse
client requests need both inbound GUID fields, and a redirected negative aura
is still blocked by intrinsic totem immunity. The corrected tests check the
inbound layout and the redirected `IMMUNE2` outcome respectively. Neither
required weakening a production rule.

## Validation

The combined Release solution build passes with zero warnings and errors.
Focused Game checks pass **68/68** cases: 17 resurrection, 22 totem immunity,
26 spell magnet, two immunity packet cases and one held area-child boundary.
Focused production World checks pass **10/10** cases: seven resurrection,
two Grounding and one totem immunity. The build-5875 mock client self-test
passes **59/59 checks**, receiving 148 frames.

The full Release solution passes **14,128 tests**, with **eight fixture-dependent
skips** and no failures. This adds 78 passing cases to the preceding gameplay
baseline of 14,050. No provider or proprietary-data skip is counted as a pass.

| Test project | Passed | Skipped |
|---|---:|---:|
| Cryptography | 8,017 | 0 |
| Data | 796 | 6 |
| Game | 3,757 | 1 |
| MockClient | 195 | 0 |
| Realm | 37 | 0 |
| World | 1,326 | 1 |

Commands, using .NET SDK 10.0.301 from `global.json`:

```sh
dotnet build ArcaneCore.slnx -c Release --no-restore -m:1 --verbosity minimal
dotnet test ArcaneCore.slnx -c Release --no-build -m:1 --verbosity minimal
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
git diff --check
```

Logs are retained outside the worktree in `/workspace/scratch/`:
`spell-goal-release-build.log`, `spell-goal-focused-game.log`,
`spell-goal-focused-world.log`, `spell-goal-release-tests.log`,
and `spell-goal-mock-self-test.json`.
All commands exited successfully. `git diff --check` is clean. The skipped
cases require proprietary DBC/world data or the configured provider race
fixture; the external provider matrix remains unconfigured in this workspace.

## Acceptance boundaries

Tests use synthetic spell, creature and character fixtures through both the
Game engine and production World socket host. Real build-5875 client acceptance,
proprietary DBC/world content verification, and external MariaDB/PostgreSQL
provider runs remain pending. The resurrection request's two trailing boolean
bytes follow pinned vmangos and corroborating cmangos; wow_messages currently
documents only one. The discrepancy is recorded in the resurrection evidence.

Self-resurrection, pet resurrection and release-dialog buttons are separate
follow-ups. The teleport service cannot address a different instance on the
same map; such offers remain unaccepted. Grounding covers the documented
selector and eligibility rules, while full charm-level, existing-charmer,
hidden-GM and AoE-immune eligibility remain bounded follow-ups. General area
aura rank and tick fidelity, missile travel time and collision-pushed totem
placement are also outside this tranche.
