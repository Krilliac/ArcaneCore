using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// CMSG_RECLAIM_CORPSE in a battleground (vmangos HandleReclaimCorpseOpcode, MiscHandler.cpp:594-599): refused until the
/// player's match is in progress ("die with hellfire during battleground preparation, and resurrect after the door"), and the
/// body comes back at full health and mana instead of half.
/// </summary>
public sealed class CorpseReclaimBattlegroundTests
{
    private const long T = 1_700_000_000;
    private const uint WarsongGulch = 489;

    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(WarsongGulch, 0, MapType.Battleground, 0, 20, 0, -1, 0, 0, "Warsong Gulch", ""),
        ],
        [],
        [],
        [],
        []);

    private sealed record Rig(WorldRuntime World, MapCombat Combat, FixedDeathClock Clock, Player Player);

    private sealed class FixedPresence(BattlegroundStatus? status) : IBattlegroundPresence
    {
        public BattlegroundStatus? Status { get; set; } = status;

        public BattlegroundStatus? MatchStatusOf(ObjectGuid player) => Status;
    }

    /// <summary>A dead, released player standing on its body 30 s later (the reclaim delay of a first death).</summary>
    private static Rig ReleasedGhost(uint mapId)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        WorldMaps.Of(world).Load(Content);
        var clock = new FixedDeathClock(T);
        DeathHooks.Register(world, new DeathHooks(new DeathOptions(), clock));
        Map map = world.GetMap(mapId);
        map.Combat.Hooks = new TestCombatHooks();
        var session = new FakeSession(2);
        Player player = CombatTestKit.AddPlayer(world, 2, 10, 20, session, mapId: mapId);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        world.RunTick(1);
        Assert.Same(map, player.Map);

        map.Combat.Kill(null, player);
        Assert.True(map.Combat.RepopPlayer(player));
        clock.Now += 30;
        return new Rig(world, map.Combat, clock, player);
    }

    [Fact]
    public void Reclaim_InABattleground_RestoresFullHealthAndMana()
    {
        Rig r = ReleasedGhost(WarsongGulch);
        using WorldRuntime world = r.World;

        Assert.True(r.Combat.TryReclaimCorpse(r.Player));

        Assert.Equal(1000u, r.Player.Health);
        Assert.Equal(1000u, r.Player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void Reclaim_OnAContinent_StillRestoresHalf()
    {
        Rig r = ReleasedGhost(0);
        using WorldRuntime world = r.World;

        Assert.True(r.Combat.TryReclaimCorpse(r.Player));

        Assert.Equal(500u, r.Player.Health);
    }

    [Fact]
    public void Reclaim_IsRefusedUntilTheMatchIsInProgress()
    {
        Rig r = ReleasedGhost(WarsongGulch);
        using WorldRuntime world = r.World;
        var presence = new FixedPresence(BattlegroundStatus.WaitJoin); // died in the preparation room
        Assert.True(DeathSeams.Of(world).TryRegisterBattlegrounds(presence));

        Assert.False(r.Combat.TryReclaimCorpse(r.Player));
        Assert.False(r.Player.IsAlive);
        Assert.NotNull(r.Player.Combat.Corpse);

        presence.Status = BattlegroundStatus.InProgress;
        Assert.True(r.Combat.TryReclaimCorpse(r.Player));
        Assert.Equal(1000u, r.Player.Health);
    }
}
