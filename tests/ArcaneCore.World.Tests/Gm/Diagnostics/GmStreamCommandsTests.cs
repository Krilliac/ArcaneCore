using ArcaneCore.Game.Entities;
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

        Assert.StartsWith("You follow no stream channels", (await LinesAsync(gm, ".arcane stream list"))[0], StringComparison.Ordinal);
        Assert.StartsWith("Following [moves]", Assert.Single(await LinesAsync(gm, ".arcane stream on moves")), StringComparison.Ordinal);
        Assert.Equal(["moves"], stream.Subscriptions(guid).Select(s => s.Channel));
        Assert.StartsWith("[moves] turns off in ", (await LinesAsync(gm, ".arcane stream list"))[0], StringComparison.Ordinal);

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
        await LinesAsync(gm, ".arcane stream on procs");

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
        await LinesAsync(gm, ".arcane stream on procs");
        await LinesAsync(gm, ".arcane stream off all");
        DebugWriteEntry[] all = [.. Writes(host).Skip(5)];
        Assert.Equal(["gm stream moves", "gm stream procs"], all.Select(e => e.Target));
        Assert.All(all, e => Assert.Equal(("subscribed", "on", "off", DebugWriteScope.Live), (e.Field, e.OldValue, e.NewValue, e.Scope)));
    }

    [Fact]
    public async Task On_TakesAScope_RefusesAllForMoves_RejectsUnknownChannelsAndScopes_AndResubscribingChangesTheScope()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        GmStream stream = host.WorldServices.GetRequiredService<GmStreamFeature>().Stream;
        ArcaneCore.Game.ObjectGuid guid = (await host.PlayerAsync("Gmaaa")).Guid;

        Assert.Equal("[moves] cannot be followed for everyone (all); use target or near.", Assert.Single(await LinesAsync(gm, ".arcane stream on moves all")));
        Assert.Equal("Unknown channel 'proc'. Available: damage, procs, spells, moves, anticheat.", Assert.Single(await LinesAsync(gm, ".arcane stream on proc")));
        Assert.Equal("Unknown scope 'everyone'; use target, near or all.", Assert.Single(await LinesAsync(gm, ".arcane stream on damage everyone")));
        Assert.Empty(stream.Details(guid));

        Assert.StartsWith("Following [moves] (near)", Assert.Single(await LinesAsync(gm, ".arcane stream on MOVES near")), StringComparison.Ordinal);
        Assert.StartsWith("Following [damage] (all)", Assert.Single(await LinesAsync(gm, ".arcane stream on damage all")), StringComparison.Ordinal);
        Assert.StartsWith("Following [damage] (target)", Assert.Single(await LinesAsync(gm, ".arcane stream on damage")), StringComparison.Ordinal);
        Assert.Equal(
            [("damage", ArcaneCore.Game.Diagnostics.GmStreamScope.Target), ("moves", ArcaneCore.Game.Diagnostics.GmStreamScope.Near)],
            stream.Details(guid).Select(d => (d.Channel, d.Scope)));
    }

    [Fact]
    public async Task List_ShowsScopes_ThenEveryAvailableChannel()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        await LinesAsync(gm, ".arcane stream on procs near");

        string[] lines = await LinesAsync(gm, ".arcane stream list", 7);

        Assert.StartsWith("[procs] turns off in ", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(", scope near", lines[0], StringComparison.Ordinal);
        Assert.Equal("Available channels (.arcane stream on <channel> [scope]):", lines[1]);
        Assert.Equal(["damage [target|near|all]", "procs [target|near|all]", "spells [target|near|all]", "moves [target|near]", "anticheat [target|near|all]"],
            lines.Skip(2).Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)]));
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task ADemotedGm_LosesTheSubscriptionsAndTheFeed_AfterTheAccountStoreRankDrops()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        GmStream stream = host.WorldServices.GetRequiredService<GmStreamFeature>().Stream;
        Player player = await host.PlayerAsync("Gmaaa");
        ArcaneCore.Game.ObjectGuid guid = player.Guid;
        Assert.True(stream.Subscribe(guid, "alpha"));
        Assert.True(stream.Subscribe(guid, "beta"));

        // Before the demotion the feed arrives (so the later silence means something).
        stream.Publish("alpha", "before the demotion");
        Assert.Equal(["[alpha] before the demotion"], await gm.CollectChatLinesAsync(1));

        // Demote through the account store, the way the realm console's set-gmlevel does. The live session keeps its login-time rank.
        Assert.True(await host.Accounts.UpdateSecurityAsync("GMA", AccountSecurity.Player));
        Assert.Equal(AccountSecurity.GameMaster, player.Security);
        await WorldTestHost.WaitForAsync(() => stream.Subscriptions(guid).Count == 0, "the demoted GM's subscriptions to end after the stored rank is re-read");

        // The session still caches GameMaster, so the GM can ask for the feed again: it must be refused and nothing delivered.
        Assert.True(stream.Subscribe(guid, "alpha"));
        stream.Publish("alpha", "after the demotion");
        await WorldTestHost.WaitForAsync(() => stream.Subscriptions(guid).Count == 0, "the re-subscription of a demoted GM to be ended");
        Assert.DoesNotContain(await gm.CollectAsync(), p => p.Opcode == ArcaneCore.Protocol.WorldOpcode.SmsgMessagechat);
        Assert.False(stream.IsActive);
    }

    [Fact]
    public async Task AGmWhoLeavesThroughACommittedClusterTransfer_LosesEverySubscription()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        GmStream stream = host.WorldServices.GetRequiredService<GmStreamFeature>().Stream;
        Player player = await host.PlayerAsync("Gmaaa");
        Assert.True(stream.Subscribe(player.Guid, "alpha"));
        Assert.True(stream.Subscribe(player.Guid, "moves"));

        // The capture detaches the player; the subscriptions survive an aborted transfer (the player is re-added), and end when the commit retires it.
        Assert.True(await host.OnWorldAsync(() => host.World.DetachPlayerForTransfer(player)));
        Assert.Equal(2, stream.Subscriptions(player.Guid).Count);
        Assert.True(await host.OnWorldAsync(() => host.World.RetireDetachedPlayer(player)));

        Assert.Empty(stream.Subscriptions(player.Guid));
        Assert.False(stream.IsActive);
    }
}
