# Character boost (`.character boost`)

An ArcaneCore operator command. vmangos and mangos have no equivalent, so none of the numbers below is a retail value; the code comments cite the
vmangos files for each server rule the boost reuses. Code: `src/ArcaneCore.World/Gm/Character/Boost/`. Tests:
`tests/ArcaneCore.World.Tests/Gm/Character/CharacterBoostCommandTests.cs` and `CharacterBoostPlannerTests.cs`. How to use it for bots and test
characters: [bot provisioning](../ops/bot-provisioning.md).

## Behaviour

`.character boost [$playername] #level` (Administrator) takes the named online player, else the selected player, else the invoker, and brings
it to `#level`. Nothing changes unless every check passes. The command is refused when:

* the level is outside 1 to the server maximum;
* the target is not in the world, is dead, is in combat, or is settling a quest reward;
* the level is below the character's own (`.levelup -N` lowers a level; a boost never does, so "spells up to the level" stays true);
* the item templates, or every vendor, quest and loot source for them, are not loaded (no kit can be chosen honestly).

The steps run in this order, each through the server's own path so the client and a managed bot's caches see what a normal level-up, learn and
equip would show:

1. **Level.** `LevelCommands.ApplyLevel` (shared with `.levelup`; clears experience). When the requested level equals the character's level the step
   is skipped, so a re-boost does not reset experience.
2. **Spells.** Every class-trainer spell available at the new level (the `.learn all_trainer` loop, repeated until a rank unlocks nothing more), then
   the spells the character's class and race would have earned from class quests up to the level. Both through `SpellSystem.LearnSpell`.
3. **Skills** to their maximum for the level (`.maxskill`).
4. **Gear**, below. Whatever was worn in a slot that changes moves to the bags, never destroyed. A two-hander moves the off hand to the bags.
5. **Ammo.** A character wearing a bow, gun or crossbow gets one stack of the best obtainable ammo that fits it, and selects it.
6. **Action bars**, below.
7. **Money**: 100 copper per level squared (1g 96s at level 14), never lowering a larger purse.
8. Save. The reply lists the counts, any slot with no candidate and any slot left unchanged with the refusal.

Running it again at the same level changes nothing (kept items, no new buttons, no new spells, experience and a larger purse untouched).

## Gear selection

* **Pool.** Item templates of quality green or lower, required level and item level at most the level, no random property, not deprecated or flagged
  unobtainable, no skill, spell, reputation, honor, city-rank, quest-start, duration, area or map bound, not quest-bound, an equippable inventory type
  (not shirt, tabard, bag, quiver, ammo), **and obtainable**: sold by a vendor (`npc_vendor`), offered by a quest (reward choices and rewards) or
  dropped by a loot row (`mincountOrRef >= 0`). The live `item_template` never sets the unobtainable flag and carries test and monster-only entries,
  so a source is required.
* **Proficiency.** Whether the character may wear an item is the server's own rule, `PlayerInventory.CanUseItem` (class and race masks, weapon and
  armor skill, level), passed to the planner. An item the character has no skill for is never a candidate, however strong; the slot falls back to the
  best usable item or is reported empty. Covered by the integration test with a stronger axe and no Axes skill.
* **Choice per slot.** Stat-weight score times a style factor (`PlayerbotItemScore`), then item level, then the lower entry. This is a deliberate
  choice over "highest item level at or below the level": the kit follows the build's weights (a managed bot uses its own talent build; other
  characters use a fixed table by class), so a priest gets spirit and intellect and a warrior strength and stamina, and item level only breaks ties.
  Slots are chosen in a fixed order, hands first, because the off hand depends on the main hand (a shield tank takes a one-hander and a shield; a
  two-hand build takes a two-hander when one is usable; dual wielders fill the off hand only if the character can dual wield, and never with a second
  copy of a unique item).
* **Cost.** The obtainable set and the per-level candidate lists are built once per content load and level, not per command; a content reload
  replaces the content objects and so invalidates them.

## Action bars

Attack (6603) goes first, then the class's spells by the spell level of their first rank, highest known rank only; a button holding a lower rank of a
spell whose higher rank is known is upgraded in place, and item and macro buttons are kept. The primary page is buttons 0-11 (the warrior's Battle
Stance page is 72-83, from the ClassicDB start rows); further spells go to buttons 12-23. After the writes the full 120 values are sent as
SMSG_ACTION_BUTTONS and the buttons are saved with the character.

## UNVERIFIED limits

* **In-session redraw.** The 120-value SMSG_ACTION_BUTTONS is the login packet (vmangos `SendInitialActionButtons`). That the 1.12.1 client redraws
  its bars when it receives it again mid-session is **UNVERIFIED**. The data is saved, so relogging shows
  the bars.
* **Stance bars.** The warrior Defensive and Berserker stance pages, and the druid and rogue shapeshift or stealth pages, are never written; their
  offsets are not in the references.
* **Page 2.** Buttons 12-23 as the overflow page is an ArcaneCore layout choice, not a wire fact.
* **Non-retail amounts.** The money amount and the item-level bound are ArcaneCore choices, not retail values.
