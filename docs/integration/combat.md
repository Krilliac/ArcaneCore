# Integration notes: melee combat (`feat/combat`)

This branch is built on the seam PR #1 (`feat/fleet-plan`, 165b885). It does not edit
`WorldServiceCollectionExtensions.cs`, `ChatHandlers.cs`, `CharacterHandlers.cs`,
`WorldRuntime.cs`, any DbContext or `WorldTestHost.cs`.

## Shared-file edits (minimal, additive)

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.Game/Entities/Unit.cs` | Added `public UnitCombat Combat => field ??= new UnitCombat(this);`. `IsAlive` is now `Health > 0 && Combat.DeathState == DeathState.Alive`. | Each unit needs per-unit combat state (victim, attackers, threat, swing timers, death state). A ghost has health 1 but is not alive (vmangos `Unit::IsAlive` is `m_deathState == ALIVE`). |

**Per-map update (lead, after merge):** combat no longer edits `Map.cs`. `MapCombat` is an
`IMapUpdater` with `[DefaultMapUpdater(Order = 0)]`, so `WorldRuntime.GetMap` attaches it to
every map, first, and `Map.Update` runs it at step (1c) before the other per-map systems.
`map.Combat` is the C# 14 extension property in `Game/Combat/MapCombatExtensions.cs`
(`map.FindUpdater<MapCombat>()`). `OnPlayerRemoved` repeats the logout cleanup (a no-op after
`PlayerLoggingOut`). See the per-map row in `seams.md`.

## Registrations (discovered, no shared list touched)

- `src/ArcaneCore.World/Combat/CombatHandlers.cs` is an `IOpcodeHandlerGroup`. It handles
  CMSG_ATTACKSWING, CMSG_ATTACKSTOP, CMSG_SETSHEATHED, CMSG_REPOP_REQUEST,
  CMSG_RECLAIM_CORPSE, MSG_CORPSE_QUERY and CMSG_TOGGLE_PVP. **Conflict risk:** if another
  area (items or the spellbook) also registers CMSG_SETSHEATHED or CMSG_TOGGLE_PVP, startup
  throws on the duplicate. Keep one owner.
- `MapCombat` subscribes to `WorldRuntime.PlayerLoggingOut`. It detaches attackers and
  threat and removes the player's corpse while the player is still in its map.

## Schema

There are no schema changes and no `IDataModule`, so this branch needs no schema version.
Death state and corpses are not persisted yet (see `docs/areas/combat.md`). Persisting them
needs a characters-DB version from the lead.

## Seams for other areas

- **Creatures / AI:** implement `ICombatCreature` on `Creature`, with `IsInEvadeMode`,
  `CanParry`, `CanBlock`, `CanCrush`, `IsWorldBoss`, `RegeneratesHealth`, `OnAttackedBy`
  (aggro, the vmangos `AI()->AttackedBy`) and `OnJustDied`. Call `map.Combat.Track(creature)`
  on spawn so it gets swing, regen and death updates. Then `map.Combat.Attack(creature, target)`
  and `AttackStop` drive auto-attack. `ThreatList.SelectVictim` implements the 110%/130% rule.
- **Spells:** use `map.Combat.DealDamage(attacker, victim, damage, ...)` for damage, kills,
  threat and combat state, `Kill`, and `unit.Combat.NoteManaUsed()` for the 5-second rule.
  Swap `CombatHooks.ApplyGhostForm` / `RemoveGhostForm` for aura 8326 (or 20584 for Night
  Elves).
- **Items / stats:** override `CombatHooks.GetWeaponSkill`, `GetDefenseSkill`,
  `HasOffhandWeapon`, `PlayerCanParry`, `PlayerCanBlock` and `GetShieldBlockValue`, and fill
  `UNIT_FIELD_MINDAMAGE`/`MAXDAMAGE`, `PLAYER_*_PERCENTAGE` and armor.
- **Graveyards:** override `CombatHooks.RepopAtGraveyard(player)` to teleport the ghost
  (vmangos `Player::RepopAtGraveyard`). The default leaves it at the corpse.
- **Factions:** override `CombatHooks.IsFriendly` / `CanAttack` with faction templates.
  The default is team-by-race for players.
- Register hooks per world: `CombatHooks.Register(world, hooks)`.
