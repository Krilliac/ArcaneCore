# Warlock, mage and utility spells (wave 4 lane "warlock-mage-utility")

Status: in progress on branch `claude/vw5-warlock-mage-utility` (base `claude/vw4-integration` 7313b9e). Each section below is one
delivered slice with its scope, limits, provenance and the file list. Nothing is copied from the reference servers; every formula
cites the vmangos source it was checked against (`D:\refs\vmangos`, GPL, read-only).

Slices the design listed and this lane did not deliver are in "Not delivered" at the end, with the reason.

## wlm-02 Spell scripts (`Game/Spells/Scripts`, `World/Spells/Utility/SpellScriptFeature.cs`)

vmangos keeps class logic in `SpellScript` objects keyed by spell id (`src/scripts/spells/spell_warlock.cpp`, `spell_mage.cpp`).
`ISpellScript` + `[SpellScript(ids...)]` + `SpellScriptRegistry` (reflection discovery, one script per spell id, a duplicate id,
a missing attribute or an empty id list throws) + `SpellScriptDispatcher` mirror it without editing `SpellSystem`:

| Hook | Where it runs | vmangos |
|---|---|---|
| `OnCheckCast` | `ISpellCastCheck`, phase `Final`, order `int.MaxValue` (after every other check, also for triggered casts) | Spell.cpp:6480-6481 |
| `OnCast` | `ISpellCastObserver.OnCast`: power and ammo are taken, targets and effects not yet run | Spell.cpp:3716-3724 |
| `OnEffectExecute` | chained DUMMY (3) and SCRIPT_EFFECT (77) handlers; runs before the previously installed handler | Spell.cpp:5254-5257 |
| `OnSuccessfulDispel` | chained DISPEL wrapper: only when the target lost at least one aura stack | scripts/spells |

SCRIPT_EFFECT has no behaviour of its own in vmangos (SpellEffects.cpp:3685-3700, :4560-4570 fall through to the database script
table, which is empty here), so the dispatcher installs a no-op for it: the mage Teleport spells and the Create Healthstone ranks
no longer log "effect 77 is not implemented". Chaining keeps any handler another feature installed before the dispatcher; a feature
that replaces DUMMY, SCRIPT_EFFECT or DISPEL after it without chaining would drop the scripts (the world feature attaches after
`SpellFeature`, alphabetically).

Limits: `OnEffectExecute` is raised for DUMMY and SCRIPT_EFFECT only (vmangos raises it before every effect; no script of this lane
needs the others). vmangos `OnSummon` is not provided because nothing here raises it yet. When the crafting lane's cost hook
(`ISpellCostTaker`) merges, script `OnCast` must stay after it (a reagent failure must never fire a script); the integrator checks
the observer order. No script ships in this slice: the scripts of later slices register their ids with the attribute.

Tests: `tests/ArcaneCore.Game.Tests/Spells/Utility/SpellScriptTests.cs` (12 tests: hook order, the late-check ordering, power already
taken at `OnCast`, no unsupported-effect log, dispel only on removal, duplicate/unknown/unmarked scripts, double install, chaining
before and after).

## wlm-03 Pet and minion targets (`Game/Spells/Utility/Targets`, `World/Spells/Utility/PetTargetFeature.cs`)

Three implicit targets the built-in switch did not serve (they were logged as "implicit target N is not implemented" and hit nothing):

| Target | Meaning | vmangos |
|---|---|---|
| 5 `UNIT_CASTER_PET` | the caster's pet (`UNIT_FIELD_SUMMON`), else the creature it charms (`UNIT_FIELD_CHARM`) | Spell.cpp:2212-2222 |
| 27 `UNIT_CASTER_MASTER` | the charmer or owner of the caster (Sacrifice) | Spell.cpp:2770-2772 |
| 32 `LOCATION_UNIT_MINION_POSITION` | caster position + (effect radius, orientation + 0.25 pi); radius 0 when the effect has no radius index; location-only, the caster carries the effect (every Summon Pet spell) | Spell.cpp:2975-3022 |

`CasterPetCastCheck` (phase `Items`, before the equipment check) is the "check pet presents" block (Spell.cpp:5545-5568): no pet is
`NO_PET`, a dead pet `TARGETS_DEAD`, and a triggered cast is `DONT_REPORT`. Limits: vmangos answers `DONT_REPORT` only for a cast triggered
by an aura (`m_triggeredByAuraSpell`); the check context carries only "triggered", so every triggered cast is silent. The pet line-of-sight
check (:5567) is not made. The point of target 32 is the unclamped offset at the caster's Z (vmangos `GetFirstCollisionPosition`; the same
limit as the existing caster-relative locations 41-47). Installing twice throws (a second selector for one target id fails closed).

Tests: `tests/ArcaneCore.Game.Tests/Spells/Utility/PetTargetTests.cs` (9 tests, run RED against an empty `Install` first: 7 failed).

## wlm-07 Soul Shards (`Game/Spells/Warlock/ChannelDeathItemAura.cs`, `SoulShardRules.cs`)

`SPELL_AURA_CHANNEL_DEATH_ITEM` (86) had no handler, so Drain Soul and Shadowburn (14 DB spells, 9 trainer ranks) never made a Soul Shard.
`ChannelDeathItemAura` is an `ISpellHandlerModule` (discovered, no world feature needed) implementing vmangos
`Aura::HandleChannelDeathItem` (SpellAuras.cpp:2833-2880):

* only when the holder was removed because its target died: `SpellAuraHolder.RemovedByDeath` (new internal flag, set by
  `SpellSystem.RemoveAurasOnDeath` before the remove handlers run; two shared-file edits, `SpellAuraHolder.cs` and `SpellSystem.Death.cs`,
  one line and one property each) is the vmangos `AURA_REMOVE_BY_DEATH` remove mode. Expiry, dispel and cancel make nothing;
* the caster must be a player still in the world; the effect value is the count and the effect's `ItemType` the item;
* warlock family spells make one item per warlock and target: a second channel-death-item aura of the same caster still on the victim
  postpones it (`HasAuraTypeByCaster`), so Shadowburn plus Drain Soul give one shard and two warlocks one each;
* a Soul Shard (6265) needs `Player::IsHonorOrXPTarget` (victim above the caster's gray level via `ExperienceFormulas.GrayLevel`, not a
  totem or pet; Player.cpp:19943-19957) and, for a creature victim, a tap by the caster;
* no room: `SendEquipError` is sent whenever the store is not fully ok (also for a partial fit), the part that fits is stored, nothing when
  nothing fits; the item push (`created`) goes to the caster.

Limits, recorded: (1) there is no tap list in the combat system (the threat-and-aggro lane owns it), so `ISoulShardTapSource` is a seam
(`SoulShardRules.UseTapSource`) with no shipped implementation, and without one every kill counts as tapped; the loot recipients cannot stand
in because the loot bag only exists after the death; (2) the creature template's xp multiplier of 0 and `UNIT_STATE_NO_KILL_REWARD` are not
modelled; (3) no `Spells:Warlock:SoulShards:RequireTap` switch was added: the design's switch would only have let a host skip a retail rule, and
nothing needs it until a tap source exists.

Tests: `tests/ArcaneCore.Game.Tests/Spells/Utility/SoulShardTests.cs` (10 tests; RED first: 6 failed, the 4 negative cases passed by design).
