using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Battlegrounds;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Battlegrounds;

public sealed class BattlegroundCommandTests
{
    [Fact]
    public void BgCommands_RequireTheVmangosGmRank()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "bg status", "bg start", "bg stop" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task StatusListsRunningMatchesAndQueues_StartAndStopNeedAMatch()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("BGCMD", "Bgcommand", AccountSecurity.GameMaster);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".bg status");
        Assert.Equal("Currently running battlegrounds: 0", (await gm.ReadChatAsync()).Text);
        int templateCount = await host.OnWorldAsync(() => new[] { BattlegroundType.AlteracValley, BattlegroundType.WarsongGulch,
            BattlegroundType.ArathiBasin }.Count(type => host.WorldServices.GetRequiredService<BattlegroundFeature>().Manager.TemplateOf(type) is not null));
        for (int i = 0; i < templateCount; i++)
        {
            Assert.Contains("queue:", (await gm.ReadChatAsync()).Text);
        }

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".bg start");
        Assert.Equal("You are not in a battleground.", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".bg stop");
        Assert.Equal("You are not in a battleground.", (await gm.ReadChatAsync()).Text);
    }
}
