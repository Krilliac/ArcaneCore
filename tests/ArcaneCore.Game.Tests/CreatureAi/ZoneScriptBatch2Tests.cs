using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>Zone script batch 2: the Defias Traitor, Yenniku, Kanati, OOX-09/HL and Foreman Klaven Mortwake.</summary>
public sealed class ZoneScriptBatch2Tests
{
    private static (WorldRuntime World, Player Player, Creature Npc) Start(uint entry, params CreatureTemplate[] extra)
    {
        CreatureContent content = new([Template(entry), .. extra], [Spawn(1, entry, 0, 0)], [], [], [], new CreatureAiContent([], []));
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices());
        (Player player, _) = AddPlayer(world, 1, 5, 0);
        return (world, player, Assert.Single(system.Creatures, c => c.Entry == entry));
    }

    [Theory]
    [InlineData(DefiasTraitorAI.Entry, typeof(DefiasTraitorAI))]
    [InlineData(YennikuAI.Entry, typeof(YennikuAI))]
    [InlineData(KanatiAI.Entry, typeof(KanatiAI))]
    [InlineData(Oox09AI.Entry, typeof(Oox09AI))]
    [InlineData(ForemanKlavenMortwakeAI.Entry, typeof(ForemanKlavenMortwakeAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        (WorldRuntime world, _, Creature npc) = Start(entry);
        using (world)
        {
            Assert.IsType(ai, npc.AI);
        }
    }

    [Fact]
    public void Yenniku_ReleaseCalmsHimForAMinuteThenHisFactionComesBack()
    {
        (WorldRuntime world, Player player, Creature yenniku) = Start(YennikuAI.Entry);
        using (world)
        {
            var ai = (YennikuAI)yenniku.AI!;
            uint own = yenniku.FactionTemplate;

            ai.OnSpellHit(yenniku, new SpellInfo { Id = YennikuAI.SpellYennikusRelease }); // not a player
            Assert.False(ai.IsCalmed);
            ai.OnSpellHit(player, new SpellInfo { Id = YennikuAI.SpellYennikusRelease });
            Assert.True(ai.IsCalmed);
            Assert.Equal(YennikuAI.FactionHordeGeneric, yenniku.FactionTemplate);

            Run(world, YennikuAI.CalmMs - 1_000);
            Assert.True(ai.IsCalmed);
            Run(world, 2_000);
            Assert.False(ai.IsCalmed);
            Assert.Equal(own, yenniku.FactionTemplate);
        }
    }

    [Fact]
    public void Kanati_AmbushSummonsThreeGalakAssassins()
    {
        (WorldRuntime world, _, Creature kanati) = Start(KanatiAI.Entry, Template(KanatiAI.NpcGalakAssassin));
        using (world)
        {
            Assert.Equal(3, ((KanatiAI)kanati.AI!).SpawnGalak());
        }
    }

    [Fact]
    public void Oox09_LiesDeadUntilEscorted_AndAmbushesCountTowardTheCap()
    {
        (WorldRuntime world, _, Creature oox) = Start(Oox09AI.Entry, Template(Oox09AI.NpcMaraudingOwl));
        using (world)
        {
            var ai = (Oox09AI)oox.AI!;
            Assert.Equal(StandState.Dead, oox.StandState);
            ai.Ambush(Oox09AI.NpcMaraudingOwl, 1, 1, 0);
            Assert.Equal(3, ai.SummonCount);
        }
    }
}
