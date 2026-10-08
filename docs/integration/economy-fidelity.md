# Economy fidelity (wave 3)

Brings the economy feature ([economy](economy.md)) closer to vanilla 1.12.1 as implemented by vmangos, the primary reference for this lane (`D:\refs\vmangos`, GPL, read for verification only; nothing copied). Every behavior below is re-implemented from the cited lines. Where a deviation is deliberate it sits behind an `Economy` option whose default is the vmangos value. No store method, query, index or schema version changed: the settlement contract of [economy](economy.md) is untouched.

Note on the reference: vmangos and mangos-classic are emulators, not the retail client. Two defaults (expired money-only letters are deleted, a letter with attachments can be deleted) follow the emulators and are listed as decisions for the developer.

## Delivered

| Area | Behavior (retail default) | Option | vmangos source |
| --- | --- | --- | --- |
| Auction deposit | Single-precision arithmetic in vmangos order (`float(sell*count*units)`, `* depositPercent`, `/ 100.0f`, minimum, then `* rate`). 98 of 240,444 (item, count, duration, percent) combinations of the classic-db item table differ by 1 copper from integer math (e.g. sell price 104729, 24 h, 75%: 942560, not 942561). The integer product is taken in 64 bits (vmangos wraps it in 32; shipped data never reaches that, max 24,000,000). | `AuctionRateDeposit` 1.0 | `AuctionHouse/AuctionHouseMgr.cpp:98-110`, `World.cpp:535` |
| Auction cut | `cutPercent * bid * rate / 100.0f` in single precision. The product stays 64-bit on purpose (vmangos wraps it above about 286M copper at 15%). | `AuctionRateCut` 1.0 | `AuctionHouseMgr.cpp:845-848`, `World.cpp:536` |
| Auction listing | Zero bid or duration dropped silently; start bid or buyout above 2,000,000,000 refused with NOT_ENOUGH_MONEY; bid above buyout; house; per-account limit with the system message; duration; item errors answer INVENTORY / ITEM_NOT_FOUND (empty guid: ITEM_NOT_FOUND); expiry is `minutes * 60 * rate`. | `AuctionRateTime` 1.0, `AuctionAccountConcurrentLimit` 0 | `Handlers/AuctionHouseHandler.cpp:230-405`, `World.cpp:534,538` |
| Auction refusals | A bid above the bidder's money and a cancel whose cut the seller cannot pay get no answer. | `AuctionSilentRefusals` true | `AuctionHouseHandler.cpp:498-503, 592-594` |
| Auction browse | The list is ordered by buyout price (then id), so the 50 rows of a page follow the buyout; the maximum level applies only with a minimum level; the chest slot also lists robes. | none | `AuctionHouseMgr.cpp:71-75, 711-790` |
| Mail send | Subject over 64 bytes, body over 500 bytes and COD above 100,000,000 are dropped without an answer; money is checked before the recipient's box (refused when it holds more than 100), then team, then an open trade (INTERNAL_ERROR); a COD without an item is zeroed; letters without a body carry the COPIED flag. | `MailSubjectMaxLength` 64, `MailBodyMaxLength` 500, `MailMaxCodCopper` 100000000, `MailOversizeAnswersError` false, `MaxMailboxSize` 100 (refuse above) | `Handlers/MailHandler.cpp:155-166, 243-276, 404-416` |
| Mail timing | Letters with an item or money arrive after one hour, text-only letters at once; expiry (3 days COD, else 30) counts from delivery; an auction note without item and money lives one hour; SMSG_RECEIVED_MAIL is sent when a delayed letter becomes deliverable (checked each second, also after login); reading a letter clamps a longer remaining life to 3 days; the list holds at most 254 letters; a returned item crossing accounts is delayed like a new letter. | `MailDeliveryDelaySeconds` 3600, `MailReadExpiryDays` 3 (0 off) | `MailHandler.cpp:404-406, 454-458, 758-766`, `Mail/Mail.cpp:252-291, 312-326`, `Chat/MasterPlayer.cpp:66-76, 175-209`, `World.cpp:691` |
| Mail expiry | Player letters that are not COD payments or already returned are returned once, with an item or money (the pre-lane behaviour; no retail source shows money-only letters being deleted). COD-payment, returned and system letters are deleted with their escrow item. vmangos returns only letters with an item. | `ReturnExpiredMoneyOnlyMail` true (false: vmangos, money-only letters deleted) | `ObjectMgr.cpp:6995-7044`, mangos-classic `ObjectMgr.cpp:6140,6188` |
| Mail delete | Only an emptied, non-COD letter may be deleted (pre-lane behaviour). vmangos allows any letter except cash on delivery and destroys the attachment (escrow row removed in the same settlement). | `AllowDeleteWithAttachments` false (true: vmangos) | `MailHandler.cpp:469-491` |
| Mailbox | The default access requires a spawned, interactable mailbox object of the player's map within interaction distance (`GameObjectMapSystem.FindInteractable`). A map without a game-object system keeps the earlier permissive rule. The `IMailboxAccess` seam stays replaceable. | none | `MailHandler.cpp:64-73` |
| Trade accept | An accept right after a modification is bounced with BACK_TO_TRADE. vmangos measures with whole-second `time()`, so its effective delay is "not within the same second"; that is the default, a true 200 ms is the precise mode. | `TradeScamPreventionMs` 200 (0 off), `TradeScamPreventionWholeSeconds` true | `Handlers/TradeHandler.cpp:251-257, 657-658` |
| Trade negotiation | Set or clear of gold or item clears the partner's acceptance with BACK_TO_TRADE on every call, stamps both modification times; an unchanged value does nothing more; a real change also clears the owner's acceptance and refreshes only the trader's view. | none | `TradeHandler.cpp:675-746`, `Objects/TradeData.cpp:57-140` |
| Trade gold and space | Not enough gold: notification 801 to the short player, BACK_TO_TRADE to the partner. Not enough bag space: the window stays open, 802 to the player who cannot receive and 803 to the partner, both acceptances cleared. Texts are classic-db `mangos_string` 801-803. | `TradeSpaceNotifications` true (false: earlier close-window behavior) | `TradeHandler.cpp:274-290, 420-455` |
| Auction outbid notice (wave 2) | SMSG_AUCTION_BIDDER_NOTIFICATION to the outbid player is built from the auction before it takes the new bid: the player's own GUID, its bid and that bid's outbid step. A cancelled auction tells its online bidder with SMSG_AUCTION_REMOVED_NOTIFICATION. | none | `AuctionHouseHandler.cpp:148-195, 513, 535` |
| Bidder list outbid ids (wave 2) | CMSG_AUCTION_LIST_BIDDER_ITEMS reads the client's outbid auction ids and lists those still in the house first (client order), then the player's highest bids; page and total cover both. A body without the list lists no outbid ids. | none | `AuctionHouseHandler.cpp:665-679, 708-728`, `AuctionHouseMgr.cpp:679-693`, `Packets/AuctionHouse.cpp:11-23` |

