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
/// Bot groups and real players (<see cref="PlayerbotGroupCoordinator"/>): a real player's party keeps its party bot (vmangos
/// PartyBotAI master behaviour, unchanged), a short group may invite a nearby player when allowed, the GM commands report the groups,
/// and a bot left inside an instance without a group walks out.
/// </summary>
public abstract class PlayerbotGroupPlayerTestBase
{
    private protected const byte Human = 1, Warrior = 1, Priest = 5, Mage = 8;
    private protected const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    /// <summary>The player holds <paramref name="quest"/> unfinished (it is on the bots' goal) and, when given, the bot remembers them with that affinity and those flags.</summary>
    private protected static async Task GivePlayerQuestAsync(GroupTestWorld world, string playerName, uint quest)
        => await world.OnWorldAsync(() =>
        {
            Player player = world.World.Host.World.FindOnlinePlayer(playerName)!;
            PlayerNpcState state = world.World.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!;
            state.Quests.GetOrAdd(quest).Status = QuestStatus.Incomplete;
            return true;
        });

    private protected static async Task RememberPlayerAsync(GroupTestWorld world, (Guid Id, string Name) bot, string playerName, short affinity, PlayerbotRelationFlags flags = PlayerbotRelationFlags.None)
        => await world.OnWorldAsync(() =>
        {
            Player self = world.Player(bot.Id), them = world.World.Host.World.FindOnlinePlayer(playerName)!;
            world.Bots.Social.Memory.Load([new PlayerbotRelation(bot.Id, (int)self.Guid.Low, them.Guid.Value, true, affinity, 6, 500, flags, world.Bots.Social.Memory.NowMs, "", "")]);
            return true;
        });

    /// <summary>Advances up to <paramref name="maxMs"/> in short slices, putting the bot back on the player before each, and returns whether the player was invited.</summary>
    private protected static async Task<bool> RunBesidePlayerUntilAsync(GroupTestWorld world, Guid botId, string playerName, uint maxMs)
    {
        const uint SliceMs = 1_000;
        for (uint elapsed = 0; elapsed < maxMs; elapsed += SliceMs)
        {
            await world.OnWorldAsync(() =>
            {
                Player them = world.World.Host.World.FindOnlinePlayer(playerName)!;
                world.Player(botId).Relocate(them.X + 2, them.Y, them.Z, 0, world.World.Host.World.NowMs);
                return true;
            });
            if (await world.RunUntilAsync(SliceMs, () => PlayerInvited(world))) return true;
        }

        return PlayerInvited(world);
    }

    private protected static bool PlayerInvited(GroupTestWorld world) => world.Coordinator.Groups.Any(g => g.Members.Any(m => m.Real && m.Invited));

    /// <summary>
    /// Accepts the invitation and waits for the server's answer (the player's group list), which is read from the socket in real time: the
    /// manual clock does not wait for a packet still on its way, so advancing it right after the send can use up the budget before the
    /// server has even read the accept.
    /// </summary>
    private protected static async Task AcceptAsync(WorldTestClient player)
    {
        await player.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await player.ReadUntilAsync(WorldOpcode.SmsgGroupList);
    }

    /// <summary>A one-bot, one-player group that has moved on from forming (the player accepted the bot's invitation).</summary>
    private protected static async Task<WorldTestClient> FormPairAsync(GroupTestWorld world, string accountName, string playerName, string botName)
    {
        WorldTestClient player = await world.World.EnterWorldAsync(accountName, playerName, AccountSecurity.Player);
        var bot = await world.AddBotAsync(botName, Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, playerName, DuoQuest);
        await RememberPlayerAsync(world, bot, playerName, 30);
        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await AcceptAsync(player);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Travelling or PlayerbotGroupState.Engaging)), await world.TraceAsync());
        return player;
    }

    /// <summary>
    /// Logs the player out on the world thread, as a completed logout does (the same <c>LogoutPlayer</c> an instant logout calls). A logout
    /// request is not used: it is refused in combat (the pair is fighting), and otherwise runs a 20 s countdown that starts only when the
    /// packet is read from the socket, which the virtual clock does not wait for.
    /// </summary>
    private protected static async Task LogOutAsync(GroupTestWorld world, string name)
    {
        await world.OnWorldAsync(() =>
        {
            world.World.Host.World.LogoutPlayer(world.World.Host.World.FindOnlinePlayer(name)!);
            return true;
        });
        Assert.Null(await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(name)));
    }
}
