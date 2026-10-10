# Global script hooks and script modules

ArcaneCore's equivalent of AzerothCore's `ScriptMgr` and `ScriptDefines/*` (PlayerScript, WorldScript, UnitScript, ItemScript,
AllSpellScript, GroupScript, GuildScript, BGScript), and of its `modules/mod-*` loader.

## Hooks (`ArcaneCore.Game/Scripting`)

A hook class implements one or more interfaces and overrides only the methods it needs. Every method has an empty default.

| Interface | Hooks | Raised from |
|---|---|---|
| `IPlayerHooks` | `OnLogin`, `OnLogout`, `OnLevelChanged(oldLevel)`, `OnKill`, `OnKilled`, `OnChat` (false drops the line), `OnAddonMessage` (false drops the line), `OnDuelStart`, `OnDuelEnd` | `WorldRuntime.NotifyLoggedIn` / `RemovePlayer`, `PlayerProgression.GiveLevel`, `MapCombat.Kill`, `ChatHandlers` (after commands, before every chat feature; not addon messages), `ChatHandlers` addon branch for `OnAddonMessage` (`AddonChannel` on, after the addon mute/flood check, before the features; `Prefix` and `Text` are split at the first TAB of the client's `"%s\t%s"`, `Prefix` is null without a TAB), `DuelService.UpdateDuelFlag` / `Complete` |
| `IWorldHooks` | `OnStartup`, `OnUpdate(diff)`, `OnConfigReload` | `WorldRuntime.Run` (before the first tick), `RunTick` (after `WorldTick`, before the maps), `.reload config` commit |
| `IUnitHooks` | `OnDamage(ref damage)`, `OnDeath` | `MapCombat.DealDamage` (entry), `MapCombat.Kill` |
| `IItemHooks` | `OnUse` (true takes the use over), `OnEquip`, `OnUnequip` | `ItemUseService.UseItem` (after its checks), `PlayerInventory.EquipmentChanged` (bridged) |
| `ISpellHooks` | `OnCastFinished` | a spell-system observer (bridged) |
| `IGroupHooks` | `OnMemberAdded`, `OnMemberRemoved`, `OnDisband` | `GroupManager` events (bridged) |
| `IGuildHooks` | `OnMemberAdded`, `OnMemberRemoved` | `GuildManager` events (bridged) |
| `IBattlegroundHooks` | `OnStart`, `OnEnd(winner)` | `Battleground.StartBattleground` / `EndBattleground` (through `BattlegroundPorts.Scripts`) |

**Cost.** `ScriptHookRegistry.Register` reads the class's interface map and adds the object only to the hooks it overrides, like
AzerothCore's enabled-hook lists. Each hook keeps its own array, so a dispatch nobody registered for is one field load and a length check,
with no allocation (`ScriptHookRegistryTests.EmptyDispatch_AllocatesNothing`). The bridged hooks are not even subscribed unless a module
registered one.

**Rules.**
- Registration happens before the world thread starts. `WorldRuntime.Start` freezes the registry.
- A class that overrides nothing is refused, because it is almost always a signature typo.
- Hooks run on the world thread. A hook that throws is logged and the other hooks still run. A throwing `OnDamage` leaves the damage
  unchanged.
- The registry is per world (`WorldRuntime.Scripts`), so tests with several worlds stay isolated.

## Modules (`ArcaneCore.World/Scripting`)

Implement `IScriptModule` (`Name`, `Register(ScriptModuleContext)`, optional `ReloadConfig(section)`) anywhere in `ArcaneCore.World`.
It is discovered like the other seams, by reflection and in type-name order. A module loads only when `Modules:<Name>:Enabled` is true.
Its settings are the rest of the `Modules:<Name>` section. Give the settings class a `SectionName` constant (`Modules:<Name>`) and an `Enabled` property so the configuration reference documents it (`DuelResetSettings`). On `.reload config`, every loaded module gets its re-read section, and then
the `IWorldHooks.OnConfigReload` hooks run.

Shared edits: `WorldRuntime.cs`, `PlayerProgression.cs`, `MapCombat.Melee.cs`, `DuelService.cs`, `ItemUseService.cs`, `Battleground.cs`,
`BattlegroundPorts.cs`, `BattlegroundManager.cs`, `BattlegroundWorldHost.cs`, `ChatHandlers.cs`, `ConfigContentReloadable.cs` (one
dispatch line or property each) and `appsettings.json` (the `Modules` section).

## Sample: mod-duel-reset

`Game/Scripting/Modules/DuelResetScript.cs` and `World/Scripting/Modules/DuelReset/DuelResetModule.cs` port
[mod-duel-reset](https://github.com/azerothcore/mod-duel-reset) (`src/DuelReset.cpp`, `DuelReset_scripts.cpp`, `conf/duelreset.conf.dist`).
It is off by default. Turn it on with `Modules:DuelReset:Enabled`; the keys are `Cooldowns`, `HealthMana`, `CooldownAge`, `Zones`
and `Areas`.

Deviations from the module:
- No spell cooldown mods in the ten-minute and age checks.
- Pet cooldowns are cleared outright (no age or ten-minute filter) at the duel start and again when a won duel ends, and are never saved or restored, as in the TrinityCore original (`duel_reset.cpp`). The owner's client is told per spell with `SMSG_CLEAR_COOLDOWN` carrying the pet's GUID. The pet's global cooldown and school lockouts are not touched.
- Saved state is dropped at the end of every duel. The module keeps it after a fled duel.
- Zone or area id 0 never matches, so an unknown position stays outside the whitelist.

mod-duel-reset is AGPL-3.0. This is a re-implementation of its behavior, not a code copy; AGPL-3.0 and GPL-3.0 are compatible
(GPLv3 section 13).
