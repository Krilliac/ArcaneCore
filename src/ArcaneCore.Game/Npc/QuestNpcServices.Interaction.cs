using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;
using ItemInventoryResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.Game.Npc;

/// <summary>How a GM's <c>.quest add</c> ended (<see cref="QuestNpcServices.GmAddQuest"/>).</summary>
public enum GmQuestAddResult
{
    /// <summary>The quest is in the log; its delta went to persistence.</summary>
    Added,

    /// <summary>The player's journal is not loaded, or its quest settlement is pending: nothing changed.</summary>
    NotReady,

    /// <summary>The quest already takes a log slot (mangos would give it a second one).</summary>
    AlreadyInLog,

    /// <summary>The quest needs an adapter this server lacks (<see cref="QuestNpcServices.MissingAdapters"/>); withheld.</summary>
    Unsupported,

    /// <summary>No free log slot; the player was sent SMSG_QUESTLOG_FULL.</summary>
    LogFull,

    /// <summary>The quest's source item cannot be given; the player was told why.</summary>
    SourceItemRefused,
}

/// <summary>How a GM's <c>.quest complete</c> ended (<see cref="QuestNpcServices.GmCompleteQuest"/>).</summary>
public enum GmQuestCompleteResult
{
    /// <summary>The quest is complete and turn-in ready as far as its objectives go.</summary>
    Completed,

    /// <summary>The player's journal is not loaded, or its quest settlement is pending: nothing changed.</summary>
    NotReady,

    /// <summary>The player has no status for the quest (mangos: "Quest %u not found.").</summary>
    NotOnQuest,

    /// <summary>The quest has failed (a timed quest that ran out); ArcaneCore refuses to force it complete.</summary>
    Failed,
}

/// <summary>
/// Creature quest status, details, acceptance and abandonment. Reimplemented from vmangos/core
/// 4b3d241cffe245a1f68da11380bce96c23db48c0 Handlers/QuestHandler.cpp and Objects/Player.cpp
/// CanSeeStartQuest, CanAddQuest, AddQuest, RemoveQuestAtSlot and TakeQuestSourceItem. Source items,
/// delivery counters, exploration triggers, repeatable and timed quests have adapters; source spells,
/// party confirmation, PvP activation and auto rewards still fail closed.
/// <para>
/// Item-started quests: the client asks for the details of <c>item_template.startquest</c> with the item's own
/// GUID as the quest giver, so <see cref="QuestgiverQueryQuest"/> and <see cref="AcceptQuest"/> accept an item in
/// the player's bags whose template starts the quest (mangos zero Item::HasQuest, Object/Item.h:393, and the
/// TYPEMASK_..._OR_ITEM lookups of QuestHandler.cpp:171-181, 264-273). The GM tooling at the end of this file
/// (<c>.quest add|remove|complete</c>, World/Gm/Quest) follows mangos zero ChatCommands/QuestCommands.cpp:47-283.
/// </para>
/// </summary>
public sealed partial class QuestNpcServices
{
    /// <summary>A quest entered the player's journal (cmangos Player::AddQuest starts quest_start DB scripts after the journal change).</summary>
    public event Action<Player, ObjectGuid, Quest>? QuestAccepted;

