using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// world_map_eastern_kingdoms (world_eastern_kingdoms.cpp) and event_wildhammer_message (hinterlands.cpp:363-374, event 8608), on a real
/// map 0 with Falstad Wildhammer and a player next to him.
/// </summary>
public sealed class EasternKingdomsWorldMapTests
{
    private const float Z = 83.5f;

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            var aiContent = new CreatureAiContent([], [], new BroadcastTextCatalog(
                [new BroadcastText((uint)EasternKingdomsWorldMapData.YellFalstadInvaders, "Invaders! Invaders!", string.Empty, 0, 0, 0, [0, 0, 0], [0, 0, 0])]));
            CreatureContent content = new([Template(EasternKingdomsWorldMapData.NpcFalstadWildhammer, b => { b.Name = "Falstad Wildhammer"; b.Faction = 55; })],
                [Spawn(1, EasternKingdomsWorldMapData.NpcFalstadWildhammer, 0f, 0f, Z)], [], [], [], aiContent);
            (World, Map, System) = CreateAiSystem(content);
            (Player, Session) = AddPlayer(World, 1, 1f, 0f);
            Player.Relocate(1f, 0f, Z, 0, 0);
            Falstad = Assert.Single(System.Creatures);
            Data = Assert.IsType<EasternKingdomsWorldMapData>(WorldMapData.Create(Map));
            Data.Initialize();
            Map.AddUpdater(Data);
        }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Creature Falstad { get; }

        public EasternKingdomsWorldMapData Data { get; }

        public List<string> Heard() => [.. Packets(Session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void WildhammerMessage_FlagsThePlayerForPvp_AndFalstadYellsTheAlarm()
    {
        using var rig = new Rig();
        rig.Data.OnCreatureCreate(rig.Falstad);
        Assert.Equal(0u, (uint)(rig.Player.UnitFlags & UnitFlags.Pvp));
        rig.Session.Clear();

        Assert.True(rig.Data.OnSpellEvent(rig.Player, EasternKingdomsWorldMapData.EventWildhammerMessage));

        Assert.NotEqual(0u, (uint)(rig.Player.UnitFlags & UnitFlags.Pvp));
        Assert.Contains("Invaders! Invaders!", rig.Heard());
        Assert.Equal(ScriptedEventResult.Handled, ScriptedEvents.Start(rig.Map, EasternKingdomsWorldMapData.EventWildhammerMessage, rig.Player, null));
    }

    [Fact]
    public void WildhammerMessage_WithoutFalstadInStorage_OnlyFlags_AndOtherSourcesOrEventsDoNothing()
    {
        using var rig = new Rig();
        rig.Session.Clear();

        Assert.True(rig.Data.OnSpellEvent(rig.Player, EasternKingdomsWorldMapData.EventWildhammerMessage));
        Assert.NotEqual(0u, (uint)(rig.Player.UnitFlags & UnitFlags.Pvp));
        Assert.Empty(rig.Heard());

        rig.Data.OnCreatureCreate(rig.Falstad);
        Assert.True(rig.Data.OnSpellEvent(rig.Falstad, EasternKingdomsWorldMapData.EventWildhammerMessage));
        Assert.False(rig.Data.OnSpellEvent(rig.Player, 1));
        Assert.Empty(rig.Heard());
    }
}
