using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A real player answers a waiting bot's LFM ("invite me"): <see cref="PlayerbotGroupCoordinator.Volunteer"/> may promise the invitation
/// (<see cref="PlayerbotVolunteerOutcome.Ready"/>) only when a dry-run match over the bot's real candidates includes the player, the group
/// then forms although the bots' own quest-group cap is full, the player is placed ahead of surplus damage bots, and the bot's whisper says
/// "ready" only for <see cref="PlayerbotVolunteerOutcome.Ready"/>. <c>InvitePlayers</c> is off throughout: only the volunteer path can invite.
/// </summary>
public sealed class PlayerbotVolunteerWorldTests
{
    private const byte Human = 1, Warrior = 1, Mage = 8;
    private const uint Fireball = 133;

    private static void Quiet(PlayerbotOptions options, int maxGroups = 16)
    {
        options.MoveSpeed = 0.1f;                         // bots stay where the test puts them
        options.Social.Guilds.Enabled = false;
        options.Groups.InvitePlayers = false;             // only a volunteer can be invited
        options.Groups.MaxGroups = maxGroups;
        options.Groups.MinTank = 0;
        options.Groups.MinHealer = 0;
        options.Groups.Cooperation.Recruit = false;
    }

    private static bool PlayerInvited(GroupTestWorld world) => world.Coordinator.Groups.Any(g => g.Members.Any(m => m.Real && m.Invited));

    private static Player Online(GroupTestWorld world, string name) => world.World.Host.World.FindOnlinePlayer(name)!;

    private static Task<PlayerbotVolunteerResult> VolunteerAsync(GroupTestWorld world, Guid bot, string playerName)
        => world.OnWorldAsync(() => world.Coordinator.Volunteer(bot, Online(world, playerName)));

