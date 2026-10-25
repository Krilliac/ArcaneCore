using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Gm.Audit;
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

    private static DebugWriteEntry[] Writes(WorldTestHost host)
        => [.. host.WorldServices.GetRequiredService<GmAuditFeature>().DebugWrites.Tail(100).Where(e => e.Command.Contains("stream", StringComparison.Ordinal))];

    [Fact]
    public async Task OnRenewOffAndOffAll_RecordOneDebugWriteEachForWhatChanged_AndNoOpsRecordNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await LinesAsync(gm, ".arcane stream on moves");
        DebugWriteEntry on = Assert.Single(Writes(host));
        Assert.EndsWith("stream on", on.Command, StringComparison.Ordinal);
        Assert.Equal(("gm stream moves", "subscribed", "off", "on", DebugWriteScope.Live), (on.Target, on.Field, on.OldValue, on.NewValue, on.Scope));

        await LinesAsync(gm, ".arcane stream on moves");
        DebugWriteEntry renew = Writes(host)[1];
        Assert.Equal(("gm stream moves", "expires"), (renew.Target, renew.Field));
        Assert.EndsWith("Z", renew.OldValue, StringComparison.Ordinal);
        Assert.EndsWith("Z", renew.NewValue, StringComparison.Ordinal);
        Assert.Equal(DebugWriteScope.Live, renew.Scope);

        await LinesAsync(gm, ".arcane stream off moves");
        DebugWriteEntry off = Writes(host)[2];
        Assert.EndsWith("stream off", off.Command, StringComparison.Ordinal);
        Assert.Equal(("gm stream moves", "subscribed", "on", "off", DebugWriteScope.Live), (off.Target, off.Field, off.OldValue, off.NewValue, off.Scope));

        await LinesAsync(gm, ".arcane stream off moves");
        await LinesAsync(gm, ".arcane stream off all");
        Assert.Equal(3, Writes(host).Length);

        await LinesAsync(gm, ".arcane stream on moves");
        await LinesAsync(gm, ".arcane stream on proc");
        await LinesAsync(gm, ".arcane stream off all");
        DebugWriteEntry[] all = [.. Writes(host).Skip(5)];
        Assert.Equal(["gm stream moves", "gm stream proc"], all.Select(e => e.Target));
        Assert.All(all, e => Assert.Equal(("subscribed", "on", "off", DebugWriteScope.Live), (e.Field, e.OldValue, e.NewValue, e.Scope)));
    }
}
