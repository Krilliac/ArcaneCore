using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Talents;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Talents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

public sealed class GmResetTalentCommandTests
{
    [Fact]
    public void ResetTalents_NeedsTheGameMasterRetailLevel()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("reset talents", AccountSecurity.Moderator));
        Assert.Equal("reset talents", table.Lookup("reset talents", AccountSecurity.GameMaster).Path);
        Assert.Equal(3, table.Lookup("reset talents", AccountSecurity.GameMaster).Command?.RequiredLevel(table.Gm));
    }

    [Fact]
    public async Task ResetTalents_ReturnsSpentPointsWithoutChargingThePlayer()
    {
        var fixture = new TalentWorldFixture();
        TalentTestServices.Current.Value = fixture;
        QuestInteractionTestServices.Current.Value = new QuestInteractionFixture();
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            TalentTestServices.Current.Value = null;
            QuestInteractionTestServices.Current.Value = null;
        }

        await using (host)
        await using (WorldTestClient gm = await host.EnterWorldAsync("RESETGM", "Resetgm", AccountSecurity.GameMaster))
        {
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Resetgm")!;
                player.Level = 10;
                player.Money = 100_000;   // enough for a paid respec, so a charge would show
                var service = host.WorldServices.GetRequiredService<TalentFeature>().Service!;
                service.InitTalentForLevel(player);
                Assert.True(service.LearnTalent(player, 1, 0));
                Assert.Equal(1u, service.UsedPoints(player));
            });

            await gm.SendChatAsync(ChatType.Say, Language.Common, ".reset talents");
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<TalentFeature>().Service!
                .UsedPoints(host.World.FindOnlinePlayer("Resetgm")!) == 0, "talent points returned");
            Assert.Equal(100_000u, await host.PlayerStateAsync("Resetgm", player => player.Money));
        }
    }
}
