using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Pets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// Movement paths outside HandleMovement must not store a client position the anticheat never judged (docs/areas/anticheat.md). A block
/// stored unjudged is laundered: on the next real movement packet MovementAntiCheat.CheckServerPosition sees the server position moved,
/// takes it for a server relocation and re-baselines, so the jump is never scored.
/// <list type="bullet">
/// <item>CMSG_MOVE_NOT_ACTIVE_MOVER naming the player itself (vmangos HandleMoveNotActiveMoverOpcode, MovementHandler.cpp:893-972 at
/// vmangos d9173eca: the reject window :919-920, the spline (taxi) test :933-937 and the cheat tests :946-952 all run before
/// HandleMoverRelocation :955).</item>
/// <item>A possessed unit moved by its controller (vmangos HandleMovementOpcodes :318-325 runs the cheat tests for a player mover; a
/// creature mover gets none there, so for a creature the server only scores, it never refuses).</item>
/// </list>
/// </summary>
public sealed class MovementBypassTests
{
    private const int NoBaselineGapMs = 600_000;
    private const float Blink = 80f;

    private static AntiCheatFeature FeatureOf(WorldTestHost host) => host.WorldServices.GetRequiredService<AntiCheatFeature>();

    private static async Task<ConcurrentQueue<(string Player, AntiCheatFinding Finding)>> ArmAsync(WorldTestHost host)
    {
        // No baseline gap: a pause under full-suite load must not turn the blink into a fresh, unscored baseline (AntiCheatWorldTests.ApplyAsync).
        await host.OnWorldAsync(() => FeatureOf(host).ApplyOptions(new AntiCheatOptions { BaselineGapMs = NoBaselineGapMs }));
        var seen = new ConcurrentQueue<(string, AntiCheatFinding)>();
        FeatureOf(host).FindingRecorded += (player, finding, _, _) => seen.Enqueue((player.Name, finding));
        return seen;
    }

    private static byte[] Block(uint time, float x, float y, float z, MovementFlags flags = MovementFlags.Forward)
    {
        var writer = new PacketWriter(48);
        new MovementInfo { Flags = flags, Time = time, X = x, Y = y, Z = z }.Write(writer);
        return writer.ToArray();
    }

    private static byte[] NotActiveMover(ulong guid, uint time, float x, float y, float z)
    {
        var writer = new PacketWriter(56);
        writer.WriteUInt64(guid);
        new MovementInfo { Flags = MovementFlags.None, Time = time, X = x, Y = y, Z = z }.Write(writer);
        return writer.ToArray();
    }

    private static byte[] Guid64(ulong guid) => BitConverter.GetBytes(guid);

    // --- B2: CMSG_MOVE_NOT_ACTIVE_MOVER for the player itself ----------------------------------------------------------------

    [Fact]
    public async Task ANotActiveMoverNamingThePlayerItself_IsJudgedByTheAntiCheat_EveryTimeItIsReArmed()
    {
        await using var host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host);
        await using WorldTestClient client = await host.EnterWorldAsync("SELFMOVER", "Selfmover");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Selfmover");
        (ulong guid, float x, float y, float z) = await host.OnWorldAsync(() => (player.Guid.Value, player.X, player.Y, player.Z));

        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Empty(seen);

