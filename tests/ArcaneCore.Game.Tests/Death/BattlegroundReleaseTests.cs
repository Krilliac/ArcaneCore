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
/// Releasing the spirit in a battleground (vmangos Player::BuildPlayerRepop, Player.cpp:4586-4589): "Waiting to Resurrect" (2584) is cast
/// before the ghost form when the player is in a battleground; the battleground area does the cast through
/// <see cref="IBattlegroundPresence.OnSpiritReleased"/>.
/// </summary>
public sealed class BattlegroundReleaseTests
{
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

    private sealed class Recorder(List<string> log) : IBattlegroundPresence, IGhostForm
    {
        public BattlegroundStatus? MatchStatusOf(ObjectGuid player) => BattlegroundStatus.InProgress;

        public void OnSpiritReleased(Player player) => log.Add($"released {player.Guid.Counter}");

        public void Apply(Player player) => log.Add("ghost");

        public void Remove(Player player)
        {
        }
    }

    private static (WorldRuntime World, Map Map, Player Player) DeadPlayer(uint mapId)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        WorldMaps.Of(world).Load(Content);
        DeathHooks.Register(world, new DeathHooks(new DeathOptions(), new FixedDeathClock(1_700_000_000)));
        Map map = world.GetMap(mapId);
        map.Combat.Hooks = new TestCombatHooks();
        Player player = CombatTestKit.AddPlayer(world, 2, 10, 20, new FakeSession(2), mapId: mapId);
        world.RunTick(1);
        map.Combat.Kill(null, player);
        return (world, map, player);
    }

    [Fact]
    public void ReleasingTheSpirit_TellsTheBattlegrounds_BeforeTheGhostForm()
    {
        (WorldRuntime world, Map map, Player player) = DeadPlayer(WarsongGulch);
        using (world)
        {
            List<string> log = [];
            var recorder = new Recorder(log);
            Assert.True(DeathSeams.Of(world).TryRegisterBattlegrounds(recorder));
            Assert.True(DeathSeams.Of(world).TryRegisterGhostForm(recorder));

            Assert.True(map.Combat.RepopPlayer(player));

            Assert.Equal([$"released {player.Guid.Counter}", "ghost"], log);
        }
    }

    [Fact]
    public void ADefaultPresence_DoesNothingOnRelease()
    {
        (WorldRuntime world, Map map, Player player) = DeadPlayer(0);
        using (world)
        {
            Assert.True(DeathSeams.Of(world).TryRegisterBattlegrounds(new StatusOnly()));
            Assert.True(map.Combat.RepopPlayer(player));
            Assert.True((player.Flags & PlayerFlags.Ghost) != 0);
        }
    }

    private sealed class StatusOnly : IBattlegroundPresence
    {
        public BattlegroundStatus? MatchStatusOf(ObjectGuid player) => null;
    }
}
