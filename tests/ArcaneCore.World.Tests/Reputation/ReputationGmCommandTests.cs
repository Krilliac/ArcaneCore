using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>
/// <c>.modify rep</c>, <c>.lookup faction</c> and <c>.character reputation</c> (vmangos CharacterCommands.cpp:1955-1969,
/// 4330-4422; LookupCommands.cpp:1384-1478; texts mangos_string 305-327). The fixture catalog has Booty Bay (list 0),
/// Stormwind (list 7, visible and peace forced for humans) and the hidden Alliance parent (list 10).
/// </summary>
public sealed class ReputationGmCommandTests
{
    private static WorldTestHost Start(bool factions = true)
    {
        if (factions)
        {
            ReputationTestServices.Current.Value = new MemoryReputationStore();
        }

        try { return WorldTestHost.Start(); }
        finally { ReputationTestServices.Current.Value = null; }
    }

    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    [Fact]
    public void Levels_FollowRetail_ModifyRepBasicAdmin_LookupAndCharacterTicketMaster()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("modify rep", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("modify rep", AccountSecurity.Administrator));
        Assert.Null(table.Resolve("lookup faction", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("lookup faction", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("character reputation", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("character reputation", AccountSecurity.GameMaster));
    }

    [Fact]
    public async Task ModifyRep_SetsAValueOrARankPlusDelta_AndRefusesBadInput()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("REPGM", "Repgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify rep 21 3000");
        Assert.Equal($"Faction Booty Bay (21) reputation of {Link("Repgm")} was set to  3000!", (await gm.ReadChatAsync()).Text);
        Assert.Equal(3000, await Reputation(host, 21));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify rep 21 hon 100"); // the start of Honored (9000) plus 100
        Assert.Equal($"Faction Booty Bay (21) reputation of {Link("Repgm")} was set to  9100!", (await gm.ReadChatAsync()).Text);
        Assert.Equal(9100, await Reputation(host, 21));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify rep 21 hated");
        Assert.Contains("was set to -42000!", (await gm.ReadChatAsync()).Text);
        Assert.Equal(-42000, await Reputation(host, 21));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify rep 21 honored 12000");
        Assert.Equal("delta must be between 0 and 11999 (inclusive)", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify rep 21 zzz");
        Assert.Equal("Invalid parameter zzz", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify rep 9999 100");
        Assert.Equal("Faction 9999 unknown!", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify rep");
        Assert.StartsWith("Syntax:", (await gm.ReadChatAsync()).Text);
        Assert.Equal(-42000, await Reputation(host, 21)); // refused input changed nothing
    }

    [Fact]
    public async Task LookupFaction_ListsMatchesWithTheTargetsStanding_AndFlags()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("REPLK", "Replk", AccountSecurity.Administrator);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup faction STORM");
        Assert.Equal("72 - |cffffffff|Hfaction:72|h[Stormwind enUS]|h|r Neutral|h|r (0) [visible] [peace forced]", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup faction booty");
        Assert.Equal("21 - |cffffffff|Hfaction:21|h[Booty Bay enUS]|h|r Neutral|h|r (0)", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup faction zzz");
        Assert.Equal("No faction found!", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup faction");
        Assert.StartsWith("Syntax:", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task CharacterReputation_ListsEveryFactionOfThePlayer_InSlotOrder()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("REPCH", "Repch", AccountSecurity.Administrator);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".character reputation");
        Assert.StartsWith("21 - |cffffffff|Hfaction:21|h[Booty Bay enUS]|h|r Neutral|h|r (0)", (await gm.ReadChatAsync()).Text);
        Assert.Equal("72 - |cffffffff|Hfaction:72|h[Stormwind enUS]|h|r Neutral|h|r (0) [visible] [peace forced]", (await gm.ReadChatAsync()).Text);
        Assert.Equal("469 - |cffffffff|Hfaction:469|h[Alliance enUS]|h|r Neutral|h|r (0) [hidden]", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task WithoutFactionData_EveryCommandSaysSo_InsteadOfSilentlySucceeding()
    {
        await using WorldTestHost host = Start(factions: false);
        await using WorldTestClient gm = await host.EnterWorldAsync("REPNO", "Repno", AccountSecurity.Administrator);
        await gm.CollectAsync();
        foreach (string command in new[] { ".modify rep 21 100", ".lookup faction booty", ".character reputation" })
        {
            await gm.SendChatAsync(ChatType.Say, Language.Common, command);
            Assert.Contains("Reputation:FactionDbcPath", (await gm.ReadChatAsync()).Text);
        }
    }

    private static Task<int> Reputation(WorldTestHost host, uint faction) => host.PlayerStateAsync("Repgm",
        p => ((WorldSession)p.Session).Services.GetRequiredService<ReputationFeature>().Service.GetReputation(p, faction));
}
