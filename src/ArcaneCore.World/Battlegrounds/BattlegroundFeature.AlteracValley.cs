using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// The world hooks of the battleground rules that are not tied to one match: the quests a participant is rewarded (vmangos Player::RewardQuest
/// → BattleGroundAV::HandleQuestComplete), the Alterac Valley quartermasters' gossip (the ScriptDev menu of Murgot Deepforge and Regzar) and
/// the battleground's spell cast check (Spell::CheckCast → BattleGround::CheckSpellCast).
/// </summary>
public sealed partial class BattlegroundFeature
{
    private void InstallMatchHooks()
    {
        if (services.GetService<QuestNpcFeature>()?.Services is { } npcs)
        {
            npcs.QuestRewarded += OnQuestRewarded;
            npcs.GossipScript ??= new AvQuartermasterGossip(this);
        }

        services.GetService<SpellFeature>()?.System.RegisterCastCheck(new BattlegroundCastCheck(this));
    }

    /// <summary>
    /// vmangos Player::RewardQuest (Player.cpp:13093-13095): a participant of an Alterac Valley match rewarded by a creature hands the quest to
    /// the match, with the quest's first required item and count for the collectors' counters.
    /// </summary>
    private void OnQuestRewarded(Player player, ObjectGuid questGiver, Quest quest)
    {
        if (BattlegroundOf(player.Guid) is AlteracValley av && player.Map?.FindObject(questGiver) is Game.Creatures.Creature)
        {
            av.HandleQuestComplete(player.Guid, questGiver, quest.Id, quest.ReqItemId[0], quest.ReqItemCount[0]);
        }
    }

    /// <summary>The gossip of the Alterac Valley quartermasters (<see cref="AlteracValley.QuartermasterMenu"/>), for a participant of the match.</summary>
    private sealed class AvQuartermasterGossip(BattlegroundFeature feature) : INpcGossipScript
    {
        public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
        {
            if (feature.BattlegroundOf(player.Guid) is not AlteracValley av
                || av.QuartermasterMenu(npc.Entry, player.Guid, faction => RankOf(player, faction)) is not { } menu)
            {
                return null;
            }

            ScriptedGossipItem[] items = [.. menu.Items.Select(i => new ScriptedGossipItem(0, TextOf(player, i.BroadcastTextId), 1, i.Action))];
            return new ScriptedGossipMenu(ShowQuests: true, menu.NpcTextId, items);
        }

        public uint Select(Player player, NpcInfo npc, uint sender, uint action)
            => feature.BattlegroundOf(player.Guid) is AlteracValley av && npc.Entry is AlteracValley.NpcMurgot or AlteracValley.NpcRegzar
                ? av.QuartermasterSelect(npc.Guid, player.Guid, action)
                : 0;

        private int RankOf(Player player, uint faction)
            => feature.Services.GetService<ReputationFeature>()?.Service is { } reputation ? (int)reputation.GetRank(player, faction) : AlteracValley.RankNeutral;

        /// <summary>vmangos GossipMenu::AddMenuItem(int32): a broadcast text, the female text for a female player when there is one.</summary>
        private string TextOf(Player player, uint id)
        {
            if (feature.Services.GetService<CreatureWorldFeature>()?.Content.Ai.BroadcastTexts.Find(id) is not { } text)
            {
                return $"[text {id}]";
            }

            return player.Gender == Gender.Female && text.FemaleText.Length > 0 ? text.FemaleText : text.Text;
        }
    }

    /// <summary>
    /// vmangos Spell::CheckCast (Spell.cpp:5705-5716): after the range check, a participant's cast that no aura triggered is put to its
    /// battleground (the phase before the power check, as the reference asks right before CheckPower).
    /// </summary>
    private sealed class BattlegroundCastCheck(BattlegroundFeature feature) : ISpellCastCheck
    {
        public SpellCheckPhase Phase => SpellCheckPhase.Power;

        public int Order => 0;

        public SpellCastResult Check(in SpellCastCheckContext context)
        {
            if (context.Caster is not Player player || feature.BattlegroundOf(player.Guid) is not { } bg)
            {
                return SpellCastResult.CastOk;
            }

            return bg.CheckSpellCast(player.Guid, context.Spell.Id) is { } refused ? (SpellCastResult)refused : SpellCastResult.CastOk;
        }
    }
}
