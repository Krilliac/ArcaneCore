# Integration of the Codex continuation (2026-10-07)

`claude/integrate-20261007` (the ccr line) merged `codex/server-continue-20261004` (four commits since main `7313b9eb`). Both lines had grown
parallel implementations of several subsystems. The rule applied: keep both lines' features; where both implemented the same
behaviour, keep the implementation with the stronger tests and the closer vmangos fidelity, and port the other line's tests onto it
(or record why a test was dropped). The slice notes of the Codex line in this folder describe the Codex implementation as it was
written; where a row below says the ccr implementation was kept, that note is history.

## Schema renumbering

The ccr line's module versions were kept; the Codex modules moved above the highest ccr number (each constant carries a comment).

| Component | Module | Codex version | Merged version |
|---|---|---|---|
| world | `ReservedNameWorldDataModule` | 21 | 32 |
| world | `ItemEnchantmentWorldDataModule` (`spell_proc_item_enchant`) | 22 | 33 |
| world | `CreatureDisplayScaleDataModule` | 23 | 34 |
| world | `SpellEnchantChargesWorldDataModule` | 24 | 35 |
| world | `StartingSkillWorldDataModule` | 25 | 36 |
| world | `CreatureTextTemplateDataModule` | 27 | 37 |
| world | `NpcTemplateServiceMetadataModule` | 26 | dropped (the ccr `creature_template` NPC metadata columns, world 21, are kept) |
| characters | `PersistentPetDataModule` | 21 | 29 |
| characters | `ItemCooldownOwnerDataModule` | 22 | 30 |
| characters | `PetCooldownDataModule` | 23 | 31 |
| characters | `PetNamingDataModule` | 24 | 32 |
| characters | `ManagedPlayerbotDataModule` | 25 | 33 |
| auth | `ManagedPlayerbotProvisionDataModule` | 4 | 4 |

A database created by the Codex line at world 22 or more, or characters 22 or more, does not upgrade in place: its module rows sit at
numbers the merged line gives to other modules. Recreate such a database, or move its rows over by hand.

## Subsystems implemented on both lines

| Subsystem | Kept | From the other line |
|---|---|---|
| NPC service metadata | ccr `creature_template` columns | Codex configured-row validation (fail closed), playerbot trainer destinations read the template |
| `.banlist character` | ccr character directory walk, `Bans:MaxListedEntries` | Codex batched history existence check |
| Ghost form | ccr `IGhostForm` seam | Codex ghost tests (wisp, relog, fallback) run on the seam |
| Threat | ccr formula, `spell_threat`, NO_HELPFUL_THREAT 0x08 | Codex "no threat, no AttackedBy" rule, leech and energize threat assists |
| Death durability | Codex (EX3 exemption, caller flag) | ccr player-controlled stand-ins count as the tapper |
| Hit durability | ccr | - |
| Regeneration | ccr modifiers | Codex `Rate.Health`, MOD_POWER_REGEN timing and food/drink visuals |
| Resurrection requests | ccr `ResurrectionService` | Codex Revive Pet branch, corpse-owner resolution, save after the resurrection |
| Self-resurrection | ccr selection and effect | Codex guards, save after the resurrection, empty CMSG_SELF_RES only |
| Item use (CMSG_USE_ITEM) | Codex `SpellSystem.HandleItemUse` and cast-pipeline item mechanics | ccr cast item check, vmangos ITEM_USE_CANCELS aura removal |
| Reagents | Codex 8-slot `SpellReagent`, staging in the cast | ccr tool check and trade filter |
| Enchanting | ccr engine and SpellItemEnchantment catalog | Codex enchant charges and PPM tables, combat procs, trade settlement |
| Equip spells and item sets | ccr `ItemEquipSpells` | Codex shapeshift form rule (`IFormChangeListener`) |
| Invisibility | ccr `InvisibilityAuras` | Codex `Invisibility` query facade and tests |
| Stat auras | ccr (integer buff fields, the client reads UF_TYPE_INT) | Codex intellect latent-mana test |
| Transform aura | ccr structure (display source, Orb of Deception) | Codex vmangos semantics: box display, positive-over-negative rule, active-only reset, form display return, transform scale |
| SUMMON_PET | one handler | Codex hunter Call Pet (entry 0), ccr warlock demons (other entries) |
| GM tickets | ccr ticket lane | Codex CMSG_GMTICKET_SYSTEMSTATUS (reports enabled) |
| `.pinfo`, `.guid`, `.lookup spell` | ccr | Codex tests; `.pinfo` gained vmangos' lower-security check |

Behaviour that differed between the lines and now follows vmangos: aura refresh and stacking need the same cast item
(Unit.cpp:3134-3135); equip auras are saved like other auras (Player::SaveAura); party member stats truncate 16-bit fields
(GroupHandler.cpp:611-700); a refreshed aura does not flag the out-of-range party update (SpellAuraHolder::Refresh); self-resurrection
dithers with `floor(v + roll)` (Random.cpp:80-88); reputation reactions decide friendliness as well as attackability.
