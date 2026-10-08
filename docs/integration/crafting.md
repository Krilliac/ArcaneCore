# Integration: Crafting professions (lane `claude/vw5-crafting-professions`)

For the integrator. The area description (what is delivered, limits, sources) is [docs/areas/crafting.md](../areas/crafting.md). Base: `claude/vw4-integration` 7313b9e.

## Schema

**None.** No Characters, World or Auth change, no new constant to renumber. The enchantment catalog is the client's `SpellItemEnchantment.dbc` read from a configured path;
enchantments persist in the existing `item_instance.enchantments` triple. MariaDB and PostgreSQL are therefore not exercised by anything this lane added; there is nothing
provider-specific to run on hosted CI.

## Configuration

`Crafting:Enabled` (true), `Enchanting:Enabled` (true), `Enchanting:SpellItemEnchantmentDbcPath` (unset: enchanting inactive, a warning), `Enchanting:GmAllowTrades` (true).
A drift test (`CraftingDocsTests`) fails when a key named here is not a real option.

## Single owners (do not register these again in another lane)

| What | Owner | Failure mode of a duplicate |
|---|---|---|
| `SPELL_EFFECT_CREATE_ITEM` handler (and its cast check) | `CreateItemSpells.Install` (called by `CraftingFeature`) | `Install` throws if a handler exists; `RegisterEffect` elsewhere would silently replace it. Conjures (`warlock-mage-utility`) and battleground marks (`battlegrounds`, use `CreateItemSpells.DoCreateItem(..., grantSkillUp: false)`) rely on it. |
| `CMSG_USE_ITEM` handler | `UseItemHandlers` (`World/Items/UseItemFeature.cs`) | A second `table.OnWorld(CmsgUseItem, ...)` conflicts. `quests-advanced`, `warlock-mage-utility` and the consumables of other lanes should call `ItemUseService`/`SpellSystem.CastItemSpell`. |
| Reagent check and cost taker | `ReagentRules.Install` | Throws on a second call. |
| `ENCHANT_ITEM`, `ENCHANT_ITEM_TEMPORARY`, `ENCHANT_HELD_ITEM` handlers, `ItemTargetFitCheck` | `EnchantItemSpells.Install` (called by `EnchantingFeature`) | Throws. |
| Recently Bandaged observer | `FirstAidObserver.Install` | Throws. |

## Shared-file edits (all additive unless noted)

- `Game/Spells/SpellSystem.cs`: `Prepare`/`CheckCast` take an optional `castItem`; `SpellCast.CastItem`; `TakePower` is skipped for an item cast (vmangos `Spell.cpp:5053`); `TakeCosts(cast)` between
  `TakePower` and `TakeAmmo` (the ranged lane may register through `ISpellCostTaker` instead of the inline `TakeAmmo` call, whichever merges first); `TakeCastItem(cast)` after the target loop;
  `SendCastResult` is now `internal`; item cooldown categories in `IsSpellReady`/`AddCooldown` (`ItemSpellCooldowns.Pick`);
  **behaviour change**: a self cast now tests `ImmunityRules.IsImmuneToSpell(..., castOnSelf: true)` and can be `Immune` (vmangos `SpellCaster.cpp:175-180`).
- `Game/Spells/SpellInfo.cs`: `IsPositive` returns false for a `MECHANIC_IMMUNITY` aura to bandage, shield, mount or invulnerability (vmangos `SpellEntry.cpp:976-987`); the aura-engine lane's own rewrite of
  `IsPositive` should keep that clause.
- `Game/Spells/SpellCombatSeams.cs`, `SpellSystem.EffectChecks.cs`, `SpellSystem.Seams.cs`: optional `CastItem` on `SpellCastCheckContext` / `SpellEffectCheckContext`.
- `Game/Spells/SpellAuraHolder.cs`, `SpellSystem.Auras.cs`: `SpellAuraHolder.CastItemGuid`, set when an aura is created by a cast with a cast item. `SpellSystem.ItemCast.cs` (new): `CastItemSpell`, `RemoveAurasDueToItemSpell`.
- `Game/Spells/Checks/EquippedItemCastCheck.cs`: one condition: a cast with a trade-slot item target is skipped like a GUID item target.
- `Game/Items/Item.cs`: `internal uint?[] LiveEnchantDuration` and its use in `ToData()` (the saved remaining time of a running temporary enchantment).
- `Game/Stats/PlayerStatState.cs`, `PlayerStatSystem.cs`: `TotalDamage(hand)` / `AddTotalDamage(hand, delta)` (the flat damage term, vmangos `UNIT_MOD_DAMAGE_*` `TOTAL_VALUE`) and its use as `TotalValue` of `UpdateDamagePhysical`.
  It is 0 for everyone but enchanted weapons, so existing damage values are unchanged. If the aura lane later adds flat weapon damage auras it should add to the same term.
