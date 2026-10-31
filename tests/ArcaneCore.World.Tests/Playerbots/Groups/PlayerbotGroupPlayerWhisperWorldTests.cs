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
/// Which players are invited, trades, and an ignored player's whisper. Split from the former <c>PlayerbotGroupPlayerTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotGroupPlayerTestBase"/>.
/// </summary>
public sealed class PlayerbotGroupPlayerWhisperWorldTests : PlayerbotGroupPlayerTestBase
{
    [Fact]
    public async Task AStranger_OnTheSameGoal_IsInvitedOnlyOnceTheBotsLikeThem()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPSTRANGE", "Groupstrange", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askertwo", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Groupstrange", DuoQuest);

        // The waiting bot grinds and wanders; an invite only reaches a player within InvitePlayerYards, so each phase keeps the bot beside the player
        // (otherwise a "not invited" phase proves nothing and the final phase depends on where the bot happened to roam).
        Assert.False(await RunBesidePlayerUntilAsync(world, bot.Id, "Groupstrange", 30_000), "a stranger with no affinity and no AI chat opt-in is not invited");
        await RememberPlayerAsync(world, bot, "Groupstrange", 19);
        Assert.False(await RunBesidePlayerUntilAsync(world, bot.Id, "Groupstrange", 10_000), "affinity 19 is below InvitePlayerAffinity (20)");
        await RememberPlayerAsync(world, bot, "Groupstrange", 20);
        Assert.True(await RunBesidePlayerUntilAsync(world, bot.Id, "Groupstrange", 30_000), await world.TraceAsync());
    }

    [Fact]
    public async Task APlayerOnAnotherGoal_AndANoWhispersPlayer_AreNeverInvited()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await using WorldTestClient elsewhere = await world.World.EnterWorldAsync("GROUPELSE", "Groupelse", AccountSecurity.Player);
        await using WorldTestClient quiet = await world.World.EnterWorldAsync("GROUPQUIET", "Groupquiet", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerthree", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Groupquiet", DuoQuest);
        await RememberPlayerAsync(world, bot, "Groupelse", 90);                                         // liked, but not on the goal
        await RememberPlayerAsync(world, bot, "Groupquiet", 90, PlayerbotRelationFlags.NoWhispers);     // on the goal, liked, but asked for quiet

        await world.RunAsync(60_000);
        Assert.False(PlayerInvited(world), await world.TraceAsync());
    }

    /// <summary>The rival is on the goal and opted in to AI chat, so only the bot's rival flag keeps it out; forgiven, the same player is invited.</summary>
    [Fact]
    public async Task AnOptedInPlayerOnTheGoal_WhoIsARival_IsNeverInvited_UntilTheBotForgives()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await using WorldTestClient rival = await world.World.EnterWorldAsync("GROUPRIVAL", "Grouprival", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerrival", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Grouprival", DuoQuest);
        await rival.SendChatAsync(ChatType.Whisper, Language.Common, "ai on", bot.Name);
        Assert.True(await world.RunUntilPacedAsync(() => world.Bots.Chat!.Safety.Player("Grouprival")?.AiReplies == true, 10_000), $"the player opted in to AI chat{Environment.NewLine}{world.Bots.Chat!.Status()}{Environment.NewLine}{await world.TraceAsync()}");

        await RememberPlayerAsync(world, bot, "Grouprival", -80);
        Assert.True(await world.OnWorldAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Grouprival")!;
            return world.Bots.Social.Memory.TryGet(bot.Id, them.Guid.Value, out PlayerbotRelation relation) && world.Bots.Social.Memory.IsRival(relation);
        }), "the bot holds the player as a rival");
        await world.RunAsync(60_000);
        Assert.False(PlayerInvited(world), await world.TraceAsync());

        await RememberPlayerAsync(world, bot, "Grouprival", 0); // forgiven: the same opted-in player on the goal now qualifies
        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
    }

    /// <summary>"On the same quest" is the help-quest invite's reason, not a reason to open a trade: only a friend, a guildmate or a liked player gets one.</summary>
    [Theory]
    [InlineData(0, false)]  // a stranger on the same quest
    [InlineData(15, true)]  // liked: at least AcceptInviteAffinity (10)
    public async Task ATrade_IsOpenedForALikedPlayer_NotForAStrangerOnTheSameQuest(short affinity, bool opened)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Groups.InvitePlayers = false;
            options.MoveSpeed = 0.1f; // the bot stays within trade reach of the player
        });
        await using WorldTestClient player = await world.World.EnterWorldAsync("TRADEPLAYER", "Tradeplayer", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Traderbot", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Tradeplayer", DuoQuest);
        if (affinity != 0) await RememberPlayerAsync(world, bot, "Tradeplayer", affinity);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());

        // Away from the creatures (a bot in a fight opens no trade), the player a few yards from the bot.
        await world.OnWorldAsync(() =>
        {
            var far = new System.Numerics.Vector3(StartX - 1000f, StartY, StartZ);
            uint now = world.World.Host.World.NowMs;
            world.Player(bot.Id).Relocate(far.X, far.Y, far.Z, 0, now);
            world.World.Host.World.FindOnlinePlayer("Tradeplayer")!.Relocate(far.X + 3f, far.Y, far.Z, 0, now);
            return true;
        });
        await world.RunAsync(1_000);
        await player.CollectAsync();

        var dispatched = new System.Collections.Concurrent.ConcurrentQueue<WorldOpcode>();
        world.Session(bot.Id).ManagedDispatchObserver = opcode => dispatched.Enqueue(opcode);
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "trade me", bot.Name);
        // The player keeps standing beside the bot, which drifts while it waits (the answer comes after a typing delay).
        void Stick()
        {
            Player self = world.Player(bot.Id), other = world.World.Host.World.FindOnlinePlayer("Tradeplayer")!;
            other.Relocate(self.X + 2f, self.Y, self.Z, 0, world.World.Host.World.NowMs);
        }

        Assert.True(await world.RunUntilPacedAsync(() => { Stick(); return dispatched.Contains(WorldOpcode.CmsgMessagechat); }, 60_000), $"the bot answers the whisper{Environment.NewLine}{world.Bots.Chat!.Status()}{Environment.NewLine}{await world.TraceAsync()}");
        await world.RunUntilAsync(3_000, () => { Stick(); return dispatched.Contains(WorldOpcode.CmsgInitiateTrade); }); // the trade follows the answer
        Assert.Equal(opened, dispatched.Contains(WorldOpcode.CmsgInitiateTrade));
        Assert.Equal(opened, (await player.CollectAsync()).Any(packet => packet.Opcode == WorldOpcode.SmsgTradeStatus));
    }

    /// <summary>A player on the bot's ignore list is not heard: no answer to a whisper (the same whisper from a player not ignored is answered).</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AWhisperFromAnIgnoredPlayer_GetsNoAnswer(bool ignored, bool answered)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Groups.InvitePlayers = false;
        });
        await using WorldTestClient player = await world.World.EnterWorldAsync("IGNOREDONE", "Ignoredone", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Ignorerbot", Human, Warrior, 1, []);
        if (ignored)
            await world.OnWorldAsync(() =>
            {
                Social.SocialTestKit.Social(world.World.Host).Context.Friends.Get(world.Player(bot.Id)).Set(world.World.Host.World.FindOnlinePlayer("Ignoredone")!.Guid.Low, ArcaneCore.Kernel.Social.SocialFlags.Ignored, true);
                return true;
            });
        await player.CollectAsync();
        // The bot's answer reaches the player as a whisper (the player's own whisper comes back as WhisperInform; other bot talk is not a whisper).
        // A reader watches the player's socket for it while the world runs, so neither a late whisper nor a late answer depends on a quiet window.
        int replied = 0;
        using var stopReading = new CancellationTokenSource();
        Task reading = Task.Run(async () =>
        {
            // CollectAsync reads only once bytes are available, so stopping never cuts a packet in half.
            while (!stopReading.IsCancellationRequested && Volatile.Read(ref replied) == 0)
            {
                if ((await player.CollectAsync(TimeSpan.FromMilliseconds(50))).Any(p => p.Opcode == WorldOpcode.SmsgMessagechat && p.Payload.Length > 0 && p.Payload[0] == (byte)ChatType.Whisper))
                    Volatile.Write(ref replied, 1);
            }
        });
        var dispatched = new System.Collections.Concurrent.ConcurrentQueue<WorldOpcode>();
        world.Session(bot.Id).ManagedDispatchObserver = opcode => dispatched.Enqueue(opcode);
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "trade me", bot.Name);
        // The bot has 30 s of game time to answer. Paced, not RunAsync: the whisper is still on its way over the socket and the answer comes from the
        // chat worker after a typing delay, and a free-running manual clock spends all 30 s before either on a loaded machine.
        await world.RunUntilPacedAsync(() => Volatile.Read(ref replied) == 1, 30_000);
        // Chat the bot sent in the last ticks is still on its way to the player: wait for it (bounded) before stopping the reader.
        if (dispatched.Contains(WorldOpcode.CmsgMessagechat)) await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(10)));
        await stopReading.CancelAsync();
        await reading;
        Assert.True(answered == (Volatile.Read(ref replied) == 1), "ignoreHas=" + await world.OnWorldAsync(() => Social.SocialTestKit.Social(world.World.Host).Context.Friends.Get(world.Player(bot.Id)).Has(world.World.Host.World.FindOnlinePlayer("Ignoredone")!.Guid.Low, ArcaneCore.Kernel.Social.SocialFlags.Ignored)));
    }
}
