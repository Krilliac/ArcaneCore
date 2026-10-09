using ArcaneCore.Game;
using ArcaneCore.Game.Progression;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Character;

public sealed class ResetStatsCommandTests
{
    [Fact]
    public void ResetStats_RequiresTheVmangosDeveloperRank()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("reset stats", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("reset stats", AccountSecurity.Administrator));
    }

    [Fact]
    public async Task ResetStats_ReappliesLevelBaseValues_AndChecksTargetRank()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("RSTADMIN", "Rstadmin", AccountSecurity.Administrator);
        await using WorldTestClient targetClient = await host.EnterWorldAsync("RSTTARGET", "Rsttarget");
        var target = await host.PlayerAsync("Rsttarget");
        uint originalFaction = target.FactionTemplate;
        await host.OnWorldAsync(() =>
        {
            PlayerProgression progression = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression;
            progression.UseLevelStats(new PlayerLevelStatsTable([
                new KeyValuePair<(byte, byte, byte), PlayerLevelStats>(
                    ((byte)target.Race, (byte)target.Class, target.Level), new PlayerLevelStats(75, 55, 10, 10, 10, 10, 10)),
            ]));
            target.SetUInt32(UpdateFields.UnitFieldBaseHealth, 1);
            target.MaxHealth = 999;
            target.FactionTemplate = 77;
            host.World.FindOnlinePlayer("Rstadmin")!.Selection = target.Guid;
        });

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".reset stats");
        Assert.Contains("Stats of", (await admin.ReadChatAsync()).Text);
        Assert.Equal((75u, 55u, originalFaction), await host.PlayerStateAsync("Rsttarget", p =>
            (p.GetUInt32(UpdateFields.UnitFieldBaseHealth), p.GetUInt32(UpdateFields.UnitFieldBaseMana), p.FactionTemplate)));
        Assert.InRange(await host.PlayerStateAsync("Rsttarget", p => p.MaxHealth), 75u, 200u);
    }
}
