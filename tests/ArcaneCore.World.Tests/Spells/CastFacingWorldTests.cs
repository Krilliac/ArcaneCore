using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Creatures;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// .cast back and .cast target turn the selected unit to the invoker before it casts (vmangos Unit::SetFacingToObject, Unit.cpp:2796-2804:
/// only a stopped unit turns; Unit::SetFacingTo, Unit.cpp:2785-2794: the orientation and a facing spline for the observers), for a creature and for a player.
/// </summary>
public sealed class CastFacingWorldTests
{
    private const uint WolfEntry = 20102;
    private const uint WolfSpawn = 88201;

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);

    private sealed record Scene(WorldTestHost Host, CreatureTestContext Creatures);

    private static Scene StartWithAWolf()
    {
        var wolf = new CreatureTemplate
        {
            Entry = WolfEntry, Name = "Facing Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35, CreatureType = 1, Family = 1,
            MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        // The human start is (-8949.95, -132.49, 83.53): the wolf is 10 yards east of it, facing 0 (east), so the invoker is behind it.
        var creatures = new CreatureTestContext(new CreatureContent(
            [wolf], [new CreatureSpawn { Guid = WolfSpawn, Entry = WolfEntry, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f }], [], [], []));
        CreatureTestStore.Current.Value = creatures;
        try
        {
            return new Scene(WorldTestHost.Start(), creatures);
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }

    private static Creature Wolf(Scene scene) => scene.Creatures.Feature!.FindSystem(0)!.Creatures.Single(c => c.Spawn?.Guid == WolfSpawn);

    private static (ulong Guid, byte Type, float Angle) ParseMonsterMove(byte[] payload)
    {
        byte mask = payload[0];
        ulong guid = 0;
        int offset = 1;
        for (int i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                guid |= (ulong)payload[offset++] << (8 * i);
            }
        }

        offset += 12 + 4; // the start point, the spline id
        byte type = payload[offset++];
        return (guid, type, type == (byte)MonsterMoveType.FacingAngle ? BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(offset)) : 0f);
    }

    private static List<(ulong Guid, byte Type, float Angle)> FacingMoves(IEnumerable<(WorldOpcode Opcode, byte[] Payload)> packets, ulong guid)
        => [.. packets.Where(p => p.Opcode == WorldOpcode.SmsgMonsterMove).Select(p => ParseMonsterMove(p.Payload))
            .Where(m => m.Guid == guid && m.Type == (byte)MonsterMoveType.FacingAngle)];

    private static async Task<List<(WorldOpcode Opcode, byte[] Payload)>> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectFromAsync(WorldOpcode.SmsgMessagechat, Quiet);
    }

    private static float Bearing(WorldObject from, WorldObject to) => Creature.NormalizeOrientation(MathF.Atan2(to.Y - from.Y, to.X - from.X));

    [Theory]
    [InlineData("cast back")]
    [InlineData("cast target")]
    public async Task ACommandThatMakesAStandingCreatureCast_TurnsItToTheInvoker_AndTellsTheObservers(string subcommand)
    {
        Scene scene = StartWithAWolf();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await host.EnterWorldAsync("FWGM", "Fwgm", AccountSecurity.Administrator);
        await gm.CollectAsync(Quiet);
        (ulong wolfGuid, float expected, float before) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Fwgm")!;
            Creature wolf = Wolf(scene);
            player.Selection = wolf.Guid;
            wolf.Combat.Victim = player; // the victim of .cast target is the invoker
            Assert.False(wolf.IsMoving);
            return (wolf.Guid.Value, Bearing(wolf, player), wolf.Orientation);
        });
        Assert.True(MathF.Abs(before - expected) > 1f, "the setup must start the wolf facing away from the invoker");

        List<(WorldOpcode Opcode, byte[] Payload)> packets = await RunAsync(gm, $".{subcommand} {Heal} triggered");

        Assert.Equal(expected, await host.OnWorldAsync(() => Wolf(scene).Orientation), 0.001);
        (ulong _, byte _, float angle) = Assert.Single(FacingMoves(packets, wolfGuid));
        Assert.Equal(expected, angle, 0.001); // SMSG_MONSTER_MOVE with the facing-angle type, to the observer
    }

    [Fact]
    public async Task ACreatureThatIsMoving_DoesNotTurn_WhenItIsMadeToCast()
    {
        Scene scene = StartWithAWolf();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await host.EnterWorldAsync("FMGM", "Fmgm", AccountSecurity.Administrator);
        await gm.CollectAsync(Quiet);
        (ulong wolfGuid, float bearing) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Fmgm")!;
            Creature wolf = Wolf(scene);
            player.Selection = wolf.Guid;
            scene.Creatures.Feature!.FindSystem(0)!.MovePath(wolf, [new Vector3(wolf.X + 40f, wolf.Y, wolf.Z)], run: false, SplineFacing.None); // a long walk east
            Assert.True(wolf.IsMoving);
            return (wolf.Guid.Value, Bearing(wolf, player));
        });
        await gm.CollectAsync(Quiet); // the walk's own SMSG_MONSTER_MOVE

        List<(WorldOpcode Opcode, byte[] Payload)> packets = await RunAsync(gm, $".cast back {Heal} triggered");

        Assert.Empty(FacingMoves(packets, wolfGuid));
        float after = await host.OnWorldAsync(() => Wolf(scene).Orientation);
        Assert.True(MathF.Abs(after - bearing) > 1f, "a walking creature keeps its heading (east), not the bearing to the invoker");
    }

    [Theory]
    [InlineData("cast back", false)]
    [InlineData("cast target", false)]
    [InlineData("cast back", true)]
    public async Task APlayerThatStandsStill_TurnsToTheInvoker_ButOneThatIsMovingDoesNot(string subcommand, bool moving)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("FPGM", "Fpgm", AccountSecurity.Administrator);
        await using WorldTestClient caster = await host.EnterWorldAsync("FPCAST", "Fpcast");
        await gm.CollectAsync(Quiet);
        await caster.CollectAsync(Quiet);
        (ulong casterGuid, float expected) = await host.OnWorldAsync(() =>
        {
            Player invoker = host.World.FindOnlinePlayer("Fpgm")!;
            Player other = host.World.FindOnlinePlayer("Fpcast")!;
            other.SetPosition(invoker.X + 10f, invoker.Y, invoker.Z, 1f); // 10 yards east of the invoker, facing 1 radian: the bearing to the invoker is pi
            if (moving)
            {
                MovementInfo info = other.Movement;
                info.Flags |= MovementFlags.Forward;
                other.ApplyMovement(in info, info.Time);
            }

            invoker.Selection = other.Guid;
            other.Combat.Victim = invoker;
            return (other.Guid.Value, Bearing(other, invoker));
        });

        await RunAsync(gm, $".{subcommand} {Heal} triggered");
        List<(WorldOpcode Opcode, byte[] Payload)> seenByTheCaster = await caster.CollectAsync(Quiet);

        float after = await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Fpcast")!.Orientation);
        List<(ulong Guid, byte Type, float Angle)> turns = FacingMoves(seenByTheCaster, casterGuid);
        if (moving)
        {
            Assert.Equal(1f, after);
            Assert.Empty(turns);
        }
        else
        {
            Assert.Equal(expected, after, 0.001);
            Assert.Equal(expected, Assert.Single(turns).Angle, 0.001);
        }
    }
}
