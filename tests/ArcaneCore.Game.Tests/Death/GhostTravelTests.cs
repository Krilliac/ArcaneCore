using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Travel;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// Corpses and dungeons: the corpse arrow (vmangos HandleCorpseQueryOpcode, QueryHandler.cpp:155-201), ghosts at dungeon entrances
/// (HandleAreaTriggerOpcode, MiscHandler.cpp:712-756) and the revival of a ghost that enters the map its corpse lies in
/// (Player::TeleportTo, Player.cpp:1953-1966).
/// </summary>
public sealed class GhostTravelTests
{
    private const long T = 1_700_000_000;

    // 33: a dungeon whose entrance is on map 0 at (-230, 1570); 40 and 41: nested dungeons (41 inside 40); 1: a continent.
    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            new MapTemplate(33, 0, MapType.Instance, 0, 10, 0, 0, -230f, 1570f, "Shadowfang Keep", ""),
            new MapTemplate(40, 0, MapType.Instance, 0, 5, 0, 0, 10f, 20f, "Outer", ""),
            new MapTemplate(41, 40, MapType.Instance, 0, 5, 0, 0, 30f, 40f, "Inner", ""),
        ],
        [], [], [], []);

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            World = TestWorld.CreateRuntime();
            DeathHooks.Register(World, new DeathHooks(new DeathOptions(), new FixedDeathClock(T)));
            WorldMaps.Of(World).Load(Content);
            Session = new FakeSession(9);
            Player = CombatTestKit.AddPlayer(World, 9, 100, 100, Session);
            World.RunTick(1);
            Session.Clear();
        }

        public WorldRuntime World { get; }

        public FakeSession Session { get; }

        public Player Player { get; }

        /// <summary>A ghost on map 0 whose body lies on <paramref name="mapId"/> (as after a login: RestoreGhost).</summary>
        public void GhostWithBodyOn(uint mapId, float x = 5f, float y = 6f, float z = 7f)
        {
            PlayerLife.ApplyGhostState(Player);
            World.GetMap(0).Combat.RestoreGhost(Player, new CorpseSnapshot(mapId, x, y, z, 1f, T - 10, (byte)CorpseType.ResurrectablePve));
        }

        public void Dispose() => World.Dispose();
    }

    private static (byte Found, int Map, float X, float Y, float Z, uint CorpseMap) Parse(byte[] reply) => reply.Length == 1
        ? (reply[0], 0, 0, 0, 0, 0)
        : (reply[0], BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1)), BinaryPrimitives.ReadSingleLittleEndian(reply.AsSpan(5)),
            BinaryPrimitives.ReadSingleLittleEndian(reply.AsSpan(9)), BinaryPrimitives.ReadSingleLittleEndian(reply.AsSpan(13)),
            BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(17)));

    [Fact]
    public void ACorpseInADungeon_SeenFromAnotherMap_IsShownAtTheEntrance_WithTheRealMapLast()
    {
        using var rig = new Rig();
        rig.GhostWithBodyOn(33);

        var reply = Parse(MapCombat.BuildCorpseQuery(rig.Player));

        float ground = rig.World.GetMap(0).GetHeight(-230f, 1570f, GridDefines.MaxHeight);
        Assert.Equal((1, 0, -230f, 1570f, ground, 33u), reply);
    }

    [Fact]
    public void ACorpseOnThePlayersOwnMap_IsShownWhereItLies()
    {
        using var rig = new Rig();
        rig.GhostWithBodyOn(0);

        Assert.Equal((1, 0, 5f, 6f, 7f, 0u), Parse(MapCombat.BuildCorpseQuery(rig.Player)));
    }

    [Fact]
    public void ACorpseOnAnotherContinent_IsShownWhereItLies()
    {
        using var rig = new Rig();
        rig.GhostWithBodyOn(1);

        Assert.Equal((1, 1, 5f, 6f, 7f, 1u), Parse(MapCombat.BuildCorpseQuery(rig.Player)));
    }

    [Fact]
    public void WithoutACorpse_NothingIsFound()
    {
        using var rig = new Rig();

        Assert.Equal((0, 0, 0f, 0f, 0f, 0u), Parse(MapCombat.BuildCorpseQuery(rig.Player)));
    }

    // --- the ghost rules at dungeon entrances ------------------------------------------------------

    private static readonly AreaTriggerTeleport ToOuter = new(1, "Outer entrance", "", 0, 40, 1, 1, 1, 0);
    private static readonly AreaTriggerTeleport ToInner = new(2, "Inner entrance", "", 0, 41, 2, 2, 2, 0);
    private static readonly AreaTriggerTeleport ToShadowfang = new(3, "SFK entrance", "", 0, 33, 3, 3, 3, 0);
    private static readonly AreaTriggerTeleport ToContinent = new(4, "Back out", "", 0, 1, 4, 4, 4, 0);
    private static readonly AreaTriggerTeleport[] All = [ToOuter, ToInner, ToShadowfang, ToContinent];

    private static GhostEntry Resolve(Rig rig, AreaTriggerTeleport trigger) =>
        GhostEntryRules.Resolve(rig.Player, trigger, WorldMaps.Of(rig.World).Registry, All);

    [Fact]
    public void AGhostMayNotEnterADungeonThatIsNotItsCorpsesDungeon_NorACorpseOnAContinentsOne()
    {
        using var rig = new Rig();
        rig.GhostWithBodyOn(33);

        GhostEntry other = Resolve(rig, ToOuter);

        Assert.True(other.Refused);
        Assert.Equal("You cannot enter Outer while in ghost form.", other.Message);
        rig.Player.Combat.Corpse = null;
        rig.GhostWithBodyOn(0);
        Assert.True(Resolve(rig, ToOuter).Refused); // a body on the continent: no dungeon at all
    }

    [Fact]
    public void AGhostMayEnterItsCorpsesDungeon_ByItsOwnTrigger()
    {
        using var rig = new Rig();
        rig.GhostWithBodyOn(40);

        GhostEntry entry = Resolve(rig, ToOuter);

        Assert.False(entry.Refused);
        Assert.Same(ToOuter, entry.Trigger);
    }

    [Fact]
    public void AGhostWhoseCorpseIsInANestedDungeon_EntersThroughTheParent_AndLandsAtTheInnerEntrance()
    {
        using var rig = new Rig();
        rig.GhostWithBodyOn(41);

        GhostEntry entry = Resolve(rig, ToOuter);

        Assert.Same(ToInner, entry.Trigger); // "need find areatrigger to inner dungeon for landing point"
    }

    [Fact]
    public void TheLivingAndTheTriggersIntoContinents_AreNotTouched()
    {
        using var rig = new Rig();
        Assert.Same(ToOuter, Resolve(rig, ToOuter).Trigger); // alive
        rig.GhostWithBodyOn(33);
        Assert.Same(ToContinent, Resolve(rig, ToContinent).Trigger); // a ghost may always leave
    }

    // --- the revival at the dungeon door -----------------------------------------------------------

    [Fact]
    public void AGhostThatFarTeleportsIntoTheMapOfItsCorpse_IsResurrectedAtHalfHealth_AndTheCorpseGoes()
    {
        using var rig = new Rig();
        var teleports = new TeleportService(rig.World, _ => { }, _ => { });
        rig.GhostWithBodyOn(33);
        Assert.False(rig.Player.IsAlive);

        Assert.True(teleports.TeleportTo(rig.Player, 33, 1, 1, 1, 0));

        Assert.True(rig.Player.IsAlive);
        Assert.Equal(rig.Player.MaxHealth / 2, rig.Player.Health);
        Assert.Null(rig.Player.Combat.Corpse);
        Assert.Equal(0u, (uint)(rig.Player.Flags & PlayerFlags.Ghost));
    }

    [Fact]
    public void AGhostEnteringAnotherMapThanItsCorpses_StaysDead()
    {
        using var rig = new Rig();
        var teleports = new TeleportService(rig.World, _ => { }, _ => { });
        rig.GhostWithBodyOn(33);

        Assert.True(teleports.TeleportTo(rig.Player, 40, 1, 1, 1, 0));

        Assert.False(rig.Player.IsAlive);
        Assert.NotNull(rig.Player.Combat.Corpse);
    }

    [Fact]
    public void ANearTeleport_OrAPlayerStillWaitingAtItsBody_IsNotRevived()
    {
        using var rig = new Rig();
        var teleports = new TeleportService(rig.World, _ => { }, _ => { });
        rig.GhostWithBodyOn(0);
        Assert.True(teleports.TeleportTo(rig.Player, 0, 1, 1, 1, 0)); // same map
        Assert.False(rig.Player.IsAlive);

        // an unreleased player (CORPSE state) is not a ghost: nothing revives it on a map change
        using var other = new Rig();
        var teleports2 = new TeleportService(other.World, _ => { }, _ => { });
        other.Player.Health = 0;
        other.World.GetMap(0).Combat.KillPlayer(other.Player);
        Assert.True(teleports2.TeleportTo(other.Player, 33, 1, 1, 1, 0));
        Assert.False(other.Player.IsAlive);
    }
}
