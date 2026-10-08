using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// CHAT_MSG_BATTLEGROUND and CHAT_MSG_BATTLEGROUND_LEADER over real sockets (vmangos HandleChatMessageOpcode,
/// ChatHandler.cpp:579-615): the speaker's battleground group hears it, the speaker included; only its leader may use the
/// leader channel; outside a battleground both are dropped. The roster is a fake (the battleground daemon is not wired yet).
/// </summary>
public sealed class BattlegroundChatTests
{
    private sealed class FixedRoster : IBattlegroundChatRoster
    {
        public Dictionary<ObjectGuid, BattlegroundChatTeam> Teams { get; } = [];

        public BattlegroundChatTeam? TeamOf(ObjectGuid player) => Teams.GetValueOrDefault(player);
    }

    [Fact]
    public async Task BattlegroundChat_ReachesTheSpeakersTeam_TheLeaderChannelOnlyFromTheLeader()
    {
        var roster = new FixedRoster();
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IBattlegroundChatRoster>(roster));
        await using WorldTestClient leader = await host.EnterWorldAsync("BGLEAD", "Bglead");
        await using WorldTestClient mate = await host.EnterWorldAsync("BGMATE", "Bgmate");
        await using WorldTestClient enemy = await host.EnterWorldAsync("BGFOE", "Bgfoe");
        await using WorldTestClient outsider = await host.EnterWorldAsync("OUTSIDE", "Outside");
        ObjectGuid lead = (await host.PlayerAsync("Bglead")).Guid;
        ObjectGuid friend = (await host.PlayerAsync("Bgmate")).Guid;
        ObjectGuid foe = (await host.PlayerAsync("Bgfoe")).Guid;
        var ours = new BattlegroundChatTeam([lead, friend], lead);
        roster.Teams[lead] = ours;
        roster.Teams[friend] = ours;
        roster.Teams[foe] = new BattlegroundChatTeam([foe], foe);
        foreach (WorldTestClient c in new[] { leader, mate, enemy, outsider })
        {
            await c.CollectAsync();
        }

        await mate.SendChatAsync(ChatType.Battleground, Language.Common, "flag mid");
        ChatMessage heard = await leader.ReadChatAsync();
        Assert.Equal((ChatType.Battleground, "flag mid", friend.Value), (heard.Type, heard.Text, heard.Sender));
        Assert.Equal("flag mid", (await mate.ReadChatAsync()).Text);   // the speaker hears it too

        await mate.SendChatAsync(ChatType.BattlegroundLeader, Language.Common, "not the leader");
        await leader.SendChatAsync(ChatType.BattlegroundLeader, Language.Common, "defend base");
        ChatMessage order = await mate.ReadChatAsync();
        Assert.Equal((ChatType.BattlegroundLeader, "defend base"), (order.Type, order.Text));

        await outsider.SendChatAsync(ChatType.Battleground, Language.Common, "anyone?");
        await Task.Delay(200);
        Assert.DoesNotContain(await enemy.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);
        Assert.DoesNotContain(await outsider.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);
        Assert.DoesNotContain(await leader.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat
            && ChatMessage.Parse(p.Payload).Text is "not the leader" or "anyone?");
    }

    [Fact]
    public async Task WithoutARoster_BattlegroundChatIsDropped()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("ALONE", "Alone");
        await a.CollectAsync();
        await a.SendChatAsync(ChatType.Battleground, Language.Common, "hello?");
        await a.AssertSilentAsync(TimeSpan.FromMilliseconds(200));
    }
}
