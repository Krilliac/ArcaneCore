# Vendors and trainers (build 5875)

The NPC service uses the existing inventory, skills and talent systems. `D:\refs` was read only; no reference code or data was copied.

## Delivered

| Area | Behavior and read-only reference |
| --- | --- |
| Vendor list and purchase | `CMSG_LIST_INVENTORY` sends visible `npc_vendor` rows with compact one-based slots, stock, price, durability and BuyCount. Empty lists include a zero error byte. Purchase checks vendor, item, stock, reputation, money and capacity. Vanilla list items have no extended cost. `D:\refs\vmangos\src\game\Handlers\ItemHandler.cpp:693-777`; `D:\refs\vmangos\src\game\Objects\Player.cpp:18389-18508`. |
| Stock | Each creature GUID has its own limited item count. A purchase subtracts `BuyCount * count`; each `incrtime` interval adds `BuyCount` up to `maxcount`. Unlimited rows appear as `0xFFFFFFFF`. `D:\refs\vmangos\src\game\Objects\Creature.cpp:3429-3505`; `D:\refs\vmangos\src\game\Handlers\ItemHandler.cpp:768`. |
| Prices | Vendor list, purchase and trainer prices use single-precision `uint32(base * reputationDiscount + 0.5f)`. `IPlayerReputation` supplies the discount; absent service means 1.0. `D:\refs\vmangos\src\game\Handlers\ItemHandler.cpp:747-765`; `D:\refs\vmangos\src\game\Objects\Player.cpp:18442-18445`; `D:\refs\vmangos\src\game\Handlers\NPCHandler.cpp:112-119,306-315`. |
| Sell and buyback | Selling pays `SellPrice * count`; the paid amount is the buyback price. Twelve buyback slots carry price and timestamp update fields. A full list replaces the oldest. Buyback charges its stored price; sale and buy failures use the vanilla result packets. Buyback is session-only. `D:\refs\vmangos\src\game\Handlers\ItemHandler.cpp:442-645`; `D:\refs\vmangos\src\game\Objects\Player.cpp:11425-11469`. |
| Repair | `DurabilityCosts.dbc` selects an item-level/subclass multiplier and `DurabilityQuality.dbc` selects `(quality + 1) * 2`. Base cost is `uint32(lost * multiplier * qualityFactor)`; reputation then rounds with `+ 0.5f`, minimum one copper. Repair-all traverses equipment, backpack and equipped bags with their contents, excluding bank, buyback and keyring. An unaffordable item stays broken. Guild-bank payment is absent. `D:\refs\vmangos\src\game\Objects\Player.cpp:4900-4977`. |
| Trainers | The list greys known spells, marks unmet level, skill, rank, class or race requirements red, and marks an exhausted first primary profession rank green-disabled. Purchase rechecks requirements and sends `SMSG_TRAINER_BUY_SUCCEEDED` or `SMSG_TRAINER_BUY_FAILED` (unavailable 0, money 1, skill 2). Class, pet, mount and profession trainer metadata is checked. `D:\refs\vmangos\src\game\Objects\Player.cpp:4234-4299`; `D:\refs\vmangos\src\game\Handlers\NPCHandler.cpp:97-139,141-342`; `D:\refs\vmangos\src\game\Objects\Creature.cpp:1523-1569`. |
| Talent reset | The existing talent service owns class-trainer gossip, wipe confirmation, one-gold first reset, later five-gold multiplier steps, cap and 30-day decay. `D:\refs\vmangos\src\game\Objects\Player.cpp:4030-4072,4075-4140,8260-8267`; see [talents](talents.md). |
| Bank | A banker opens the bank and inventory moves recheck access. Slot purchase uses the next `BankBagSlotPrices.dbc` row and persists `bank_bag_slots` with money (Characters schema v21). Failed purchase sends `SMSG_BUY_BANK_SLOT_RESULT` as one `u32`: too many 0, insufficient funds 1, not banker 2; success has no reply. `D:\refs\vmangos\src\game\Handlers\ItemHandler.cpp:872-909`; `D:\refs\vmangos\src\game\Objects\Player.cpp:14661-14664,16401-16404`; `D:\refs\vmangos\sql\characters.sql:74`; `D:\refs\wow_messages\wow_message_parser\wowm\world\item\smsg_buy_bank_slot_result.wowm:3-12`. |

## Configuration and limits

`NpcServices` accepts developer-supplied build-5875 DBC paths for durability costs/quality, bank bag prices and skill line abilities. Missing price tables fail closed: repair and bank slot sales are unavailable. World schema v21 stores `gossip_menu_id`, `trainer_type`, `trainer_class`, `trainer_race` and `trainer_spell` from imported vmangos `creature_template` rows. The creature lookup supplies these to trainer and gossip handlers. `NpcServices:NpcTemplates` remains an explicit override. `D:\refs\vmangos\src\game\Objects\CreatureDefines.h:242,272-275`; `D:\refs\vmangos\src\game\Objects\Creature.cpp:622,1375-1441`.

`spell_chain.req_spell` is not represented in the rank catalog, so an additional spell prerequisite can be missing. Honor-rank vendor items fail closed until an honor seam exists. The normal asynchronous character save queue persists purchased bank slots with money; durable success is established at the save/flush boundary. Real 1.12.1 client acceptance remains pending.

The NPC content store reads `npc_trainer` rows keyed by creature entry. vmangos also has
trainer-template/`trainer_id` indirection (`D:\refs\vmangos\src\game\Handlers\NPCHandler.cpp:275-290`);
that content path is not imported yet. A vmangos dump that uses only the indirection needs
its trainer spells materialized into ArcaneCore's `npc_trainer` table before those trainers
offer lessons. Whether the selected content dump uses that path is an open data question.

## Verification

Release solution build (0 warnings, 0 errors) and the Cryptography, Data (SQLite), Game, MockClient, Realm and World suites plus the MockClient self-test passed locally. MariaDB/PostgreSQL provider tests run only on hosted CI and were not run locally. Real 1.12.1 client acceptance was not performed.
