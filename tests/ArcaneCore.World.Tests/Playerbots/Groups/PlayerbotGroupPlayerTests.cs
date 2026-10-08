using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Bot groups and real players (<see cref="PlayerbotGroupCoordinator"/>): a real player's party keeps its party bot (vmangos
/// PartyBotAI master behaviour, unchanged), a short group may invite a nearby player when allowed, the GM commands report the groups,
/// and a bot left inside an instance without a group walks out.
/// </summary>
public sealed class PlayerbotGroupPlayerTests
{
    private const byte Human = 1, Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    [Fact]
    public async Task ARealPlayersPartyBot_StaysItsMastersBot_AndTheCoordinatorLeavesItAlone()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => options.Party.Allowlist = ["Realmaster"]);
        var bot = await world.AddBotAsync("Partyone", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), world.Trace());
        await using WorldTestClient master = await world.World.EnterWorldAsync("REALMASTER", "Realmaster", AccountSecurity.Player);

        PlayerbotOperationResult invited = await world.OnWorldAsync(() => world.Bots.InviteToGroup(world.World.Host.World.FindOnlinePlayer("Realmaster")!, bot.Name));
        Assert.True(invited.Success, invited.Code);

        // The party AI drives it (follows its master); the coordinator stops waiting for it and never takes it into a bot group.
        Assert.True(await world.RunUntilAsync(5_000, () => world.Bots.IsPartyDriven(bot.Id)), world.Trace());
        await world.RunAsync(10_000);
        Assert.True(world.Bots.IsPartyDriven(bot.Id));
        Assert.False(world.Coordinator.Drives(bot.Id));
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
        Assert.Empty(world.Coordinator.Groups);
        Assert.Empty(await world.OnWorldAsync(() => world.Bots.FindBrain(bot.Id)?.GroupHeld.ToArray() ?? []));
        Assert.Null((await world.OnWorldAsync(() => world.Bots.Snapshot().Single(s => s.BotId == bot.Id))).Group);
    }

    [Fact]
    public async Task WithInvitePlayers_AGroupOneShort_InvitesANearbyRealPlayer_ThroughTheOrdinaryInvitation()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPHELPER", "Grouphelper", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerone", Human, Warrior, 1, []);

        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Any(g => g.Members.Any(m => m.Real && m.Invited))), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(bot.Id, group.LeaderBotId); // a real player never leads a bot group
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        Assert.Equal(bot.Name, new PacketReader(invite).ReadCString());

        // The player declines: the group is given up and that player is not asked again for a while.
        await player.SendAsync(WorldOpcode.CmsgGroupDecline, []);
        Assert.True(await world.RunUntilAsync(10_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("Grouphelper did not join", StringComparison.Ordinal));
        await world.RunAsync(10_000);
        Assert.Empty(world.Coordinator.Groups);
    }

    [Fact]
    public async Task TheGmCommands_ReportTheGroups_TheirMembers_RolesAndState()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        await world.AddBotAsync("Cmdtank", Human, Warrior, 10, [Taunt]);
        await world.AddBotAsync("Cmdprie", Human, Priest, 10, [LesserHeal, Smite]);
        await world.AddBotAsync("Cmdmage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), world.Trace());
        await using WorldTestClient gm = await world.World.EnterWorldAsync("GROUPGM", "Groupgm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".playerbot groups");
        Assert.StartsWith("Bot groups: enabled, 1 running", (await gm.ReadChatAsync()).Text);
        string line = (await gm.ReadChatAsync()).Text;
        Assert.StartsWith("group 1 quest:" + OgreEntry, line);
        Assert.Contains("Cmdtank(tank,leader)", line);
        Assert.Contains("Cmdprie(healer)", line);
        Assert.Contains("Cmdmage(damage)", line);

        // The status line's group part and the inspection's BOTINSPECT group line.
        Assert.StartsWith("group=1:healer:", (await world.OnWorldAsync(() => world.Bots.Snapshot().Single(s => s.Name == "Cmdprie"))).Group);
        Assert.StartsWith("group=1:tank:", (await world.Bots.InspectAsync("Cmdtank"))!.Group);
        Assert.EndsWith(":leader", (await world.Bots.InspectAsync("Cmdtank"))!.Group);
    }

    [Fact]
    public async Task ABotInABotLedGroup_StillAnswersAPlayersWhisper_AndTheGroupFormedThroughItsIntakeStays()
    {
        // Bot chat (on by default) and the coordinator share the party intake: the members' acceptances of the leader's invitations
        // must not be swallowed by the chat, and a grouped bot must still answer a real player (wave 8 merge of bot-chat and bot-groups).
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => options.Chat.PerPlayerCooldownSeconds = 0);
        var tank = await world.AddBotAsync("Chattank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Chatprie", Human, Priest, 10, [LesserHeal, Smite]);
        var mage = await world.AddBotAsync("Chatmage", Human, Mage, 10, [Fireball]);
        Assert.True(world.Options.Chat.Enabled);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), world.Trace());
        Assert.Equal(3, await world.OnWorldAsync(() => world.GroupManager.GetGroup(world.Player(tank.Id).Guid)?.MemberCount ?? 0));

        await using WorldTestClient player = await world.World.EnterWorldAsync("CHATGROUPP", "Chatgroupp", AccountSecurity.Player);
        await player.CollectAsync();
        ulong bot = await world.OnWorldAsync(() => world.Player(healer.Id).Guid.Value);
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.Drives(healer.Id)));
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "what level are you?", healer.Name);

        ArcaneCore.World.Playerbots.Chat.PlayerbotChat chat = world.Bots.Chat!;
        Assert.True(await world.RunUntilAsync(10_000, () => chat.Status().Answered >= 1), world.Trace());
        await world.RunAsync(500); // the reply is said on the world thread at the next tick
        ChatMessage reply;
        do reply = await player.ReadChatAsync(); while (reply.Type != ChatType.Whisper || reply.Sender != bot);
        Assert.Matches(@"\b10\b", reply.Text);
        Assert.NotEqual(ArcaneCore.World.Playerbots.Party.PlayerbotChatCommands.PoliteReply, reply.Text);

        // The bot is still in its group and still driven by the coordinator; the mage was never turned away either.
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.Drives(healer.Id) && world.Coordinator.Drives(mage.Id)));
        Assert.True(await world.OnWorldAsync(() => world.GroupManager.AreInSameGroup(world.Player(tank.Id).Guid, world.Player(healer.Id).Guid)));
    }

    [Fact]
    public async Task AServerGroupOfBotsNobodyLeads_IsLeft()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        var first = await world.AddBotAsync("Strayone", Human, Warrior, 5, []);
        var second = await world.AddBotAsync("Straytwo", Human, Mage, 5, []);
        // A group of bots the coordinator does not own (as the server restores one after a restart).
        await world.OnWorldAsync(() =>
        {
            world.GroupManager.Invite(world.Player(first.Id), second.Name);
            world.GroupManager.Accept(world.Player(second.Id));
            Assert.True(world.GroupManager.AreInSameGroup(world.Player(first.Id).Guid, world.Player(second.Id).Guid));
            return true;
        });

        Assert.True(await world.RunUntilAsync(10_000, () => world.GroupManager.GetGroup(world.Player(first.Id).Guid) is null
            && world.GroupManager.GetGroup(world.Player(second.Id).Guid) is null), world.Trace());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("left a group of bots nobody leads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABotLeftInsideAnInstanceWithoutAGroup_WalksOutThroughTheExit()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], dungeon: true);
        var bot = await world.AddBotAsync("Strandone", Human, Warrior, 12, [], EntranceArea);
        Assert.True(await world.OnWorldAsync(() => world.World.Services.GetRequiredService<TeleportFeature>().Teleports
            .TeleportTo(world.Player(bot.Id), 36, -16.4f, -383.07f, 61.78f, 0f)));
        Assert.True(await world.RunUntilAsync(30_000, () => world.Player(bot.Id) is { IsInWorld: true, MapId: 36 }), world.Trace());

        // Saved inside and logged in again: the login gate lets it into the dungeon it is in (PlayerbotMapPolicy.MayStayOnMap; the
        // default AllowedMaps [0, 1] refused it before, and three such faults disabled the bot).
        Assert.True((await world.Bots.StopAsync(bot.Name)).Success);
        PlayerbotOperationResult started = await world.Bots.StartAsync(bot.Name);
        Assert.True(started.Success, started.Code);
        Assert.True(await world.RunUntilAsync(30_000, () => world.Player(bot.Id) is { IsInWorld: true, MapId: 36 }), world.Trace());

        Assert.True(await world.RunUntilAsync(120_000, () => world.Player(bot.Id) is { IsInWorld: true, MapId: 0 }), world.Trace());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("Strandone is inside map 36 without a group", StringComparison.Ordinal));
        Assert.True(await world.RunUntilAsync(10_000, () => !world.Coordinator.Drives(bot.Id)), world.Trace());
    }
}
