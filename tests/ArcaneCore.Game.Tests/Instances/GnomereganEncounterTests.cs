using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Gnomeregan;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Thermaplugg's aggro on sight and evade reset, and Emi's gossip wrapper reading its option from gossip_texts.</summary>
public sealed class GnomereganEncounterTests
{
    private static NpcInfo Npc(Creature creature, DungeonScriptHarness run)
        => new(creature.Guid, creature.Template.Entry, creature.Spawn!.Guid, NpcFlags.Gossip,
            run.Map.MapId, creature.X, creature.Y, creature.Z, creature.BoundingRadius, true, false, false, false, 0);

    [Fact]
    public void Thermaplugg_AggroesOnSight_AndAWipeSendsHimBackToPhaseOne()
    {
        using DungeonScriptHarness run = new(map => new GnomereganInstance(map), [ThermapluggAi.Entry], [ThermapluggAi.Entry], null,
            new CreatureAiServices { Hostility = new AlwaysHostile() });
        Creature boss = run.Creature(ThermapluggAi.Entry);
        var ai = Assert.IsType<ThermapluggAi>(boss.AI);
        for (int i = 0; i < 20; i++) run.Tick(50);
        Assert.Same(run.Player, boss.Combat.Victim);
        Assert.Equal(EncounterState.InProgress, run.Data.GetData(GnomereganInstance.TypeThermaplugg));

        boss.Health = boss.MaxHealth * 2 / 5;
        run.Tick(50);
        Assert.True(ai.PhaseTwo);

        run.Creatures.EnterEvadeMode(boss);

        Assert.False(ai.PhaseTwo); // boss_thermaplugg.cpp Reset (EnterEvadeMode): m_bIsPhaseTwo = false
    }

    [Fact]
    public void EmiGossip_ReadsItsOptionFromGossipTexts_AndPassesOtherNpcsToTheWrappedScript()
    {
        var dbText = new CreatureAiText(EmiGossipScript.StartOptionText, "Ready when you are.", 0, 0, 0);
        using DungeonScriptHarness run = new(map => new GnomereganInstance(map), [EmiShortfuseAi.Entry, 7850], [EmiShortfuseAi.Entry, 7850],
            null, null, null, [dbText]);
        var inner = new RecordingGossip();
        INpcGossipScript gossip = DungeonScriptHooks.WrapGossip(inner);

        ScriptedGossipMenu menu = Assert.IsType<ScriptedGossipMenu>(gossip.Hello(run.Player, Npc(run.Creature(EmiShortfuseAi.Entry), run)));
        Assert.Equal("Ready when you are.", Assert.Single(menu.Items).Text);
        Assert.Equal(0u, menu.NpcTextId); // SEND_GOSSIP_MENU(GetGossipTextId(creature)): the creature's own text

        NpcInfo kernobee = Npc(run.Creature(7850), run);
        Assert.Same(RecordingGossip.Menu, gossip.Hello(run.Player, kernobee));
        Assert.Equal(7u, gossip.SelectReply(run.Player, kernobee, 1, 2).NpcTextId);
        Assert.Equal([7850u, 7850u], inner.Seen);
    }

    [Fact]
    public void EmiGossip_SelectDoesNotRestartTheEscortOnceGrubbisIsDone()
    {
        using DungeonScriptHarness run = new(map => new GnomereganInstance(map), [EmiShortfuseAi.Entry], [EmiShortfuseAi.Entry]);
        run.Data.SetData(GnomereganInstance.TypeGrubbis, EncounterState.Done);
        INpcGossipScript gossip = DungeonScriptHooks.WrapGossip(null);
        NpcInfo emi = Npc(run.Creature(EmiShortfuseAi.Entry), run);

        Assert.True(gossip.SelectReply(run.Player, emi, 1, EmiGossipScript.StartAction).Close);

        Assert.Equal(EncounterState.Done, run.Data.GetData(GnomereganInstance.TypeGrubbis));
        Assert.Equal(0, Assert.IsType<EmiShortfuseAi>(run.Creature(EmiShortfuseAi.Entry).AI).Phase);
    }

    private sealed class RecordingGossip : INpcGossipScript
    {
        public static readonly ScriptedGossipMenu Menu = new(false, 7, []);

        public List<uint> Seen { get; } = [];

        public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
        {
            Seen.Add(npc.Entry);
            return Menu;
        }

        public uint Select(Player player, NpcInfo npc, uint sender, uint action) => SelectReply(player, npc, sender, action).NpcTextId;

        public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
        {
            Seen.Add(npc.Entry);
            return new ScriptedGossipReply(7);
        }
    }
}
