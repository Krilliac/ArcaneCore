using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Uldaman;
using ArcaneCore.Game.Maps;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class DungeonScriptRegistrationTests
{
    [Theory]
    [InlineData(90u)]  // Gnomeregan
    [InlineData(189u)] // Scarlet Monastery
    [InlineData(129u)] // Razorfen Downs
    [InlineData(47u)]  // Razorfen Kraul
    [InlineData(70u)]  // Uldaman
    [InlineData(209u)] // Zul'Farrak
    public void ScriptDev2DungeonHasInstanceData(uint mapId)
        => Assert.True(InstanceScriptRegistry.Default.HasScript(mapId));

    [Fact]
    public void InstanceDataAttachedAfterAGridLoaded_GivesTheAlreadyBuiltCreatureItsScriptAi()
    {
        // InstanceManager.AttachInstanceData reports creatures a system loaded early to OnCreatureCreate after their AI was built.
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(Content(
            [Template(UldamanInstance.Archaedas)], [Spawn(1, UldamanInstance.Archaedas, 5, 0)]));
        AddPlayer(world, 1, 0, 0);
        Creature archaedas = Assert.Single(system.Creatures);
        Assert.IsNotType<ArchaedasAi>(archaedas.AI);

        var data = new UldamanInstance(map);
        map.AddUpdater(data);
        data.OnCreatureCreate(archaedas);

        Assert.IsType<ArchaedasAi>(archaedas.AI);
    }
}
