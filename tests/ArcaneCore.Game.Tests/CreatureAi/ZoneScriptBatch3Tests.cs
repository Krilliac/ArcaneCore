using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>Zone script batch 3: Prospector Remtravel and the Barrens' Venture Co. stealth-alert speakers.</summary>
public sealed class ZoneScriptBatch3Tests
{
    [Theory]
    [InlineData(ProspectorRemtravelAI.Entry, typeof(ProspectorRemtravelAI))]
    [InlineData(GallywixAI.Entry, typeof(GallywixAI))]
    [InlineData(ForemanSilixizAI.Entry, typeof(ForemanSilixizAI))]
    [InlineData(VentureCoLookoutAI.Entry, typeof(VentureCoLookoutAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        CreatureContent content = new([Template(entry)], [Spawn(1, entry, 0, 0)], [], [], [], new CreatureAiContent([], []));
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices());
        using (world)
        {
            AddPlayer(world, 1, 5, 0);
            Assert.IsType(ai, Assert.Single(system.Creatures, c => c.Entry == entry).AI);
        }
    }
}
