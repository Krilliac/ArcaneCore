using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// A managed bot and a real socket client in one world (real handlers, real clock): the invite policy at work, the GM's
/// <c>.playerbot invite</c>, whispered commands and the stranger reply. Each test waits for a specific packet or world state.
/// </summary>
public sealed class PlayerbotPartyWorldTests
{
    [Fact]
    public async Task AStrangersInvite_IsDeclined_UnderTheDefaultPolicy()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partydecline");
        await using WorldTestClient stranger = await host.EnterWorldAsync("PARTYSTRANGER", "Partystrange");

        await stranger.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Partydecline"));

        byte[] decline = await stranger.ReadUntilAsync(WorldOpcode.SmsgGroupDecline);
        Assert.Equal("Partydecline", new PacketReader(decline).ReadCString());
        Assert.False(await host.OnWorldAsync(() => Groups(host).GetGroup(host.World.FindOnlinePlayer("Partydecline")!.Guid) is not null));
        Assert.False(PartyTestHost.Feature(host).IsPartyDriven(bot));
    }

    /// <summary>
    /// GuildOrFriends counts only the BOT's friend list. Any player can put any bot on their own list with one CMSG_ADD_FRIEND, so
    /// that is no consent: such an invite is declined (the policy would otherwise work like Anyone). Once the bot has the player on
    /// its own list, the invite is accepted and the bot follows its new master.
    /// </summary>
    [Fact]
    public async Task BefriendingTheBot_DoesNotLetAPlayerInviteIt_ButBeingOnTheBotsOwnFriendListDoes()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partyfriend");
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYFRIENDM", "Partyfriendm");
        ObjectGuid botGuid = await host.PlayerStateAsync("Partyfriend", p => p.Guid);
        ObjectGuid masterGuid = await host.PlayerStateAsync("Partyfriendm", p => p.Guid);

        await master.SendAsync(WorldOpcode.CmsgAddFriend, PartyTestHost.CString("Partyfriend"));
        await host.WaitForWorldAsync(() => Social(host).Friends.Get(host.World.FindOnlinePlayer("Partyfriendm")!)
            .Has(botGuid.Low, Kernel.Social.SocialFlags.Friend), "the player befriends the bot");
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Partyfriend"));
        Assert.Equal("Partyfriend", new PacketReader(await master.ReadUntilAsync(WorldOpcode.SmsgGroupDecline)).ReadCString());
        Assert.False(await host.OnWorldAsync(() => Groups(host).GetGroup(botGuid) is not null));

        await host.OnWorldAsync(() =>
        {
            WorldSession session = PartyTestHost.Feature(host).FindSession(bot)!;
            session.ManagedBudget = null;
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgAddFriend, PartyTestHost.CString("Partyfriendm")));
        });
        await host.WaitForWorldAsync(() => Social(host).Friends.Get(host.World.FindOnlinePlayer("Partyfriend")!)
            .Has(masterGuid.Low, Kernel.Social.SocialFlags.Friend), "the bot befriends the player");
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Partyfriend"));

        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(bot), "the party AI drives the bot");
        PlayerbotStatus status = PartyTestHost.Feature(host).Snapshot().Single(s => s.BotId == bot);
        Assert.True(status.Goal is PlayerbotGoalKind.Follow or PlayerbotGoalKind.Assist, status.Goal.ToString());
        PlayerbotInspection inspection = (await PartyTestHost.Feature(host).InspectAsync("Partyfriend"))!;
        Assert.Equal("Partyfriendm", inspection.Master);
        Assert.Equal(PlayerbotPartyMode.Follow, inspection.PartyMode);
    }

    /// <summary>
    /// The capture queue is bounded (128 packets, drop-oldest): an invitation whose SMSG_GROUP_INVITE was evicted before the bot read
    /// it is still answered, from the group state.
    /// </summary>
    [Fact]
    public async Task AnInvitationWhosePacketWasEvicted_IsStillAnswered()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partyflood");
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYFLOODM", "Partyfloodm");

        await host.OnWorldAsync(() =>
        {
            Player inviter = host.World.FindOnlinePlayer("Partyfloodm")!;
            Groups(host).Invite(inviter, "Partyflood");
            Assert.NotNull(Groups(host).GetInvite(host.World.FindOnlinePlayer("Partyflood")!.Guid));
            WorldSession session = PartyTestHost.Feature(host).FindSession(bot)!;
            for (int i = 0; i < 200; i++) session.Send(WorldOpcode.SmsgEmote, new byte[12]); // evicts the SMSG_GROUP_INVITE
        });

        await host.WaitForWorldAsync(() => Groups(host).AreInSameGroup(host.World.FindOnlinePlayer("Partyfloodm")!.Guid,
            host.World.FindOnlinePlayer("Partyflood")!.Guid), "the bot accepts the invitation");
    }

    /// <summary>
    /// A command the master sends the moment the bot joined, before the bot's first turn in the action budget, is kept: that turn
    /// does not reset it to follow. The bot joins (the GM path, synchronous) and the master's 'stay' whisper is in its queue in the
    /// same world step, so the next tick's intake reads it before any think.
    /// </summary>
    [Fact]
    public async Task AnOrderSentBeforeTheBotsFirstTurn_IsKept()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partyearly");
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYEARLYM", "Partyearlym");
        ulong botGuid = (await host.PlayerStateAsync("Partyearly", p => p.Guid)).Value;

        await host.OnWorldAsync(() =>
        {
            Player inviter = host.World.FindOnlinePlayer("Partyearlym")!;
            Assert.Equal("invited", PartyTestHost.Feature(host).InviteToGroup(inviter, "Partyearly").Code);
            Assert.False(PartyTestHost.Feature(host).IsPartyDriven(bot));
            PartyTestHost.Feature(host).FindSession(bot)!.Send(WorldOpcode.SmsgMessagechat,
                global::ArcaneCore.World.Packets.ChatPackets.BuildMessage(ChatType.Whisper, Language.Common, inviter.Guid, "stay", ChatTag.None));
        });

        Assert.Equal("Staying here.", (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(bot), "the party AI drives the bot");
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).Snapshot().Single(s => s.BotId == bot).Goal == PlayerbotGoalKind.Follow,
            "the party AI has thought");
        Assert.Equal(PlayerbotPartyMode.Stay, await host.OnWorldAsync(() => PartyTestHost.Feature(host).FindParty(bot)!.Mode));
    }

    [Fact]
    public async Task TheGmInviteCommand_PutsTheBotIntoTheGmsGroup_WhateverThePolicy()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.None);
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partygmbot");
        await using WorldTestClient gm = await host.EnterWorldAsync("PARTYGM", "Partygm", AccountSecurity.GameMaster);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".playerbot inspect Partygmbot");
        Assert.Equal("BOTINSPECT party=none", await ReadSystemLineAsync(gm, "BOTINSPECT party="));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".playerbot invite Partygmbot");

        string reply = await ReadSystemLineAsync(gm, "Playerbot ");
        Assert.Equal("Playerbot ok: invited (Partygmbot).", reply);
        await host.WaitForWorldAsync(() => Groups(host).AreInSameGroup(host.World.FindOnlinePlayer("Partygm")!.Guid,
            host.World.FindOnlinePlayer("Partygmbot")!.Guid), "the bot is in the GM's group");
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(bot), "the party AI drives the bot");
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".playerbot inspect Partygmbot");
        Assert.Equal("BOTINSPECT party=master:Partygm mode:follow", await ReadSystemLineAsync(gm, "BOTINSPECT party="));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".playerbot invite Partygmbot");
        Assert.Equal("Playerbot ok: already-in-your-group (Partygmbot).", await ReadSystemLineAsync(gm, "Playerbot "));
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".playerbot invite Nosuchbot");
        Assert.Equal("Playerbot failed: bot-not-running (Nosuchbot).", await ReadSystemLineAsync(gm, "Playerbot "));
    }

    [Fact]
    public async Task TheMastersWhisperedStatus_IsAnswered_AndAStrangersWhisperGetsThePoliteReply()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partytalk");
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYTALKM", "Partytalkm");
        await using WorldTestClient stranger = await host.EnterWorldAsync("PARTYTALKS", "Partytalks");
        ulong botGuid = (await host.PlayerStateAsync("Partytalk", p => p.Guid)).Value;
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Partytalk"));
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(bot), "the party AI drives the bot");

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "STATUS", "Partytalk");
        ChatMessage status = await PartyTestHost.ReadWhisperFromAsync(master, botGuid);
        Assert.Matches(@"^Level 1, health \d+%, mana none, (following Partytalkm|fighting .+)\.$", status.Text);

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "stay", "Partytalk");
        Assert.Equal("Staying here.", (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).FindParty(bot)?.Mode == PlayerbotPartyMode.Stay, "stay mode");

        await stranger.SendChatAsync(ChatType.Whisper, Language.Common, "stay", "Partytalk");
        Assert.Equal(PlayerbotChatCommands.PoliteReply, (await PartyTestHost.ReadWhisperFromAsync(stranger, botGuid)).Text);
        Assert.Equal(PlayerbotPartyMode.Stay, await host.OnWorldAsync(() => PartyTestHost.Feature(host).FindParty(bot)!.Mode));

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "dance", "Partytalk");
        Assert.Equal(PlayerbotChatCommands.Help, (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);

        await master.SendChatAsync(ChatType.Party, Language.Common, "passive");
        Assert.Equal("Passive: I will not attack.", (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);
        Assert.Equal(PlayerbotPartyMode.Passive, await host.OnWorldAsync(() => PartyTestHost.Feature(host).FindParty(bot)!.Mode));

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "leave", "Partytalk");
        Assert.Equal("Leaving the group.", (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);
        await host.WaitForWorldAsync(() => !PartyTestHost.Feature(host).IsPartyDriven(bot), "the bot left; the brain drives it");
    }

    /// <summary>
    /// mangoszero AcceptResurrectAction: a dead bot accepts a group member's resurrection (and is brought back by it) and declines a
    /// stranger's. AutoRevive is off, and the offer is made in the same world-thread step as the death, so the bot's next tick reads it
    /// (intake comes before any think) before it could release its spirit.
    /// </summary>
    [Fact]
    public async Task ADeadBot_AcceptsAMembersResurrection_AndDeclinesAStrangers()
    {
        await using WorldTestHost host = PartyTestHost.Start(options =>
        {
            options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
            options.Party.AutoRevive = false;
        });
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partydead");
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYDEADM", "Partydeadm");
        await using WorldTestClient stranger = await host.EnterWorldAsync("PARTYDEADS", "Partydeads");
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Partydead"));
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(bot), "the party AI drives the bot");
        global::ArcaneCore.Game.Death.Resurrection.ResurrectionService service = host.WorldServices.GetRequiredService<global::ArcaneCore.World.Death.ResurrectionFeature>().Service!;

        await host.OnWorldAsync(() =>
        {
            Player dead = host.World.FindOnlinePlayer("Partydead")!;
            dead.Health = 0;
            dead.Map!.Combat.KillPlayer(dead);
            Assert.True(service.Request(dead, host.World.FindOnlinePlayer("Partydeads")!, "Partydeads", 50, 0, sickness: false, noResTimer: true));
        });
        await host.WaitForWorldAsync(() => !global::ArcaneCore.Game.Death.Resurrection.ResurrectionRequests.IsRequested(host.World.FindOnlinePlayer("Partydead")!),
            "the stranger's offer is declined");
        Assert.False(await host.PlayerStateAsync("Partydead", p => p.IsAlive));

        await host.OnWorldAsync(() =>
        {
            Player dead = host.World.FindOnlinePlayer("Partydead")!;
            Assert.True(service.Request(dead, host.World.FindOnlinePlayer("Partydeadm")!, "Partydeadm", 50, 0, sickness: false, noResTimer: true));
        });
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Partydead") is { IsAlive: true }, "the member's resurrection brings the bot back");
    }

    private static Game.Groups.GroupManager Groups(WorldTestHost host) => Social(host).Groups;

    private static Game.Social.SocialContext Social(WorldTestHost host) => host.WorldServices.GetRequiredService<SocialFeature>().Context;

    private static async Task<string> ReadSystemLineAsync(WorldTestClient client, string prefix)
    {
        while (true)
        {
            ChatMessage line = ChatMessage.Parse(await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat));
            if (line.Type == ChatType.System && line.Text.StartsWith(prefix, StringComparison.Ordinal)) return line.Text;
        }
    }
}
