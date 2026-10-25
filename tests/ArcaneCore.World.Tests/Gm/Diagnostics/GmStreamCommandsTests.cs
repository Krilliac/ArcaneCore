using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Gm.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Gm.Audit.TicketTests;

namespace ArcaneCore.World.Tests.Gm.Diagnostics;

public sealed class GmStreamCommandsTests
{
    [Theory]
    [InlineData("moves", true)]
    [InlineData("anti-cheat_2", true)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("bad|channel", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz", false)]
    public void IsValidChannel_AcceptsShortNamesOfLettersDigitsDashAndUnderscore(string channel, bool valid)
        => Assert.Equal(valid, GmStreamCommands.IsValidChannel(channel));

    [Fact]
    public async Task OnOffAndList_ChangeOnlyTheInvokingGmsSubscriptions()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        GmStream stream = host.WorldServices.GetRequiredService<GmStreamFeature>().Stream;
        ArcaneCore.Game.ObjectGuid guid = (await host.PlayerAsync("Gmaaa")).Guid;

        Assert.StartsWith("You follow no stream channels", Assert.Single(await LinesAsync(gm, ".arcane stream list")), StringComparison.Ordinal);
        Assert.StartsWith("Following [moves]", Assert.Single(await LinesAsync(gm, ".arcane stream on moves")), StringComparison.Ordinal);
        Assert.Equal(["moves"], stream.Subscriptions(guid).Select(s => s.Channel));
        Assert.StartsWith("[moves] turns off in ", Assert.Single(await LinesAsync(gm, ".arcane stream list")), StringComparison.Ordinal);

        stream.Publish("moves", "pos 1 2 3");
        Assert.Equal(["[moves] pos 1 2 3"], await gm.CollectChatLinesAsync(1));

        Assert.Equal("Stopped following [moves].", Assert.Single(await LinesAsync(gm, ".arcane stream off moves")));
        Assert.Equal("You do not follow [moves].", Assert.Single(await LinesAsync(gm, ".arcane stream off moves")));
        Assert.Empty(stream.Subscriptions(guid));
    }

    [Fact]
    public async Task OffAll_EndsEverySubscription_AndABadChannelIsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        GmStream stream = host.WorldServices.GetRequiredService<GmStreamFeature>().Stream;
        ArcaneCore.Game.ObjectGuid guid = (await host.PlayerAsync("Gmaaa")).Guid;
        await LinesAsync(gm, ".arcane stream on moves");
        await LinesAsync(gm, ".arcane stream on proc");

        Assert.Equal("Stopped 2 channel(s).", Assert.Single(await LinesAsync(gm, ".arcane stream off all")));
        Assert.Empty(stream.Subscriptions(guid));

        string[] refused = await LinesAsync(gm, ".arcane stream on bad|name");
        Assert.Contains(refused, l => l.Contains("Syntax: .arcane stream on", StringComparison.Ordinal));
        Assert.Empty(stream.Subscriptions(guid));
    }
}
