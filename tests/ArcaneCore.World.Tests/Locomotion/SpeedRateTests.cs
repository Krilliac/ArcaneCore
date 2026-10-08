using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>
/// The configured player speed rates (<c>Locomotion:Player*SpeedRate</c>, non-retail when not 1; the MaNGOS Zero fork's Movement.*SpeedRate)
/// end to end: the create block of a loading character, the re-send of <c>.reload config</c> and <c>.movement set</c>, and the turn rate ack.
/// </summary>
public sealed class SpeedRateTests
{
    private static Task SayAsync(WorldTestClient client, string text) => client.SendChatAsync(ChatType.Say, Language.Common, text);

    private static Task<LocomotionOptions> OptionsAsync(WorldTestHost host)
        => host.OnWorldAsync(() => LocomotionEnvironment.RegisteredOptions(host.World) ?? throw new InvalidOperationException("no locomotion options"));

    private static byte[] Floats(params float[] values)
    {
        var writer = new PacketWriter(values.Length * 4);
        foreach (float value in values)
        {
            writer.WriteSingle(value);
        }

        return writer.ToArray();
    }

    private static float ForcedSpeed(byte[] order)
    {
        var reader = new PacketReader(order.AsSpan(order.Length - 4));
        return reader.ReadSingle();
    }

    [Fact]
    public async Task ALoadingCharacter_GetsTheConfiguredSpeeds_InItsCreateBlock_WithoutForceOrders()
    {
        await using var host = WorldTestHost.Start();
        LocomotionOptions options = await OptionsAsync(host);
        await host.OnWorldAsync(() =>
        {
            options.PlayerSpeedRate = 2.0f;
            options.PlayerRunSpeedRate = 1.5f;
            options.PlayerTurnRate = 0.5f;
        });

        await using WorldTestClient client = await host.EnterWorldAsync("FAST", "Fast");
        byte[] self = client.LoginPacket(WorldOpcode.SmsgUpdateObject);
        List<(WorldOpcode Opcode, byte[] Payload)> after = await client.CollectAsync();
        Player player = await host.PlayerAsync("Fast");

        // walk, run, run back, swim, swim back (x2 overall, run x1.5 on top), then the turn rate (its own x0.5).
        byte[] speeds = Floats(2.5f * 2, 7.0f * 3, 4.5f * 2, 4.722222f * 2, 2.5f * 2, 3.141594f * 0.5f);
        Assert.True(self.AsSpan().IndexOf(speeds) >= 0, "the self create block carries the configured speeds");
        Assert.Equal(21.0f, await host.OnWorldAsync(() => player.RunSpeed));
        Assert.DoesNotContain(after, p => p.Opcode is WorldOpcode.SmsgForceRunSpeedChange or WorldOpcode.SmsgForceWalkSpeedChange or WorldOpcode.SmsgForceTurnRateChange);
    }

    [Fact]
    public async Task RetailRates_LeaveTheBaseSpeeds()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PLAIN", "Plain");
        byte[] self = client.LoginPacket(WorldOpcode.SmsgUpdateObject);

