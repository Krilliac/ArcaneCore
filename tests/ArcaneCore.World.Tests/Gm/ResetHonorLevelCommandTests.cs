using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character;
using ArcaneCore.World.Honor;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>.reset honor and .reset level (vmangos HandleResetHonorCommand, HandleResetLevelCommand).</summary>
public sealed class ResetHonorLevelCommandTests
{
    [Fact]
    public void Both_NeedTheDeveloperLevel()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "reset honor", "reset level" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.GameMaster));
            Assert.NotNull(table.Resolve(path, AccountSecurity.Administrator));
        }
    }

    [Fact]
    public async Task ResetLevel_PutsThePlayerBackAtTheStartLevel()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RESETLVL", "Resetlvl", AccountSecurity.Administrator);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".levelup 4");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Resetlvl")!.Level == 5, "levelup");

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".reset level");

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Resetlvl")!.Level == 1, "reset level");
    }

    [Fact]
    public async Task ResetHonor_AnswersWhenHonorIsOff_OrResets()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RESETHON", "Resethon", AccountSecurity.Administrator);
        bool enabled = host.WorldServices.GetService<HonorFeature>()?.ActiveService is not null;

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".reset honor");

        string reply = (await gm.ReadChatAsync()).Text;
        Assert.Equal(enabled ? ResetHonorLevelExtension.HonorReset("Resethon") : ResetHonorLevelExtension.HonorUnavailable, reply);
    }
}