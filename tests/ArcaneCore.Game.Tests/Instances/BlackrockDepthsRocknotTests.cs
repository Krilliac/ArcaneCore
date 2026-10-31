using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
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
            [.. Enumerable.Range(1, 3).Select(i => (BlackrockDepthsInstance.NpcRocknot, EscortAI.EscortPathId,
                new CreatureWaypoint((uint)i, -16f + i, -383f, 61.78f, 0, 0)))],
            null);

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
}