        Assert.True(self.AsSpan().IndexOf(Floats(2.5f, 7.0f, 4.5f, 4.722222f, 2.5f, 3.141594f)) >= 0);
    }

    [Fact]
    public async Task ReloadConfig_AppliesNewRates_AndReSendsEveryOnlinePlayersSpeeds()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await using WorldTestClient other = await host.EnterWorldAsync("OTHER", "Other");
        await admin.CollectAsync();
        await other.CollectAsync();
        host.WorldServices.GetRequiredService<ConfigurationManager>().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Locomotion:PlayerRunSpeedRate"] = "2",
            ["Locomotion:PlayerTurnRate"] = "1.5",
        });

        await SayAsync(admin, ".reload config");

        Assert.Equal(7.0f * 2, ForcedSpeed(await other.ReadUntilAsync(WorldOpcode.SmsgForceRunSpeedChange)));
        Assert.Equal(3.141594f * 1.5f, ForcedSpeed(await other.ReadUntilAsync(WorldOpcode.SmsgForceTurnRateChange)));
        Assert.Equal(7.0f * 2, ForcedSpeed(await admin.ReadUntilAsync(WorldOpcode.SmsgForceRunSpeedChange)));
        LocomotionOptions options = await OptionsAsync(host);
        Assert.Equal(2.0f, options.PlayerRunSpeedRate);
        Assert.Equal(1.5f, options.PlayerTurnRate);
        Player player = await host.PlayerAsync("Other");
        Assert.Equal(3.141594f * 1.5f, await host.OnWorldAsync(() => player.TurnRate));
        Assert.Equal(7.0f, await host.OnWorldAsync(() => player.RunSpeed)); // the run speed waits for the ack
        Assert.DoesNotContain(await other.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgForceWalkSpeedChange); // unchanged types are not re-sent
    }

    [Fact]
    public async Task ReloadConfig_ClampsAnOutOfRangeRate()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        host.WorldServices.GetRequiredService<ConfigurationManager>().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Locomotion:PlayerSwimSpeedRate"] = "50",
        });

        await SayAsync(admin, ".reload config");

        Assert.Equal(4.722222f * 10, ForcedSpeed(await admin.ReadUntilAsync(WorldOpcode.SmsgForceSwimSpeedChange)));
        Assert.Equal(10.0f, (await OptionsAsync(host)).PlayerSwimSpeedRate);
    }

    [Fact]
    public async Task MovementSet_ChangesTheRate_ForEveryOnlinePlayer()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await using WorldTestClient other = await host.EnterWorldAsync("OTHER", "Other");
        await admin.CollectAsync();
        await other.CollectAsync();

        await SayAsync(admin, ".movement set swimback 3");

        Assert.Equal(2.5f * 3, ForcedSpeed(await other.ReadUntilAsync(WorldOpcode.SmsgForceSwimBackSpeedChange)));
        string reply = (await admin.ReadChatAsync()).Text;
        Assert.StartsWith("Movement: swimback 1 -> 3", reply);
        Assert.Contains("2 online player(s)", reply);
        Assert.Equal(3.0f, (await OptionsAsync(host)).PlayerSwimBackSpeedRate);

        await SayAsync(admin, ".movement rates");
        Assert.StartsWith("Movement rates: speedrate 1 x (run 1, runback 1, swim 1, swimback 3, walk 1), turn 1.", (await admin.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task MovementSet_RefusesAnOutOfRangeValue_AndChangesNothing()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await SayAsync(admin, ".movement set run 11");

        Assert.Contains("out of range", (await admin.ReadChatAsync()).Text);
        Assert.Equal(1.0f, (await OptionsAsync(host)).PlayerRunSpeedRate);
    }

    [Fact]
    public async Task MovementSet_IsNotAvailableBelowAdministrator()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await SayAsync(gm, ".movement set run 2");

        Assert.Equal("This command is not available to you.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(1.0f, (await OptionsAsync(host)).PlayerRunSpeedRate);
    }

    [Fact]
    public async Task TheTurnRateAck_IsRelayedToTheObservers()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient mover = await host.EnterWorldAsync("MOVER", "Mover");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await mover.CollectAsync();
        await watcher.CollectAsync();
        Player player = await host.PlayerAsync("Mover");
        float turn = 3.141594f * 2;

        await host.OnWorldAsync(() =>
        {
            player.Locomotion.ConfiguredSpeedRates = PlayerSpeedRates.Retail with { Turn = 2.0f };
            UnitSpeed.SetTurnRate(player, 1.0f);
        });
        Assert.Equal(ForcedSpeed(await mover.ReadUntilAsync(WorldOpcode.SmsgForceTurnRateChange)), turn);

        var info = new MovementInfo { Flags = MovementFlags.None, Time = 500, X = -8949.95f, Y = -132.493f, Z = 83.5312f };
        var ack = new PacketWriter(56);
        ack.WriteUInt64(player.Guid.Value);
        ack.WriteUInt32(0);
        info.Write(ack);
        ack.WriteSingle(turn);
        await mover.SendAsync(WorldOpcode.CmsgForceTurnRateChangeAck, ack.ToArray());

        byte[] relayed = await watcher.ReadUntilAsync(WorldOpcode.MsgMoveSetTurnRate);
        Assert.Equal(turn, ForcedSpeed(relayed));
    }
}
