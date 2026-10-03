using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>Trainers (vmangos SendTrainerList, HandleTrainerBuySpellOpcode, Creature::IsTrainerOf, Player::GetTrainerSpellState).</summary>
public sealed partial class QuestNpcServices
{
    /// <summary>TRAIN_FAIL_UNAVAILABLE / NOT_ENOUGH_MONEY / NOT_ENOUGH_SKILL (vmangos TrainingFailureReason).</summary>
    private const uint TrainFailUnavailable = 0;
    private const uint TrainFailNotEnoughMoney = 1;
    private const uint TrainFailNotEnoughSkill = 2;

    /// <summary>REP_EXALTED (vmangos ReputationRank).</summary>
    private const byte RepExalted = 7;

    /// <summary>CMSG_TRAINER_LIST (vmangos HandleTrainerListOpcode).</summary>
    public void TrainerList(Player player, ObjectGuid guid)
    {
        if (Ready(player) is { } s)
        {
            SendTrainerList(s, guid);
        }
    }

    /// <summary>
    /// CMSG_TRAINER_BUY_SPELL (vmangos HandleTrainerBuySpellOpcode): the spell must be on the
    /// trainer's list and GREEN, affordable after the reputation discount, and the teaching cast
    /// must succeed; only then is the money taken.
    /// </summary>
    public void BuyTrainerSpell(Player player, ObjectGuid guid, uint spellId)
    {
        if (Ready(player) is not { } s)
        {
            return;
        }

        if (InteractableNpc(player, guid, NpcFlags.Trainer) is not { } npc || !IsTrainerOf(s, npc, true) || Deps.Spells is not { } spells)
        {
            Send(player, WorldOpcode.SmsgTrainerBuyFailed, NpcPackets.TrainerBuyFailed(guid, spellId, TrainFailUnavailable));
            return;
        }

        TrainerSpell? trainerSpell = Npcs.TrainerSpells(npc.Entry).FirstOrDefault(t => t.Spell == spellId);
        TrainerSpellInfo? info = trainerSpell is null ? null : spells.DescribeTrainerSpell(trainerSpell.Spell);
        if (trainerSpell is null || info is null)
        {
            Send(player, WorldOpcode.SmsgTrainerBuyFailed, NpcPackets.TrainerBuyFailed(guid, spellId, TrainFailUnavailable));
            return;
        }

        if (GetTrainerSpellState(player, trainerSpell, info) != TrainerSpellState.Green)
        {
            Send(player, WorldOpcode.SmsgTrainerBuyFailed, NpcPackets.TrainerBuyFailed(guid, spellId, TrainFailNotEnoughSkill));
            return;
        }

        uint cost = Discounted(trainerSpell.SpellCost, PriceDiscount(player, npc));
        if (player.Money < cost)
        {
            Send(player, WorldOpcode.SmsgTrainerBuyFailed, NpcPackets.TrainerBuyFailed(guid, spellId, TrainFailNotEnoughMoney));
            return;
        }

        if (spells.CastTeachingSpell(player, npc.Guid, trainerSpell.Spell))
        {
            ModifyMoney(s, -(long)cost);
            Send(player, WorldOpcode.SmsgTrainerBuySucceeded, NpcPackets.TrainerBuySucceeded(guid, spellId));
        }
        else
        {
            Send(player, WorldOpcode.SmsgTrainerBuyFailed, NpcPackets.TrainerBuyFailed(guid, spellId, TrainFailUnavailable));
        }

        Flush(s);
    }

    /// <summary>vmangos WorldSession::SendTrainerList (npc_trainer rows; trainer templates are not modelled).</summary>
    internal void SendTrainerList(PlayerNpcState s, ObjectGuid guid)
    {
        Player player = s.Quests.Player;
        if (InteractableNpc(player, guid, NpcFlags.Trainer) is not { } npc)
        {
            LogMissing("SendTrainerList", guid);
            return;
        }

        if (!IsTrainerOf(s, npc, true) || Deps.Spells is not { } spells)
        {
            return;
        }

        float discount = PriceDiscount(player, npc);
        bool canLearnPrimary = spells.GetFreePrimaryProfessionPoints(player) > 0;
        var entries = new List<TrainerListEntry>();
        foreach (TrainerSpell trainerSpell in Npcs.TrainerSpells(npc.Entry))
        {
            if (spells.DescribeTrainerSpell(trainerSpell.Spell) is not { } info || !spells.IsSpellFitByClassAndRace(player, info.LearnedSpell))
            {
                continue;
            }

            // SendTrainerSpellHelper: level from npc_trainer.reqlevel, else the learned spell's level.
            uint level = trainerSpell.ReqLevel != 0 ? trainerSpell.ReqLevel : info.SpellLevel;

            // The client wants chain node 2 only when node 1 is set: req first when present.
            (uint node1, uint node2) = info.ChainReq != 0 ? (info.ChainReq, info.ChainPrev) : (info.ChainPrev, 0u);
            entries.Add(new TrainerListEntry(trainerSpell.Spell, GetTrainerSpellState(player, trainerSpell, info),
                Discounted(trainerSpell.SpellCost, discount), canLearnPrimary, info.LearnedIsPrimaryProfessionFirstRank,
                (byte)Math.Min(level, byte.MaxValue), trainerSpell.ReqSkill, trainerSpell.ReqSkillValue, node1, node2));
        }

        Send(player, WorldOpcode.SmsgTrainerList, NpcPackets.TrainerList(npc.Guid, npc.TrainerType, entries, NpcPackets.TrainerHello));
    }

