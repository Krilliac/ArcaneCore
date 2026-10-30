using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
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
    private const byte Human = 1, Orc = 2, Warrior = 1, Priest = 5, Mage = 8;
    private const uint Fireball = 133, LesserHeal = 2050;

    private static void Quiet(PlayerbotOptions options, int maxGroups = 16, int minTank = 0)
    {
        options.MoveSpeed = 0.1f;                         // bots stay where the test puts them
        options.Social.Guilds.Enabled = false;
        options.Groups.InvitePlayers = false;             // only a volunteer can be invited
        options.Groups.MaxGroups = maxGroups;
        options.Groups.MinTank = minTank;
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

    public enum Unplaceable { TooFar, OtherMap, OutOfLevelRange, Dead }

    /// <summary>(C) A player the coordinator cannot place gets no promise: not Ready, and no invitation ever comes.</summary>
    [Theory]
    [InlineData(Unplaceable.TooFar)]
    [InlineData(Unplaceable.OtherMap)]
    [InlineData(Unplaceable.OutOfLevelRange)]
    [InlineData(Unplaceable.Dead)]
    public async Task AVolunteerTheCoordinatorCannotPlace_IsNotReady_AndGetsNoInvitation(Unplaceable why)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => Quiet(options));
        await world.World.PrepareCharacterAsync("VOLNOPE", "Volnope", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volnopebot", Warrior, why == Unplaceable.OutOfLevelRange ? (byte)10 : (byte)1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLNOPE", "Volnope", AccountSecurity.Player);
        await PlaceAsync(world, "Volnope", why);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, bot.Id, "Volnope");

        // Headcount is not short (one bot plus the player make the group of two): the honest answer is that the player cannot be placed.
        Assert.Equal(PlayerbotVolunteerOutcome.CannotPlace, result.Outcome);
        Assert.False(world.Coordinator.IsVolunteer(Online(world, "Volnope").Guid, (PlayerbotGroupGoalKind.Quest, OgreEntry)), "an unplaceable volunteer was kept");
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
                case Unplaceable.Dead:
                    player.Health = 0;
                    player.Map!.Combat.KillPlayer(player);
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
    private static async Task<(string? Said, string? Inviter)> AskForInviteAsync(GroupTestWorld world, WorldTestClient player, (Guid Id, string Name) bot, bool expectInvite)
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
        Task<(ChatMessage Whisper, string Inviter)> both = expectInvite ? PartyTestHost.ReadWhisperAndInviteAsync(player, botGuid)
            : PartyTestHost.ReadWhisperFromAsync(player, botGuid).ContinueWith(t => (t.Result, string.Empty), TaskScheduler.Default);
        await world.RunUntilPacedAsync(() => both.IsCompleted, 20_000);
        if (!both.IsCompleted) return (null, null);
        (ChatMessage whisper, string inviter) = await both;
        return (whisper.Text, inviter);
    }

    private static readonly string[] ReadyLines = [.. PlayerbotVoiceTemplates.Lines["voice.reply.lfg.ready"]];
    private static readonly string[] CantLines = [.. PlayerbotVoiceTemplates.Lines["voice.reply.lfg.cant"]];
    private static readonly string[] ExpiredLines = [.. PlayerbotVoiceTemplates.Lines["voice.reply.lfg.expired"]];

    /// <summary>(E) Control and the live bug: "sure, inv in a sec" goes only to a player the bot can really group with.</summary>
    [Fact]
    public async Task TheBotSaysReadyToAPlayerItCanGroup_AndTheInvitationFollows()
    {
        await using GroupTestWorld world = await StartVoiceWorldAsync();
        await world.World.PrepareCharacterAsync("VOLVOICE", "Volvoice", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volvoicebot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLVOICE", "Volvoice", AccountSecurity.Player);

        (string? said, string? inviter) = await AskForInviteAsync(world, player, bot, expectInvite: true);

        Assert.NotNull(said);
        Assert.Contains(said, ReadyLines);
        Assert.Equal(bot.Name, inviter); // the whisper and the invitation arrive in either order
    }

    [Fact]
    public async Task TheBotDoesNotSayReady_ToAPlayerItCannotPlace()
    {
        await using GroupTestWorld world = await StartVoiceWorldAsync();
        await world.World.PrepareCharacterAsync("VOLMUTE", "Volmute", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volmutebot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLMUTE", "Volmute", AccountSecurity.Player);
        await PlaceAsync(world, "Volmute", Unplaceable.TooFar);

        (string? said, _) = await AskForInviteAsync(world, player, bot, expectInvite: false);

        Assert.NotNull(said); // the voice path works: the bot answers, and honestly
        Assert.Contains(said, CantLines);
        Assert.DoesNotContain(said, ReadyLines);
        Assert.False(PlayerInvited(world));
    }

    // --- the roles, the cap on the Match path, expiry ---------------------------------------------------------------------------------

    /// <summary>With the default tank quota a higher-level warrior volunteer would win the tank slot and lead: a bot tanks and leads instead.</summary>
    [Fact]
    public async Task AWarriorVolunteerAboveTheBotWarrior_IsReady_AndTheBotTanksAndLeads()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => Quiet(options, minTank: 1));
        await world.World.PrepareCharacterAsync("VOLTANK", "Voltank", AccountSecurity.Player);
        var warrior = await WaitingBotAsync(world, "Voltankbot", Warrior, 1, []);
        await WaitingBotAsync(world, "Voltankmage", Mage, 1, [Fireball]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLTANK", "Voltank", AccountSecurity.Player);
        await world.OnWorldAsync(() =>
        {
            Online(world, "Voltank").Level = 3;
            return true;
        });

        PlayerbotVolunteerResult result = await VolunteerAsync(world, warrior.Id, "Voltank");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        await AssertInvitedByAsync(world, player, warrior.Name);
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Members.Any(m => m.Real));
        Assert.Equal(warrior.Id, group.LeaderBotId);
        Assert.Equal(PlayerbotGroupRole.Tank, group.Members.Single(m => m.BotId == warrior.Id).Role);
    }

    /// <summary>The volunteer waits (Noted, a true count), the bots then become enough while the cap is full: the Match path forms the group.</summary>
    [Fact]
    public async Task ANotedVolunteer_IsPlacedByMatch_WhileBotGroupsFillTheCap()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLMP", "Volmp", AccountSecurity.Player);
        await FillTheCapAsync(world, "Volmpfill", 3, Mage, [Fireball]);
        var first = await WaitingBotAsync(world, "Volmpone", Mage, 1, [Fireball]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLMP", "Volmp", AccountSecurity.Player);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, first.Id, "Volmp");

        Assert.Equal(PlayerbotVolunteerOutcome.Noted, result.Outcome);
        Assert.Equal(1, result.Missing); // three are asked for: one bot and the player are two
        Assert.Single(world.Coordinator.Groups);
        var second = await world.AddBotAsync("Volmptwo", Human, Mage, 1, [Fireball]); // joins the volunteer at once: it is never seen waiting
        Assert.True(await world.RunUntilPacedAsync(() => PlayerInvited(world), 30_000), "the volunteer was not placed by Match" + Environment.NewLine + await world.TraceAsync());
        Assert.Equal(2, world.Coordinator.Groups.Count); // beyond MaxGroups = 1: a group with a real player is not held back by the cap
        Assert.Contains(world.Coordinator.Groups, g => g.Members.Any(m => m.Real) && g.Members.Any(m => m.BotId == first.Id) && g.Members.Any(m => m.BotId == second.Id));
    }

    /// <summary>A volunteer that ran out unplaced hears one honest whisper from the bot.</summary>
    [Fact]
    public async Task AnExpiredVolunteer_HearsTheBotSayItCouldNotGetAGroupTogether()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Quiet(options);
            options.Chat.Enabled = true;
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Chat.Voice.LookingForGroup = false;
        });
        await world.World.PrepareCharacterAsync("VOLEXP", "Volexp", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volexpbot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLEXP", "Volexp", AccountSecurity.Player);
        Assert.True(await world.RunUntilAsync(10_000, () => world.Bots.Chat!.Responder is not null), await world.TraceAsync());
        Assert.Equal(PlayerbotVolunteerOutcome.Noted, (await VolunteerAsync(world, bot.Id, "Volexp")).Outcome);
        ulong botGuid = (await world.OnWorldAsync(() => world.Player(bot.Id).Guid)).Value;

        await world.RunAsync(PlayerbotGroupCoordinator.VolunteerLifeMs + 15_000);

        string? said = null;
        for (int i = 0; i < 6 && said is null; i++) // other whispers of the bot, if any, are not the one asked about
        {
            Task<ChatMessage> reply = PartyTestHost.ReadWhisperFromAsync(player, botGuid);
            await world.RunUntilPacedAsync(() => reply.IsCompleted, 20_000);
            if (!reply.IsCompleted) break;
            string text = (await reply).Text;
            if (ExpiredLines.Contains(text)) said = text;
        }

        Assert.NotNull(said);
    }

    public enum Engaged { Ungrouped, InvitePending, Grouped }

    /// <summary>Only a volunteer who is still free is told: one with an invitation pending or in a group of their own hears nothing.</summary>
    [Theory]
    [InlineData(Engaged.Ungrouped)]
    [InlineData(Engaged.InvitePending)]
    [InlineData(Engaged.Grouped)]
    public async Task AnExpiringVolunteer_IsQueuedForTheWhisper_OnlyWhileFree(Engaged engaged)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Quiet(options);
            options.Chat.Voice.Enabled = false; // the voice would take the queue
        });
        await world.World.PrepareCharacterAsync("VOLQ", "Volq", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volqbot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLQ", "Volq", AccountSecurity.Player);
        Assert.Equal(PlayerbotVolunteerOutcome.Noted, (await VolunteerAsync(world, bot.Id, "Volq")).Outcome);
        await world.RunAsync(PlayerbotGroupCoordinator.VolunteerLifeMs - 20_000); // an invitation lapses on its own: engage the player just before the end
        if (engaged != Engaged.Ungrouped)
        {
            await world.OnWorldAsync(() =>
            {
                world.GroupManager.Invite(world.Player(bot.Id), "Volq");
                if (engaged == Engaged.Grouped) world.GroupManager.Accept(Online(world, "Volq"));
                return true;
            });
        }

        await world.RunAsync(40_000);

        var queued = await world.OnWorldAsync(() => world.Coordinator.TakeExpiredVolunteers());
        if (engaged == Engaged.Ungrouped) Assert.Equal(bot.Id, Assert.Single(queued).BotId);
        else Assert.Empty(queued);
    }

    // --- review cycle 2 ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A volunteer placed by Form is not invited until the forming ticks, so the group manager does not know them yet: whispering a second
    /// waiting bot in that window must not book them into a second group.
    /// </summary>
    [Fact]
    public async Task AVolunteerAlreadyPlacedInAFormingGroup_IsNotBookedIntoASecondOne()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLDB", "Voldb", AccountSecurity.Player);
        await FillTheCapAsync(world, "Voldbfill", 2, Warrior, []);
        var a = await WaitingBotAsync(world, "Voldba", Warrior, 1, []);
        var b = await WaitingBotAsync(world, "Voldbb", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLDB", "Voldb", AccountSecurity.Player);

        (PlayerbotVolunteerResult first, PlayerbotVolunteerResult second) = await world.OnWorldAsync(() =>
        {
            Player real = Online(world, "Voldb");
            return (world.Coordinator.Volunteer(a.Id, real), world.Coordinator.Volunteer(b.Id, real)); // back to back: no tick between
        });

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, first.Outcome);
        Assert.Equal(PlayerbotVolunteerOutcome.Grouped, second.Outcome);
        Assert.Equal(2, world.Coordinator.Groups.Count); // the cap group and the one with the player
        Assert.Single(world.Coordinator.Groups, g => g.Members.Any(m => m.Real));
        Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == b.Id);
    }

    /// <summary>The player is a tank class and no bot tanks: a bot leads, and a healer bot is preferred to a damage one.</summary>
    [Fact]
    public async Task WhenThePlayerTanks_AHealerBotLeads_NotADamageBot()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Quiet(options, minTank: 1);
            options.Groups.MinHealer = 1;
        });
        await world.World.PrepareCharacterAsync("VOLHEAL", "Volheal", AccountSecurity.Player);
        var mage = await WaitingBotAsync(world, "Volhealmage", Mage, 1, [Fireball]); // waits longest
        var priest = await WaitingBotAsync(world, "Volhealprie", Priest, 1, [LesserHeal]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLHEAL", "Volheal", AccountSecurity.Player);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, mage.Id, "Volheal");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Members.Any(m => m.Real));
        Assert.Equal(PlayerbotGroupRole.Tank, group.Members.Single(m => m.Real).Role);
        Assert.Equal(PlayerbotGroupRole.Healer, group.Members.Single(m => m.BotId == priest.Id).Role);
        Assert.Equal(priest.Id, group.LeaderBotId);
    }

    /// <summary>A full cap lets only a goal with a live volunteer through: another team's waiting goal is passed over without building candidates.</summary>
    [Fact]
    public async Task WhenTheCapIsFull_AGoalWithoutALiveVolunteer_StaysUnformedAndUnbuilt()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLCS", "Volcs", AccountSecurity.Player);
        await FillTheCapAsync(world, "Volcsfill", 3, Mage, [Fireball]);
        var first = await WaitingBotAsync(world, "Volcsone", Mage, 1, [Fireball]);
        var horde = await world.AddBotAsync("Volcshorde", Orc, Warrior, 1, []);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == horde.Id)), await world.TraceAsync());
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLCS", "Volcs", AccountSecurity.Player);
        Assert.Equal(PlayerbotVolunteerOutcome.Noted, (await VolunteerAsync(world, first.Id, "Volcs")).Outcome);
        long before = await world.OnWorldAsync(() => world.Coordinator.CapSkippedGoals);

        await world.RunAsync(10_000);

        Assert.True(await world.OnWorldAsync(() => world.Coordinator.CapSkippedGoals) > before, "the other team's goal was not passed over");
        Assert.Single(world.Coordinator.Groups);
        Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == horde.Id);
    }

    // --- an instance goal: a volunteer plan must hold a waiting anchor ----------------------------------------------------------------
    //
    // Deadmines (map 36, fallback band 15-28), a group of three that needs one tank (MinTank = 1). The bots are all mages, so the (human
    // warrior) player is the only tank and is always placed; the bots then make the group up oldest first. Level 60 bots wait only on a
    // derived reason while a level 20 bot, in band, anchors the goal: a plan of derived-only bots (and the player) must not be promised.

    private static void DungeonQuiet(PlayerbotOptions options)
    {
        Quiet(options, minTank: 1);
        options.Social.Lists.Enabled = false; // no companion reason: the level 60 bots wait on the anchor alone
        options.Groups.LevelRange = 50;
    }

    private static async Task<(Guid Id, string Name)> WaitingAtTheEntranceAsync(GroupTestWorld world, string name, byte level)
    {
        var bot = await world.AddBotAsync(name, Human, Mage, level, [Fireball], EntranceArea);
        Assert.True(await world.RunUntilAsync(120_000, () => world.HasQuest(bot.Id, DungeonQuest) && world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());
        return bot;
    }

    private static Task PlaceAtTheEntranceAsync(GroupTestWorld world, string name, byte? level = null)
        => world.OnWorldAsync(() =>
        {
            Player player = Online(world, name);
            if (level is { } set) player.Level = set;
            player.Relocate(EntranceArea.X, EntranceArea.Y, EntranceArea.Z, 0, world.World.Host.World.NowMs);
            return true;
        });

    private static async Task<uint> SinceAsync(GroupTestWorld world, Guid bot)
        => await world.OnWorldAsync(() => world.Coordinator.WaitingBots.Single(w => w.BotId == bot).SinceMs);

    private static async Task StopSeedAsync(GroupTestWorld world, (Guid Id, string Name) seed)
    {
        var stopped = await world.Bots.StopAsync(seed.Name);
        Assert.True(stopped.Success, stopped.Code);
        await world.RunAsync(2 * PlayerbotGroupCoordinator.SeedRecheckMs);
    }

    /// <summary>
    /// The volunteer's bot is the youngest waiter and holds no anchor of its own. The size-1 list (that bot, then the longest waiting) is two
    /// derived-only bots, and with the tanking player it makes a plan Match accepts but that holds no waiting anchor; the full list is the
    /// one whose oldest two bots include the in-band anchor. The unanchored plan is passed over and the full list forms the group.
    /// </summary>
    [Fact]
    public async Task ForAnInstanceGoal_APlanWithNoWaitingAnchor_IsPassedOver_AndTheNextListFormsTheGroupWithTheAnchor()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], DungeonQuiet, dungeon: true);
        await world.World.PrepareCharacterAsync("VOLANC", "Volanc", AccountSecurity.Player);
        var seed = await WaitingAtTheEntranceAsync(world, "Volseed", 20);
        var oldest = await WaitingAtTheEntranceAsync(world, "Voldoldest", 60);
        var anchor = await WaitingAtTheEntranceAsync(world, "Volanchor", 20);
        var youngest = await WaitingAtTheEntranceAsync(world, "Voldyoungest", 60);
        await StopSeedAsync(world, seed); // the first anchor leaves: the anchor is now the 2nd oldest of the three that wait
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLANC", "Volanc", AccountSecurity.Player);
        await PlaceAtTheEntranceAsync(world, "Volanc", level: 20); // in band, so a plan with the player passes Match's qualifying rule

        // Preconditions: three bots wait, in the order oldest derived, anchor, youngest derived; nothing formed (no bot can tank).
        foreach (var bot in new[] { oldest, anchor, youngest })
            Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
        uint oldestSince = await SinceAsync(world, oldest.Id), anchorSince = await SinceAsync(world, anchor.Id), youngestSince = await SinceAsync(world, youngest.Id);
        Assert.True(unchecked((int)(anchorSince - oldestSince)) > 0 && unchecked((int)(youngestSince - anchorSince)) > 0, "the waiters are not in the expected order");
        Assert.Empty(world.Coordinator.Groups);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, youngest.Id, "Volanc");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Members.Any(m => m.Real));
        Assert.Equal(3, group.Members.Count);
        Assert.Contains(group.Members, m => m.BotId == anchor.Id); // the plan holds a waiting anchor
        Assert.Contains(group.Members, m => m.BotId == oldest.Id);
        Assert.DoesNotContain(group.Members, m => m.BotId == youngest.Id);
    }

    /// <summary>
    /// No list makes a plan with a waiting anchor (the anchor is the youngest waiter, left out of both lists): the volunteer is not promised
    /// anything, no group forms, and the volunteer stays kept across the Match ticks (a rejected plan loses nobody).
    /// </summary>
    [Fact]
    public async Task ForAnInstanceGoal_WhenNoPlanHoldsAWaitingAnchor_TheVolunteerIsKept_AndNoGroupForms()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], DungeonQuiet, dungeon: true);
        await world.World.PrepareCharacterAsync("VOLKEEP", "Volkeep", AccountSecurity.Player);
        var seed = await WaitingAtTheEntranceAsync(world, "Volkseed", 20);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLKEEP", "Volkeep", AccountSecurity.Player);
        await PlaceAtTheEntranceAsync(world, "Volkeep", level: 20);
        // One bot waits: the group lacks one (a true count), so the volunteer is kept.
        PlayerbotVolunteerResult noted = await VolunteerAsync(world, seed.Id, "Volkeep");
        Assert.Equal(PlayerbotVolunteerOutcome.Noted, noted.Outcome);
        Assert.Equal(1, noted.Missing);
        var key = await world.OnWorldAsync(() => world.Coordinator.WaitingBots.Single(w => w.BotId == seed.Id).Goal.Key);
        ObjectGuid volunteer = Online(world, "Volkeep").Guid;
        Assert.True(world.Coordinator.IsVolunteer(volunteer, key));

        // The player steps out of reach while the others arrive, so the group does not form on the way: two derived-only bots, then the
        // anchor (the youngest), then the first anchor leaves. No plan of the three that wait can then hold the anchor.
        await world.OnWorldAsync(() =>
        {
            Player real = Online(world, "Volkeep");
            real.Relocate(real.X + 3000f, real.Y, real.Z, 0, world.World.Host.World.NowMs);
            return true;
        });
        var first = await WaitingAtTheEntranceAsync(world, "Volkfirst", 60);
        var second = await WaitingAtTheEntranceAsync(world, "Volksecond", 60);
        var anchor = await WaitingAtTheEntranceAsync(world, "Volkanchor", 20);
        await StopSeedAsync(world, seed);
        foreach (var bot in new[] { first, second, anchor })
            Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
        Assert.True(world.Coordinator.IsVolunteer(volunteer, key), "the volunteer expired before the check");
        Assert.Empty(world.Coordinator.Groups);

        await PlaceAtTheEntranceAsync(world, "Volkeep"); // back in reach: every Match tick now plans with the volunteer
        // The window below must end before the volunteer would expire on its own, or a kept volunteer could not be told from an expired one.
        Assert.True(world.Coordinator.VolunteerRemainingMs(volunteer) > 65_000, $"setup used too much of the volunteer's life ({world.Coordinator.VolunteerRemainingMs(volunteer)} ms left)");

        Assert.False(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Count > 0 || PlayerInvited(world)), "a group formed without a waiting anchor" + Environment.NewLine + await world.TraceAsync());
        Assert.True(world.Coordinator.IsVolunteer(volunteer, key), "the volunteer was dropped when its plan was rejected");
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.Null(await world.OnWorldAsync(() => world.GroupManager.GetInvite(volunteer)));
        foreach (var bot in new[] { first, second, anchor })
            Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
    }
}
