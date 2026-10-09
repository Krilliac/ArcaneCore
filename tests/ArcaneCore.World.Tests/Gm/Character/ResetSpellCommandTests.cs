using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Character;

public sealed class ResetSpellCommandTests
{
    [Fact]
    public void ResetSpells_RequiresTheVmangosDeveloperRank()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("reset spells", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("reset spells", AccountSecurity.Administrator));
    }

    [Fact]
    public async Task ResetSpells_RemovesExtraSpells_AndLearnsRaceClassDefaults()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RESETSPELL", "Resetspell", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("RESETSPELLV", "Resetspellv");
        var target = await host.PlayerAsync("Resetspellv");
        byte race = (byte)target.Race;
        byte cls = (byte)target.Class;
        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([new SpellInfo { Id = 991001 }, new SpellInfo { Id = 991002 }],
                [(race, cls, 991001u)], []);
            feature.System.LearnSpell(target, 991002);
            host.World.FindOnlinePlayer("Resetspell")!.Selection = target.Guid;
        });

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".reset spells");
        Assert.Equal("Spells of |cffffffff|Hplayer:Resetspellv|h[Resetspellv]|h|r reset.", (await gm.ReadChatAsync()).Text);
        Assert.Equal((true, false), await host.OnWorldAsync(() =>
        {
            SpellbookCache book = host.WorldServices.GetRequiredService<SpellFeature>().Spellbook;
            return (book.HasSpell(target, 991001), book.HasSpell(target, 991002));
        }));
    }
}
