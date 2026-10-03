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
- **Factions:** the daemon registers `FactionCombatHooks` (`src/ArcaneCore.Game/Combat/FactionCombatHooks.cs`) through
  `WorldCombatHooksFeature` (`src/ArcaneCore.World/Combat/`) when a `FactionTemplateCatalog` is loaded (a registered
  catalog, else `Creatures:FactionTemplateDbcPath`). It is a **template-only subset** of vmangos
  `WorldObject::IsValidAttackTarget` (`Objects/Object.cpp:3745-3815`), not "vmangos CanAttack". Template reaction
  (`GetFactionReactionTo`, `Object.cpp:3734-3741`; record logic `Database/DBCStructure.h:362-388`): hostile if `IsHostileTo`
  (tested first), else friendly if `IsFriendlyTo` either way, else neutral; a template missing from the catalog is neutral
  (`Object.cpp:3705-3709`). Pairs where neither unit has `UNIT_FLAG_PLAYER_CONTROLLED` (`UnitDefines.h:494`;
  `UnitFlags.PlayerControlled`, set on players and on any flagged pet/charm/totem) are attackable only if the reaction is
  hostile in either direction (`Object.cpp:3760-3763`), so neutral creature pairs are not attackable and creature
  assist/call-for-help (`CreatureMapSystem.Ai.cs`) only picks hostile-reaction enemies. Every other non-PvP pair is refused
  if the reaction is friendly in either direction (`Object.cpp:3767-3769`); neutral stays attackable. Player versus player is
  the base rule (team friendly, PvP flag). **With no catalog loaded (or an empty one) nothing is registered and
  the permissive `CombatHooks.Default` applies: a player may attack any non-player unit** (a warning is logged once).
  **Only `CanAttack` is overridden; `IsFriendly` is deliberately untouched** (vmangos `IsFriendlyTo` /
  `IsValidHelpfulTarget` polarity is not reproduced), so spell targeting (friendly AoE, chain heal, dispel
  polarity) and `CombatHandlers` keep the base rule and friendly NPCs do not become friendly-spell targets.
  **Not modelled (backlog; needs Faction.dbc data and a reputation manager):** reputation / at-war making a
  reputation-capable faction hostile (`Object.cpp:3677-3693`, `3714-3731`, `FACTION_FLAG_AT_WAR`;
  `reputationListID >= 0` = `CanHaveReputation`, `DBCStructure.h:346`); neutral-versus-neutral attackable only when at war
  for reputation-capable factions (`Object.cpp:3775-3792`); contested-guard (`IsContestedGuardFaction` +
  `PLAYER_FLAGS_CONTESTED_PVP` => hostile, `Object.cpp:3714-3716`, `3682-3685`); GM players reading NEUTRAL and forced
  reactions (`Object.cpp:3625-3637`); duel / same-group / FFA ordering (`Object.cpp:3648-3664`) and the PvP block
  (`Object.cpp:3796-3815`); resolving the affecting player of a pet/charm (no owner field exists; the pet branch uses only the
  flag and templates).
  The base `CombatHooks.IsFriendly` makes same-team players friendly so `CanAttack` refuses same-team duels, whereas
  vmangos reports duel opponents HOSTILE first (`Object.cpp:3651`, `3799-3800`); ArcaneCore has no duel system yet (no
  duel code under `src/`), so no live path is affected; a duel implementation must bypass or extend `CanAttack`.
  Evidence: automated tests plus the vmangos references cited above (`D:\refs\vmangos`), no 1.12.1 client (charter 1.3).
- Register hooks per world: `CombatHooks.Register(world, hooks)` (last writer wins) or
  `CombatHooks.TryRegister(world, hooks)` (first wins, returns false otherwise).
