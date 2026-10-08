# Area: Crafting professions

Lane `claude/vw5-crafting-professions`, base `claude/vw4-integration` (7313b9e: waves 1-3). The cross-area contract (shared-file edits, configuration,
behaviour changes the integrator must know) is in [docs/integration/crafting.md](../integration/crafting.md).

Standing directive: as close to vanilla 1.12.1 as possible, taken from the references, every deviation behind a configuration option that defaults to
retail or listed under **Limits**. References, in order of authority: **vmangos** (`D:\refs\vmangos`, the 1.12.1 branch of its `#if SUPPORTED_CLIENT_BUILD`
blocks), **mangos-classic**, **gtker wow_messages**, **classic-db** (read only: ids and numbers as fixtures, nothing copied). Each piece of code cites
file:line; where vmangos and another reference disagree the choice is stated.

## What the lane found

Crafting was "a missing hook layer around primitives that already existed": the skill-up maths (`SkillRules.CraftChance`, `PlayerSkills.UpdateCraft`), the
inventory half of a craft (`PlayerInventory.CreateItemFromSpell`), trainers, the spell-focus check and the cooldown/persistence machinery were in place, but
no CREATE_ITEM handler, no reagent or tool rules, no way to cast from an item (`CMSG_USE_ITEM` had no handler, so recipes, bandages and enchant items could
not be used) and no enchantment engine. All 1,159 tradeskill crafts reported "not implemented".

## Delivered

**Reagents and tools** (`Game/Crafting/ReagentRules.cs`, `World/Crafting/CraftingFeature.cs`, `SpellInfo.Items.cs`)
- `SpellInfo.Reagents` (item, count) and `SpellInfo.Totems` (tool items) come from `Reagent1-8`, `ReagentCount1-8`, `Totem1-2` of `spell_template`; a reagent
  slot with a non-positive item id and a zero totem slot are absent (vmangos `Spell.cpp:7254`, `:7292`; `SpellEntry.h:635-637`).
- `ReagentCastCheck` (cast phase `Items`, order Equipment + 60, after the spell focus at +50): a missing reagent is `ITEM_NOT_READY`, a missing tool
  `ITEM_GONE`, reagents first (`Spell.cpp:7249-7306`). When the cast item is itself a reagent, is used up by the cast and the recipe needs more than one,
  the count grows by one (`:7266-7280`, `:5101-5113`). Items offered in an open trade neither count nor are destroyed (`Player::HasItemCount` and
  `DestroyItemCount` skip `IsInTrade`, `Player.cpp:8681-8734`): offering the only cloth in a trade slot makes the recipe `ITEM_NOT_READY`.