        // The client gives up "itself" with a block 80 yards away, then moves on from there; SET_ACTIVE_MOVER(self) re-arms it.
        for (int round = 1; round <= 2; round++)
        {
            float from = x + 0.7f + ((round - 1) * Blink);
            await Task.Delay(100);
            time += 100;
            await client.SendAsync(WorldOpcode.CmsgMoveNotActiveMover, NotActiveMover(guid, time, from + Blink, y, z));
            await Task.Delay(100);
            time += 100;
            await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, from + Blink, y, z));
            await client.SendAsync(WorldOpcode.CmsgSetActiveMover, Guid64(guid));
            await client.CollectThroughBarrierAsync();

            // RED on origin/main: the block was stored unjudged and the heartbeat after it was taken as a server relocation, so nothing was scored.
            Assert.True(seen.Count(s => s.Player == "Selfmover" && s.Finding.Type == AntiCheatViolation.Teleport) >= round,
                $"round {round}: the 80-yard NOT_ACTIVE_MOVER block was not scored as a teleport ({seen.Count} findings: {string.Join(", ", seen.Select(s => s.Finding.Type))})");
        }
    }

    [Fact]
    public async Task ANotActiveMoverNamingThePlayerItself_OnATaxi_IsIgnored()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("TAXIMOVER", "Taximover");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Taximover");
        (ulong guid, float x, float y, float z) = await host.OnWorldAsync(() =>
        {
            player.UnitFlags |= UnitFlags.TaxiFlight; // the flight spline moves the player (vmangos: movespline not Finalized, MovementHandler.cpp:936-937)
            return (player.Guid.Value, player.X, player.Y, player.Z);
        });

        await client.SendAsync(WorldOpcode.CmsgMoveNotActiveMover, NotActiveMover(guid, 100_000, x + Blink, y, z));
        await client.CollectThroughBarrierAsync();

        // RED on origin/main: the player was relocated 80 yards off the flight path.
        Assert.Equal(x, await host.OnWorldAsync(() => player.X));
    }

    [Fact]
    public async Task ANotActiveMoverNamingThePlayerItself_InsideTheRejectWindow_IsIgnored()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("REJECTMOVER", "Rejectmover");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Rejectmover");
        (ulong guid, float x, float y, float z) = await host.OnWorldAsync(() =>
        {
            // vmangos RejectMovementPacketsFor / "Do not accept packets sent before this time" (MovementHandler.cpp:919-920, :1035-1040).
            player.Locomotion.RejectMovementPacketsFor(unchecked((uint)Clock.Milliseconds()), 60_000);
            return (player.Guid.Value, player.X, player.Y, player.Z);
        });

        await client.SendAsync(WorldOpcode.CmsgMoveNotActiveMover, NotActiveMover(guid, 100_000, x + Blink, y, z));
        await client.CollectThroughBarrierAsync();

        // RED on origin/main: the block was stored inside the window that protects server-set movement flags.
        Assert.Equal(x, await host.OnWorldAsync(() => player.X));
    }

    // --- B3: a possessed unit moved by its controller ------------------------------------------------------------------------

    [Fact]
    public async Task APossessedCreature_BlinkedByItsController_IsScoredAgainstTheController()
    {
        await using var host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host);
        await using WorldTestClient client = await host.EnterWorldAsync("POSSESSOR", "Possessor");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Possessor");
        (ulong guid, float x, float y, float z) = await host.OnWorldAsync(() =>
        {
            var template = new CreatureTemplate { Entry = 991_701, Name = "Possessed mob", MinLevel = 1, MaxLevel = 1, DisplayIds = [903], Faction = 14, AIName = "NullAI" };
            Creature mob = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!)
                .SpawnTemporary(template, player.X + 5, player.Y, player.Z, 0);
            host.WorldServices.GetRequiredService<PetsFeature>().Service.Charms.ModPossess(player, mob, apply: true, AuraRemoveMode.Default, spell: null);
            Assert.Same(mob, player.GetMover());
            return (mob.Guid.Value, mob.X, mob.Y, mob.Z);
        });

        await client.SendAsync(WorldOpcode.CmsgSetActiveMover, Guid64(guid));
        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Equal(x + 0.7f, await host.OnWorldAsync(() => ((Creature)player.GetConfirmedMover()!).X));
        Assert.Empty(seen);

        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f + Blink, y, z));
        await client.CollectThroughBarrierAsync();

        // The block is still stored (vmangos refuses nothing for a creature mover); it is scored, against the controller.
        Assert.Equal(x + 0.7f + Blink, await host.OnWorldAsync(() => ((Creature)player.GetConfirmedMover()!).X));
        // RED on origin/main: a possessed unit's movement reached no anticheat check at all.
        Assert.Contains(seen, s => s.Player == "Possessor" && s.Finding.Type == AntiCheatViolation.Teleport);
    }

    [Fact]
    public async Task APossessedPlayer_BlinkedByItsController_IsScoredAgainstTheController_NotTheVictim()
    {
        await using var host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host);
        await using WorldTestClient client = await host.EnterWorldAsync("MINDCTRL", "Mindctrl");
        await using WorldTestClient victimClient = await host.EnterWorldAsync("MINDVICT", "Mindvict");
        await client.CollectAsync();
        await victimClient.CollectAsync();
        Player player = await host.PlayerAsync("Mindctrl");
        Player victim = await host.PlayerAsync("Mindvict");
        (ulong guid, float x, float y, float z) = await host.OnWorldAsync(() =>
        {
            host.WorldServices.GetRequiredService<PetsFeature>().Service.Charms.ModPossess(player, victim, apply: true, AuraRemoveMode.Default, spell: null);
            Assert.Same(victim, player.GetMover());
            return (victim.Guid.Value, victim.X, victim.Y, victim.Z);
        });

        await client.SendAsync(WorldOpcode.CmsgSetActiveMover, Guid64(guid));
        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Equal(x + 0.7f, await host.OnWorldAsync(() => victim.X));
        Assert.Empty(seen);

        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f + Blink, y, z));
        await client.CollectThroughBarrierAsync();

        Assert.Equal(x + 0.7f + Blink, await host.OnWorldAsync(() => victim.X));
        // RED on origin/main: nothing scored. vmangos runs the controller's cheat tests on a possessed player (MovementHandler.cpp:318-325).
        Assert.Contains(seen, s => s.Player == "Mindctrl" && s.Finding.Type == AntiCheatViolation.Teleport);
        Assert.DoesNotContain(seen, s => s.Player == "Mindvict");
    }
}
