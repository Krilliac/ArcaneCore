# Inventory, bags and bank (build 5875)

This area extends [items.md](items.md). The current implementation uses the 1.12.1 slot model in `InventorySlots`, the rules in `PlayerInventory.Rules`, the mutations in `PlayerInventory.Operations` and `PlayerInventory.Storage`, and the world opcode handlers in `World/Items`. The latest integration baseline is [wave3-integration.md](../integration/wave3-integration.md). Reference paths below are read-only; no source or data was imported from them.

## Verified rules and data

| Behavior | ArcaneCore owner | 1.12.1 reference |
| --- | --- | --- |
| Equipment 0-18, carried bags 19-22, backpack 23-38, bank 39-62, bank bags 63-68, keyring 81-96; keyring capacity grows with level | `InventorySlots`, `PlayerInventory.Rules` | `D:\refs\vmangos\src\game\Objects\Player.cpp:8572-8647`, `D:\refs\vmangos\src\game\Objects\Player.h` (`EquipmentSlots`, `KeyRingSlots`) |
| General bags, soul, herb and enchanting bags, quivers and ammo pouches use their item class, subclass and **ordinal** bag-family value to admit items; special bags refuse nested bags | `ItemTemplateRules.CanGoIntoBag` | `D:\refs\vmangos\src\game\Objects\Item.cpp:139-190`, `D:\refs\vmangos\src\game\Objects\ItemPrototype.h:87-98,174-185` |
| Engineering supplies is item bag family 8, but there is no engineering-container subclass in pre-BC. The 1.12 special-bag switch has no engineering case. Engineering materials fit general bags. | `BagFamily.EngineeringSupplies`; no engineering-only bag rule | `D:\refs\vmangos\src\game\Objects\ItemPrototype.h:87-98,174-185`, `D:\refs\vmangos\src\game\Objects\Item.cpp:139-190` |
| Bank bag slot purchase (`CMSG_BUY_BANK_SLOT`) results and the persisted bank slot count are owned by the vendor/trainer fidelity lane (`SMSG_BUY_BANK_SLOT_RESULT`, `bank_bag_slots`); this lane does not touch them. | `QuestNpcServices.BuyBankSlot` (vendor-trainer-fidelity lane) | `D:\refs\vmangos\src\game\Handlers\ItemHandler.cpp:872-910`, `D:\refs\vmangos\src\game\Server\Packets\Item.cpp:137-145` |
| Moves, merges, swaps, auto-store, auto-equip, splits and destruction use the common store/equip/unequip checks; invalid source positions report `ITEM_NOT_FOUND`, invalid destinations report `ITEM_DOESNT_GO_TO_SLOT`. The split destination is validated as a non-explicit position, as in vmangos. | `PlayerInventory.Operations`, `World/Items/ItemHandlers` | `D:\refs\vmangos\src\game\Handlers\ItemHandler.cpp:36-126`, `D:\refs\vmangos\src\game\Objects\Player.cpp:11010-11295` |
| `SMSG_INVENTORY_CHANGE_FAILURE` includes the result, required level for the level error, both item GUIDs and the bag slot. The enum follows all build-5875 `EQUIP_ERR_*` values. | `InventoryResult`, `ItemPackets.InventoryChangeFailure` | `D:\refs\vmangos\src\game\Server\Packets\Item.cpp:270-283`, `D:\refs\vmangos\src\game\Objects\ItemDefines.h:36-110` |
| Class/race, required skill/rank/spell/level/honor/reputation, proficiency, unique carried count, unique-equipped entry, quiver limit, dual wield, two-hand/off-hand and combat restrictions are checked before equip. | `PlayerInventory.Rules`, `PlayerItemRequirements` | `D:\refs\vmangos\src\game\Objects\Player.cpp:9659-9775,10026-10109,20671-20682` |
| Pickup/quest items bind when stored; BoE items bind when equipped or put in an equipped bag. Bound items cannot leave through trade/mail/auction. The normal split, merge and destroy paths preserve item flags and counts. | `PlayerInventory.Storage`, `PlayerInventory.Economy`, `PlayerInventory.Operations` | `D:\refs\vmangos\src\game\Objects\Item.cpp:932-952`, `D:\refs\vmangos\src\game\Objects\Player.cpp:10461-10482,10961-11092` |
| An item holding generated loot or money cannot be offered through trade or transferred through mail/auction; an emptied loot record may leave. | `PlayerInventory.CanBeTraded` and `CanTransferOut` | `D:\refs\vmangos\src\game\Objects\Item.cpp:932-952`, `D:\refs\vmangos\src\game\Objects\Item.h:130-134` |

## Limits and open questions

- The source template has `MaxCount` and `UniqueEquipped` by item entry. There is no modeled item-limit-category field, and vmangos 1.12's `CanEquipUniqueItem` checks the entry itself (`Player.cpp:20671-20682`). A category system needs confirmed build-5875 data before it can be added.
- The inventory has no separate `ITEM_LOCKED` mutation gate. The template lock and dynamic unlocked flag are used by item opening; vmangos inventory movement does not check lock state in its store/equip operations. A generated loot container's temporary-loot state has no direct equivalent in `Item`, so the vmangos `ALREADY_LOOTED` move/split guard is not implemented here. The generated-loot transfer guard is implemented.
- Refundable-item timers/categories from later expansions are absent in this 1.12 implementation. No later-expansion refund rule is enabled.
- `CanEquipItem` still refuses a two-hander if its off-hand item cannot fit in inventory. vmangos may mail that item in other unequip paths. The real 5875 client has not been used to validate this lane's inventory-error packets.

## Verification

Verified by the integrator: Release build, Game/World tests (see the lane report). Hosted MariaDB/PostgreSQL and a real build-5875 client were not run. No schema version changed.
