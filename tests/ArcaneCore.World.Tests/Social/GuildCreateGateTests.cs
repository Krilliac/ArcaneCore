using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// CMSG_GUILD_CREATE is honoured by default as in vmangos and ignored with World:Guild:AllowClientGuildCreate=false;
/// vmangos disconnects a name over 24 characters
/// (GuildHandler.cpp:47-72).
/// </summary>
public sealed class GuildCreateGateTests
{
    private static async Task<(WorldTestHost Host, WorldTestClient Client, SocialFeature Feature)> StartAsync()
    {
        var host = WorldTestHost.Start();
        WorldTestClient client = await host.EnterWorldAsync("FOUNDER", "Founder");
        SocialFeature feature = await host.PlayerStateAsync("Founder", p => ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>());
        await feature.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        await client.CollectAsync();
        return (host, client, feature);
    }

    private static Task SendCreateAsync(WorldTestClient client, string name)
    {
        var writer = new PacketWriter(32);
        writer.WriteCString(name);
        return client.SendAsync(WorldOpcode.CmsgGuildCreate, writer.ToArray());
    }

    /// <summary>Packets are handled in order: once CMSG_GUILD_INFO is answered, the create before it has been handled.</summary>
    private static async Task SettleAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgGuildInfo, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgGuildCommandResult);
    }

    [Fact]
    public async Task WhenSwitchedOff_AClientGuildCreateFoundsNothing()
    {
        (WorldTestHost host, WorldTestClient client, SocialFeature feature) = await StartAsync();
        await using (host)
        await using (client)
        {
            await host.OnWorldAsync(() => feature.Context.Guilds.Options.AllowClientGuildCreate = false);
            await SendCreateAsync(client, "Free Guild");
            await SettleAsync(client);

            Assert.Null(await host.OnWorldAsync(() => feature.Context.Guilds.GetByName("Free Guild")));
        }
    }

    [Fact]
    public async Task ByDefault_AClientGuildCreateFoundsTheGuild()
    {
        (WorldTestHost host, WorldTestClient client, SocialFeature feature) = await StartAsync();
        await using (host)
        await using (client)
        {
            await SendCreateAsync(client, "Free Guild");
            await host.WaitForWorldAsync(() => feature.Context.Guilds.GetByName("Free Guild") is not null, "the guild to be founded");
        }
    }

    [Fact]
    public async Task AnOversizedName_DisconnectsTheClient_WhenCreationIsAllowed()
    {
        (WorldTestHost host, WorldTestClient client, SocialFeature feature) = await StartAsync();
        await using (host)
        await using (client)
        {
            await SendCreateAsync(client, new string('G', 25));
            await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the oversized request to disconnect the session");

            Assert.Null(await host.OnWorldAsync(() => feature.Context.Guilds.GetByName(new string('G', 25))));
        }
    }
}
