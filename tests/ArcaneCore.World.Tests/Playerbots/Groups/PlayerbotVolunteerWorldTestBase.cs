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
public abstract class PlayerbotVolunteerWorldTestBase
{
    private protected const byte Human = 1, Orc = 2, Warrior = 1, Priest = 5, Mage = 8;
    private protected const uint Fireball = 133, LesserHeal = 2050;

    private protected static void Quiet(PlayerbotOptions options, int maxGroups = 16, int minTank = 0)
    {
        options.MoveSpeed = 0.1f;                         // bots stay where the test puts them
        options.Social.Guilds.Enabled = false;
        options.Groups.InvitePlayers = false;             // only a volunteer can be invited
        options.Groups.MaxGroups = maxGroups;
        options.Groups.MinTank = minTank;
        options.Groups.MinHealer = 0;
        options.Groups.Cooperation.Recruit = false;
    }

    private protected static bool PlayerInvited(GroupTestWorld world) => world.Coordinator.Groups.Any(g => g.Members.Any(m => m.Real && m.Invited));

    private protected static Player Online(GroupTestWorld world, string name) => world.World.Host.World.FindOnlinePlayer(name)!;

    private protected static Task<PlayerbotVolunteerResult> VolunteerAsync(GroupTestWorld world, Guid bot, string playerName)
        => world.OnWorldAsync(() => world.Coordinator.Volunteer(bot, Online(world, playerName)));

    /// <summary>Bots (one group of <paramref name="size"/>) that take the cap's single slot and, with the clock crawling, keep it.</summary>
    private protected static async Task FillTheCapAsync(GroupTestWorld world, string prefix, int size, byte cls, uint[] spells)
    {
        for (int i = 0; i < size; i++)
            await world.AddBotAsync(prefix + (char)('a' + i), Human, cls, 1, spells);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 1), "the cap group did not form" + Environment.NewLine + await world.TraceAsync());
    }

    private protected static async Task<(Guid Id, string Name)> WaitingBotAsync(GroupTestWorld world, string name, byte cls, byte level, uint[] spells)
    {
        var bot = await world.AddBotAsync(name, Human, cls, level, spells);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());
        return bot;
    }

    /// <summary>The player gets the leader bot's ordinary invitation within 30 s of game time.</summary>
    private protected static async Task AssertInvitedByAsync(GroupTestWorld world, WorldTestClient player, string botName)
    {
        Assert.True(await world.RunUntilPacedAsync(() => PlayerInvited(world), 30_000), "no group formed with the volunteer" + Environment.NewLine + await world.TraceAsync());
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite, TimeSpan.FromSeconds(30));
        Assert.Equal(botName, new PacketReader(invite).ReadCString());
    }

    public enum Unplaceable { TooFar, OtherMap, OutOfLevelRange, Dead }

    private protected static async Task PlaceAsync(GroupTestWorld world, string name, Unplaceable why)
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

    private protected static async Task<GroupTestWorld> StartVoiceWorldAsync()
        => await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            Quiet(options);
            options.Chat.Enabled = true;
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Chat.Voice.LookingForGroup = false; // the bots post no calls of their own over the one the test puts on the board
        });

    /// <summary>The bot has an open LFM post; the player whispers "inv me" and the bot's whisper (or its silence, within 20 s of game time) is returned.</summary>
    private protected static async Task<(string? Said, string? Inviter)> AskForInviteAsync(GroupTestWorld world, WorldTestClient player, (Guid Id, string Name) bot, bool expectInvite)
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

    private protected static readonly string[] ReadyLines = [.. PlayerbotVoiceTemplates.Lines["voice.reply.lfg.ready"]];
    private protected static readonly string[] CantLines = [.. PlayerbotVoiceTemplates.Lines["voice.reply.lfg.cant"]];
    private protected static readonly string[] ExpiredLines = [.. PlayerbotVoiceTemplates.Lines["voice.reply.lfg.expired"]];

    public enum Engaged { Ungrouped, InvitePending, Grouped }

    // --- an instance goal: a volunteer plan must hold a waiting anchor ----------------------------------------------------------------
    //
    // Deadmines (map 36, fallback band 15-28), a group of three that needs one tank (MinTank = 1). The bots are all mages, so the (human
    // warrior) player is the only tank and is always placed; the bots then make the group up oldest first. Level 60 bots wait only on a
    // derived reason while a level 20 bot, in band, anchors the goal: a plan of derived-only bots (and the player) must not be promised.

    private protected static void DungeonQuiet(PlayerbotOptions options)
    {
        Quiet(options, minTank: 1);
        options.Social.Lists.Enabled = false; // no companion reason: the level 60 bots wait on the anchor alone
        options.Groups.LevelRange = 50;
    }

    private protected static async Task<(Guid Id, string Name)> WaitingAtTheEntranceAsync(GroupTestWorld world, string name, byte level)
    {
        var bot = await world.AddBotAsync(name, Human, Mage, level, [Fireball], EntranceArea);
        Assert.True(await world.RunUntilAsync(120_000, () => world.HasQuest(bot.Id, DungeonQuest) && world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());
        return bot;
    }

    private protected static Task PlaceAtTheEntranceAsync(GroupTestWorld world, string name, byte? level = null)
        => world.OnWorldAsync(() =>
        {
            Player player = Online(world, name);
            if (level is { } set) player.Level = set;
            player.Relocate(EntranceArea.X, EntranceArea.Y, EntranceArea.Z, 0, world.World.Host.World.NowMs);
            return true;
        });

    private protected static async Task<uint> SinceAsync(GroupTestWorld world, Guid bot)
        => await world.OnWorldAsync(() => world.Coordinator.WaitingBots.Single(w => w.BotId == bot).SinceMs);

    private protected static async Task StopSeedAsync(GroupTestWorld world, (Guid Id, string Name) seed)
    {
        var stopped = await world.Bots.StopAsync(seed.Name);
        Assert.True(stopped.Success, stopped.Code);
        await world.RunAsync(2 * PlayerbotGroupCoordinator.SeedRecheckMs);
    }
}
