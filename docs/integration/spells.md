# Integration notes: Spells

Area: the core of the spell system (branch `feat/spells`). It is built on the seams in
[seams.md](seams.md) (PR #1, `feat/fleet-plan`).

## Assigned schema versions

| Component | Version | Module | Tables |
|---|---|---|---|
| world | **5** | `ArcaneCore.Data.Content.Spells.SpellWorldDataModule` | `spell_template`, `spell_cast_times`, `spell_duration`, `spell_range`, `spell_radius`, `playercreateinfo_spell`, `spell_target_position` |
| characters | **4** | `ArcaneCore.Data.Characters.Spells.CharacterSpellDataModule` | `character_spell` |

Assigned in the [2026-10-03 integration candidate](fleet-20261003.md). The source
branch originally requested world v2 and characters v3.

## Shared files edited

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.World/Handlers/LoginSequence.cs` | `SendInitialPacketsBeforeAddToMap` sends `SpellFeature.BuildInitialSpells(player)` (resolved with `session.Services.GetService<SpellFeature>()`). If the feature isn't registered it falls back to the old empty `CharacterPackets.BuildInitialSpells()`. Two `using` lines were added. | SMSG_INITIAL_SPELLS from the database (vmangos `Player::SendInitialSpells`). LoginSequence is not on the avoid list, and the seam has no other hook in this stage. |
| `tests/ArcaneCore.Data.Tests/M6StoreTests.cs` | `M5CharactersDatabase_UpgradesToVersion2` now asserts `CharacterDbContext.Schema.CurrentVersion` instead of the literal `2`. | Any characters `IDataModule` (here v3) raises the current version, so the upgrade test failed for every area. The intent (an M5 database upgrades to the current version) is unchanged. |
| `ArcaneCore.slnx` | One additive `<Project>` line for `tools/spell-import/ArcaneCore.SpellImport.csproj`. | Lets the DBC importer tool build in CI. |

Nothing on the avoid list was touched (WorldServiceCollectionExtensions, ChatHandlers,
CharacterHandlers, WorldRuntime, DbContexts, WorldTestHost).

## New folders (owned by this area)

- `src/ArcaneCore.Game/Spells/`: the world-thread spell engine (`SpellSystem`), packets, auras and seams.
- `src/ArcaneCore.Data/Content/Spells/`, `src/ArcaneCore.Data/Characters/Spells/`: rows, data modules, EF stores and the DBC reader/importer.
- `src/ArcaneCore.World/Spells/`: `SpellFeature` (IWorldFeature), `SpellHandlers` (IOpcodeHandlerGroup), `SpellCommands` (ICommandGroup), the spellbook cache and the store factory.
- `tools/spell-import/`: a console tool that imports the DBCs.
- `tests/**/Spell*`.

## Seams this area offers to other areas

| Need | Seam |
|---|---|
| Creatures as spell targets or casters | Set `SpellFeature.System.Units` to an `ISpellUnitResolver` that also finds creatures. The default `MapPlayerResolver` only finds players. |
| Real damage, healing, threat and death | Set `SpellFeature.System.Damage` to an `IDamageSink`. The default `HealthOnlyDamageSink` only clamps health, and leaves 0 health to combat. |
| Far teleports | Set `SpellFeature.System.Teleports` to an `ITeleportSink`. The default `NearTeleportSink` handles the same map only. It sends MSG_MOVE_TELEPORT_ACK. The client's ack handler belongs to the movement/map area. |
| New spell effects and auras | `SpellSystem.RegisterEffect(SpellEffectName, handler)` and `SpellSystem.RegisterAura(AuraType, AuraHandler)`. Call them from your own `IWorldFeature.Attach` after resolving `SpellFeature`. |
| New spell effects and auras without editing a shared file | Implement `ISpellHandlerModule` (`Register(SpellSystem)`) in `ArcaneCore.Game`. It is found by reflection, ordered by full type name, and applied by the `SpellSystem` constructor; replacing an installed handler fails at startup. See [spells area](../areas/spells.md#handler-modules-and-spell-breadth). Handlers registered by modules are not "built-in" for `HasBuiltInEffectHandler` (only `TeleportUnits` and `Summon` are asked). |
| Teaching spells (trainers, quests, items) | `SpellFeature.System.LearnSpell(player, spellId)`. It persists the spell, sends SMSG_LEARNED_SPELL, and applies passives. |
| Casting from other systems (items, procs, scripts) | `SpellFeature.System.CastSpell(caster, spellId, targets, triggered)` |
| Interrupting (combat damage, movement) | `SpellFeature.System.CancelCast` / `CancelChannel` |
| Preflighting a teleport or a summon (quest rewards) | `ITeleportSink.CanTeleport` and `ISpellSummonSink.CanSummon` ask, without side effects, whether the effect would be accepted now. A sink implementation must answer both; the daemon's `WorldSpellTeleportSink` delegates players to `TeleportService.CanTeleportTo`. `SpellSystem.TryResolveTeleportDestination` is the destination lookup of `EffectTeleportUnits`, shared so that preflight and effect agree. |
| Is an effect handler still the built-in one? | `SpellSystem.HasBuiltInEffectHandler(effect)` is true until `RegisterEffect` replaces it. Quest reward preflight models only the built-in `TeleportUnits` and `Summon` handlers, so a replaced handler makes such a reward unsupported. |
| Announcing a spell the book already holds | `SpellSystem.AnnounceLearnedSpell` (SMSG_LEARNED_SPELL) and `CastLearnedPassive`; `LearnSpell` is these two plus the book write. Quest rewards persist the spell with the journal and call them from the publication step. |

## Integration status and remaining work

1. **Map update hook.** `SpellFeature` ticks from a timer that posts `Update` to the world thread
   once per tick interval, with at most one post pending. A `WorldRuntime` per-tick event (or an
   `IWorldFeature.Update(diff)`) would remove the timer.
2. **Character loading and creation now use #9's hooks.** Starting spells are persisted
   before creation succeeds. Login drains preceding queued writes, loads the character's
   persisted book, and fails closed on database errors. Failed queued writes are reconciled
   against the authoritative cache before reload. Startup preload remains for compatibility.
3. **Character deletion cleanup.** `CharacterSpellDataModule.DeleteCharacterDataAsync` removes spell
   rows transactionally, with conditional store cleanup for an already deleted character. A successful
   real-store `SpellbookCache.DeleteCharacter` now retires an earlier failed-write recovery marker when
   no replacement cached book exists. Failed deletes, missing stores and replacement-book races retain
   recovery. See [the bounded follow-up](faction-spellbook-cleanup-20261005.md).
4. **Combat, creature lookup, and teleports are connected.** `WorldSpellSinks` resolves units
   through `Map.FindObject`, deals damage through `MapCombat`, and sends player teleports
   through `TeleportService` including far transfer and acknowledgement. Healing distributes
   effective healing threat and enters combat. The [school-threat follow-up](ghost-wolf-school-threat-20261005.md)
   applies ModThreat once to generated spell damage/healing; flat metadata, threat talents and class coefficients remain.
5. **Empty books across restart.** An intentionally emptied in-memory book is preserved on
   relog, but a restarted daemon cannot distinguish zero persisted rows from a legacy character
   awaiting defaults without additional metadata.
6. **Far transfer preserves spell state.** Online players awaiting a world-port ack retain
   their aura/cast state with simulation paused while detached. Cooldowns use absolute time.

## Revocable aura caster ownership

The [qualified lifetime follow-up](aura-caster-ownership.md) binds a holder to a
weak, revocable token for the exact caster Unit. Actual logout/non-transit
forgetting revokes that token, including when the caster's own state was pruned.
Same-GUID replacement cannot receive old effect attribution, threat or quest kill
credit, pause an old holder through its settlement, or inherit its stacks. Late
old removal also cannot forget replacement state. Legitimate map-only transit
keeps ownership, and an exact owner's stack/refresh behavior remains intact.

Missing/revoked casters keep the existing target fallback. Holder GUID and periodic
aura log provenance remain; triggered spell logs retain their actual resolved or
fallback actor. This adds no saved aura state or broader offline gameplay. Twenty
new Game/World cases and full native/provider suites cover the bounded contract.
Actual two-session/client aura behavior remains pending in the user's selected
chat; source-only weak-lifetime reasoning does not claim a forced-GC measurement.

## Follow-up: persistence, targeting, effects and combat rules

Round 2 (`feat/spells-persistence`) adds cooldown/aura persistence across logout (characters
schema: the spell-state module is v9 in the integrated allocation Auth2/World8/Characters10), area/cone/chain/party targeting with a line-of-sight seam, weapon, leech,
dispel, interrupt, summon and party area aura effects, vanilla hit/crit/resist rules and
pushback/channel interrupts. See [spells-persistence.md](spells-persistence.md).

## Quest reward spells (handoff item 5)

Quest settlement now accepts reward spells that only teach spells or create items (persisted
atomically with the journal, never cast) and transient spells (cast once after the commit), with
the player-aware preflight above. See [quest-progression.md](quest-progression.md). The
quest-hold guards also reach this area: `SpellbookCache.ForgetSpell` and `.unlearn` refuse a
character held by a quest settlement, and `SpellbookCache.AdoptCommitted` adds committed spells
to an existing cached book without queueing a write.
