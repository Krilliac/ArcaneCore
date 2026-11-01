using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Blackrock Depths npc_rocknot (quest 4295 Rocknot's Ale) and npc_mistress_nagmara (quest 4201 Potion of Love), from mangos-classic
/// blackrock_depths.cpp QuestRewarded_npc_rocknot and QuestRewarded_npc_mistress_nagmara, through the real instance script and AIs.
/// </summary>
public sealed class BlackrockDepthsRocknotTests
{
    private static DungeonScriptHarness Harness()
        => new(m => new BlackrockDepthsInstance(m),
            [BlackrockDepthsInstance.NpcRocknot, BlackrockDepthsInstance.NpcNagmara, BlackrockDepthsInstance.NpcPhalanx],
            [BlackrockDepthsInstance.NpcNagmara, BlackrockDepthsInstance.NpcPhalanx, BlackrockDepthsInstance.NpcRocknot],
            null, null,
            [.. Enumerable.Range(1, 34).Select(i => (BlackrockDepthsInstance.NpcRocknot, EscortAI.EscortPathId,
                new CreatureWaypoint((uint)i, -16f + i * 0.1f, -383f, 61.78f, 0, 0)))
                .Concat(Enumerable.Range(1, 2).Select(i => (BlackrockDepthsInstance.NpcPhalanx, EscortAI.EscortPathId,
                    new CreatureWaypoint((uint)i, -12f + i * 0.1f, -383f, 61.78f, 0, 0))))],
            null,
            (BlackrockDepthsInstance.GoBarKegShot, GameObjectType.Goober));

    [Fact]
    public void RocknotsAle_ThreeTurnIns_CountThreeAlesAndStartTheWalkToTheBarrel()
    {
        using DungeonScriptHarness h = Harness();
        var instance = (BlackrockDepthsInstance)h.Data;
        Creature rocknot = h.Creature(BlackrockDepthsInstance.NpcRocknot);
        var ai = Assert.IsType<RocknotAI>(rocknot.AI);

        ai.OnQuestRewarded(h.Player, RocknotAI.QuestAle);
        Assert.Equal(EncounterState.InProgress, instance.GetData(BlackrockDepthsInstance.TypeRocknot));
        Assert.Equal(1, instance.BarAleCount);
        h.Tick(2000);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting)); // he only cheers

        ai.OnQuestRewarded(h.Player, 99_999); // another quest counts nothing
        Assert.Equal(1, instance.BarAleCount);
        ai.OnQuestRewarded(h.Player, RocknotAI.QuestAle);
        ai.OnQuestRewarded(h.Player, RocknotAI.QuestAle);
        Assert.Equal(3, instance.BarAleCount);
        Assert.Equal(EncounterState.Special, instance.GetData(BlackrockDepthsInstance.TypeRocknot));

        ai.OnQuestRewarded(h.Player, RocknotAI.QuestAle); // a fourth is ignored
        Assert.Equal(3, instance.BarAleCount);
        h.Tick(2000);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
    }

    [Fact]
    public void RocknotsAle_AfterTheBarEventIsDone_IsIgnored()
    {
        using DungeonScriptHarness h = Harness();
        var instance = (BlackrockDepthsInstance)h.Data;
        instance.SetData(BlackrockDepthsInstance.TypeRocknot, EncounterState.Done);
        var ai = Assert.IsType<RocknotAI>(h.Creature(BlackrockDepthsInstance.NpcRocknot).AI);
        ai.OnQuestRewarded(h.Player, RocknotAI.QuestAle);
        Assert.Equal(0, instance.BarAleCount);
        Assert.Equal(EncounterState.Done, instance.GetData(BlackrockDepthsInstance.TypeRocknot));
    }

    [Fact]
    public void PotionOfLove_Reward_SendsNagmaraToRocknotAndTakesTheirQuestFlags()
    {
        using DungeonScriptHarness h = Harness();
        Creature nagmara = h.Creature(BlackrockDepthsInstance.NpcNagmara);
        Creature rocknot = h.Creature(BlackrockDepthsInstance.NpcRocknot);
        nagmara.NpcFlags |= (uint)NpcFlags.Gossip | (uint)NpcFlags.QuestGiver;
        rocknot.NpcFlags |= (uint)NpcFlags.QuestGiver;
        var ai = Assert.IsType<NagmaraAI>(nagmara.AI);

        ai.OnQuestRewarded(h.Player, 99_999);
        Assert.Equal(0, ai.Phase);
        ai.OnQuestRewarded(h.Player, NagmaraAI.QuestPotionOfLove);

        Assert.Equal(1, ai.Phase);
        Assert.Equal(0u, nagmara.NpcFlags & ((uint)NpcFlags.Gossip | (uint)NpcFlags.QuestGiver));
        Assert.Equal(0u, rocknot.NpcFlags & (uint)NpcFlags.QuestGiver);
    }

    private sealed class RewardSeam(bool rewarded) : IInstanceQuestRewards
    {
        public bool? IsRewarded(Player player, uint questId) => rewarded && questId == NagmaraAI.QuestPotionOfLove;
        public void Update(Map map, uint diffMs) { }
        public void OnPlayerRemoved(Map map, Player player) { }
    }

    private static NpcInfo InfoOf(Creature c)
        => new(c.Guid, c.Template.Entry, c.Spawn?.Guid ?? 0, NpcFlags.Gossip | NpcFlags.QuestGiver, c.Map!.MapId,
            c.X, c.Y, c.Z, c.BoundingRadius, true, false, false, false, 0);

    [Fact]
    public void PotionOfLove_AfterTheRewardTheRepeatLineAppearsAndRestartsTheScene()
    {
        using DungeonScriptHarness h = Harness();
        h.Map.AddUpdater(new RewardSeam(true));
        Creature nagmara = h.Creature(BlackrockDepthsInstance.NpcNagmara);
        Creature rocknot = h.Creature(BlackrockDepthsInstance.NpcRocknot);
        nagmara.NpcFlags |= (uint)NpcFlags.Gossip | (uint)NpcFlags.QuestGiver;
        rocknot.NpcFlags |= (uint)NpcFlags.QuestGiver;
        var ai = Assert.IsType<NagmaraAI>(nagmara.AI);
        NpcInfo npc = InfoOf(nagmara);

        ScriptedGossipMenu menu = Assert.IsType<ScriptedGossipMenu>(ai.Hello(h.Player, npc));
        Assert.Equal(NagmaraAI.GossipNpcTextRepeat, menu.NpcTextId);
        Assert.True(menu.ShowQuests);
        ScriptedGossipItem item = Assert.Single(menu.Items);
        Assert.Equal(NagmaraAI.GossipItemRepeat, item.Text);
        Assert.Equal(0, ai.Phase);

        Assert.False(ai.SelectReply(h.Player, npc, item.Sender, 99).Close); // another action does nothing
        Assert.Equal(0, ai.Phase);

        ScriptedGossipReply reply = ai.SelectReply(h.Player, npc, item.Sender, item.Action);
        Assert.True(reply.Close);
        Assert.Equal(1, ai.Phase);
        Assert.Equal(0u, nagmara.NpcFlags & ((uint)NpcFlags.Gossip | (uint)NpcFlags.QuestGiver));
        Assert.Equal(0u, rocknot.NpcFlags & (uint)NpcFlags.QuestGiver);
    }

    [Fact]
    public void PotionOfLove_BeforeTheRewardTheRepeatLineIsNotOfferedOrAccepted()
    {
        using DungeonScriptHarness h = Harness();
        h.Map.AddUpdater(new RewardSeam(false));
        Creature nagmara = h.Creature(BlackrockDepthsInstance.NpcNagmara);
        var ai = Assert.IsType<NagmaraAI>(nagmara.AI);
        NpcInfo npc = InfoOf(nagmara);

        Assert.Null(ai.Hello(h.Player, npc));
        Assert.False(ai.SelectReply(h.Player, npc, 1, NagmaraAI.ActionRepeat).Close);
        Assert.Equal(0, ai.Phase);
    }

    private static void Reach(RocknotAI ai, params uint[] pointIds)
    {
        foreach (uint id in pointIds)
        {
            ai.OnMovementInform(MovementGeneratorType.Point, id);
        }
    }

    [Fact]
    public void AleEscort_AtPoint9_StopsAndRocknotStaysAtTheBar()
    {
        using DungeonScriptHarness h = Harness();
        Creature rocknot = h.Creature(BlackrockDepthsInstance.NpcRocknot);
        var ai = Assert.IsType<RocknotAI>(rocknot.AI);
        for (int i = 0; i < 3; i++)
        {
            ai.OnQuestRewarded(h.Player, RocknotAI.QuestAle);
        }

        h.Tick(2000);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));

        Reach(ai, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting)); // the escort ends at point 9
        h.Tick(2500);

        Assert.True(rocknot.IsInWorld);
        Assert.Contains(h.Creatures.Creatures, c => c.Template.Entry == BlackrockDepthsInstance.NpcRocknot);
    }

    [Fact]
    public void PotionOfLoveEscort_AfterPoint34_RocknotStaysPausedInPlace()
    {
        using DungeonScriptHarness h = Harness();
        var instance = (BlackrockDepthsInstance)h.Data;
        Creature rocknot = h.Creature(BlackrockDepthsInstance.NpcRocknot);
        var ai = Assert.IsType<RocknotAI>(rocknot.AI);

        instance.SetData(BlackrockDepthsInstance.TypeNagmara, EncounterState.Special);
        h.Tick(50);
        Assert.Equal(EncounterState.InProgress, instance.GetData(BlackrockDepthsInstance.TypeNagmara));
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));

        Reach(ai, 1);
        Reach(ai, [.. Enumerable.Range(10, 25).Select(i => (uint)i)]);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Paused));
        h.Tick(2500);

        Assert.True(rocknot.IsInWorld);
        Assert.Contains(h.Creatures.Creatures, c => c.Template.Entry == BlackrockDepthsInstance.NpcRocknot);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Paused));
    }

    [Fact]
    public void AleEvent_AfterTheKegIsShot_PhalanxWalksToTheBarDoorAndStaysThere()
    {
        using DungeonScriptHarness h = Harness();
        var instance = (BlackrockDepthsInstance)h.Data;
        Creature rocknot = h.Creature(BlackrockDepthsInstance.NpcRocknot);
        Creature phalanx = h.Creature(BlackrockDepthsInstance.NpcPhalanx);
        var ai = Assert.IsType<RocknotAI>(rocknot.AI);
        var phalanxAi = Assert.IsType<PhalanxAI>(phalanx.AI);
        for (int i = 0; i < 3; i++)
        {
            ai.OnQuestRewarded(h.Player, RocknotAI.QuestAle);
        }

        h.Tick(2000);
        Reach(ai, 1, 2, 3, 4, 5, 6, 7);
        h.Tick(2000);
        h.Tick(1000);
        h.Tick(5000);

        Assert.Equal(GameObjectState.Active, h.Object(BlackrockDepthsInstance.GoBarKegShot).State);
        Assert.Equal(EncounterState.Done, instance.GetData(BlackrockDepthsInstance.TypeRocknot));
        Assert.True(phalanxAi.HasEscortState(EscortAI.EscortState.Escorting));

        phalanxAi.OnMovementInform(MovementGeneratorType.Point, 1);
        phalanxAi.OnMovementInform(MovementGeneratorType.Point, 2);
        h.Tick(2500);

        Assert.True(phalanx.IsInWorld);
        Assert.Contains(h.Creatures.Creatures, c => c.Template.Entry == BlackrockDepthsInstance.NpcPhalanx);
        Assert.True(phalanxAi.HasEscortState(EscortAI.EscortState.Paused));
    }
}
