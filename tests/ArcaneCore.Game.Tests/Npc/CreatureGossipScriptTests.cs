using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// ScriptDev creature gossip (a creature's ScriptName owns pGossipHello/pGossipSelect, e.g. mangos-classic boss_majordomo_executus.cpp
/// GossipHello/GossipSelect_boss_majordomo): the creature's own script answers ahead of the world-wide fallback script, and a choice may
/// replace the menu with the script's next one (SEND_GOSSIP_MENU after a new set of ADD_GOSSIP_ITEMs).
/// </summary>
public sealed class CreatureGossipScriptTests
{
    private sealed class StepAi(Creature creature) : CreatureAI(creature), INpcGossipScript
    {
        public ScriptedGossipMenu? Hello(Player player, NpcInfo npc) => new(false, 4995, [new(0, "Tell me more.", 1, 1)]);
        public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;
        public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
            => new(0) { NextMenu = new(false, 5011, [new(0, "What else do you have to say?", 1, 2)]) };
    }

    private sealed class FallbackScript : INpcGossipScript
    {
        public ScriptedGossipMenu? Hello(Player player, NpcInfo npc) => new(false, 1, [new(0, "Fallback", 9, 9)]);
        public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 2;
    }

    private static uint TextId(byte[] gossipMessage)
    {
        var reader = new PacketReader(gossipMessage);
        reader.ReadUInt64();
        return reader.ReadUInt32();
    }

    [Fact]
    public void CreatureScript_AnswersBeforeTheFallback_AndItsReplyReplacesTheMenu()
    {
        using var kit = new NpcServiceKit(NpcFlags.Gossip);
        kit.Services.GossipScript = new FallbackScript();
        Map map = kit.World.GetMap(0);
        var content = new CreatureContent([CreatureTestSupport.Template(NpcServiceKit.Entry)], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content);
        map.AddUpdater(creatures);
        creatures.RegisterEntryAi(NpcServiceKit.Entry, c => new StepAi(c));
        Creature creature = creatures.SpawnTemporary(content.FindTemplate(NpcServiceKit.Entry)!, kit.Npc.X, kit.Npc.Y, kit.Npc.Z, 0);
        Assert.IsType<StepAi>(creature.AI);
        kit.Npc = kit.Npc with { Guid = creature.Guid };

        kit.Services.GossipHello(kit.Player, creature.Guid);
        Assert.Equal(4995u, TextId(kit.Single(WorldOpcode.SmsgGossipMessage)));

        kit.Services.GossipSelectOption(kit.Player, creature.Guid, 0, null);
        Assert.Equal(5011u, TextId(kit.Single(WorldOpcode.SmsgGossipMessage)));
    }

    [Fact]
    public void WithoutACreatureScript_TheFallbackStillAnswers()
    {
        using var kit = new NpcServiceKit(NpcFlags.Gossip);
        kit.Services.GossipScript = new FallbackScript();

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.Equal(1u, TextId(kit.Single(WorldOpcode.SmsgGossipMessage)));
    }
}
