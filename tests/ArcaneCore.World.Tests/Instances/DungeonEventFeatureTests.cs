using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Instances.Scripts.BlackwingLair;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

public sealed class DungeonEventFeatureTests
{
    [Fact]
    public async Task TheWorldDiscoversTheDungeonTriggers_AndInstallsTheAvatarSendEventEffect()
    {
        Assert.Contains(typeof(DungeonEventFeature), WorldFeatures.FeatureTypes);
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance))
            .BuildServiceProvider();
        var saves = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves,
            NullLogger<WorldRuntime>.Instance);

        new DungeonEventFeature(services).Attach(world);

        Assert.True(services.GetRequiredService<SpellFeature>().System.HasEffectHandler(SpellEffectName.SendEvent));
    }

    [Fact]
    public async Task DoomrelsGossipJoinsTheQuestNpcScripts_OnTheFirstWorldCommand_BesideAnEarlierScript()
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance))
            .AddSingleton(sp => new QuestNpcFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<QuestNpcFeature>.Instance))
            .BuildServiceProvider();
        var saves = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves,
            NullLogger<WorldRuntime>.Instance);
        QuestNpcServices npcs = services.GetRequiredService<QuestNpcFeature>().Services;
        var earlier = new NoGossip();
        npcs.GossipScript = earlier;

        new DungeonEventFeature(services).Attach(world);
        Assert.Same(earlier, npcs.GossipScript); // not yet: the quest feature attaches later and rebuilds its services
        world.RunTick(0);

        Assert.IsType<NpcGossipScriptChain>(npcs.GossipScript);
        Assert.Contains(ArcaneCore.World.Tests.Npc.GossipScriptLayers.Of(npcs.GossipScript), script => script is BlackwingLairGossip);
    }

    private sealed class NoGossip : INpcGossipScript
    {
        public ScriptedGossipMenu? Hello(ArcaneCore.Game.Entities.Player player, NpcInfo npc) => null;

        public uint Select(ArcaneCore.Game.Entities.Player player, NpcInfo npc, uint sender, uint action) => 0;
    }
}
