# Ranged lane S06: quivers and ammo pouches

Branch `claude/vw5-ranged-combat`. Retail only. Stores exercised: none (the aura is recomputed from the worn equipment at login; nothing is persisted).

## Delivered

| Piece | Where | Reference |
|---|---|---|
| The 24 quivers and pouches of classic-db carry an ON_EQUIP item spell (14824-14829: aura 141, +10 to +15 percent; Light Quiver 2101 -> 14824, Ribbly's Quiver 2662 -> 14828, Gnoll Skin Bandolier 19320 -> 14829). Wearing one casts it on the player, taking it off removes it; at login the worn one is re-applied once (any leftover aura is removed first, so it can never stack). | `Game/Ranged/QuiverHaste.cs`, `World/Ranged/RangedFeature.cs` (`PlayerLoggedIn`) | vmangos `Player::ApplyEquipSpell`, `_ApplyItemMods` for every slot below BAG_END (`Player.cpp:6828-6833`) |
| `PlayerInventory.BagEquipChanged(item, slot, equipped)` for the four bag slots 19-22 (raised from `EquipItem` / `RemoveItem`) and `ReplayBagEquips()`. Bags were outside every hook before: the stat applier only sees slots below 19. | `Items/PlayerInventory.Storage.cs` | |
| The aura handler (S01) decides: only a player whose ranged weapon has `ammo_type != 0` (bows, guns, crossbows, and thrown weapons, which carry 4), evaluated once at apply; a weapon swapped later does not re-evaluate it (vmangos behaviour, pinned by a test); it stacks multiplicatively with Rapid Fire. | `Game/Spells/Auras/AttackSpeedAuras.cs` | vmangos `SpellAuras.cpp:5141-5166` |

## Limits

* Only quivers (item class 11) are handled: a general ON_EQUIP engine (weapon and armor "Equip:" spells, `ApplyEquipSpell` for everything worn) does not exist in the tree. Whoever builds it should make this class a thin consumer.
* Quest and economy paths that place an item straight into a bag slot (`PlaceInOwnSlot` from rewards or mail) do not raise the event; the login replay and the equip/unequip paths cover the normal cases.
* `Equip`ping a quiver while dead or in a transit state is not special-cased.