- New files (no conflict expected): `Game/Crafting/**`, `Game/Items/ItemUse/**`, `Game/Entities/Player.Enchantments.cs`, `Game/Spells/SpellInfo.Items.cs`, `SpellCostSeams.cs`, `SpellSystem.ItemCast.cs`, `Kernel/Crafting/**`,
  `Data/Crafting/**`, `World/Crafting/**`, `World/Items/UseItemFeature.cs`.
- `World/Spells/SpellStoreFactory.cs`: maps `Reagent1-8`, `ReagentCount1-8`, `Totem1-2` (additive).

## Behaviour changes a player or a test can see

- All tradeskill crafts, conjures, quest/item spells that create items and the three enchant effects now work (before: "not implemented"). Quest reward preflight classifies `CreateItem` as a durable grant either
  way (`QuestRewardEffects.cs:311-313`); `QuestRewardEffectCapabilityTests` and `QuestRewardAsyncRecoveryTests` were run green.
- `CMSG_USE_ITEM` now does something (it was an unhandled opcode): bandages, recipes, potions, food, charged items.
- A cast from an item takes no power and may use the item's own cooldown category.
- A second bandage on a recently bandaged target is `Immune`.

## Stale statements this lane leaves in other docs (not edited here)

- `docs/areas/skills.md:100-110` and `docs/integration/skills.md:61-71` ("CREATE_ITEM unhandled", "no use-item").
- `docs/areas/items.md:84-96` (use-item blocked on `SpellCast.CastItem`, charges never consumed, enchant slots raw).
- `docs/areas/spells.md:154-155`, `:288` (CREATE_ITEM and reagents; `SpellInfo` carries no reagent fields).
- `docs/areas/class-shaman-paladin.md:97-98`, `docs/areas/rogue.md:49`, `docs/areas/fishing-special-loot.md:93` (blocked on the enchant engine). Since then the COMBAT_SPELL enchantment effects (imbues, poisons) proc through `SpellSystem.HandleItemCombatProc`, and the Flametongue and Rockbiter proc scripts are delivered ([class-scripts](../areas/class-scripts.md)).

## Open questions for the integrator or the developer

1. **Real 1.12.1 DBCs** are at `D:\server-Zero\run\dbc` (1.12-shaped; `SpellItemEnchantment.dbc` 1,460 rows). Point `Enchanting:SpellItemEnchantmentDbcPath` at it for a real run; extract the same files from the client `dbc.MPQ` if the
   server-Zero copy is not the retail build for you. The real-DBC probe needs `ARCANECORE_TEST_DBC_DIR`.
2. **Melee outcome event.** Combat-spell enchants (62 referenced enchants) and the item `ChanceOnHit` spells need a published melee/ranged hit outcome; the threat or warrior-proc lane is the natural owner.
   `Player::CastItemCombatSpell` (`Player.cpp:7310-7355`) is the spec; `ItemEnchantments`, `PlayerEnchantments.Apply(item, slot, false)` and `ItemEnchantments.Clear` are the primitives it uses.
3. **`spell_enchant_charges`**: classic-db has no such table; temporary enchantments have 0 charges. Which enchant spells carry charges in vmangos' world database?
4. **Known recipe**: retail behaviour on using a known recipe is unverified.
5. **CMSG_USE_ITEM / ENCHANTMENTLOG against a real client** (owner/caster order of the log).
6. **Trade window enchanting** resolves the partner's item through `EconomyFeature.TradeOf`; it is tested with a stub resolver (the full trade flow was not driven).
7. **Rogue poisons** need server-side spell scripts (the poison spells carry enchant id 0).

## Verification

Full build 0 warnings 0 errors; Game, World and Data suites and the MockClient self-test were run (numbers in the lane report). Timing-sensitive tests use explicit time steps. The load-sensitive classes
`QuestSettlementResponsivenessTests.SettlementCapacity_*` and `AuctionRecoveryTests` failed intermittently in a loaded full run and passed when rerun alone; neither touches this lane.
