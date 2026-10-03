# Integration notes: NPC services and flight paths (`feat/npc-services`)

Base: `codex/integrate-feature-fleet-20261003` (draft PR #11, `0d32fba`). Fleet round 2 area:
gossip, vendor, repair, trainer, innkeeper, banker, spirit healer and flight master
interactions that the #11 ledger listed as missing. The original lane had no schema
change. The vendor/trainer fidelity follow-up adds Characters v21 for bank bag slots
and World v21 for trainer/gossip metadata; the world v6 quests/NPC module is unchanged.

Provenance: behaviour follows vmangos/core (`ItemHandler.cpp`, `NPCHandler.cpp`,
`TaxiHandler.cpp`, `Player.cpp`, `FlightPathMovementGenerator`, `DBCfmt.h`), cross-checked
against cmangos-classic. Both are GPL, so the code is re-expressed rather than copied, and each
method names the vmangos function it mirrors. Client payload layouts are from
gtker/wow_messages 70abb9de (MIT). The SMSG_MONSTER_MOVE flying form (Catmull-Rom, full Vector3
points) follows vmangos `PacketBuilder::WriteMonsterMove`, because gtker models only the
linear form.

## What works

| Service | Opcodes | Notes |
|---|---|---|
| Gossip routing | CMSG_GOSSIP_SELECT_OPTION | gossip_menu_option rows route to vendor/armorer, trainer, taxi, innkeeper (SMSG_BINDER_CONFIRM), banker (SMSG_SHOW_BANK), spirit healer (SMSG_SPIRIT_HEALER_CONFIRM) and spirit guide. Petitioner, tabard, auctioneer, stable and battlemaster raise `QuestNpcServices.ForeignOptionSelected` for their owners. Coded options need a single NUL-terminated code of at most 255 bytes. |
| Vendors | CMSG_LIST_INVENTORY, CMSG_BUY_ITEM, CMSG_BUY_ITEM_IN_SLOT, CMSG_SELL_ITEM, CMSG_BUYBACK_ITEM | Buy uses BuyCount stacks, limited stock with `incrtime` restock (vmangos `GetVendorItemCurrentCount`) and full-bag equip errors. Sell accepts the whole stack or splits a partial stack; SellPrice 0, bags with contents, bank items and others' items are refused. The 12 buyback slots (69–80) carry the PLAYER_FIELD_BUYBACK_PRICE/TIMESTAMP fields; when they are full, the oldest is replaced. Buyback charges the slot price. 1.12 has no extended cost. |
| Repair | CMSG_REPAIR_ITEM | Repairs one item or all of them (equipment, backpack and the contents of equipped bags). Cost is `uint(lost × DurabilityCosts[ilvl][subclass] × DurabilityQuality[(q+1)×2])`, discounted, with a minimum of 1. It is paid item by item and stops at the first item the player can't afford. Repairing restores the item's mods when a worn item was broken. |
| Trainers | CMSG_TRAINER_LIST, CMSG_TRAINER_BUY_SPELL | `npc_trainer` rows. Class, mount, trade-skill and pet trainer types follow vmangos `IsTrainerOf`, including the refusal gossip texts. States: green, red (level, missing rank, class/race via SkillLineAbility, skill) and gray (known). Learning goes through `SpellSystem.LearnSpell` (the existing spellbook): the teaching spell's LEARN_SPELL triggers, otherwise the spell itself. Money is taken only after the spell is learned. |
| Innkeeper | CMSG_BINDER_ACTIVATE | Not allowed in instances. Updates `Player.Home` (saved with the character), sends SMSG_BINDPOINTUPDATE and SMSG_PLAYERBOUND, and closes the gossip. The hearthstone keeps using the existing spell teleport and ack flow (`ITeleportSink`, home bind). |
| Banker | CMSG_BANKER_ACTIVATE, CMSG_BUY_BANK_SLOT | SMSG_SHOW_BANK. `PlayerInventory.CanUseBank` re-checks that the banker is still interactable. A priced slot purchase saves the count and money in one character snapshot; failed purchases send SMSG_BUY_BANK_SLOT_RESULT. See [vendor and trainer area](../areas/vendors-trainers.md). |
| Spirit healer | CMSG_SPIRIT_HEALER_ACTIVATE | Ghosts only. Uses the combat death/corpse flow: `MapCombat.ResurrectAtSpiritHealer` restores 50% health and mana and removes the corpse. Then resurrection sickness (15007) from level 11, shortened to (level − 10) minutes below level 20 and sent as SMSG_UPDATE_AURA_DURATION; 25% durability loss on equipment and bags; and a save. Ghosts can use only spirit healers and guides; the living can't use those. |
| Flight masters | CMSG_TAXINODE_STATUS_QUERY, CMSG_TAXIQUERYAVAILABLENODES, CMSG_ACTIVATETAXI, CMSG_ACTIVATETAXIEXPRESS, CMSG_MOVE_SPLINE_DONE (ignored) | Node discovery (SMSG_NEW_TAXI_PATH and SMSG_TAXINODE_STATUS, stored in the characters v5 taxi mask) and SMSG_SHOWTAXINODES. Activation checks run in vmangos order: busy, already flying, every node known, mounted, too far, a path per hop, a team mount, and money (`ceil(total × discount)`). **Real flight:** `TaxiFlightSystem` sets the mount display and RemoveClientControl plus TaxiFlight, sends SMSG_ACTIVATETAXIREPLY OK and a flying SMSG_MONSTER_MOVE through the TaxiPathNode waypoints (a straight line when the DBC isn't supplied), moves the player at 32 yd/s on every map update, chains the hops of multi-hop routes (express), then dismounts and sends a stop spline at the destination. Money is charged only after the flight starts. Client movement packets are ignored during a flight. A teleport (position drift), a map change or leaving the map aborts the flight; logging out lands the player at the destination first. |

Reputation: when an `IPlayerReputation` is registered in DI, its `GetPriceDiscount` and ranks
are used. Otherwise prices are undiscounted. Nothing depends on a reputation service.

Rounding follows vmangos single-precision maths: vendor and trainer prices use
`uint(price × discount + 0.5f)`, taxi uses `ceil`, and repair uses `uint(cost × discount + 0.5f)` with a
minimum of 1.

## Configuration (`NpcServices` section, all optional)

```json
"NpcServices": {
  "TaxiPathNodeDbcPath": ".../TaxiPathNode.dbc",
  "SkillLineAbilityDbcPath": ".../SkillLineAbility.dbc",
  "DurabilityCostsDbcPath": ".../DurabilityCosts.dbc",
  "DurabilityQualityDbcPath": ".../DurabilityQuality.dbc",
  "BankBagSlotPricesDbcPath": ".../BankBagSlotPrices.dbc",
  "NpcTemplates": [ { "Entry": 1234, "GossipMenuId": 0, "TrainerType": 0, "TrainerClass": 1, "TrainerRace": 0, "TrainerSpell": 0 } ]
}
```

The DBC files are build-5875 files supplied by the developer and are never downloaded. Each
reader checks the exact field count and record size (`DBCfmt.h`) and throws
`InvalidDataException` on a mismatch. When a table is missing, the matching feature degrades:
flights use straight lines, repair repairs nothing, bank slots are not sold, and trainers have
no rank chain while every class fits. World schema v21 imports the creature_template
gossip and trainer columns; `NpcTemplates` can override them.

## Registrations (discovered, no shared list touched)

- `src/ArcaneCore.World/Npc/NpcServiceHandlers.cs` is an `IOpcodeHandlerGroup` that handles
  the opcodes above with exact payload lengths; anything else disconnects. **Conflict risk:**
  startup throws on a duplicate if another area also registers CMSG_GOSSIP_SELECT_OPTION,
  CMSG_LIST_INVENTORY, CMSG_BUY_ITEM, CMSG_SELL_ITEM, CMSG_TRAINER_*, CMSG_BINDER_ACTIVATE,
  CMSG_BANKER_ACTIVATE, CMSG_BUY_BANK_SLOT, CMSG_SPIRIT_HEALER_ACTIVATE, CMSG_TAXI* or
  CMSG_MOVE_SPLINE_DONE.
- `src/ArcaneCore.World/Npc/NpcServicesFeature.cs` is an `IWorldFeature`. It loads the DBC
  tables and options, then fills only the `QuestNpcDependencies` that are still null: items,
  flights, spell learner, reputation from DI, maps and resurrection. It attaches a flight
  updater to every map and handles logout (land, then save) and login (buyback session clock).

## Shared-file edits

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.Game/Items/PlayerInventory.cs` | `AllItems` and `CreateSnapshot` skip buyback slots 69–80. | Buyback items are neither counted (GetItemCount, GetItemByGuid, quest counters) nor saved, as in vmangos. |
| `src/ArcaneCore.Game/Npc/NpcServiceContracts.cs` | `IItemService` gains default-implemented buyback, repair, durability, bank and bank-slot members plus a `BuybackInfo` record. `ITaxiFlights.StartFlight` now takes `(player, nodes, pathIds, mountEntry)` and has a default `IsFlying`. New `IResurrection`. | Service seams. Existing implementers keep compiling, apart from the StartFlight signature, which had no implementation before. |
| `src/ArcaneCore.Game/Npc/QuestNpcServices.cs` | `QuestNpcDependencies` gains a trailing optional `Resurrection`. `InteractableNpc` lets ghosts use only spirit healers and guides. | Spirit healer access. |
| `src/ArcaneCore.Game/Npc/QuestNpcServices.Vendor.cs` | Adds `BuybackItem` and `RepairItem`; prices use floor in single precision. | Vendors and repair. |
| `src/ArcaneCore.Game/Npc/QuestNpcServices.Trainer.cs` | Trainer costs use floor. | vmangos rounding. |
| `src/ArcaneCore.Game/Npc/QuestNpcServices.Travel.cs` | Adds `ActivateTaxiExpress`; the two activations share `ActivateTaxiPath`, which builds the per-hop path list, uses ceil, and charges only after the flight starts. | Flights. |
| `src/ArcaneCore.Game/Npc/QuestNpcServices.Gossip.cs` | The SpiritHealer and Banker options now confirm and show the bank instead of being forwarded. | Gossip routing. |
| `src/ArcaneCore.World/Npc/QuestNpcFeature.cs` | `BuildServices` passes its dependencies through `NpcServicesFeature.Extend` when that feature is present. | Wiring without editing DI registration. |
| `src/ArcaneCore.World/Handlers/MovementHandlers.cs` | Ignores client movement while `UnitFlags.TaxiFlight` is set. | The server owns the position during a flight. |

## Limits (honest)

- **Skills:** the skills feature is the owner when it is active (`SkillsFeature.IsActive`); without skill
  content (or in `Skills:Mode=Legacy`) players have no `Skills`, every skill value reads 0 for trainers, and
  the skill conditions fail closed. spell_chain `req_spell`
  (talent-dependent ranks) is not modelled; only the SkillLineAbility forward chain is.
- **Bank bag slots:** Characters schema v21 adds `bank_bag_slots`. Missing BankBagSlotPrices data still prevents purchase.
- **CMSG_BUY_ITEM_IN_SLOT** resolves the requested bag and slot before storage.
- **Mount:** only UNIT_FIELD_MOUNTDISPLAYID is set. UNIT_FLAG_MOUNT is not set because its 1.12
  value isn't verified here.
- **Flights:** logging out mid-flight lands the player at the destination instead of resuming
  the flight on login. Observers who come into view mid-hop get no spline (they see the player
  move by position updates). Movement is linear between waypoints, while the client draws a
  Catmull-Rom curve; both end on the same nodes. TaxiPathNode delays and flags are ignored.
- **Spirit healer:** no corpse bones and no graveyard teleport when the corpse's graveyard
  differs. The 17251 cast visual is not shown.
- **Buyback** items are not saved across logout, as in vmangos.
- Quest item counters rely on `PlayerInventory.ItemCountChanged`; `InventoryItemService`
  doesn't report quest events itself.
- No shapeshift or casting check before a flight; that needs the auras/spells owners.

## Possible merge conflicts

- The reputation worker may also edit `CreatureQuestLookup` or `QuestNpcFeature.BuildServices`.
  Keep both: this branch only wraps the dependencies with `ExtendDependencies`, and `Extend`
  fills only null members.
- The quest-progression worker may touch `QuestNpcServices.cs` and `QuestNpcDependencies`.
  This branch appends one optional record member at the end.

## Tests

- `tests/ArcaneCore.Game.Tests/Npc/`: 44 tests across vendors (buy, sell, buyback, stock,
  repair, rounding), trainers, innkeeper, taxi (discovery, activation, express multi-hop,
  landing, aborts, packet layouts), bank, spirit healer (real death, release and resurrect)
  and gossip routing. Adverse cases include no money, wrong level, out of range, dead player,
  hostile NPC, wrong class and full bags.
- `tests/ArcaneCore.Data.Tests/Npc/NpcServiceDbcTests.cs`: 5 tests with synthetic DBC images
  for each reader, including wrong layouts.
- `tests/ArcaneCore.World.Tests/Npc/NpcServiceWorldTests.cs`: 24 tests covering handler
  registration, malformed payloads disconnecting, and innkeeper/banker flows over the socket.

## Schema follow-up

- World v21 stores `creature_template.gossip_menu_id`, `trainer_type`, `trainer_class`,
  `trainer_race` and `trainer_spell`; `NpcServices:NpcTemplates` remains an override.
- Characters bank bag slot count was added in v21 by the vendor/trainer fidelity lane.
