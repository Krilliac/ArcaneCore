# Playerbot defense and carried armor

The controller distinguishes an outgoing auto-attack victim from incoming attackers.
After pending loot and completed quest-return handling, a valid outgoing creature victim
keeps priority. Otherwise an ordinary attackable incoming creature can become the defensive
target, including creatures above the optional grind level limit. The route is retained while
that target is unchanged. A combat timer with no victim or attackers does not open another
optional fight. An already selected corpse can still enter normal loot handling.

When idle, after immediate food/drink recovery and before optional town or grind decisions,
bots can equip a strictly higher-armor plain item that they already carry. This first policy
supports fixed head, shoulder, chest, waist, leg, foot, wrist, hand and back slots. It rejects
broken items, enchantments, random properties, stats, resistances, item spells and set effects.
Weapons, shields, jewelry and bags are outside this policy. Selection is bounded to 128 items
and one admitted action per update. Item-aware usability checks precede the ordinary
`CMSG_AUTOEQUIP_ITEM` handler; the handler retains final equipment and settlement authority.
Equipment selection yields to active casts, combat, death, depleted action budget and pending
corpse loot. It does not grant items, skill points, levels or synthetic stats.

`.playerbot inspect` reports a same-map attacker count and at most four identities, independently
of the outgoing victim. It also reports equipped main-hand and foot item IDs and durability,
effective main-hand weapon skill and the current armor field. Capture runs on the World thread
without changing gameplay. Missing values are explicitly unavailable. Each attacker has a
separate bounded output line.

The policy uses the existing `UnitCombat.Attackers`, `CanAttack`, `CanUseItem(Item)` and inventory
auto-equip contracts. Their source grounding remains in the combat and item modules, including
the pinned CMaNGOS/vmangos references. Local ClassicDB 1.12.1 data and live Source82 observation
identified starter weapons, low sword skills and an unequipped armor upgrade; no proprietary
data is included here. Supplemental WoWWiki/Wowhead material must remain version checked.

Broader gear scoring, weapon replacement, durability repair planning, safer post-reclaim travel
and durable quest progression remain separate work. A passing local suite does not establish
original-client acceptance or prove that recurrent deaths have ended.
