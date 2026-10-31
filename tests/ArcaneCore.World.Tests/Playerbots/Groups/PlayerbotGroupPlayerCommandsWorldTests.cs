using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The invite cooldown, the GM group commands, whispers inside a bot-led group, and a bot left inside an instance. Split from the former <c>PlayerbotGroupPlayerTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotGroupPlayerTestBase"/>.
/// </summary>
public sealed class PlayerbotGroupPlayerCommandsWorldTests : PlayerbotGroupPlayerTestBase
{
    [Fact]
    public async Task APlayerInvitedOnce_IsNotInvitedAgainWithinTheCooldown()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPCOOL", "Groupcool", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerfour", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Groupcool", DuoQuest);
        await RememberPlayerAsync(world, bot, "Groupcool", 40);

        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        ObjectGuid guid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Groupcool")!.Guid);
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.InvitedRecently(guid)));
        await player.SendAsync(WorldOpcode.CmsgGroupDecline, []);
        Assert.True(await world.RunUntilPacedAsync(() => world.Coordinator.Groups.Count == 0, 10_000), await world.TraceAsync());

        // The decline's own hold (ten minutes) ends first; the thirty minute cooldown still keeps the bots from asking again.
        await world.RunAsync(11 * 60_000);
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.InvitedRecently(guid)));
        Assert.False(PlayerInvited(world), await world.TraceAsync());
    }

    [Fact]
    public async Task TheGmCommands_ReportTheGroups_TheirMembers_RolesAndState()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        await world.AddBotAsync("Cmdtank", Human, Warrior, 10, [Taunt]);
        await world.AddBotAsync("Cmdprie", Human, Priest, 10, [LesserHeal, Smite]);
        await world.AddBotAsync("Cmdmage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        await using WorldTestClient gm = await world.World.EnterWorldAsync("GROUPGM", "Groupgm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".playerbot groups");
        Assert.StartsWith("Bot groups: enabled, 1 running", (await gm.ReadSystemChatAsync()).Text);
        string line = (await gm.ReadSystemChatAsync()).Text;
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
        // Forming ends either with every member joined or with the group giving up (an invitation refused or unanswered, or a member not online,
        // for InviteTimeoutMs of game time; it then disbands). The bots' acceptances are game-time driven (PlayerbotPartyAI.Intake runs once per
        // think interval of game time for an ungrouped invitee, and the answer goes through a synchronous managed action), so the manual clock
        // cannot outrun them. A group that gave up would have passed the old wait ("state is not Forming") and failed later on the member count
        // without a reason (hypothesis: the failing run was not reproduced). Wait for the group that completed instead, so a group that gave up
        // fails with the trace and the coordinator's events. Why a 3-bot group would give up is still unknown.
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State is not (PlayerbotGroupState.Forming or PlayerbotGroupState.Disbanding) && g.Members.All(m => m.Joined))), await world.TraceAsync());
        Assert.Equal(3, await world.OnWorldAsync(() => world.GroupManager.GetGroup(world.Player(tank.Id).Guid)?.MemberCount ?? 0));
        Assert.DoesNotContain(await world.OnWorldAsync(() => world.Coordinator.Events.ToArray()), e => e.Contains("gave up") || e.Contains("did not join"));

        await using WorldTestClient player = await world.World.EnterWorldAsync("CHATGROUPP", "Chatgroupp", AccountSecurity.Player);
        await player.CollectAsync();
        ulong bot = await world.OnWorldAsync(() => world.Player(healer.Id).Guid.Value);
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.Drives(healer.Id)));
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "what level are you?", healer.Name);

        ArcaneCore.World.Playerbots.Chat.PlayerbotChat chat = world.Bots.Chat!;
        // The answer comes from a chat worker thread in real time, so the wait is bounded by real time, not by game ticks.
        Assert.True(await world.RunUntilPacedAsync(() => chat.Status().Answered >= 1, 10_000), $"{chat.Status()}{Environment.NewLine}{await world.TraceAsync()}");
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
            && world.GroupManager.GetGroup(world.Player(second.Id).Guid) is null), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("left a group of bots nobody leads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABotLeftInsideAnInstanceWithoutAGroup_WalksOutThroughTheExit()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], dungeon: true);
        var bot = await world.AddBotAsync("Strandone", Human, Warrior, 12, [], EntranceArea);
        Assert.True(await world.OnWorldAsync(() => world.World.Services.GetRequiredService<TeleportFeature>().Teleports
            .TeleportTo(world.Player(bot.Id), 36, -16.4f, -383.07f, 61.78f, 0f)));
        Assert.True(await world.RunUntilAsync(30_000, () => world.Player(bot.Id) is { IsInWorld: true, MapId: 36 }), await world.TraceAsync());

        // Saved inside and logged in again: the login gate lets it into the dungeon it is in (PlayerbotMapPolicy.MayStayOnMap; the
        // default AllowedMaps [0, 1] refused it before, and three such faults disabled the bot).
        Assert.True((await world.Bots.StopAsync(bot.Name)).Success);
        PlayerbotOperationResult started = await world.Bots.StartAsync(bot.Name);
        Assert.True(started.Success, started.Code);
        Assert.True(await world.RunUntilAsync(30_000, () => world.Player(bot.Id) is { IsInWorld: true, MapId: 36 }), await world.TraceAsync());

        Assert.True(await world.RunUntilAsync(120_000, () => world.Player(bot.Id) is { IsInWorld: true, MapId: 0 }), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("Strandone is inside map 36 without a group", StringComparison.Ordinal));
        Assert.True(await world.RunUntilAsync(10_000, () => !world.Coordinator.Drives(bot.Id)), await world.TraceAsync());
    }
}
