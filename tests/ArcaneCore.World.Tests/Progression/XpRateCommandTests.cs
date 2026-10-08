using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Progression;

/// <summary><c>.modify xprate</c> (vmangos HandleModifyXpRateCommand, CharacterCommands.cpp:62-92) with the retail Rate.XP.Personal.Min/Max of 1.</summary>
public sealed class XpRateCommandTests
{
    private static Task SayAsync(WorldTestClient client, string text) => client.SendChatAsync(ChatType.Say, Language.Common, text);

    [Fact]
    public async Task APlayer_MaySetOne_ButNotMoreThanTheMaximum()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("XP", "Xp");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Xp");

        await SayAsync(client, ".modify xprate 2");
        Assert.Equal("You can't set XP rate above 1!", (await client.ReadChatAsync()).Text);
        Assert.StartsWith("Syntax: .modify xprate", (await client.ReadChatAsync()).Text); // vmangos returns false: the help follows
        Assert.StartsWith("Set your experience rate", (await client.ReadChatAsync()).Text);
        Assert.Equal(-1.0f, await host.OnWorldAsync(() => player.PersonalXpRate));

        await SayAsync(client, ".modify xprate 0.5");
        Assert.Equal("You can't set XP rate below 1!", (await client.ReadChatAsync()).Text);
        Assert.StartsWith("Syntax: .modify xprate", (await client.ReadChatAsync()).Text);
        Assert.StartsWith("Set your experience rate", (await client.ReadChatAsync()).Text);

        await SayAsync(client, ".modify xprate 1");
        Assert.Equal("You have changed your XP rate to 1 times normal experience gain.", (await client.ReadChatAsync()).Text);
        Assert.Equal(1.0f, await host.OnWorldAsync(() => player.PersonalXpRate));
    }

    [Fact]
    public async Task AGameMaster_MayGoAboveTheMaximum()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Player player = await host.PlayerAsync("Gm");

        await SayAsync(gm, ".modify xprate 5");

        Assert.Equal("You have changed your XP rate to 5 times normal experience gain.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(5.0f, await host.OnWorldAsync(() => player.PersonalXpRate));
    }
}