    /// <summary>
    /// vmangos Creature::IsTrainerOf: a non-empty list and, by trainer type, the class (class),
    /// hunter (pets) or race unless exalted with the trainer's faction (mounts). With
    /// <paramref name="msg"/> a refusing trainer shows its class/race gossip text.
    /// </summary>
    internal bool IsTrainerOf(PlayerNpcState s, NpcInfo npc, bool msg)
    {
        if ((npc.NpcFlags & NpcFlags.Trainer) == 0 || Npcs.TrainerSpells(npc.Entry).Count == 0)
        {
            return false;
        }

        Player p = s.Quests.Player;
        uint refusal = 0;
        switch (npc.TrainerType)
        {
            case TrainerType.Class when (byte)p.Class != npc.TrainerClass:
                refusal = npc.TrainerClass switch
                {
                    (byte)Class.Druid => 4913,
                    (byte)Class.Hunter => 10090,
                    (byte)Class.Mage => 328,
                    (byte)Class.Paladin => 1635,
                    (byte)Class.Priest => 4436,
                    (byte)Class.Rogue => 4797,
                    (byte)Class.Shaman => 5003,
                    (byte)Class.Warlock => 5836,
                    (byte)Class.Warrior => 4985,
                    _ => 0,
                };
                break;
            case TrainerType.Pets when p.Class != Class.Hunter:
                refusal = 3620;
                break;
            case TrainerType.Mounts when npc.TrainerRace != 0 && (byte)p.Race != npc.TrainerRace:
                if (Deps.Reputation is { } rep && npc.FactionId != 0 && rep.GetReputationRank(p, npc.FactionId) == RepExalted)
                {
                    return true;
                }

                // vmangos switches on trainer_class here (a long-standing quirk), so the race text is
                // chosen from trainer_class values that equal race ids.
                refusal = npc.TrainerClass switch
                {
                    (byte)Race.Dwarf => 5865,
                    (byte)Race.Gnome => 4881,
                    (byte)Race.Human => 5861,
                    (byte)Race.NightElf => 5862,
                    (byte)Race.Orc => 5863,
                    (byte)Race.Tauren => 5864,
                    (byte)Race.Troll => 5816,
                    (byte)Race.Undead => 624,
                    _ => 0,
                };
                break;
            default:
                return true;
        }

        if (msg)
        {
            s.Menu.ClearMenus();
            if (refusal != 0)
            {
                SendGossipMenu(s, npc.Guid, refusal);
            }
        }

        return false;
    }

    /// <summary>vmangos Player::GetTrainerSpellState.</summary>
    private TrainerSpellState GetTrainerSpellState(Player p, TrainerSpell trainerSpell, TrainerSpellInfo info)
    {
        if (trainerSpell.Spell == 0 || Deps.Spells is not { } spells)
        {
            return TrainerSpellState.Red;
        }

        if (spells.HasSpell(p, info.LearnedSpell))
        {
            return TrainerSpellState.Gray;
        }

        if (!spells.IsSpellFitByClassAndRace(p, info.LearnedSpell))
        {
            return TrainerSpellState.Red;
        }

        uint spellLevel = trainerSpell.ReqLevel != 0 ? trainerSpell.ReqLevel : info.SpellLevel;
        if (p.Level < spellLevel)
        {
            return TrainerSpellState.Red;
        }

        if ((info.ChainPrev != 0 && !spells.HasSpell(p, info.ChainPrev)) || (info.ChainReq != 0 && !spells.HasSpell(p, info.ChainReq)))
        {
            return TrainerSpellState.Red;
        }

        if (trainerSpell.ReqSkill != 0 && spells.GetSkillValueBase(p, trainerSpell.ReqSkill) < trainerSpell.ReqSkillValue)
        {
            return TrainerSpellState.Red;
        }

        if (!info.IsPrimaryProfessionLearn)
        {
            return TrainerSpellState.Green;
        }

        return info.TeachesPrimaryProfessionFirstRank && spells.GetFreePrimaryProfessionPoints(p) == 0
            ? TrainerSpellState.GreenDisabled
            : TrainerSpellState.Green;
    }
}
