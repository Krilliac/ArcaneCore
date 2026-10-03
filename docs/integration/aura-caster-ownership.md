# Aura caster ownership

An aura on another unit must keep the identity of the unit that originally cast it. A new login can create a different `Player` with the same character GUID; that replacement must not become the actor of an old periodic damage, healing, or triggered-spell effect, receive its threat or kill credit, or delay it through the replacement's quest settlement.

This is a bounded ownership change in `SpellAuraHolder.cs`, `SpellSystem.Auras.cs`, and `SpellSystem.cs`. It preserves the existing missing-caster fallback and wire metadata. It adds no schema module, persisted aura state, broader spell targeting rules, reward feature, or clustering behavior.

## Provenance and qualification

Source `838dbc952a8126fee8f7d2ef5d5e7761cbb2e40d` was qualified before preserving merge `407bfb1fb86dade233d954d0e65668fafb9f3679`. Final canonical head and CI are recorded on [draft #11](https://github.com/Krilliac/ArcaneCore/pull/11) and the integration outcome.

| Item | Exact identity or result |
| --- | --- |
| Qualified starting canonical head | `4cf5e09c2b2b9ee0392d2dba3d704174079c347f` |
| Isolated source branch | `codex/aura-caster-ownership-20261003` |
| Isolated source worktree | `ArcaneCore-aura-ownership` |
| Qualified committed source SHA | `838dbc952a8126fee8f7d2ef5d5e7761cbb2e40d` |
| Source full-provider CI run, exact head, conclusion | SUCCESS at exact source, [run 37108332318](https://github.com/Krilliac/ArcaneCore/actions/runs/37108332318); **9,019 passed**, zero failures/skips; Release zero warnings/errors; job 111161119860 completed **08:04:52 UTC**. Standalone **59 checks / 142 frames / 6,297 ms**. |
| Canonical preserving merge SHA | `407bfb1fb86dade233d954d0e65668fafb9f3679`, after exact-source full CI success |
| Final canonical documentation head | Recorded on [draft #11](https://github.com/Krilliac/ArcaneCore/pull/11) and the integration outcome |
| Final canonical full-provider CI run and conclusion | Exact links/results recorded on [draft #11](https://github.com/Krilliac/ArcaneCore/pull/11) and the integration outcome |
| Draft PR #11 head/accounting update | Canonical candidate remains [draft #11](https://github.com/Krilliac/ArcaneCore/pull/11); source accounting is in [the fleet ledger](fleet-20261003.md) |
| Schema/module changes in this slice | None; existing auth/characters/world versions remain `2/6/6` |

Before the change, holders retained the caster GUID and resolved that GUID in the target's map when checking settlement or executing periodic effects. Corrected baseline regressions reproduced wrong actor attribution, replacement healing threat, settlement pauses, stale stacking and late cleanup. Against exact retained production, 13 cases failed for the ownership gaps and seven compatibility cases passed; all 20 passed on the fix. This document does not claim that previously stored character data was corrupted.

## Ownership contract

Each holder captures an `AuraCasterOwner` token at application. The token contains a `WeakReference<Unit>` and a revocation flag; the system associates tokens with exact `Unit` objects through a `ConditionalWeakTable`. A holder does not gain a strong reference to the caster or its session, and there is no permanent GUID tombstone. Existing target ownership is unchanged. The spell system and these lifecycle operations run on the world thread.

An actor is usable only when the token is unrevoked, its exact unit still exists and is in the world, its map is the holder target's map, and the normal unit resolver returns that same instance for the stored caster GUID. Matching GUIDs alone are insufficient. A foreign resolver result, different map, missing unit, or revoked token yields no caster.

`SpellSystem.RemoveUnit(unit)` revokes ownership before looking for spell state. This also covers casters whose own cast/cooldown state has already become idle and been pruned, while an aura remains on another target. It forgets state only if the state under the GUID belongs to that exact unit; delayed cleanup of an old unit cannot remove a replacement's state. Non-transit forgetting also revokes the token. Ordinary idle pruning does not revoke it, because the live caster may still own a foreign aura.

Logout revocation remains permanent for the old token. Re-adding even the same object and casting again obtains a fresh token; old foreign holders cannot regain ownership. A newly created same-GUID player also gets a different token.

Periodic damage, periodic healing, and periodic triggered spells use the exact resolved caster when available. Otherwise they continue the existing behavior of using the aura target as the effect actor. An expired owner therefore does not transfer attribution to a replacement session. This fallback does not introduce persisted or offline aura gameplay. The holder retains its original `CasterGuid` and caster level; periodic aura logs for damage, healing, and energize retain the original GUID. A triggered spell's own combat log uses the actual resolved or fallback actor, as before; it does not promise the old caster GUID in that new spell's log. Target-only periodic energize behavior is unchanged.

Quest settlement pauses a holder only when its target or its currently valid exact caster is pending. During that pause, both duration and periodic timers remain unchanged. A pending replacement with the same GUID cannot pause the old holder. Its old owner's missing-caster fallback continues under the existing target settlement checks.

A stackable recast from the same ownership token retains the existing stack increment, clamp, amount recalculation, and duration refresh. A different token, including one created after revocation for the same object or GUID, replaces the matching old holder and starts at one stack. Existing rules for positive aura replacement and non-stackable recasts remain in place.

A legitimate far map transit is distinct from world departure: `IsInTransit` retains the caster's spell state while that unit is temporarily outside map simulation. A target that is itself in transit retains its existing pause behavior. An aura left on a target in another map cannot resolve that caster there and follows the existing target fallback. When the exact owner and target again share a map and normal resolution returns the owner, attribution can resume without a new ownership lifetime. Ordinary map movement does not invoke the world logout revocation hook. Actual logout followed by re-entry does.

## Proof matrix

All 20 focused cases passed, and exact committed-source native/provider suites include them. World names below are from `tests/ArcaneCore.World.Tests/Spells/AuraCasterLifecycleTests.cs`; Game names are from `tests/ArcaneCore.Game.Tests/Spells/AuraCasterOwnershipTests.cs`. The matrix distinguishes direct execution from source-only lifetime arguments.

| Boundary | Source assertions inspected or required | Qualification |
| --- | --- | --- |
| Same-GUID replacement, periodic damage and trigger | `AuraCasterLifecycleTests.DepartedCasterAura_ActualCreatureDeathDoesNotCreditSameGuidReplacement` exercises public world logout/re-entry, the actual World resolver and damage sink, creature death, the combat death event, and `QuestObjectiveAdapter`. It asserts target fallback as killer, no quest kill credit, no replacement threat, and unchanged holder GUID. Theory covers both effect paths. | PASS; intended defect reproduced against retained baseline |
| Same-GUID replacement, periodic healing | `DepartedHealCasterAura_RealHealingThreatNeverMovesToSameGuidReplacement` uses the real heal/threat path. Healing continues, threat stays on the fallback target, and the replacement remains outside combat. | PASS; intended defect reproduced against retained baseline |
| Ordinary map movement with the same owner | `SameOwnerMapTransfer_PreservesActualPeriodicKillAndQuestCredit` moves both exact objects atomically between real maps on the world thread, then checks the same holder, actual death attribution, and quest credit to the original owner. This is not a client far-transfer handshake test. | PASS; retained compatibility behavior |
| Delayed old cleanup | `LateOldCasterRemoval_DoesNotForgetReplacementSelfAura` invokes old-instance `RemoveUnit` and `WorldRuntime.RemovePlayer` after replacement login. The replacement and its fresh self aura survive and continue healing. | PASS; intended defect reproduced against retained baseline |
| Same-object logout and re-entry | `LogoutAndSameObjectReentry_FreshCastCannotReactivateOldForeignAuraOwnership` checks public logout/re-entry and a fresh cast; the old foreign damage holder still uses target fallback and gives no quest kill credit. | PASS; intended defect reproduced against retained baseline |
| Revocation without caster spell state; same-object re-entry | `RevokedCaster_ReaddingSameObjectDoesNotReviveOldAuraOwnership` covers explicit removal after idle pruning and ordinary non-transit `Update` forgetting. Both routes re-add the same object and require old-holder target fallback. | PASS; intended defect reproduced against retained baseline |
| Weak ownership lifetime | Production inspection finds only a weak caster reference in the holder token and a `ConditionalWeakTable` association, with no GUID tombstone. The inspected Game file does not include a forced-GC collection case; this is a source-level ownership argument, not measured runtime collection behavior. | Source reviewed; no forced-GC execution claim |
| Missing actor and wire provenance | `MissingCaster_PreservesTargetFallbackAndOriginalPeriodicLogGuid` covers damage, heal, and trigger; it decodes periodic aura packets and the triggered combat packet with their distinct GUID contracts. | PASS; retained compatibility behavior |
| Replacement settlement isolation | `ReplacementCasterPending_DoesNotPauseOrReceiveAttributionForOriginalAura` covers damage, heal, energize, and trigger. Duration/ticks advance, effects remain on the target, a recording sink never attributes to the replacement, and packet fields are decoded. | PASS; intended defect reproduced against retained baseline |
| Valid caster and target settlement holds | `SameCaster_ReapplicationStacksRefreshesAndStillPausesWhileThatActorIsPending` preserves the exact owner's hold. `TargetPending_StillPausesAnOrphanAuraWithoutCatchingUp` preserves the target hold, then resumes a single normally scheduled tick after release. | PASS; retained compatibility behavior |
| Same-owner and replacement stacks | `SameCaster_ReapplicationStacksRefreshesAndStillPausesWhileThatActorIsPending` checks increment, cap, refreshed duration, and amounts. `ReplacementCasterReapplication_ReplacesOldHolderAndResetsStackDurationAndTickSchedule` checks a new same-GUID player's fresh holder, one stack, reused visible slot, full duration, and new tick schedule. A fresh token after same-object revocation also prevents stacking by source contract; that reapplication variant has no dedicated case in the inspected file. | PASS; same-object reapplication variant reviewed at source only |
| Late cleanup with replacement cooldowns | `LateRemovalOfOldCaster_PreservesReplacementAuraCooldownAndAttribution` checks that delayed removal preserves the replacement's fresh self aura, cooldown, and correct healing actor while the old foreign aura uses fallback. | PASS; intended defect reproduced against retained baseline |
| Far transit lifecycle | `RealFarTransit_SameCasterReturningToTargetMapKeepsOriginalOwnership` uses real `TeleportService.TeleportTo`, world ticks, and `HandleWorldportAck` for a round trip. Its own aura survives transit; the foreign aura uses target fallback while the caster is absent, then resumes attribution to the same exact owner on return. It records actor and timing; this is not an actual client handshake run. | PASS; retained compatibility behavior |

The inspected World harness runs `WorldRuntime` with `SpellFeature`, the real World resolver/damage sink, map combat, and `QuestObjectiveAdapter`, using synthetic spell/creature records and recording player sessions. It uses a no-op save queue and no client socket. Its six cases include two parameterized damage/trigger cases and four facts. Quest events reach a recorder through the actual adapter; this is not a proof of persisted quest counters. The harness can establish actor, threat, lifecycle, and quest-event behavior, but cannot establish client UI, network relog, saved aura state, or database persistence.

The inspected Game file defines fourteen cases: nine theory cases and five facts. Its damage sink records actor/effect details over the existing health-only sink, and its fake sessions capture packets for field decoding. It exercises the real in-process teleport service with acknowledgments and world ticks, but does not use a real client, network session, or database. Missing-caster collection and arbitrary custom-resolver behavior are not separately proven by that harness.

| Required root gate | Result |
| --- | --- |
| Focused Game/World ownership assertions on frozen source | PASS: **14 Game + 6 World**, zero failures/skips |
| Same regressions against exact retained baseline production; expected failures recorded | Exact `4cf5e09`: Game 8 failed/6 passed, World 5 failed/1 passed; zero errors/skips. Final tests detect the intended ownership defects. |
| One targeted final ownership/lifecycle review | No material production finding in the focused static review; executable verification owned by the integrator |
| Exact committed-source full Release build, warning/error counts | PASS: zero warnings/errors, **21.88 seconds**, SDK 10.0.401 |
| Full native suite counts, failures, skips, providers | PASS: **8,907**, zero failures/skips: crypto 8,005 / SQLite data 78 / Game 450 / mock 139 / Realm 3 / World 232 |
| Standalone mock-client result and duration | PASS: **59 checks / 142 frames / 6,877 ms**; existing scenario retained |
| Exact committed-source full-provider CI succeeds before incorporation | SUCCESS, [run 37108332318](https://github.com/Krilliac/ArcaneCore/actions/runs/37108332318): **9,019**, zero failures/skips; crypto 8,005 / data 190 / Game 450 / mock 139 / Realm 3 / World 232; SQLite, MariaDB 10.11 and PostgreSQL 16 |
| Canonical preserving merge, final full CI, normal push, draft PR provenance | Merge `407bfb1fb86dade233d954d0e65668fafb9f3679` preserves source; final exact-head evidence recorded on [draft #11](https://github.com/Krilliac/ArcaneCore/pull/11) and the integration outcome |

Two `Assert.Single` predicate-overload analyzer corrections preserved the Game assertions. The World fixture's bare creature has no `CreatureMapSystem`; an unrelated creature corpse-timer assertion was corrected while retaining zero health, actual combat corpse state, exact kill actor, quest-event and threat checks. Corrected baseline and final runs supplied the counts above. Initial compilation/fixture logs were retained, not counted as ownership proof.


## Client acceptance and next handoff

Computer Use is **deferred by the user** to their selected desktop-enabled chat. No Windows/client interaction is needed for this slice's deterministic implementation and coding proof. When Nathan resumes actual acceptance, the parent should notify him that the Windows PC is needed for the two-session aura/logout/relog/UI observations. Do not launch an alternative client route, operate the selected chat, or reuse that session's files from this coding lane.

The separately supplied real-client observations remain attributed only to `f8ae6e8b5f805e94f145194a82f80e876fa2dec3`: auth/realm list, character creation, world entry, and bounded movement/jump observations. They are user-reported evidence, not this lane's independent verification. Standard logout, a fresh login with saved position, and world restart/relog were not observed. See [the reported client run](client-run-f8ae6e8-20261003.md).

The separately selected NPC/quest UI candidate remains `248accc71acbe70144b92c33761d8f9ae0ab07cc`, with real greeting/menu/accept/kill/choice-reward/relog acceptance pending. See [the quest UI handoff](quest-ui-acceptance.md). Neither pin is silently advanced to this aura source or the canonical integration head.

Actual two-session aura acceptance remains **PENDING**: apply an aura from one live session to another unit, observe its ticks, depart the caster, relog that character as a fresh session, and check that old-holder damage/healing/trigger effects, threat, and quest credit do not bind to the replacement. The selected client chat must choose and record a qualified aura-enabled candidate and suitable disposable content before that run; neither the empty f8 fixture nor the bounded quest UI fixture is claimed to supply it. Same-owner map transfer and logout/re-entry observations must be attributed to the tested candidate. No new client actions or fixture setup are performed by this draft.

Remaining acceptance also includes the pending f8 normal logout/relog/restart sequence and the separate NPC/quest UI run. This coding qualification cannot establish broad spell coverage, complete aura stacking rules, terrain collision, clustering, or general playability. Existing auth material and client assets remain outside repository/share bundles.