- `ReagentCostTaker` destroys the reagents right after the power is spent and before the ammunition and the effects (`Spell.cpp:3716-3718`: "remove reagents
  before HandleEffects to allow place crafted item in same slot"); the bank is not touched; when the cast item is the reagent it is cleared from the cast so it is not
  used up twice.
- Standalone triggered casts ignore item requirements. A child whose parent's first reagent slot is empty checks
  its own reagent and tool; a child of a parent with that slot filled reuses the paid items
  (`Spell::IgnoreItemRequirements`, Spell.cpp:7069-7083).

**Spell cast seams** (`Spells/SpellCostSeams.cs`, `SpellSystem.ItemCast.cs`, edits in `SpellSystem.cs`)
- `ISpellCostTaker` + `SpellSystem.RegisterCostTaker`: every registered taker runs once per completed cast after `TakePower`. Other lanes that take a cost
  (the ranged lane's ammunition is still called inline) can register here instead of editing the call site.
- `SpellCast.CastItem`, `SpellSystem.CastItemSpell(player, item, spellId, targets, triggered)`, and the same item on `SpellCastCheckContext` and
  `SpellEffectCheckContext` (optional trailing parameter, existing callers compile unchanged).
- A cast from an item checks and takes no power (`Spell::CheckPower` returns OK at once, `Spell.cpp:7050-7052`; `Spell::TakePower`, `Spell.cpp:5053`); the item's own cooldown category and times replace the spell's
  (`ItemSpellCooldowns`, `Player::AddCooldown` `pickCooldowns`, `Player.cpp:22139-22160`), in the readiness test too.
- `TakeCastItem` runs after the effects (`Spell.cpp:3876-3878`).

**CREATE_ITEM** (`Game/Crafting/CreateItemSpells.cs`)
- The single handler of the server for the effect (1,159 tradeskill crafts, plus the conjures, quest and item spells that create items). Cast-time check
  (`Spell.cpp:7311-7330`): non-triggered, effect 0, the target (the caster when the B target is the caster) must be a player (`BAD_TARGETS`) and
  `CanStoreNewItem` must take `max(1, value)` items, else the equip error goes to the client and the cast ends `DONT_REPORT` before anything is consumed.
- Effect (`DoCreateItem`, `SpellEffects.cpp:1885-1990`): `PlayerInventory.CreateItemFromSpell` (stack clamp, partial store, crafter signature, push result with
  `created` set), then `Skills.UpdateCraft(spell)` only when at least one item was stored. `DoCreateItem(..., grantSkillUp: false)` is the battleground-mark path for
  that lane.
- Fail closed: `CreateItemSpells.Install` throws unless the reagent pair is installed (no craft is ever free) or when another CREATE_ITEM handler exists.
- When optional ItemRandomProperties.dbc and `item_enchantment_template` content is configured, the craft's
  `StoreNewItem` rolls and applies a property's three enchantments (vmangos `Spell::DoCreateItem`,
  SpellEffects.cpp:1950; `Item::GenerateItemRandomPropertyId`, Item.cpp:792-834). Without the content, the output has no random property.

**Use item** (`Game/Items/ItemUse/*`, `World/Items/UseItemFeature.cs`)
- `CMSG_USE_ITEM` (u8 bag, u8 slot, u8 spell index, SpellCastTargets; gtker `cmsg_use_item.wowm` 1.12) after `HandleUseItemOpcode`
  (`SpellHandler.cpp:36-140`): unknown slot, a spell index without an `ON_USE` spell, an equippable item that is not worn, `CanUseItem`, an item in a trade
  window (`ITEM_NOT_FOUND`), a non-combat spell in combat (`NOT_IN_COMBAT`), bind on use (also bind-on-pickup and quest items that were never bound), the
  `item_required_target` rule (see Limits) and the shapeshift rule (`NO_ITEMS_WHILE_SHAPESHIFTED` for a bag item, equipped items allowed since client patch 1.10).
- `CastItemUseSpell` (`Player.cpp:7362-7396`): every `ON_USE` spell of the item is cast with the item as cast item (the first not triggered, the others triggered);
  item-use-cancelled auras (`AURA_INTERRUPT_ITEM_USE_CANCELS`) are removed first.
- Cast-item checks (`CastItemCheck`, order Equipment + 10, `Spell.cpp:7109-7175`): item in trade -> `ITEM_GONE`, item no longer held -> `ITEM_NOT_READY`, a
  limited-charge spell with no charge -> `NO_CHARGES_REMAIN`, then the 1.11 rejuvenation rule for consumables (refused only when no effect is usable: a heal needs
  missing health, an energize missing power of its type; the loop keeps the last refusal).
- `TakeCastItem` (`Spell.cpp:4991-5048`): charges count down on the item (not on a stackable one), a spent expendable item (negative charges) loses one of the stack.

**Recipes** (no code; `RecipeLearningTests`)
- A recipe item (class 9) is `CMSG_USE_ITEM` + `SPELL_EFFECT_LEARN_SPELL`; `RequiredSkill`/`RequiredSkillRank`/`RequiredSpell` (95 specialisation recipes) gate it
  through `PlayerInventory.CanUseItem`. A known recipe is used up and nothing changes, as in vmangos (`EffectLearnSpell`, `SpellEffects.cpp:2435-2454`).

**Specialization teaching** (`Game/Crafting/ProfessionSpecializationGossip.cs`, `World/Crafting/CraftingFeature.cs`)
- The ClassicDB NPCs assigned `npc_prof_blacksmith` and `npc_prof_leather` offer the ScriptDev2 learn paths for Armorsmith,
  Weaponsmith, Hammersmith, Axesmith, Swordsmith, Dragonscale, Elemental and Tribal leatherworking. The server checks the
  reference's skill, level, faction rank, rewarded quest and mutually exclusive spell conditions again when a gossip line is
  selected (mangos-classic `npc_professions.cpp`: `GossipHello_npc_prof_blacksmith`, `SendActionMenu_npc_prof_blacksmith`,
  `IsEligibleSpecializeLW`, `GossipHello_npc_prof_leather`, `SendActionMenu_npc_prof_leather`). Learning casts the content's
  teaching spell. An unavailable teaching spell cannot be manufactured by the gossip handler.
- Profession gossip is attached after the quest service is built; its vendor and trainer choices use the normal NPC services.
  The existing item `RequiredSpell` rule checks specialization plans and equipment (vmangos `Player::CanUseItem`).

**First aid** (`Game/Crafting/FirstAid.cs`)
- The 19 bandage spells are data. `FirstAidObserver` is vmangos' `FirstAidScript::OnAfterHit` (`spell_item.cpp:577-594`): after a bandage hits its unit target the
  caster casts Recently Bandaged (11196, mechanic immunity to BANDAGE) triggered at it with the same cast item. It fires per hit target at the start of the channel
  (`Spell.cpp:1541-1542`): it survives an interrupt and is not applied to a missed or immune hit.
- Two rules the immunity needed: a `MECHANIC_IMMUNITY` to bandage, shield, mount or invulnerability is not a positive aura (`SpellEntry.cpp:976-987`, so it blocks
  the positive heal), and a self cast still tests immunity (`Unit::SpellHitResult` asks `IsImmuneToSpell(spell, victim == this)` before the "victim == this" return,
  `SpellCaster.cpp:175-180`).

**Enchanting** (`Kernel/Crafting`, `Data/Crafting`, `Game/Crafting/Enchanting/*`, `World/Crafting/EnchantingFeature.cs`)
- *Catalog*: `SpellItemEnchantment.dbc` is read from `Enchanting:SpellItemEnchantmentDbcPath` at startup (strict 24 fields, vmangos `DBCfmt.h:75`
  `"niiiiiixxxiiissssssssxii"`, `DBCStructure.h:639-651`), the same developer-supplied-DBC convention as the skill and shapeshift tables. **No World schema change**
  (the design had a `spell_item_enchantment` table; reading the file the client already ships avoids a second copy of the data and keeps the lane off the hosted-CI
  database matrix).
- *Engine* (`PlayerEnchantments`, after `Player::ApplyEnchantment`, `Player.cpp:11739-11884`): an enchantment counts only while its item is worn and unbroken.
  DAMAGE adds flat damage to the hand (main hand, off hand, ranged slot) through the new `PlayerStatState.TotalDamage` term the stat system reads;
  EQUIP_SPELL casts the enchant's spell triggered at the owner with the item as cast item and removes the item's aura again (`SpellAuraHolder.CastItemGuid`,
  `SpellSystem.RemoveAurasDueToItemSpell`); RESISTANCE and STAT move the update fields as deltas; TOTEM adds the Rockbiter damage `amount * delay / 1000` for shamans;
  COMBAT_SPELL is inert (see Limits). What was applied is remembered per (item, slot) so a removal undoes exactly that; apply and remove are idempotent.
  An equip spell of a player who is not in the world yet (login loads the equipment on the session task, and the spell system belongs to the world thread) is deferred to the first
  world tick: a restored aura of that spell (equip auras are permanent, so they are saved) is adopted by the item instead of being cast twice.
  The visible-item field mirrors the id for the two inspected slots. `EnchantStatsApplier` keeps the pair on the existing equip hook (apply: the item's bonuses, then
  the enchantments; remove: the reverse), through equip, unequip, swap, break and repair, and refreshes the derived stats.
- *Slots and logs* (`ItemEnchantments`, `Item::SetEnchantment` and friends, `Item.cpp:1028-1090`): permanent slot 0, temporary slot 1 and the property slots; `Set` with a
  caster logs the old enchantment fading and the new one to the owner (and the new one to onlookers) with `SMSG_ENCHANTMENTLOG` (0x01D7); `Clear` logs a fade on request.
- *Timers* (`Player.cpp:11619-11731`): a temporary enchantment's remaining time is a ledger off the item field (`Item.LiveEnchantDuration`, so the value the client shows
  is not rewritten each tick); `SMSG_ITEM_ENCHANT_TIME_UPDATE` (0x01EB) goes out when a timer starts and once at login after the player entered the world; an expired
  enchantment is removed from the stats, cleared and logged as faded; the remaining time is what `Item.ToData()` saves, so a relog resumes with the time that was left.
  Every held item is tracked, equipped or not (vmangos adds the durations whenever an item enters the inventory); a one second sweep finds arrivals and departures.
