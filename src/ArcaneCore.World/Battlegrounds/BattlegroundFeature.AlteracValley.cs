using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;
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
/// → BattleGroundAV::HandleQuestComplete, then the quest giver's pQuestRewardedNPC script), the Alterac Valley collectors' gossip (the
/// ScriptDev menus of Murgot Deepforge, Regzar and the assault collectors) and the battleground's spell cast check (Spell::CheckCast →
/// BattleGround::CheckSpellCast).
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
        if (BattlegroundOf(player.Guid) is AlteracValley av && player.Map?.FindObject(questGiver) is Game.Creatures.Creature giver
            && av.PlayerTeam(player.Guid) is { } team)
        {
            bool worldBoss = av.HandleQuestComplete(player.Guid, questGiver, quest.Id, quest.ReqItemId[0], quest.ReqItemCount[0]);
            AvCollectorGossip.QuestRewarded(giver, team, quest.Id, quest.ReqItemId[0], quest.ReqItemCount[0], worldBoss);
        }
    }

    /// <summary>
    /// The gossip of the Alterac Valley scripts, for a participant of the match: Murgot Deepforge's and Regzar's upgrade menu
    /// (<see cref="AlteracValley.QuartermasterMenu"/>) and the other collectors' assault menus (<see cref="AvCollectorGossip"/>: the beacon or the
    /// assault orders a launch hands out are given here, as vmangos StoreNewItem + SendNewItem does).
    /// </summary>
    private sealed class AvQuartermasterGossip(BattlegroundFeature feature) : INpcGossipScript
    {
        public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
        {
            if (feature.BattlegroundOf(player.Guid) is not AlteracValley av)
            {
                return null;
            }

            if (npc.Entry is AlteracValley.NpcMurgot or AlteracValley.NpcRegzar)
            {
                if (av.QuartermasterMenu(npc.Entry, player.Guid, faction => RankOf(player, faction)) is not { } menu)
                {
                    return null;
                }

                ScriptedGossipItem[] items = [.. menu.Items.Select(i => new ScriptedGossipItem(0, TextOf(player, i.BroadcastTextId), 1, i.Action))];
                return new ScriptedGossipMenu(ShowQuests: true, menu.NpcTextId, items);
            }

            if (!AvCollectorGossip.IsCollector(npc.Entry) || player.Map?.FindObject(npc.Guid) is not Game.Creatures.Creature creature
                || av.PlayerTeam(player.Guid) is not { } team)
            {
                return null;
            }

            QuestStore? quests = feature.Services.GetService<QuestNpcFeature>()?.Services.Quests;
            IEnumerable<(uint, uint)> started = quests is null
                ? []
                : quests.StartersOf(npc.Entry).Select(id => (id, quests.Get(id)?.ReqItemId[0] ?? 0u));
            AvCollectorHello hello = AvCollectorGossip.Hello(av, creature, player, team, started, faction => RankOf(player, faction));
            if (hello.Default)
            {
                return null;
            }

            if (hello.Silent)
            {
                return ScriptedGossipMenu.Nothing;
            }

            ScriptedGossipItem[] lines = [.. hello.Items.Select(i => new ScriptedGossipItem(i.Icon, TextOf(player, i.BroadcastTextId), 1, i.Action))];
            return new ScriptedGossipMenu(hello.ShowQuests, hello.NpcTextId, lines);
        }

        public uint Select(Player player, NpcInfo npc, uint sender, uint action) => SelectReply(player, npc, sender, action).NpcTextId;

        public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
        {
            if (feature.BattlegroundOf(player.Guid) is not AlteracValley av)
            {
                return default;
            }

            if (npc.Entry is AlteracValley.NpcMurgot or AlteracValley.NpcRegzar)
            {
                return new ScriptedGossipReply(av.QuartermasterSelect(npc.Guid, player.Guid, action));
            }

            if (!AvCollectorGossip.IsCollector(npc.Entry) || player.Map?.FindObject(npc.Guid) is not Game.Creatures.Creature creature
                || av.PlayerTeam(player.Guid) is not { } team)
            {
                return default;
            }

            AvCollectorChoice choice = AvCollectorGossip.Select(av, creature, team, action,
                item => player.Inventory.GetItemCount(item, inBankAlso: true) >= 1);
            if (choice.GiveItem != 0)
            {
                player.Inventory.AddItem(choice.GiveItem, 1, out _, received: true); // no room: nothing, as CanStoreNewItem fails in vmangos
            }

            return new ScriptedGossipReply(choice.NpcTextId, choice.Close, choice.Vendor);
        }

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
