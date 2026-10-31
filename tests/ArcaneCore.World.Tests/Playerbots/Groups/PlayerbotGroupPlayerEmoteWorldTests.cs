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
/// An ignored player's emote. Split from the former <c>PlayerbotGroupPlayerTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotGroupPlayerTestBase"/>.
/// </summary>
public sealed class PlayerbotGroupPlayerEmoteWorldTests : PlayerbotGroupPlayerTestBase
{
    /// <summary>A friendly emote at a bot moves its memory of the emoter and is answered; from a player on its ignore list it does neither.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true, AccountSecurity.GameMaster)] // a game master is never dropped, as in OnChat
    public async Task AnEmoteFromAnIgnoredPlayer_ChangesNothingAndGetsNoReply(bool ignored, bool noticed, AccountSecurity security = AccountSecurity.Player)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            options.Groups.InvitePlayers = false;
            options.Chat.Speech.EmoteReplies = true;
            options.Chat.Speech.EmoteReplyGapSeconds = 1;
        });
        await using WorldTestClient player = await world.World.EnterWorldAsync("EMOTEONE", "Emoteone", security);
        var bot = await world.AddBotAsync("Emotebot", Human, Warrior, 1, []);
        await world.OnWorldAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Emoteone")!;
            them.Relocate(world.Player(bot.Id).X + 2f, world.Player(bot.Id).Y, world.Player(bot.Id).Z, 0, world.World.Host.World.NowMs);
            return true;
        });
        await world.RunAsync(3_000);
        if (ignored)
            await world.OnWorldAsync(() =>
            {
                Social.SocialTestKit.Social(world.World.Host).Context.Friends.Get(world.Player(bot.Id)).Set(world.World.Host.World.FindOnlinePlayer("Emoteone")!.Guid.Low, ArcaneCore.Kernel.Social.SocialFlags.Ignored, true);
                return true;
            });
        var dispatched = new System.Collections.Concurrent.ConcurrentQueue<WorldOpcode>();
        world.Session(bot.Id).ManagedDispatchObserver = opcode => dispatched.Enqueue(opcode);
        // The server's SMSG_TEXT_EMOTE is handed to the bot's session as it would be by the broadcast: the bot walks off on its errands, and a
        // broadcast from a player left behind would not reach it.
        await world.OnWorldAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Emoteone")!;
            world.Session(bot.Id).Send(WorldOpcode.SmsgTextEmote, ArcaneCore.World.Packets.ChatPackets.BuildTextEmote(them.Guid, 101 /* WAVE */, 0, bot.Name));
            return true;
        });
        await world.RunAsync(20_000);

        bool remembered = await world.OnWorldAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Emoteone")!;
            return world.Bots.Social.Memory.TryGet(bot.Id, them.Guid.Value, out PlayerbotRelation relation) && world.Bots.Social.Memory.EffectiveAffinity(relation) != 0;
        });
        Assert.Equal(noticed, remembered);
        Assert.Equal(noticed, dispatched.Contains(WorldOpcode.CmsgTextEmote));
    }
}