- *Spells* (`EnchantItemSpells`, `ItemTargetFitCheck`): ENCHANT_ITEM (`SpellEffects.cpp:3009-3052`: the craft skill-up comes first, before the enchant lookup; the old
  enchantment comes off, the new one is set with the caster logged, goes on when worn), ENCHANT_ITEM_TEMPORARY (`:3054-3099`: `value * 1000` ms) and
  ENCHANT_HELD_ITEM (`:5009-5057`: main hand while worn, base points seconds, else the spell duration, else 10 s, never replacing a different enchantment). Cast checks
  (`Spell.cpp:7177-7200`, `:7311-7375`): `BAD_TARGETS` for a non-player, `ITEM_GONE`, `EQUIPPED_ITEM_CLASS` from the class, subclass and (for an item target) inventory-type masks
  (`Item::IsFitToSpellRequirements`, `Item.cpp:975-1003`, with the Enchant Cloak - Minor Agility data fix for spell 13419, narrowed here to cloaks only: vmangos skips the class check for every item, which leaves the weapon row's masks open to weapons), `MAINHAND_EMPTY` for held-item spells, `LOWLEVEL` below the
  spell's base level, and for an item of another owner (a trade slot target resolved through the economy feature) `NOT_TRADEABLE` for own-item-only spells and for catalog
  flag 0x01. `Enchanting:GmAllowTrades` (vmangos `GM.AllowTrades`, default true) refuses a game master's enchant when false.

## Verified against real data

- `D:\server-Zero\run\dbc` holds 1.12-shaped client DBCs: `SpellItemEnchantment.dbc` is 24 fields / 1,460 rows and decodes completely
  (`RealDbcProbe`, run with `ARCANECORE_TEST_DBC_DIR=D:\server-Zero\run\dbc`). All 373 enchant effects of classic-db (274 ENCHANT_ITEM, 86 ENCHANT_ITEM_TEMPORARY, 13 ENCHANT_HELD_ITEM), naming 272 distinct enchant ids, name an id that exists in it.
- The referenced enchants are overwhelmingly EQUIP_SPELL (178 effects; their spells are `MOD_STAT` 65, `MOD_RESISTANCE` 36, `MOD_HEALING_DONE` 20, `MOD_SKILL` 17, `MOD_DAMAGE_DONE` 15,
  `MOD_ATTACK_POWER` 10 ...), then COMBAT_SPELL (62), DAMAGE (23) and RESISTANCE (11): no referenced enchant uses the STAT type. Most enchantments therefore work through the aura
  engine; which aura types are registered is that lane's (`aura-engine-completeness`) coverage.
- Three real craft rows (3275 Linen Bandage, 7421 Runed Copper Rod, 17187 Transmute: Arcanite with tool 9149 and the 172,800,000 ms category cooldown) run end to end
  (`RealCraftRowsTests`).

## Limits (documented, not delivered)

- **Specialization unlearning and engineering switching**: the mangos-classic ScriptDev2 file displays blacksmith unlearn
  confirmations but does not implement their action cases, and its leatherworking unlearn spells (36328, 36433, 36434)
  are absent from the 1.12.1 z2815 `spell_template`. Its own header says engineering unlearn/relearn is unsupported.
  ArcaneCore does not offer these actions. The blacksmith weapon-subdiscipline learn confirmation is a direct choice in
  ArcaneCore; the reference adds an extra confirmation menu with no cost.
- **Combat-spell enchants** (crusader, fiery weapon, lifestealing, poisons' weapon procs, shaman imbues: 62 referenced) need a melee/ranged outcome event the combat
  system does not publish; `Player::CastItemCombatSpell` (`Player.cpp:7310-7355`) is not delivered. The enchantment is stored, shown and timed; it never procs.
- **Rogue poisons**: the poison item spells (for example 2823) have enchant id 0 in both classic-db and the 1.12 `Spell.dbc`, so vmangos resolves them in spell scripts, which
  this lane does not have; poisons are not applied.
- **`spell_enchant_charges`** (vmangos `SpellMgr.cpp:1889-1923`) is not in classic-db: temporary enchantments get 0 charges (duration only).
- **Skill discovery and extra-item procs are not built because they are TBC features** (vmangos dropped both tables as empty, `sql/old_migrations/20181108192531_world.sql:12-14`; every classic-db
  `skill_discovery_template` row names a spell above 25000 that 1.12 lacks, and every `skill_extra_item_template` row needs an absent TBC specialisation spell). Retail behaviour is
  "no discovery".
- **Foreign trade item targets** still need a live trade acceptance run for the triggered reagent rule
  (`Spell::IgnoreItemRequirements`, Spell.cpp:7069-7083).
- **`item_required_target`** (`Item::IsTargetValidForItemUse`) is not imported: `IItemRequiredTargets` is the seam, the default accepts every target.
- **Trial accounts** (`Player.cpp:5227-5236`) do not exist here.
- **Known recipe refusal**: vmangos neither refuses nor checks; whether retail answers "already known" could not be verified. No refusal switch was built.
- **Engineering devices** and the specialisation book interaction are still outside this area. Mining node opening and
  campfire spell-focus checks are implemented in the gathering and focus features. See
  [the profession audit](../integration/professions-audit-20261008.md).
- **Wire layouts** (`CMSG_USE_ITEM`, `SMSG_ENCHANTMENTLOG`, `SMSG_ITEM_ENCHANT_TIME_UPDATE`) come from the references only and were not seen against a real client (`needs_real_client`).
  `SMSG_ENCHANTMENTLOG` has a recorded source disagreement: gtker and cmangos-classic write the owner first, vmangos `EnchantmentLog::AppendBodyTo` writes the caster first; the owner
  first order is implemented.
- **MariaDB / PostgreSQL**: nothing in this lane touches a schema; the enchant catalog is a DBC file, enchantments persist in `item_instance.enchantments` as before.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Crafting:Enabled` | `true` | Master switch of reagents, the CREATE_ITEM effect and first aid. `false` registers none of them; the enchant effects then refuse the cast (`Unknown`) before any reagent is taken, as with `Enchanting:Enabled=false`. |
| `Enchanting:Enabled` | `true` | `false` leaves the engine and the enchant effects unregistered; enchant casts are refused (`Unknown`) and consume no reagents. |
| `Enchanting:SpellItemEnchantmentDbcPath` | unset | Build-5875 `SpellItemEnchantment.dbc`. Unset: enchanting is inactive and logs a warning; every enchant effect then refuses the cast with `Unknown` before any reagent is taken (ArcaneCore guard, no vmangos counterpart). Set but unreadable or with another layout: startup refuses. |
| `Enchanting:GmAllowTrades` | `true` | vmangos `GM.AllowTrades` (`World.cpp:680`). |

Skill gain tuning stays in the existing `Skills` options (`SkillGain.Crafting`, `SkillChance` colours).

## Acceptance recipe for a real 1.12.1 client

1. Learn Linen Bandage (First Aid trainer), cast it with Linen Cloth in the bags: the cloth is consumed, a bandage appears with the "Crafted" push line, the skill bar can rise.
2. Use the bandage on a friendly: 8 ticks of healing, the item is consumed at the start; bandage the same target again within a minute: it must not heal. Interrupt the first channel by moving: the second
   bandage is still refused.
3. A Transmute needing the Philosopher's Stone: refuses without the stone (no message of "reagents"), works with it, then shows the 2 day cooldown.
4. With Enchant Bracer - Minor Strength on a worn bracer: the bracer shows the enchantment, Strength rises by the amount in the DBC; take it off and it falls back. Check the enchant log text.
5. A temporary enchant (weapon oil or sharpening stone) shows the countdown on the item tooltip after login too.
6. Check `SMSG_ENCHANTMENTLOG` text ("Your X has been enchanted") for the owner/caster order and `CMSG_USE_ITEM` from a bag, the keyring and an equipped trinket.

## Tests

`tests/ArcaneCore.Game.Tests/{Crafting,ItemUse}`, `tests/ArcaneCore.World.Tests/{Crafting,Items/UseItemWorldTests}`, `tests/ArcaneCore.Data.Tests/Crafting`. Timing-free: casts and timers are driven with explicit time steps;
the wait on the world thread polls a condition with a ten second ceiling.
