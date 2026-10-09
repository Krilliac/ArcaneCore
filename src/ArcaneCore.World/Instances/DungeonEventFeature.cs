using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackrockDepths;
using ArcaneCore.Game.Instances.Scripts.BlackwingLair;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Instances;

/// <summary>
/// ScriptDev2 dungeon event entry points: blackrock_depths.cpp AreaTrigger_at_ring_of_law (1526),
/// sunken_templeScripts.cpp ProcessEventId_event_avatar_of_hakkar (8502), and the gossip of boss_doomrel (<see cref="DoomrelGossip"/>).
/// The area trigger listener runs after the packet's volume check; SEND_EVENT runs after its spell effect lands.
/// </summary>
public sealed class DungeonEventFeature(IServiceProvider services) : IWorldFeature, IAreaTriggerListener
{
    public const uint RingOfLawTrigger = 1526;
    public const uint AvatarOfHakkarEvent = 8502;

    private WorldRuntime? _world;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        SpellSystem spells = services.GetRequiredService<SpellFeature>().System;
        SpellEffectHandler? prior = spells.GetEffectHandler(SpellEffectName.SendEvent);
        spells.RegisterEffect(SpellEffectName.SendEvent, context =>
        {
            prior?.Invoke(context);
            if (context.Caster is Player player && context.Effect.MiscValue == AvatarOfHakkarEvent
                && player.Map?.FindUpdater<InstanceData>() is SunkenTempleInstance temple)
                temple.BeginAvatarEvent();
        });

        // The quest feature rebuilds its services when it attaches, after this one (type-name order): install on the first world command.
        world.Post(() =>
        {
            QuestNpcFeature? quests = services.GetService<QuestNpcFeature>();
            quests?.Services.AddGossipScript(new DoomrelGossip());
            quests?.Services.AddGossipScript(new BlackwingLairGossip());
        });
    }

    public void OnAreaTrigger(Player player, uint triggerId)
    {
        if (triggerId != RingOfLawTrigger || player.Map?.FindUpdater<InstanceData>() is not BlackrockDepthsInstance depths)
            return;

        // SetArenaCenterCoords(pAt->x, pAt->y, pAt->z): the trigger's own centre, not where the player stands in it.
        AreaTriggerTemplate? trigger = _world is null ? null : WorldMaps.Of(_world).FindAreaTrigger(triggerId);
        if (trigger is not null)
            depths.EnterRingOfLaw(player, trigger.X, trigger.Y, trigger.Z);
        else
            depths.EnterRingOfLaw(player, player.X, player.Y, player.Z);
    }
}
