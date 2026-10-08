using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Crafting;

/// <summary>
/// Vanilla specialization learning at the NPCs whose ClassicDB ScriptName is npc_prof_blacksmith or npc_prof_leather.
/// The offers and teaching spells follow mangos-classic ScriptDev2 npc_professions.cpp
/// (GossipHello_npc_prof_blacksmith, SendActionMenu_npc_prof_blacksmith,
/// GossipHello_npc_prof_leather, SendActionMenu_npc_prof_leather). Selection rechecks the offer
/// so a stale menu cannot teach a mutually exclusive branch after the character changes.
/// </summary>
public sealed class ProfessionSpecializationGossip(
    SpellSystem spells,
    Func<Player, uint, uint> skillValue,
    Func<Player, uint, bool> rewardedQuest,
    Func<Player, uint, byte?> reputationRank,
    INpcGossipScript? next = null) : INpcGossipScript
{
    // ScriptDev2 sc_gossip.h and npc_professions.cpp: senders, actions and icons as the reference sends them.
    internal const uint SenderMain = 1;      // GOSSIP_SENDER_MAIN
    internal const uint SenderLearn = 50;    // GOSSIP_SENDER_LEARN
    internal const uint SenderCheck = 52;    // GOSSIP_SENDER_CHECK
    internal const uint ActionTrade = 1;     // GOSSIP_ACTION_TRADE
    internal const uint ActionTrain = 2;     // GOSSIP_ACTION_TRAIN
    internal const uint ActionInfoDef = 1000; // GOSSIP_ACTION_INFO_DEF
    private const byte IconChat = 0;         // GOSSIP_ICON_CHAT
    private const byte IconVendor = 1;       // GOSSIP_ICON_VENDOR
    private const byte IconTrainer = 3;      // GOSSIP_ICON_TRAINER
    private const byte Friendly = 4;         // REP_FRIENDLY

    private static readonly uint[] LeatherQuestIds = [5141, 5145, 5144, 5146, 5143, 5148];

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(npc);
        if (!IsBlacksmith(npc.Entry) && !IsLeatherworker(npc.Entry))
        {
            return next?.Hello(player, npc);
        }

        var items = new List<ScriptedGossipItem>();
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0)
        {
            items.Add(new ScriptedGossipItem(IconVendor, "I'd like to browse your goods.", SenderMain, ActionTrade));
        }

        if (IsBlacksmith(npc.Entry))
        {
            AddBlacksmith(player, npc, items);
        }
        else
        {
            AddLeatherworker(player, npc, items);
        }

        return new ScriptedGossipMenu((npc.NpcFlags & NpcFlags.QuestGiver) != 0, 0, items);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action)
        => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(npc);
        if (!IsBlacksmith(npc.Entry) && !IsLeatherworker(npc.Entry))
        {
            return next?.SelectReply(player, npc, sender, action) ?? default;
        }

        // The client's old list id is not authority: prerequisites, quests and known spells can
        // change between hello and selection. ScriptDev2's actions must still be offered now.
        if (Hello(player, npc)?.Items.Any(item => item.Sender == sender && item.Action == action) != true)
        {
            return default;
        }

        if (action == ActionTrade)
        {
            return new ScriptedGossipReply(0, Vendor: true);
        }

        if (action == ActionTrain)
        {
            return new ScriptedGossipReply(0, Trainer: true);
        }

        // SendActionMenu_npc_prof_blacksmith / _leather. A weapon sub-discipline line (GOSSIP_SENDER_LEARN) first shows a
        // no-cost confirmation line in SendConfirmLearn_npc_prof_blacksmith; that menu-in-menu is not ported, the choice teaches.
        uint teacher = npc.Entry switch
        {
            11145 or 11176 when action == ActionInfoDef + 1 => 9790, // S_LEARN_ARMOR (S_ARMOR 9788)
            11145 or 11176 when action == ActionInfoDef + 2 => 9789, // S_LEARN_WEAPON (S_WEAPON 9787)
            11191 when action == ActionInfoDef + 5 => 17044, // S_LEARN_HAMMER (S_HAMMER 17040)
            11192 when action == ActionInfoDef + 6 => 17043, // S_LEARN_AXE (S_AXE 17041)
            11193 when action == ActionInfoDef + 7 => 17042, // S_LEARN_SWORD (S_SWORD 17039)
            7866 or 7867 when action == ActionInfoDef + 2 => 10657, // S_LEARN_DRAGON (S_DRAGON 10656)
            7868 or 7869 when action == ActionInfoDef + 4 => 10659, // S_LEARN_ELEMENTAL (S_ELEMENTAL 10658)
            7870 or 7871 when action == ActionInfoDef + 6 => 10661, // S_LEARN_TRIBAL (S_TRIBAL 10660)
            _ => 0,
        };
        if (teacher != 0 && spells.Store.Get(teacher) is not null)
        {
            spells.CastSpell(player, teacher, SpellCastTargets.ForSelf(), triggered: true);
        }

        return new ScriptedGossipReply(0, Close: true);
    }

    private void AddBlacksmith(Player player, NpcInfo npc, List<ScriptedGossipItem> items)
    {
        bool armor = Knows(player, 9788);
        bool weapon = Knows(player, 9787);
        if (skillValue(player, SkillIds.Blacksmithing) >= 225)
        {
            if (npc.Entry is 11145 or 11176 && !armor && !weapon)
            {
                // ScriptDev2 tests equality with FRIENDLY, not at least friendly.
                if (reputationRank(player, 46) == Friendly)
                    items.Add(new ScriptedGossipItem(IconChat, "Please teach me how to become an Armorsmith", SenderMain, ActionInfoDef + 1));
                if (reputationRank(player, 289) == Friendly)
                    items.Add(new ScriptedGossipItem(IconChat, "Please teach me how to become a Weaponsmith", SenderMain, ActionInfoDef + 2));
            }

            if (((npc.Entry is 11146 or 11178) && weapon) || ((npc.Entry is 5164 or 11177) && armor))
            {
                AddTrainer(npc, items);
            }
        }

        if (weapon && player.Level > 49 && skillValue(player, SkillIds.Blacksmithing) >= 250
            && !Knows(player, 17040) && !Knows(player, 17041) && !Knows(player, 17039))
        {
            switch (npc.Entry)
            {
                case 11191:
                    items.Add(new ScriptedGossipItem(IconChat, "Please teach me how to become a Hammersmith, Lilith", SenderLearn, ActionInfoDef + 5));
                    break;
                case 11192:
                    items.Add(new ScriptedGossipItem(IconChat, "Please teach me how to become an Axesmith, Kilram", SenderLearn, ActionInfoDef + 6));
                    break;
                case 11193:
                    items.Add(new ScriptedGossipItem(IconChat, "Please teach me how to become a Swordsmith, Seril", SenderLearn, ActionInfoDef + 7));
                    break;
            }
        }
    }

    private void AddLeatherworker(Player player, NpcInfo npc, List<ScriptedGossipItem> items)
    {
        if (player.Level <= 39 || skillValue(player, SkillIds.Leatherworking) < 225)
        {
            return;
        }

        uint known = npc.Entry switch
        {
            7866 or 7867 => 10656,
            7868 or 7869 => 10658,
            _ => 10660,
        };
        if (Knows(player, known))
        {
            AddTrainer(npc, items);
            return;
        }

        if (Knows(player, 10656) || Knows(player, 10658) || Knows(player, 10660)
            || !LeatherQuestIds.Any(quest => rewardedQuest(player, quest)))
        {
            return;
        }

        switch (known)
        {
            case 10656:
                items.Add(new ScriptedGossipItem(IconChat, "Please teach me how to become a Dragonscale leatherworker.", SenderCheck, ActionInfoDef + 2));
                break;
            case 10658:
                items.Add(new ScriptedGossipItem(IconChat, "Please teach me how to become an Elemental leatherworker.", SenderCheck, ActionInfoDef + 4));
                break;
            case 10660:
                items.Add(new ScriptedGossipItem(IconChat, "Please teach me how to become a Tribal leatherworker.", SenderCheck, ActionInfoDef + 6));
                break;
        }
    }

    private bool Knows(Player player, uint id) => spells.Spellbook?.HasSpell(player, id) == true;

    private static void AddTrainer(NpcInfo npc, List<ScriptedGossipItem> items)
    {
        if ((npc.NpcFlags & NpcFlags.Trainer) != 0)
            items.Add(new ScriptedGossipItem(IconTrainer, "Train me!", SenderMain, ActionTrain));
    }

    private static bool IsBlacksmith(uint entry) => entry is 11145 or 11176 or 11146 or 11178 or 5164 or 11177 or 11191 or 11192 or 11193;
    private static bool IsLeatherworker(uint entry) => entry is 7866 or 7867 or 7868 or 7869 or 7870 or 7871;
}
