using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Creatures.Scripts.Escorts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>Zone script batch 1 escorts: Galen Goodward, Miran (hand-ported) and Corporal Keeshan, Kinelory (data-driven catalog).</summary>
public sealed class ZoneEscortScriptTests
{
    private static (WorldRuntime World, CreatureMapSystem System, Creature Npc) Start(uint entry, params CreatureTemplate[] extra)
    {
        CreatureContent content = new([Template(entry), .. extra], [Spawn(1, entry, 0, 0)], [], [], [], new CreatureAiContent([], []));
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices());
        AddPlayer(world, 1, 5, 0);
        return (world, system, Assert.Single(system.Creatures, c => c.Entry == entry));
    }

    [Theory]
    [InlineData(GalenGoodwardAI.Entry, typeof(GalenGoodwardAI))]
    [InlineData(MiranAI.Entry, typeof(MiranAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        (WorldRuntime world, _, Creature npc) = Start(entry);
        using (world)
        {
            Assert.IsType(ai, npc.AI);
        }
    }

    [Theory]
    [InlineData(349u, 219u)]
    [InlineData(2713u, 660u)]
    public void CatalogEscorts_AreRegisteredWithTheirQuest(uint entry, uint quest)
    {
        EscortSpec spec = Assert.IsType<EscortSpec>(EscortSpecCatalog.Find(entry));
        Assert.Equal(quest, spec.QuestId);
        Assert.Contains(spec.Waypoints, w => w.Actions.Any(a => a.Type == "quest_complete" && a.Id == quest));
        (WorldRuntime world, _, Creature npc) = Start(entry);
        using (world)
        {
            Assert.IsType<DataDrivenEscortAI>(npc.AI);
        }
    }

    [Fact]
    public void Miran_WalksOnOnlyWhenBothRaidersAreDead()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature miran) = Start(MiranAI.Entry, Template(MiranAI.NpcDarkIronRaider));
        using (world)
        {
            var ai = (MiranAI)miran.AI!;
            Creature a = system.SummonCorpseTimedDespawn(miran, MiranAI.NpcDarkIronRaider, 1, 1, 0, 0, null, 25_000)!;
            Creature b = system.SummonCorpseTimedDespawn(miran, MiranAI.NpcDarkIronRaider, 2, 2, 0, 0, null, 25_000)!;
            typeof(MiranAI).GetField("_dwarves", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(ai, 2);

            ai.OnSummonedCreatureJustDied(a);
            Assert.Equal(1, ai.Dwarves);
            ai.OnSummonedCreatureJustDied(b);
            Assert.Equal(0, ai.Dwarves);
            ai.OnSummonedCreatureJustDied(b); // no underflow
            Assert.Equal(0, ai.Dwarves);
        }
    }
}
