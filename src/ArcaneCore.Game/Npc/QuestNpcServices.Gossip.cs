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
    /// A gossip option owned by another area was selected (auctioneer, petitioner, tabard
    /// designer, stable master, battlemaster, spirit guide). The owner opens its window.
    /// </summary>
    public event Action<Player, NpcInfo, GossipOption>? ForeignOptionSelected;

    /// <summary>
    /// Whether the "unlearn talents" option (GOSSIP_OPTION_UNLEARNTALENTS, 16) is offered to a player by a creature
    /// (vmangos Creature::CanTrainAndResetTalentsOf, see <c>Talents.TalentTrainerRules</c>). The talents area supplies it;
    /// while unset the option stays hidden (fail closed), and selecting it raises <see cref="ForeignOptionSelected"/>.
    /// </summary>
    public Func<Player, NpcInfo, bool>? UnlearnTalentsOffered { get; set; }

    /// <summary>
    /// The time to a spirit guide's next resurrection wave, in milliseconds (vmangos Creature::SendAreaSpiritHealerQueryOpcode
    /// reads its current channel's cast time). The battleground area spirit-healer channel does not exist yet, so by default
    /// no wave is running and the answer is 0; the battleground lane supplies the real time.
    /// </summary>
    public Func<ObjectGuid, uint>? SpiritGuideNextResurrectMs { get; set; }

    /// <summary>
    /// The scripts that own some creatures' gossip (vmangos ScriptDev pGossipHello / pGossipSelect): asked first at a hello, and for the
    /// lines they added at a selection. Unset: every creature uses its database menu.
    /// </summary>
    public INpcGossipScript? GossipScript { get; set; }

    /// <summary>A quest was rewarded by a quest giver (vmangos Player::RewardQuest: the battleground and the giver's OnQuestRewarded script).</summary>
    public event Action<Player, ObjectGuid, Quest>? QuestRewarded;

    /// <summary>
    /// CMSG_GOSSIP_HELLO (vmangos HandleGossipHelloOpcode, NPCHandler.cpp:345-368, no script hooks): a spirit guide first
    /// sends its resurrection timer, SMSG_AREA_SPIRIT_HEALER_TIME (:360-361), then the gossip menu goes out.
    /// </summary>
    public void GossipHello(Player player, ObjectGuid guid)
    {
        // CMSG_GOSSIP_HELLO and CMSG_QUESTGIVER_HELLO are creature requests (GetNPCIfCanInteractWith); a game
        // object opens its menu through CMSG_GAMEOBJ_USE (OpenGameObjectQuestMenu).
        if (Ready(player) is not { } s || InteractableNpc(player, guid, NpcFlags.None) is not { IsGameObject: false } npc)
        {
            LogMissing("GossipHello", guid);
            return;
        }

        if ((npc.NpcFlags & NpcFlags.SpiritGuide) != 0)
        {
            player.Session.Send(WorldOpcode.SmsgAreaSpiritHealerTime,
                Battlegrounds.BattlegroundPackets.BuildAreaSpiritHealerTime(npc.Guid, SpiritGuideNextResurrectMs?.Invoke(npc.Guid) ?? 0));
        }

        if (GossipScript?.Hello(player, npc) is { } scripted)
        {
            if (!scripted.Silent)
            {
                SendScriptedGossip(s, npc, scripted);
            }

            Flush(s);
            return;
        }

        PrepareGossipMenu(s, npc, npc.GossipMenuId);
        SendPreparedGossip(s, npc);
        Flush(s);
    }

    /// <summary>
    /// A script's menu (vmangos PrepareQuestMenu, ADD_GOSSIP_ITEM and SEND_GOSSIP_MENU in a pGossipHello): the creature's quests when asked,
    /// the script's lines, and its npc text (the creature's own text when 0).
    /// </summary>
    private void SendScriptedGossip(PlayerNpcState s, NpcInfo npc, ScriptedGossipMenu scripted)
    {
        PlayerMenu menu = s.Menu;
        menu.ClearMenus();
        if (scripted.ShowQuests)
        {
            PrepareQuestMenu(s, npc);
        }

        foreach (ScriptedGossipItem line in scripted.Items)
        {
            menu.AddGossipItem(new GossipMenuItem(line.Icon, line.Text, false, GossipOption.Gossip, string.Empty, 0, 0)
            {
                Scripted = true,
                ScriptSender = line.Sender,
                ScriptAction = line.Action,
            });
        }

        SendGossipMenu(s, npc.Guid, scripted.NpcTextId != 0 ? scripted.NpcTextId : GossipTextId(npc));
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

            // A game object has no NPC flags: only plain gossip lines show, and only on quest givers and goobers
            // (Player.cpp:12067-12086); everything else, the quest-giver option included, adds no line.
            if (!npc.IsGameObject && (option.NpcOptionNpcFlag & (uint)npc.NpcFlags) == 0)
            {
                continue;
            }

            bool hasMenuItem = npc.IsGameObject ? (GossipOption)option.OptionId == GossipOption.Gossip : (GossipOption)option.OptionId switch
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
                GossipOption.UnlearnTalents => UnlearnTalentsOffered?.Invoke(p, npc) ?? false,

                // Battlemasters and pet-skill resets need owners outside this area (talents: UnlearnTalentsOffered);
                // without them the option is hidden (fail closed). Unknown ids are hidden as in vmangos.
                _ => false,
            };

            // Only the creature branch learns a node (Player.cpp:12044-12047); a game object's taxi row is hidden and inert.
            if (!npc.IsGameObject && (GossipOption)option.OptionId == GossipOption.TaxiVendor && LearnNewTaxiNode(s, npc))
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

    /// <summary>vmangos Player::SendPreparedGossip (Player.cpp:12134-12177).</summary>
    internal void SendPreparedGossip(PlayerNpcState s, NpcInfo npc)
    {
        PlayerMenu menu = s.Menu;
        if (npc.IsGameObject)
        {
            // "probably need to find a better way here": a menu-less game object with quests opens the quest menu.
            if (menu.MenuId == 0 && menu.QuestItems.Count > 0)
            {
                SendPreparedQuest(s, npc);
                return;
            }
        }
        else
        {
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
        }

        uint textId = menu.MenuId != 0 ? GossipTextId(s, menu.MenuId, npc) : GossipTextId(npc);

        // Gameobjects should not greet players.
        if (npc.IsGameObject && menu.QuestItems.Count == 0 && menu.GossipItems.Count == 0 && textId == DefaultGossipMessage)
        {
            return;
        }

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

        // A game object only offers plain gossip and the quest list (Player.cpp:12185-12192).
        if (npc.IsGameObject && item.OptionId > GossipOption.QuestGiver)
        {
            return;
        }

        if (item.Scripted)
        {
            // The script may close the menu, open the vendor list, and answer with an npc text shown over the same lines (SEND_GOSSIP_MENU).
            if (GossipScript?.SelectReply(p, npc, item.ScriptSender, item.ScriptAction) is { } reply)
            {
                if (reply.Close)
                {
                    CloseGossip(p);
                }

                if (reply.Vendor)
                {
                    SendListInventory(s, npc.Guid);
                }

                if (reply.Trainer)
                {
                    SendTrainerList(s, npc.Guid);
                }

                if (reply.NpcTextId != 0)
                {
                    SendGossipMenu(s, npc.Guid, reply.NpcTextId);
                }
            }

            return;
        }

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
            case GossipOption.UnlearnTalents: // vmangos Player.cpp:12242-12245: CloseGossip, then the owner sends the wipe confirmation
                CloseGossip(p);
                ForeignOptionSelected?.Invoke(p, npc, item.OptionId);
                break;
            case GossipOption.SpiritHealer:
                SendSpiritHealerConfirm(p, npc);
                break;
            case GossipOption.Banker:
                ShowBank(s, npc);
                break;
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
    private uint GossipTextId(NpcInfo npc)
        => !npc.IsGameObject && Npcs.NpcGossipText(npc.SpawnId) is var id and not 0 ? id : DefaultGossipMessage;

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
