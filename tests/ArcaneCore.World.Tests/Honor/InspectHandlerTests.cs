using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Honor;
using Xunit;

namespace ArcaneCore.World.Tests.Honor;

/// <summary>
/// CMSG_INSPECT and MSG_INSPECT_HONOR_STATS over the real socket (vmangos MiscHandler.cpp:943-1036): the target must be a player
/// within 10 yards that the inspector could not attack. A refused request gets no reply at all, so each refusal test follows it with
/// an allowed request and checks that the first answer to arrive is the allowed one.
/// </summary>
public sealed class InspectHandlerTests
{
    private static byte[] Guid(ulong guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.ToArray();
    }

    private static async Task<(WorldTestHost Host, WorldTestClient A, ulong B, ulong C)> ThreePlayersAsync()
    {
        WorldTestHost host = WorldTestHost.Start();
        WorldTestClient a = await host.EnterWorldAsync("INSA", "Inspa");
        WorldTestClient b = await host.EnterWorldAsync("INSB", "Inspb");
        WorldTestClient c = await host.EnterWorldAsync("INSC", "Inspc", race: 2);
        // The test world has no faction templates, so the faction hooks see no hostility; the default hooks apply the team and PvP-flag rules.
        await host.OnWorldAsync(() =>
        {
            foreach (ArcaneCore.Game.Maps.Map map in host.World.Maps)
            {
                map.Combat.Hooks = new CombatHooks();
            }
        });
        _ = b;
        _ = c;
        ulong bGuid = await host.PlayerStateAsync("Inspb", p => p.Guid.Value);
        ulong cGuid = await host.PlayerStateAsync("Inspc", p => p.Guid.Value);
        foreach (string name in new[] { "Inspa", "Inspb", "Inspc" })
        {
            await host.PlaceAsync(name, 0, 0, 83.5f);
        }

        return (host, a, bGuid, cGuid);
    }

    [Fact]
    public void The_inspect_distance_is_ten_yards()
    {
        Assert.Equal(10f, InspectHandlers.InspectDistance);
    }

    [Fact]
    public async Task A_friendly_player_in_range_answers_the_honor_stats_with_fifty_bytes_naming_the_target()
    {
        (WorldTestHost host, WorldTestClient a, ulong b, _) = await ThreePlayersAsync();
        await using WorldTestHost owner = host;
        await host.OnWorldAsync(() =>
        {
            Player target = host.World.FindOnlinePlayer("Inspb")!;
            target.SetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills, 77);
            target.SetByte(UpdateFields.PlayerFieldBytes, 3, 9);
        });

        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, Guid(b));
        byte[] reply = await a.ReadUntilAsync(WorldOpcode.MsgInspectHonorStats);

        Assert.Equal(50, reply.Length);
        Assert.Equal(b, BinaryPrimitives.ReadUInt64LittleEndian(reply));
        Assert.Equal(9, reply[8]);                                                 // highest rank
        Assert.Equal(77u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(25))); // lifetime honorable kills
    }

    [Fact]
    public async Task A_player_farther_than_ten_yards_gets_no_answer()
    {
        (WorldTestHost host, WorldTestClient a, ulong b, ulong c) = await ThreePlayersAsync();
        await using WorldTestHost owner = host;
        await host.PlaceAsync("Inspb", 40, 0, 83.5f);

        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, Guid(b));   // out of range: silence
        await host.PlaceAsync("Inspc", 5, 0, 83.5f);
        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, Guid(c));   // an unflagged enemy in range: answered
        byte[] reply = await a.ReadUntilAsync(WorldOpcode.MsgInspectHonorStats);

        Assert.Equal(c, BinaryPrimitives.ReadUInt64LittleEndian(reply));
    }

    [Fact]
    public async Task A_pvp_flagged_enemy_cannot_be_inspected_but_a_flagged_friend_can()
    {
        (WorldTestHost host, WorldTestClient a, ulong b, ulong c) = await ThreePlayersAsync();
        await using WorldTestHost owner = host;
        await host.OnWorldAsync(() =>
        {
            MapCombat.UpdatePvp(host.World.FindOnlinePlayer("Inspc")!, true); // a real flag (a bare UnitFlags.Pvp is dropped by the next tick: no timer)
            MapCombat.UpdatePvp(host.World.FindOnlinePlayer("Inspb")!, true);
        });

        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, Guid(c));   // attackable enemy: silence
        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, Guid(b));   // a friend, flagged or not: answered
        byte[] reply = await a.ReadUntilAsync(WorldOpcode.MsgInspectHonorStats);

        Assert.Equal(b, BinaryPrimitives.ReadUInt64LittleEndian(reply));
    }

    [Fact]
    public async Task An_unknown_guid_or_a_short_packet_is_ignored()
    {
        (WorldTestHost host, WorldTestClient a, ulong b, _) = await ThreePlayersAsync();
        await using WorldTestHost owner = host;
        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, Guid(0xDEADBEEF));
        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, [1, 2, 3]);
        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, Guid(b));
        byte[] reply = await a.ReadUntilAsync(WorldOpcode.MsgInspectHonorStats);
        Assert.Equal(b, BinaryPrimitives.ReadUInt64LittleEndian(reply));
    }

    [Fact]
    public async Task Inspect_selects_the_target_and_answers_with_its_guid_under_the_same_rules()
    {
        (WorldTestHost host, WorldTestClient a, ulong b, ulong c) = await ThreePlayersAsync();
        await using WorldTestHost owner = host;
        await host.OnWorldAsync(() => MapCombat.UpdatePvp(host.World.FindOnlinePlayer("Inspc")!, true));

        await a.SendAsync(WorldOpcode.CmsgInspect, Guid(c));            // enemy, flagged: no answer, but it is still selected
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Inspa")!.Selection.Value == c, "the target to be selected");
        await a.SendAsync(WorldOpcode.CmsgInspect, Guid(b));
        byte[] reply = await a.ReadUntilAsync(WorldOpcode.SmsgInspect);

        Assert.Equal(8, reply.Length);
        Assert.Equal(b, BinaryPrimitives.ReadUInt64LittleEndian(reply));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Inspa")!.Selection.Value == b, "the second target to be selected");
    }
}
