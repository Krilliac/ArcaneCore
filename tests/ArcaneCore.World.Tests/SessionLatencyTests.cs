using ArcaneCore.Game;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Playerbots.Combat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>The two session call sites of the latency histograms: in-world packet handlers by opcode and login to in-world, each against a registry private to the host.</summary>
public sealed class SessionLatencyTests
{
    [Fact]
    public async Task EveryCompletedLogin_AddsOneLoginToWorldSample_AndARefusedLoginAddsNone()
    {
        var latency = new LatencyRegistry();
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton(latency));

        // A login for someone else's character is refused before the player is in the world.
        byte[] key = await host.AddAccountAsync("THIEF");
        await using WorldTestClient thief = await host.ConnectAsync();
        await thief.AuthenticateAsync("THIEF", key);
        var guid = new PacketWriter(8);
        guid.WriteUInt64(1);
        await thief.SendAsync(WorldOpcode.CmsgPlayerLogin, guid.ToArray());
        Assert.Equal(WorldOpcode.SmsgCharacterLoginFailed, (await thief.ReadAsync()).Opcode);
        Assert.Equal(0, latency.Get(LatencyNames.LoginToWorld).Count);

        await using WorldTestClient first = await host.EnterWorldAsync("LOGIN1", "Firstone");
        Assert.Equal(1, latency.Get(LatencyNames.LoginToWorld).Count);
        await using WorldTestClient second = await host.EnterWorldAsync("LOGIN2", "Secondone");
        Assert.Equal(2, latency.Get(LatencyNames.LoginToWorld).Count);
        Assert.True(latency.Get(LatencyNames.LoginToWorld).Snapshot().Max > 0);
    }

    [Fact]
    public async Task EveryInWorldPacketHandlerRun_AddsOneSampleToItsOpcodesHistogram()
    {
        var latency = new LatencyRegistry();
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton(latency));
        await using WorldTestClient client = await host.EnterWorldAsync("PACKETER", "Packeter");
        await client.CollectAsync();
        string name = LatencyNames.PacketHandlerPrefix + WorldOpcodeNames.GetName(WorldOpcode.CmsgMessagechat);
        long before = latency.Get(name).Count;

        await client.SendChatAsync(ChatType.Say, Language.Common, ".server motd");
        await WorldTestHost.WaitForAsync(() => latency.Get(name).Count > before, "the chat handler's latency sample");
        await client.CollectAsync();

        Assert.Equal(before + 1, latency.Get(name).Count);
        Assert.Contains(latency.Histograms(), pair => pair.Key == name);
    }

    [Fact]
    public async Task APlayerbotSession_FeedsNeitherTheLoginHistogramNorThePacketHistogram_ButARealPlayerDoes()
    {
        var latency = new LatencyRegistry();
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton(latency));
        string packetName = LatencyNames.PacketHandlerPrefix + WorldOpcodeNames.GetName(WorldOpcode.CmsgMessagechat);

        WorldSession bot = await CombatTestSessions.EnterAsync(host, "BOTACCT", "Botone");
        Assert.True(bot.IsPlayerbot);
        Assert.Equal(0, latency.Get(LatencyNames.LoginToWorld).Count); // the bot's login completed and recorded nothing
        bot.RecordHandlerLatency(WorldOpcode.CmsgMessagechat, System.Diagnostics.Stopwatch.GetTimestamp());
        Assert.Equal(0, latency.Get(packetName).Count);

        await using WorldTestClient player = await host.EnterWorldAsync("REALPLAYER", "Realone");
        Assert.Equal(1, latency.Get(LatencyNames.LoginToWorld).Count);
        WorldSession session = host.Registry.Sessions.Single(s => s.Player?.Name == "Realone");
        session.RecordHandlerLatency(WorldOpcode.CmsgMessagechat, System.Diagnostics.Stopwatch.GetTimestamp());
        Assert.Equal(1, latency.Get(packetName).Count);
    }
}