    public void QuestgiverStatusQuery(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } state || FindNpc(player, guid) is not { } npc)
        {
            return;
        }

        // vmangos HandleQuestgiverStatusQueryOpcode (QuestHandler.cpp:36-77) only refuses hostile creatures; it never tests the
        // quest-giver npc flag (four relation NPCs of classic-db lack it and still show their marks).
        DialogStatus status = DialogStatus.None;
        if (!npc.IsHostile)
        {
            foreach (uint id in EndersOf(npc))
            {
                if (Quests.Get(id) is not { IsActive: true } quest)
                {
                    continue;
                }

                // GetDialogStatus (QuestHandler.cpp:517-526): a finished-and-unrewarded quest or a takeable autocomplete quest
                // is Reward2, or RewardRep when it is an autocomplete repeatable one; otherwise an incomplete quest.
                DialogStatus candidate = (state.Quests.GetStatus(id) == QuestStatus.Complete && !state.Quests.RewardStatus(quest))
                    || (quest.IsAutoComplete && CanTakeQuest(state, quest, []))
                    ? quest.IsAutoComplete && quest.IsRepeatable ? DialogStatus.RewardRep : DialogStatus.Reward2
                    : state.Quests.GetStatus(id) == QuestStatus.Incomplete ? DialogStatus.Incomplete : DialogStatus.None;
                if ((byte)candidate > (byte)status)
                {
                    status = candidate;
                }
            }

            foreach (uint id in StartersOf(npc))
            {
                if (Quests.Get(id) is not { } quest || !CanTakeQuest(state, quest, [], visibilityOnly: true)
                    || (Options.HighLevelHideDiff >= 0 && (long)player.Level + Options.HighLevelHideDiff < quest.MinLevel))
                {
                    continue;
                }

                DialogStatus candidate = player.Level < quest.MinLevel ? DialogStatus.Unavailable
                    : quest.IsAutoComplete || (quest.IsRepeatable && !quest.HasFlag(QuestFlags.Unk2) && state.Quests.Get(id)?.Rewarded == true)
                        ? DialogStatus.RewardRep
                    : Options.LowLevelHideDiff < 0 || player.Level <= (long)QuestLevelForPlayer(player, quest) + Options.LowLevelHideDiff
                        ? DialogStatus.Available : DialogStatus.Chat;
                if ((byte)candidate > (byte)status)
                {
                    status = candidate;
                }
            }
        }

        // Quest.cpp and gtker quest/smsg_questgiver_status.wowm: GUID followed by u32 status.
        var body = new PacketWriter(12);
        body.WriteUInt64(guid.Value);
        body.WriteUInt32((uint)status);
        Send(player, WorldOpcode.SmsgQuestgiverStatus, body);
    }

    /// <summary>vmangos Player::GetQuestLevelForPlayer (Player.h:1114): the quest's level when positive, else the player's own (14 classic-db quests have level 0).</summary>
    private static int QuestLevelForPlayer(Player player, Quest quest) => quest.QuestLevel > 0 ? quest.QuestLevel : player.Level;

    public void QuestgiverQueryQuest(Player player, ObjectGuid guid, uint questId)
    {
        if (Ready(player) is null || Quests.Get(questId) is not { IsActive: true } quest)
        {
            return;
        }

        // mangos zero HandleQuestgiverQueryQuestOpcode (QuestHandler.cpp:264-273): the giver is a creature, a game object
        // or an item in the bags whose template starts the quest; the details name the giver's GUID, the item's included.
        if (QuestStartingItem(player, guid, questId) is null
            && (InteractableNpc(player, guid, NpcFlags.QuestGiver) is not { } npc
                || !(StartersOf(npc).Contains(questId) || EndersOf(npc).Contains(questId))))
        {
            return;
        }

        Send(player, WorldOpcode.SmsgQuestgiverQuestDetails, QuestPackets.Details(guid, quest, Options.RateDropMoney,
            id => Deps.Items?.GetItem(id)?.DisplayId ?? player.Inventory.Templates.Find(id)?.DisplayId ?? 0));
    }

    /// <summary>
    /// The item in the player's bags (not the bank: the client only uses bag items) that <paramref name="guid"/> names and
    /// whose template starts <paramref name="questId"/> (mangos zero Item::HasQuest, Object/Item.h:393: <c>StartQuest == quest_id</c>).
    /// A dead player cannot use one (CanInteractWithQuestGiver, QuestHandler.cpp:898-902: the only check for a non-creature,
    /// non-object giver). The lookup walks the inventory once per request; it is never on a per-tick path.
    /// </summary>
    private static Item? QuestStartingItem(Player player, ObjectGuid guid, uint questId)
        => questId != 0 && guid.High == HighGuid.Item && player.IsAlive && player.Inventory.IsLoaded
            && player.Inventory.GetItemByGuid(guid) is { } item && item.Template.StartQuest == questId
            && !InventorySlots.IsBankPos(item.BagSlot, item.Slot)
            ? item : null;

    /// <summary>
    /// CMSG_QUESTGIVER_ACCEPT_QUEST (vmangos HandleQuestgiverAcceptQuestOpcode, QuestHandler.cpp:108-196). A
    /// refusal from CanTakeQuest sends SMSG_QUESTGIVER_QUEST_INVALID with the reason of the first failing
    /// check; a full log sends SMSG_QUESTLOG_FULL; a source item that cannot be given sends
    /// SMSG_QUESTGIVER_QUEST_FAILED. The gossip window closes in every case and the player's pending share offer is
    /// consumed (QuestHandler.cpp:131-189). The giver is a quest NPC or game object, or the player who shares the
    /// quest (<see cref="AcceptFromPlayer"/>). Returns true only after creating a journal entry and handing its
    /// delta to persistence.
    /// </summary>
    public bool AcceptQuest(Player player, ObjectGuid guid, uint questId)
    {
        if (Ready(player) is not { } state)
        {
            return false;
        }

        bool accepted = false;
        if (guid.IsPlayer)
        {
            accepted = AcceptFromPlayer(player, state, guid, questId);
        }
        else if (QuestStartingItem(player, guid, questId) is { } startItem && Quests.Get(questId) is { } fromItem)
        {
            accepted = AddQuestFrom(player, state, fromItem, sharedTimerEnd: null);
            if (accepted)
            {
                DestroyStartItemIfNotNeeded(player, startItem, fromItem);
            }
        }
        else if (InteractableNpc(player, guid, NpcFlags.QuestGiver) is { } npc && Quests.Get(questId) is { } quest
            && StartersOf(npc).Contains(questId))
        {
            accepted = AddQuestFrom(player, state, quest, sharedTimerEnd: null);
        }

        if (accepted && Quests.Get(questId) is { } acceptedQuest)
        {
            QuestAccepted?.Invoke(player, guid, acceptedQuest);
        }

        if (accepted && Quests.Get(questId) is { } taken && taken.HasFlag(QuestFlags.PartyAccept))
        {
            // The fan-out belongs to the accept handler alone (QuestHandler.cpp:166-191); AddQuest and the confirm handler (332-381) have none.
            OfferPartyAccept(player, taken);
        }

        ClearShareInfo(player);
        CloseGossip(player);
        if (accepted && Quests.Get(questId) is { Template.SrcSpell: not 0 } sourceSpell && Deps.SpellCaster is { } spellCaster)
        {
            // vmangos HandleQuestgiverAcceptQuestOpcode (QuestHandler.cpp:202-203): the source spell is cast on the player
            // after the quest is in the log and the gossip window is closed.
            spellCaster.CastOnSelf(player, sourceSpell.Template.SrcSpell);
        }

        if (accepted)
        {
            QuestAccepted?.Invoke(player, guid, questId);
        }

        return accepted;
    }

    /// <summary>
    /// The common part of every accept (vmangos CanTakeQuest(msg), CanAddQuest(msg), AddQuest, then
    /// the completion check, QuestHandler.cpp:131-196 and Player.cpp:12820-12934; the party fan-out is the caller's, see <see cref="AcceptQuest"/>). A refusal sends its message and returns
    /// false. <paramref name="sharedTimerEnd"/> is the sharer's timer end for a shared timed quest (Player.cpp:12855-12860).
    /// </summary>
    private bool AddQuestFrom(Player player, PlayerNpcState state, Quest quest, long? sharedTimerEnd)
    {
        if (RefuseTakeQuest(state, quest, []) is { } refusal)
        {
            if (refusal.Message is { } reason)
            {
                Send(player, WorldOpcode.SmsgQuestgiverQuestInvalid, QuestPackets.QuestInvalid(reason));
            }

            return false;
        }

        if (!((state.Quests.Get(quest.Id)?.Rewarded != true || quest.IsRepeatable) && AcceptableQuest(quest)))
        {
            return false;
        }

        int slot = state.Quests.FindSlot(0);
        if (slot == QuestConstants.MaxQuestLogSize)
        {
            player.Session.Send(WorldOpcode.SmsgQuestlogFull, []);
            return false;
        }

        if (!CanReceiveSourceItem(player, quest))
        {
            return false;
        }

        AddQuest(player, state, quest, slot, sharedTimerEnd);
        return true;
    }

    /// <summary>
    /// vmangos Player::AddQuest (Player.cpp:12820-12934) after CanAddQuest proved <paramref name="slot"/> free and the source item
    /// storable: the status row, the timer, the log slot, the PvP flag of a PvP quest, the source item, the delivery counters and
    /// the completion check; the delta goes to persistence.
    /// </summary>
    private void AddQuest(Player player, PlayerNpcState state, Quest quest, int slot, long? sharedTimerEnd)
    {
        QuestStatusData data = state.Quests.GetOrAdd(quest.Id);
        data.Status = QuestStatus.Incomplete;
        data.Explored = false;
        Array.Clear(data.CreatureOrGOCount);
        Array.Clear(data.ItemCount);
        data.TimerEndUnix = !quest.HasSpecialFlag(QuestSpecialFlags.Timed) ? 0
            : sharedTimerEnd is { } shared ? checked(UnixNow + Math.Max(0, shared - UnixNow))
            : checked(UnixNow + quest.Template.LimitTime);
        if (data.TimerEndUnix != 0)
        {
            state.Quests.AddTimed(quest.Id);
        }

        state.Quests.SetSlot(slot, quest.Id, (uint)Math.Clamp(data.TimerEndUnix, 0, uint.MaxValue));
        if (quest.Template.Type == QuestNeeds.PvpType)
        {
            // vmangos Player::AddQuest (Player.cpp:12866-12867): a PvP quest flags the player.
            MapCombat.UpdatePvp(player, true);
        }

        state.Quests.MarkChanged(quest.Id);
        GiveSourceItem(player, quest);
        AdjustRequiredItemCounts(state, quest, data);
        RefreshCompletion(state, quest, data, slot);
        Flush(state);
    }

    /// <summary>
    /// The tail of mangos zero Player::AddQuest for an item giver (Object/PlayerQuest.cpp:780-803, "remove start item if not
    /// need"): the quest-starting item is destroyed, whole stack, unless the quest requires it back (a ReqItemId) or gives it as
    /// its source item. The item is looked up again by GUID: the source item grant may have moved stacks around.
    /// </summary>
    private static void DestroyStartItemIfNotNeeded(Player player, Item startItem, Quest quest)
    {
        uint entry = startItem.Entry;
        if (quest.ReqItemId.Contains(entry) || quest.Template.SrcItemId == entry)
        {
            return;
        }

        if (player.Inventory.GetItemByGuid(startItem.Guid) is { } live)
        {
            player.Inventory.DestroyItem(live.BagSlot, live.Slot);
        }
    }

    /// <summary>
    /// CMSG_QUESTLOG_REMOVE_QUEST (vmangos RemoveQuestAtSlot, Player.cpp:13033-13066): the source item is taken
    /// back first (and the abandon refused if it cannot come off), quest-bound required items are destroyed,
    /// then the quest leaves the log.
    /// </summary>
    public bool AbandonQuest(Player player, byte slot)
    {
        if (Ready(player) is not { } state || slot >= QuestConstants.MaxQuestLogSize
            || Quests.Get(state.Quests.SlotQuestId(slot)) is not { } quest || !AcceptableQuest(quest)
            || state.Quests.Get(quest.Id) is not { } data)
        {
            return false;
        }

        if (!TakeOrReplaceQuestStartItems(player, quest))
        {
            return false;
        }

        DestroyQuestBoundRequiredItems(player, quest);
        data.Status = QuestStatus.None;
        data.TimerEndUnix = 0;
        state.Quests.RemoveTimed(quest.Id);
        state.Quests.SetSlot(slot, 0);
        state.Quests.MarkChanged(quest.Id);
        Flush(state);
        return true;
    }

    /// <summary>CMSG_QUESTGIVER_CANCEL (vmangos HandleQuestgiverCancel, QuestHandler.cpp:314-317): CloseGossip.</summary>
    public void QuestgiverCancel(Player player)
    {
        if (Ready(player) is not null)
        {
            CloseGossip(player);
        }
    }

    /// <summary>
    /// CMSG_QUESTLOG_SWAP_QUEST (vmangos HandleQuestLogSwapQuest, QuestHandler.cpp:319-325): equal or
    /// out-of-range slots are ignored; otherwise the three slot fields trade places.
    /// </summary>
    public void SwapQuestSlots(Player player, byte slot1, byte slot2)
    {
        if (Ready(player) is not { } state || slot1 == slot2 || slot1 >= QuestConstants.MaxQuestLogSize || slot2 >= QuestConstants.MaxQuestLogSize)
        {
            return;
        }

        state.Quests.SwapSlots(slot1, slot2);
    }

    // ---- GM tooling (.quest add|remove|complete, World/Gm/Quest/QuestCommands.cs) ---------------------------------------
    // mangos zero ChatCommands/QuestCommands.cpp:47-283. World thread; every entry point hands its delta to persistence
    // before returning, so the command sees the same retained-write path as the opcode handlers.

    /// <summary>
    /// <c>.quest add</c> (HandleQuestAddCommand, QuestCommands.cpp:47-102 minus the item-started check, which the command makes
    /// against the item store): CanAddQuest (a free slot, the source item storable) then AddQuest and the completion check.
    /// ArcaneCore adds two refusals mangos lacks: a quest already in the log (mangos gives it a second slot) and a quest the
    /// support gate withholds (it would half-work). Neither CanTakeQuest nor the rewarded flag is consulted, as in mangos: a
    /// GM may add a quest the player does not qualify for; an already rewarded non-repeatable quest stays un-rewardable until
    /// <see cref="GmRemoveQuest"/> resets its flag.
    /// </summary>
    public GmQuestAddResult GmAddQuest(Player player, Quest quest)
    {
        ArgumentNullException.ThrowIfNull(quest);
        if (Ready(player) is not { } state)
        {
            return GmQuestAddResult.NotReady;
        }

        if (state.Quests.FindSlot(quest.Id) < QuestConstants.MaxQuestLogSize)
        {
            return GmQuestAddResult.AlreadyInLog;
        }

        if (!AcceptableQuest(quest))
        {
            return GmQuestAddResult.Unsupported;
        }

        int slot = state.Quests.FindSlot(0);
        if (slot == QuestConstants.MaxQuestLogSize)
        {
            player.Session.Send(WorldOpcode.SmsgQuestlogFull, []);
            return GmQuestAddResult.LogFull;
        }

        if (!CanReceiveSourceItem(player, quest))
        {
            return GmQuestAddResult.SourceItemRefused;
        }

        AddQuest(player, state, quest, slot, sharedTimerEnd: null);
        return GmQuestAddResult.Added;
    }

    /// <summary>
    /// <c>.quest remove</c> (HandleQuestRemoveCommand, QuestCommands.cpp:110-156): every log slot holding the quest is cleared
    /// and the source item taken back silently (an un-equippable one is left, as there), then the status becomes NONE and the
    /// rewarded flag is reset so a repeatable (or any) quest can be done again. A player without a status row for the quest is
    /// left alone (mangos would write a NONE row). False only when the journal is not ready.
    /// </summary>
    public bool GmRemoveQuest(Player player, Quest quest)
    {
        ArgumentNullException.ThrowIfNull(quest);
        if (Ready(player) is not { } state)
        {
            return false;
        }

        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            if (state.Quests.SlotQuestId(slot) == quest.Id)
            {
                state.Quests.SetSlot(slot, 0);
                TakeQuestSourceItem(player, quest, msg: false);
            }
        }

        if (state.Quests.Get(quest.Id) is { } data)
        {
            data.Status = QuestStatus.None;
            data.Rewarded = false;
            data.TimerEndUnix = 0;
            state.Quests.RemoveTimed(quest.Id);
            state.Quests.MarkChanged(quest.Id);
            Flush(state);
        }

        return true;
    }

    /// <summary>
    /// mangos zero Player::TakeQuestSourceItem (Object/PlayerQuest.cpp:1637-1667): SrcItemCount (at least one) of the source
    /// item is destroyed, bank included, unless one cannot be unequipped right now (then false, and the equip error when
    /// <paramref name="msg"/>). Nothing to do for a quest without a source item.
    /// </summary>
    private static bool TakeQuestSourceItem(Player player, Quest quest, bool msg)
    {
        uint entry = quest.Template.SrcItemId;
        if (entry == 0 || !player.Inventory.IsLoaded)
        {
            return true;
        }

        uint count = SourceItemCount(quest);
        ItemInventoryResult unequip = player.Inventory.CanUnequipItems(entry, count);
        if (unequip != ItemInventoryResult.Ok)
        {
            if (msg)
            {
                player.Inventory.SendEquipError(unequip, null, null, entry: entry);
            }

            return false;
        }

        player.Inventory.DestroyItemCount(entry, count, includeBank: true);
        return true;
    }

    /// <summary>
    /// <c>.quest complete</c> (HandleQuestCompleteCommand, QuestCommands.cpp:166-283) for a quest the player has a status for:
    /// the missing delivery items are stored and announced (silently skipped when they do not fit), every kill, cast and
    /// game-object objective is credited to its count (<paramref name="creatureExists"/> plays ObjectMgr::GetCreatureTemplate:
    /// a kill objective naming an unknown creature is skipped, as there; null means every creature exists), the required
    /// money is given, then the quest is forced COMPLETE with its log slot marked. The reputation objective is the caller's
    /// (the reputation owner is a World feature). A failed quest is refused instead of being forced: ArcaneCore's turn-in
    /// re-checks the objectives, so a forced status alone would leave a quest that can never be rewarded.
    /// </summary>
    /// <remarks>
    /// Exploration/event quests get their explored flag here, which mangos does not need: its forced status bypasses
    /// CanCompleteQuest, whereas ArcaneCore's reward path (<see cref="RewardBase"/>) checks every objective again.
    /// </remarks>
    public GmQuestCompleteResult GmCompleteQuest(Player player, Quest quest, Func<uint, bool>? creatureExists = null)
    {
        ArgumentNullException.ThrowIfNull(quest);
        if (Ready(player) is not { } state)
        {
            return GmQuestCompleteResult.NotReady;
        }

        if (state.Quests.Get(quest.Id) is not { } data || data.Status == QuestStatus.None)
        {
            return GmQuestCompleteResult.NotOnQuest;
        }

        if (data.Status == QuestStatus.Failed)
        {
            return GmQuestCompleteResult.Failed;
        }

        // Add quest items for quests that require items (QuestCommands.cpp:186-205).
        if (player.Inventory.IsLoaded)
        {
            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                uint id = quest.ReqItemId[i];
                uint count = quest.ReqItemCount[i];
                if (id == 0 || count == 0)
                {
                    continue;
                }

                uint owned = player.Inventory.GetItemCount(id, inBankAlso: true);
                if (owned < count)
                {
                    player.Inventory.AddItem(id, count - owned, out _, received: true, created: false, showInChat: true);
                }
            }
        }

        // All creature/GO slain/casted (QuestCommands.cpp:207-240): "not required, but otherwise it will display 'Creature slain 0/10'".
        for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
        {
            int target = quest.ReqCreatureOrGOId[i];
            uint count = quest.ReqCreatureOrGOCount[i];
            uint spellId = quest.ReqSpell[i];
            if (target == 0 || count == 0)
            {
                continue;
            }

            uint entry = (uint)Math.Abs((long)target);
            if (spellId == 0 && target > 0 && creatureExists?.Invoke(entry) == false)
            {
                continue;
            }

            for (uint z = 0; z < count; z++)
            {
                CreditCreature(state, entry, ObjectGuid.Empty, isCreature: target > 0, spellId, talking: false);
            }
        }

        // If the quest requires money (QuestCommands.cpp:261-266).
        if (quest.Template.RewOrReqMoney < 0)
        {
            ModifyMoney(state, -(long)quest.Template.RewOrReqMoney);
        }

        // CompleteQuest(entry, QUEST_STATUS_FORCE_COMPLETE) (QuestCommands.cpp:282; Player::CompleteQuest, PlayerQuest.cpp:855-875).
        AdjustRequiredItemCounts(state, quest, data);
        if (quest.HasSpecialFlag(QuestSpecialFlags.ExplorationOrEvent))
        {
            data.Explored = true;
        }

        data.Status = QuestStatus.Complete;
        int slot = state.Quests.FindSlot(quest.Id);
        if (slot < QuestConstants.MaxQuestLogSize)
        {
            state.Quests.SetSlotState(slot, QuestConstants.SlotStateComplete);
        }

        state.Quests.MarkChanged(quest.Id);
        Flush(state);
        return GmQuestCompleteResult.Completed;
    }
}
