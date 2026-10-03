using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>Gossip menus (vmangos Player::PrepareGossipMenu, SendPreparedGossip, OnGossipSelect; NPCHandler.cpp).</summary>
public sealed partial class QuestNpcServices
{
    /// <summary>vmangos mangos_string LANG_GM_ON, appended to options a GM sees despite a failed condition.</summary>
    private const string GmOnSuffix = " (ON)";

    /// <summary>
    /// A gossip option owned by another area was selected (banker, auctioneer, petitioner, tabard
    /// designer, stable master, battlemaster, spirit healer/guide). The owner opens its window.
    /// </summary>
    public event Action<Player, NpcInfo, GossipOption>? ForeignOptionSelected;

    /// <summary>CMSG_GOSSIP_HELLO (vmangos HandleGossipHelloOpcode, no script hooks).</summary>
    public void GossipHello(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } s || InteractableNpc(player, guid, NpcFlags.None) is not { } npc)
        {
            LogMissing("GossipHello", guid);
            return;
        }

        PrepareGossipMenu(s, npc, npc.GossipMenuId);
        SendPreparedGossip(s, npc);
        Flush(s);
    }

    /// <summary>
    /// Whether gossip line <paramref name="listId"/> of the current menu is coded: the client then
    /// appends a CString code to CMSG_GOSSIP_SELECT_OPTION (vmangos PlayerMenu::GossipOptionCoded).
    /// </summary>
    public bool IsGossipOptionCoded(Player player, uint listId)
        => StateOf(player) is { } s && listId < s.Menu.GossipItems.Count && s.Menu.GossipItems[(int)listId].Coded;

    /// <summary>
    /// CMSG_GOSSIP_SELECT_OPTION (vmangos HandleGossipSelectOptionOpcode → Player::OnGossipSelect):
    /// a coded option needs a non-empty code; the creature must be interactable.
    /// </summary>
    public void GossipSelectOption(Player player, ObjectGuid guid, uint listId, string? code)
    {
        if (Ready(player) is not { } s)
        {
            return;
        }

        if (IsGossipOptionCoded(player, listId) && string.IsNullOrEmpty(code))
        {
            return;
        }

        if (InteractableNpc(player, guid, NpcFlags.None) is not { } npc)
        {
            LogMissing("GossipSelectOption", guid);
            return;
        }

        OnGossipSelect(s, npc, listId);
        Flush(s);
    }

    /// <summary>
    /// CMSG_NPC_TEXT_QUERY → SMSG_NPC_TEXT_UPDATE (vmangos HandleNpcTextQueryOpcode/SendNpcTextUpdate):
    /// unknown ids answer with the default greeting. Content is immutable, so any thread.
    /// </summary>
    public PacketWriter NpcTextQueryResponse(uint textId) => NpcPackets.NpcTextUpdate(textId, Npcs.Text(textId));

    /// <summary>vmangos Player::PrepareGossipMenu (creature source).</summary>
    internal void PrepareGossipMenu(PlayerNpcState s, NpcInfo npc, uint menuId)
    {
        Player p = s.Quests.Player;
        PlayerMenu menu = s.Menu;
        menu.ClearMenus();
        menu.MenuId = menuId;

        bool defaultMenu = menuId == npc.GossipMenuId;
        bool canSeeQuests = defaultMenu && (npc.NpcFlags & NpcFlags.QuestGiver) != 0;
        IReadOnlyList<GossipMenuOption> options = Npcs.MenuOptions(menuId);
        if (options.Count == 0 && defaultMenu)
        {
            options = Npcs.MenuOptions(0);
        }

        foreach (GossipMenuOption option in options)
        {
            bool gmSkipCondition = false;
            if (option.ConditionId != 0 && !(Deps.Conditions?.IsSatisfied(option.ConditionId, p, npc) ?? false))
            {
                if (p.IsGameMaster)
                {
                    gmSkipCondition = true;
                }
                else
                {
                    if ((GossipOption)option.OptionId == GossipOption.QuestGiver)
                    {
                        canSeeQuests = false;
                    }

                    continue;
                }
            }

            if ((option.NpcOptionNpcFlag & (uint)npc.NpcFlags) == 0)
            {
                continue;
            }

            bool hasMenuItem = (GossipOption)option.OptionId switch
            {
                GossipOption.Gossip => true,
                GossipOption.QuestGiver or GossipOption.Armorer => false,
                GossipOption.SpiritHealer => !p.IsAlive,
                GossipOption.Vendor => Npcs.VendorItems(npc.Entry).Count > 0,
                GossipOption.Trainer => IsTrainerOf(s, npc, false),
                GossipOption.TaxiVendor => true,
                GossipOption.StablePet => p.Class == Class.Hunter,
                GossipOption.SpiritGuide or GossipOption.Innkeeper or GossipOption.Banker or GossipOption.Petitioner
                    or GossipOption.TabardDesigner or GossipOption.Auctioneer => true,

                // Battlemasters, talent and pet-skill resets need owners outside this area;
                // without them the option is hidden (fail closed). Unknown ids are hidden as in vmangos.
                _ => false,
            };

            if ((GossipOption)option.OptionId == GossipOption.TaxiVendor && LearnNewTaxiNode(s, npc))
            {
                menu.DiscoveredNode = true;
            }

            if (hasMenuItem)
            {
                string text = gmSkipCondition ? option.OptionText + GmOnSuffix : option.OptionText;
                menu.AddGossipItem(new GossipMenuItem(option.OptionIcon, text, option.BoxCoded != 0, (GossipOption)option.OptionId,
                    option.BoxText, option.ActionMenuId, option.ActionPoiId));
            }
        }

        if (canSeeQuests)
        {
            PrepareQuestMenu(s, npc);
        }
    }

    /// <summary>vmangos Player::SendPreparedGossip (creature source).</summary>
    internal void SendPreparedGossip(PlayerNpcState s, NpcInfo npc)
    {
        PlayerMenu menu = s.Menu;
        if (menu.DiscoveredNode && menu.QuestItems.Count == 0)
        {
            return;
        }

        // No gossip flag but quests: open the quest menu (vendors with quests keep the gossip window).
        if ((npc.NpcFlags & NpcFlags.Gossip) == 0 && menu.QuestItems.Count > 0 && (npc.NpcFlags & NpcFlags.Vendor) == 0)
        {
            SendPreparedQuest(s, npc);
            return;
        }

        uint textId = menu.MenuId != 0 ? GossipTextId(s, menu.MenuId, npc) : GossipTextId(npc);
        SendGossipMenu(s, npc.Guid, textId);
    }

    /// <summary>vmangos Player::OnGossipSelect (creature source).</summary>
    internal void OnGossipSelect(PlayerNpcState s, NpcInfo npc, uint listId)
    {
        PlayerMenu menu = s.Menu;
        if (listId >= menu.GossipItems.Count)
        {
            return;
        }

        Player p = s.Quests.Player;
        GossipMenuItem item = menu.GossipItems[(int)listId];
        switch (item.OptionId)
        {
            case GossipOption.Gossip:
                if (item.ActionPoi != 0 && Npcs.Poi(item.ActionPoi) is { } poi)
                {
                    Send(p, WorldOpcode.SmsgGossipPoi, NpcPackets.GossipPoi(poi));
                }

                if (item.ActionMenu > 0)
                {
                    PrepareGossipMenu(s, npc, (uint)item.ActionMenu);
                    SendPreparedGossip(s, npc);
                }
                else if (item.ActionMenu < 0)
                {
                    CloseGossip(p);
                    TalkedToCreature(s, npc.Entry, npc.Guid);
                }

                break;
            case GossipOption.QuestGiver:
                PrepareQuestMenu(s, npc);
                SendPreparedQuest(s, npc);
                break;
            case GossipOption.Vendor:
            case GossipOption.Armorer:
                SendListInventory(s, npc.Guid);
                break;
            case GossipOption.Trainer:
                SendTrainerList(s, npc.Guid);
                break;
            case GossipOption.TaxiVendor:
                SendTaxiMenu(s, npc);
                break;
            case GossipOption.Innkeeper:
                // Build > 1.6.1: SMSG_BINDER_CONFIRM; the client answers with CMSG_BINDER_ACTIVATE.
                CloseGossip(p);
                Send(p, WorldOpcode.SmsgBinderConfirm, NpcPackets.Guid(npc.Guid));
                break;
            case GossipOption.SpiritGuide:
                PrepareGossipMenu(s, npc, npc.GossipMenuId);
                SendPreparedGossip(s, npc);
                ForeignOptionSelected?.Invoke(p, npc, item.OptionId);
                break;
            case GossipOption.Petitioner:
            case GossipOption.TabardDesigner:
                CloseGossip(p);
                ForeignOptionSelected?.Invoke(p, npc, item.OptionId);
                break;
            case GossipOption.SpiritHealer:
            case GossipOption.Banker:
            case GossipOption.Auctioneer:
            case GossipOption.StablePet:
            case GossipOption.Battlefield:
                ForeignOptionSelected?.Invoke(p, npc, item.OptionId);
                break;
        }
    }

    /// <summary>vmangos PlayerMenu::SendGossipMenu: the prepared options and quest list.</summary>
    private void SendGossipMenu(PlayerNpcState s, ObjectGuid npc, uint textId)
    {
        var quests = s.Menu.QuestItems
            .Select(i => (Quest: Quests.Get(i.QuestId), i.Icon))
            .Where(x => x.Quest is not null)
            .Select(x => (x.Quest!, x.Icon))
            .ToArray();
        Send(s.Quests.Player, WorldOpcode.SmsgGossipMessage, NpcPackets.GossipMessage(npc, textId, s.Menu.GossipItems, quests));
    }

    /// <summary>vmangos Player::GetGossipTextId(source): npc_gossip for the spawn, else DEFAULT_GOSSIP_MESSAGE.</summary>
    private uint GossipTextId(NpcInfo npc) => Npcs.NpcGossipText(npc.SpawnId) is var id and not 0 ? id : DefaultGossipMessage;

    /// <summary>
    /// vmangos Player::GetGossipTextId(menuId, source): of the menu's texts, the one with the highest
    /// satisfied condition id (an unconditioned text when none qualifies).
    /// </summary>
    private uint GossipTextId(PlayerNpcState s, uint menuId, NpcInfo npc)
    {
        uint textId = DefaultGossipMessage;
        if (menuId == 0)
        {
            return textId;
        }

        uint lastCondition = 0;
        foreach (GossipMenu entry in Npcs.MenuTexts(menuId))
        {
            if ((entry.ConditionId == 0 && lastCondition == 0)
                || (entry.ConditionId > lastCondition && (Deps.Conditions?.IsSatisfied(entry.ConditionId, s.Quests.Player, npc) ?? false)))
            {
                lastCondition = entry.ConditionId;
                textId = entry.TextId;
            }
        }

        return textId;
    }
}
