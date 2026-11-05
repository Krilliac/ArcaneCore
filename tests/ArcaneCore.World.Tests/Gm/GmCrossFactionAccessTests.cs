using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Tests.Duel;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>
/// With GM mode on a game master inspects, group-invites and duels players of the other faction; with GM mode off the
/// faction rules are unchanged. Each permission has a GM-on case and, where the faction rule exists, a GM-off case. The duel spell itself
/// has no faction gate in the server, so its test only pins that a GM challenge across factions is requested.
/// </summary>
public sealed class GmCrossFactionAccessTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(400);

    private static Task SetGmModeAsync(WorldTestHost host, string name, bool on) => host.OnWorldAsync(() =>
    {
        Player player = host.World.FindOnlinePlayer(name)!;
        player.Flags = on ? player.Flags | PlayerFlags.Gm : player.Flags & ~PlayerFlags.Gm;
    });

    private static byte[] Guid(ulong guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.ToArray();
    }

    private static byte[] CString(string text) => [.. System.Text.Encoding.UTF8.GetBytes(text), 0];

    // ---------------------------------------------------------------------------------------------------- inspect

    private static async Task<(WorldTestHost Host, WorldTestClient A, ulong Enemy)> InspectWorldAsync()
    {
        WorldTestHost host = WorldTestHost.Start();
        WorldTestClient a = await host.EnterWorldAsync("GMXA", "Gmxinspa");
        await host.EnterWorldAsync("GMXB", "Gmxinspb", race: 2);
        await host.OnWorldAsync(() =>
        {
            foreach (ArcaneCore.Game.Maps.Map map in host.World.Maps)
            {
                map.Combat.Hooks = new CombatHooks(); // no faction templates in the test world: the default team and PvP-flag rules
            }

            MapCombat.UpdatePvp(host.World.FindOnlinePlayer("Gmxinspb")!, true);
        });
        ulong enemy = await host.PlayerStateAsync("Gmxinspb", p => p.Guid.Value);
        await host.PlaceAsync("Gmxinspa", 0, 0, 83.5f);
        await host.PlaceAsync("Gmxinspb", 5, 0, 83.5f);
        return (host, a, enemy);
    }

    [Fact]
    public async Task Inspect_OfAFlaggedEnemyFactionPlayer_IsRefusedWithGmModeOff()
    {
        (WorldTestHost host, WorldTestClient a, ulong enemy) = await InspectWorldAsync();
        await using WorldTestHost owner = host;
        await a.CollectAsync(Window);

        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, Guid(enemy));

        Assert.DoesNotContain(await a.CollectAsync(Window), p => p.Opcode == WorldOpcode.MsgInspectHonorStats);
    }

    [Fact]
    public async Task Inspect_OfAFlaggedEnemyFactionPlayer_IsAnsweredWithGmModeOn()
    {
        (WorldTestHost host, WorldTestClient a, ulong enemy) = await InspectWorldAsync();
        await using WorldTestHost owner = host;
        await SetGmModeAsync(host, "Gmxinspa", true);
        await a.CollectAsync(Window);

        await a.SendAsync(WorldOpcode.MsgInspectHonorStats, Guid(enemy));
        byte[] reply = await a.ReadUntilAsync(WorldOpcode.MsgInspectHonorStats);

        Assert.Equal(enemy, BinaryPrimitives.ReadUInt64LittleEndian(reply));
    }

    // ---------------------------------------------------------------------------------------------------- group invite

    private static async Task<(WorldTestHost Host, WorldTestClient A, WorldTestClient B)> GroupWorldAsync()
    {
        WorldTestHost host = WorldTestHost.Start();
        WorldTestClient a = await host.EnterWorldAsync("GMXGA", "Gmxgrpa");
        WorldTestClient b = await host.EnterWorldAsync("GMXGB", "Gmxgrpb", race: 2);
        await a.CollectAsync(Window);
        await b.CollectAsync(Window);
        return (host, a, b);
    }

    [Fact]
    public async Task GroupInvite_OfTheOtherFaction_IsRefusedAsWrongFactionWithGmModeOff()
    {
        (WorldTestHost host, WorldTestClient a, WorldTestClient b) = await GroupWorldAsync();
        await using WorldTestHost owner = host;

        await a.SendAsync(WorldOpcode.CmsgGroupInvite, CString("Gmxgrpb"));

        List<(WorldOpcode Opcode, byte[] Payload)> mine = await a.CollectAsync(Window);
        byte[] result = mine.First(p => p.Opcode == WorldOpcode.SmsgPartyCommandResult).Payload;
        Assert.Equal((uint)PartyResult.WrongFaction, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(result.Length - 4)));
        Assert.DoesNotContain(await b.CollectAsync(Window), p => p.Opcode == WorldOpcode.SmsgGroupInvite);
    }

    [Fact]
    public async Task GroupInvite_OfTheOtherFaction_ReachesThemWithGmModeOn()
    {
        (WorldTestHost host, WorldTestClient a, WorldTestClient b) = await GroupWorldAsync();
        await using WorldTestHost owner = host;
        await SetGmModeAsync(host, "Gmxgrpa", true);

        await a.SendAsync(WorldOpcode.CmsgGroupInvite, CString("Gmxgrpb"));

        Assert.Contains(await b.CollectAsync(Window), p => p.Opcode == WorldOpcode.SmsgGroupInvite);
    }

    // ---------------------------------------------------------------------------------------------------- duel

    /// <summary>The duel spell has no faction gate in the server, so a challenge across factions is requested with GM mode off as well: GM mode changes nothing here.</summary>
    [Fact]
    public async Task Duel_ChallengeOfTheOtherFaction_IsRequestedWithGmModeOff_AsBefore()
    {
        ManualTimeProvider clock = DuelWorldHost.NewClock();
        await using WorldTestHost host = DuelWorldHost.Start(clock);
        await using WorldTestClient a = await host.EnterWorldAsync("GMXOA", "Gmxdofa");
        await using WorldTestClient b = await host.EnterWorldAsync("GMXOB", "Gmxdofb", race: 2);
        ulong enemy = await host.PlayerStateAsync("Gmxdofb", p => p.Guid.Value);
        await host.PlaceAsync("Gmxdofa", 0, 0, 83.5f);
        await host.PlaceAsync("Gmxdofb", 4, 0, 83.5f);
        await a.CollectAsync(Window);
        await b.CollectAsync(Window);

        await a.SendAsync(WorldOpcode.CmsgCastSpell, DuelWorldHost.CastPayload(DuelWorldHost.DuelSpell, enemy));

        Assert.Contains(await b.CollectAsync(Window), p => p.Opcode == WorldOpcode.SmsgDuelRequested);
        Assert.NotEqual(0ul, await host.PlayerStateAsync("Gmxdofa", p => p.DuelArbiter));
    }

    [Fact]
    public async Task Duel_ChallengeOfTheOtherFaction_IsRequestedWithGmModeOn()
    {
        ManualTimeProvider clock = DuelWorldHost.NewClock();
        await using WorldTestHost host = DuelWorldHost.Start(clock);
        await using WorldTestClient a = await host.EnterWorldAsync("GMXDA", "Gmxdela");
        await using WorldTestClient b = await host.EnterWorldAsync("GMXDB", "Gmxdelb", race: 2);
        ulong enemy = await host.PlayerStateAsync("Gmxdelb", p => p.Guid.Value);
        await host.PlaceAsync("Gmxdela", 0, 0, 83.5f);
        await host.PlaceAsync("Gmxdelb", 4, 0, 83.5f);
        await SetGmModeAsync(host, "Gmxdela", true);
        await a.CollectAsync(Window);
        await b.CollectAsync(Window);

        await a.SendAsync(WorldOpcode.CmsgCastSpell, DuelWorldHost.CastPayload(DuelWorldHost.DuelSpell, enemy));

        Assert.Contains(await b.CollectAsync(Window), p => p.Opcode == WorldOpcode.SmsgDuelRequested);
        Assert.NotEqual(0ul, await host.PlayerStateAsync("Gmxdela", p => p.DuelArbiter));
    }
}