## Not delivered, with reason

| Item | Why |
| --- | --- |
| Auction house identity (houses 1-7 from the auctioneer's faction template, team pools, `AuctionHouse.dbc` reader, `AllowCrossTeamAuction`) | Large cross-cutting change: every house comparison, the NPC contract (`NpcInfo.FactionTemplateId`, shared file) and a DBC the references do not contain. No 1.12.1 `AuctionHouse.dbc` exists under `D:\refs`, so the percentages (15/5, 75/15) remain unverified and were not changed. Needs a developer-supplied DBC. |
| Unsold-expiry owner notification parity | The outbid and cancelled-to-bidder notices are delivered (above). The expiry path still sends SMSG_AUCTION_REMOVED_NOTIFICATION to the seller, whose first field vmangos writes as the auction id and wow_messages as the item entry (the writer follows wow_messages); not verified against a client. |
| Usable filter with `CanUseItem` and known-recipe hiding | The usable callback exists; the item-requirements primitive is not wired into this feature. (The bidder-list outbid ids are delivered, above.) |
| Suffix name search | The random property id is generated and persisted (docs/areas/items.md, "Economy-items lane") and every economy packet writes it; build 5875 has no random suffixes, so the suffix factor (property seed) is always 0. Searching listings by the "of the …" suffix text needs the client's localized ItemRandomProperties names. |
| `Item::CanBeTraded` loot checks (generated loot, being looted, bound by enchant) | Done since the 2026-10-07 review: an enchantment that can soulbind blocks the trade (finding 92, `PlayerInventoryEconomyTests.CanBeTraded_RefusesAnItemCarryingAnEnchantmentThatCanSoulbind`), and a stack holding generated loot is neither split nor merged into (finding 64). |
| Stack merging when receiving items | Done since the 2026-10-07 review (finding 53): an arriving stack fills an existing stack first and the merged instance ends as the giver's consumed item in the settlement (`PlayerInventoryEconomyTests.Stage_MergesAnArrivingStackIntoRoomOfAnExistingOne_WhenNoSlotIsFree`, Data `EconomyStoreTests.Trade_AStackMergedIntoTheReceiversStack_EndsAsTheGiversConsumedItem`). |
| System mail API, quest reward mail, level reward mail, GM stationery senders | Other lanes own the consumers; the sender API was not started. |
| Trade initiate parity (stunned, taxi), cancel distance of 5.0 between bounding radii on movement | Needs a movement hook and unit state from other lanes. |
| Expiry sweep skipping letters whose receiver is online | ArcaneCore settles expiry through the cache so the client view stays consistent; skipping could starve the fixed-size batch of the store query. Deliberately kept. |
| BEGIN_TRADE honored from either side, mail refused for GM sending items | Client never sends the former; the latter is a vmangos configuration (`GM.AllowTrades`). |
| Trial-account restrictions, MailSpam antispam, GM auction access mode | Not part of vanilla gameplay (vmangos extensions). |

## Deliberate keeps

Seller cancel after expiry stays refused and a bid at the expiry second stays refused (vmangos allows both until the sweep); 64-bit cut and deposit products; 100% durable mail through the settlement ledger.

## Decisions for the developer

1. `ReturnExpiredMoneyOnlyMail` defaults to true (retail-safe, pre-lane). Set false to delete the money of an expired money-only letter as vmangos and mangos-classic do; no retail source for either is available.
2. `AllowDeleteWithAttachments` defaults to false (pre-lane). Set true to let a player destroy an attached item or money by deleting the letter, as vmangos does.
3. `TradeScamPreventionWholeSeconds` true reproduces vmangos' whole-second behavior; set false for a real 200 ms.
4. Real-client acceptance is still open for the delayed mail notification and the trade notifications.

## Evidence

- Game rules: `AuctionFormulaTests` (expected values computed with float32 operations from classic-db sell prices), `MailTimingRulesTests`, `TradeScamPreventionTests`, `AuctionSearchParityTests`, updated `EconomyRulesTests`.
- Wave 2: the three-bot playerbot scenario `AuctionOutbidScenarioTests` (World.Tests/Playerbots/Scenarios: list, bid, outbid notice with the old bid, bidder list with outbid ids, cancel notice to the bidder).
- Mock-client wire tests (SQLite): `EconomyMailSendParityTests`, `EconomyMailExpiryParityTests`, `EconomyTradeParityTests`, `EconomyAuctionSellParityTests`, `EconomyAuctionSilentRefusalTests`; world test `MailboxAccessTests`.
- Provider evidence: nothing in this lane adds or changes a store, query or schema, so no new MariaDB or PostgreSQL semantics are involved and no provider theory was added; every test above ran on SQLite only. The existing store theories in `EconomyStoreTests` and `EconomyAuctionSnapshotTests` still run on hosted CI.
- The mock-client mail and auction tests register stand-ins for `IMailboxAccess` and `IAuctioneerAccess` because the synthetic world spawns neither object; the real mailbox check is covered by `MailboxAccessTests`.

## Shared-file edits

None outside `*/Economy/` and the economy tests, apart from this document and the pointer in [economy](economy.md).