    /// <summary>Bots (one group of <paramref name="size"/>) that take the cap's single slot and, with the clock crawling, keep it.</summary>
    private static async Task FillTheCapAsync(GroupTestWorld world, string prefix, int size, byte cls, uint[] spells)
    {
        for (int i = 0; i < size; i++)
            await world.AddBotAsync(prefix + (char)('a' + i), Human, cls, 1, spells);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 1), "the cap group did not form" + Environment.NewLine + await world.TraceAsync());
    }

    private static async Task<(Guid Id, string Name)> WaitingBotAsync(GroupTestWorld world, string name, byte cls, byte level, uint[] spells)
    {
        var bot = await world.AddBotAsync(name, Human, cls, level, spells);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());
        return bot;
    }

    /// <summary>The player gets the leader bot's ordinary invitation within 30 s of game time.</summary>
    private static async Task AssertInvitedByAsync(GroupTestWorld world, WorldTestClient player, string botName)
    {
        Assert.True(await world.RunUntilPacedAsync(() => PlayerInvited(world), 30_000), "no group formed with the volunteer" + Environment.NewLine + await world.TraceAsync());
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite, TimeSpan.FromSeconds(30));
        Assert.Equal(botName, new PacketReader(invite).ReadCString());
    }

    /// <summary>(B) The plain case, no cap pressure: Ready, then the invitation of the waiting bot, which leads.</summary>
    [Fact]
    public async Task AVolunteerBesideALoneWaitingBot_IsReady_AndIsInvitedByThatBot()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => Quiet(options));
        await world.World.PrepareCharacterAsync("VOLHAPPY", "Volhappy", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volbotone", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLHAPPY", "Volhappy", AccountSecurity.Player);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, bot.Id, "Volhappy");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        await AssertInvitedByAsync(world, player, bot.Name);
        Assert.Equal(bot.Id, world.Coordinator.Groups.Single().LeaderBotId);
    }

    /// <summary>
    /// (A) Live bug: bot groups hold every slot of MaxGroups, so Match returned before it looked at the volunteer and "inv in a sec"
    /// was never followed by an invitation. A group with a real player is not a bot quest group: it must not wait for a free slot.
    /// </summary>
    [Fact]
    public async Task WhenBotGroupsFillTheCap_AVolunteerIsStillReady_AndInvited()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLCAP", "Volcap", AccountSecurity.Player);
        await FillTheCapAsync(world, "Volcapfill", 2, Warrior, []);
        var lone = await WaitingBotAsync(world, "Volcaplone", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLCAP", "Volcap", AccountSecurity.Player);
        Assert.Single(world.Coordinator.Groups); // the cap is used up by the bots' own group

        PlayerbotVolunteerResult result = await VolunteerAsync(world, lone.Id, "Volcap");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        await AssertInvitedByAsync(world, player, lone.Name);
        Assert.Contains(world.Coordinator.Groups, g => g.LeaderBotId == lone.Id);
    }

    /// <summary>(D) Surplus: waiting damage bots already make a plan of their own; the volunteer must not be locked out of it.</summary>
    [Fact]
    public async Task WhenAsManyDamageBotsWaitAsTheGroupHolds_TheVolunteerIsStillPlacedInTheGroup()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLSURP", "Volsurp", AccountSecurity.Player);
        await FillTheCapAsync(world, "Volsurpfill", 3, Mage, [Fireball]); // the cap is used up, so the next three keep waiting
        var first = await WaitingBotAsync(world, "Volsurpone", Mage, 1, [Fireball]);
        await WaitingBotAsync(world, "Volsurptwo", Mage, 1, [Fireball]);
        await WaitingBotAsync(world, "Volsurpthree", Mage, 1, [Fireball]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLSURP", "Volsurp", AccountSecurity.Player);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, first.Id, "Volsurp");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        Assert.True(await world.RunUntilPacedAsync(() => PlayerInvited(world), 30_000), "the volunteer was not placed in a group" + Environment.NewLine + await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Members.Any(m => m.Real));
        Assert.Equal(3, group.Goal.Size);
        Assert.Single(group.Members, m => m.Real);
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite, TimeSpan.FromSeconds(30));
        Assert.Equal(group.Members.Single(m => m.BotId == group.LeaderBotId).Name, new PacketReader(invite).ReadCString());
    }

    public enum Unplaceable { TooFar, OtherMap, OutOfLevelRange }

    /// <summary>(C) A player the coordinator cannot place gets no promise: not Ready, and no invitation ever comes.</summary>
    [Theory]
    [InlineData(Unplaceable.TooFar)]
    [InlineData(Unplaceable.OtherMap)]
    [InlineData(Unplaceable.OutOfLevelRange)]
    public async Task AVolunteerTheCoordinatorCannotPlace_IsNotReady_AndGetsNoInvitation(Unplaceable why)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => Quiet(options));
        await world.World.PrepareCharacterAsync("VOLNOPE", "Volnope", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volnopebot", Warrior, why == Unplaceable.OutOfLevelRange ? (byte)10 : (byte)1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLNOPE", "Volnope", AccountSecurity.Player);
        await PlaceAsync(world, "Volnope", why);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, bot.Id, "Volnope");

        Assert.NotEqual(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        // "Noted, nothing missing" is what the whisper reads as "ready, inv in a sec": a promise nobody can keep for this player.
        Assert.False(result.Outcome == PlayerbotVolunteerOutcome.Noted && result.Missing == 0, "Noted with Missing == 0 promises an invitation");
        await world.RunAsync(30_000);
        Assert.False(PlayerInvited(world), "an unplaceable volunteer was invited");
        Assert.Null(await world.OnWorldAsync(() => world.GroupManager.GetInvite(Online(world, "Volnope").Guid)));
        Assert.DoesNotContain(world.Coordinator.Groups, g => g.Members.Any(m => m.Real));
    }

    private static async Task PlaceAsync(GroupTestWorld world, string name, Unplaceable why)
    {
        await world.OnWorldAsync(() =>
        {
            Player player = Online(world, name);
            switch (why)
            {
                case Unplaceable.TooFar: // 3000 yards from the meeting point, beyond the 1500 yard limit
                    player.Relocate(player.X + 3000f, player.Y, player.Z, 0, world.World.Host.World.NowMs);
                    break;
                case Unplaceable.OtherMap:
                    world.World.Services.GetRequiredService<ArcaneCore.World.Teleport.TeleportFeature>().Teleports.TeleportTo(player, 1, player.X, player.Y, player.Z, 0f);
                    break;
            }

            return true;
        });
        if (why == Unplaceable.OtherMap) // the teleport is applied on a later tick
            Assert.True(await world.RunUntilAsync(10_000, () => Online(world, name).MapId == 1), "the volunteer never arrived on map 1" + Environment.NewLine + await world.TraceAsync());
    }

    // --- the bot's whisper (voice) --------------------------------------------------------------------------------------------------

    private static async Task<GroupTestWorld> StartVoiceWorldAsync()
        => await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            Quiet(options);
            options.Chat.Enabled = true;
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Chat.Voice.LookingForGroup = false; // the bots post no calls of their own over the one the test puts on the board
        });

    /// <summary>The bot has an open LFM post; the player whispers "inv me" and the bot's whisper (or its silence, within 20 s of game time) is returned.</summary>
    private static async Task<string?> AskForInviteAsync(GroupTestWorld world, WorldTestClient player, (Guid Id, string Name) bot)
    {
        PlayerbotChat chat = world.Bots.Chat!;
        Assert.True(await world.RunUntilAsync(10_000, () => chat.Responder is not null), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            chat.Voice.Post(bot.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, chat.NowMs, "the ogre", MinLevel: 1));
            return true;
        });
        ulong botGuid = (await world.OnWorldAsync(() => world.Player(bot.Id).Guid)).Value;
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "inv me pls", bot.Name);
        Task<ChatMessage> reply = PartyTestHost.ReadWhisperFromAsync(player, botGuid);
        await world.RunUntilPacedAsync(() => reply.IsCompleted, 20_000);
        return reply.IsCompleted ? (await reply).Text : null;
    }

    private static readonly string[] ReadyLines = [.. PlayerbotVoiceTemplates.Lines["voice.reply.lfg.ready"]];

    /// <summary>(E) Control and the live bug: "sure, inv in a sec" goes only to a player the bot can really group with.</summary>
    [Fact]
    public async Task TheBotSaysReadyToAPlayerItCanGroup_AndTheInvitationFollows()
    {
        await using GroupTestWorld world = await StartVoiceWorldAsync();
        await world.World.PrepareCharacterAsync("VOLVOICE", "Volvoice", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volvoicebot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLVOICE", "Volvoice", AccountSecurity.Player);

        string? said = await AskForInviteAsync(world, player, bot);

        Assert.NotNull(said);
        Assert.Contains(said, ReadyLines);
        await AssertInvitedByAsync(world, player, bot.Name);
    }

    [Fact]
    public async Task TheBotDoesNotSayReady_ToAPlayerItCannotPlace()
    {
        await using GroupTestWorld world = await StartVoiceWorldAsync();
        await world.World.PrepareCharacterAsync("VOLMUTE", "Volmute", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volmutebot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLMUTE", "Volmute", AccountSecurity.Player);
        await PlaceAsync(world, "Volmute", Unplaceable.TooFar);

        string? said = await AskForInviteAsync(world, player, bot);

        if (said is not null) Assert.DoesNotContain(said, ReadyLines); // silence or a "still need N" reply are both fine; a promise is not
        Assert.False(PlayerInvited(world));
    }
}
