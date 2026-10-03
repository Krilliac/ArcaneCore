using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// /who reports the member's guild name and matches the guild filter and the search strings
/// against it (vmangos MiscHandler.cpp:147-158 guild filter, :180-196 search strings, :201-203 output).
/// </summary>
public sealed class WhoGuildTests
{
    [Fact]
    public async Task Who_ShowsGuildNames_AndTheGuildFilterAndSearchStringsMatchThem()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Keeper", AccountSecurity.GameMaster);
        await using WorldTestClient founder = await host.EnterWorldAsync("FOUNDER", "Founder");
        await using WorldTestClient loner = await host.EnterWorldAsync("LONER", "Loner");
        SocialFeature feature = await host.PlayerStateAsync("Keeper", p => ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>());
        await feature.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".guild create Founder \"Arcane\"");
        ChatMessage reply = await gm.ReadChatAsync();
        Assert.Equal("Guild Arcane created.", reply.Text);

        Assert.Equal([("Founder", "Arcane"), ("Keeper", string.Empty), ("Loner", string.Empty)], await WhoAsync(loner));
        Assert.Equal([("Founder", "Arcane")], await WhoAsync(loner, guild: "ARC"));
        Assert.Equal([("Founder", "Arcane")], await WhoAsync(loner, strings: ["xyz", "rcan"]));
        Assert.Empty(await WhoAsync(loner, guild: "Knights"));
    }

    private static async Task<List<(string Name, string Guild)>> WhoAsync(WorldTestClient client, string guild = "", string[]? strings = null)
    {
        var who = new PacketWriter(64);
        who.WriteUInt32(0);
        who.WriteUInt32(100);
        who.WriteCString(string.Empty);
        who.WriteCString(guild);
        who.WriteUInt32(0xFFFFFFFF);
        who.WriteUInt32(0xFFFFFFFF);
        who.WriteUInt32(0);
        who.WriteUInt32((uint)(strings?.Length ?? 0));
        foreach (string term in strings ?? [])
        {
            who.WriteCString(term);
        }

        await client.SendAsync(WorldOpcode.CmsgWho, who.ToArray());
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgWho));
        uint listed = reader.ReadUInt32();
        reader.ReadUInt32(); // online count
        var entries = new List<(string, string)>();
        for (int i = 0; i < listed; i++)
        {
            string name = reader.ReadCString();
            string guildName = reader.ReadCString();
            reader.ReadUInt32(); // level
            reader.ReadUInt32(); // class
            reader.ReadUInt32(); // race
            reader.ReadUInt32(); // zone
            entries.Add((name, guildName));
        }

        entries.Sort();
        return entries;
    }
}
