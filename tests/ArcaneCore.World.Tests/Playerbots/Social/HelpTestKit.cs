using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>Shared set-up of the helping, gratitude and initiative tests (P2): a hostile mob that fights a chosen victim, standing places, and rolls found by search.</summary>
internal static class HelpTestKit
{
    /// <summary>Faction 14 (monsters): hostile to every player, so the combat hooks let a bot attack it.</summary>
    public const uint MobEntry = 990_540;

    private static readonly CreatureTemplate Mob = new()
    {
        Entry = MobEntry, Name = "help test mob", CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 200_000, MaxLevelHealth = 200_000, Faction = 14,
    };

    public const uint StrongEntry = 990_542;

    /// <summary>A mob ten levels above a level-1 bot: no fight a bot of that level may take on.</summary>
    public static readonly CreatureTemplate Strong = new()
    {
        Entry = StrongEntry, Name = "help test strong mob", CreatureType = 1, MinLevel = 10, MaxLevel = 10, MinLevelHealth = 200_000, MaxLevelHealth = 200_000, Faction = 14,
    };

    public const uint EliteEntry = 990_543;

    /// <summary>An elite (rank 1) of the bot's own level: a fight only a group of three takes on.</summary>
    public static readonly CreatureTemplate Elite = new()
    {
        Entry = EliteEntry, Name = "help test elite", CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 200_000, MaxLevelHealth = 200_000, Faction = 14, Rank = 1,
    };

    private static CreatureContent AllTemplates() => new([Mob, Rare, Strong, Elite], [], [], [], []);

    // One creature system per map: each numbers its own spawns, so a second one would hand out the same guid again.
    private static readonly ConditionalWeakTable<Map, CreatureMapSystem> Systems = new();

    public const uint RareEntry = 990_541;

    private static readonly CreatureTemplate Rare = new()
    {
        Entry = RareEntry, Name = "help test rare", CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 200_000, MaxLevelHealth = 200_000, Faction = 14, Rank = 4,
    };

    /// <summary>World thread: a rare (rank 4) creature nobody updates beside <paramref name="near"/>.</summary>
    public static Creature SpawnRare(Player near, float dx = 10f)
    {
        CreatureMapSystem system = near.Map!.FindUpdater<CreatureMapSystem>()
            ?? Systems.GetValue(near.Map!, map => new CreatureMapSystem(map, AllTemplates(), random: new Random(1)));
        return system.SpawnTemporary(Rare, near.X + dx, near.Y, near.Z, 0f);
    }

    public static Player Online(SocialWorld world, string name) => world.Host.World.FindOnlinePlayer(name)!;

    /// <summary>World thread: a hostile creature nobody updates (it neither moves nor attacks on its own) beside <paramref name="near"/>.</summary>
    public static Creature SpawnMob(Player near, float dx = 3f, CreatureTemplate? template = null)
    {
        CreatureMapSystem system = near.Map!.FindUpdater<CreatureMapSystem>()
            ?? Systems.GetValue(near.Map!, map => new CreatureMapSystem(map, AllTemplates(), random: new Random(1)));
        return system.SpawnTemporary(template ?? Mob, near.X + dx, near.Y, near.Z, 0f);
    }

    /// <summary>World thread: <paramref name="mob"/> fights <paramref name="victim"/> (the swing is registered, so the mob is among its attackers) and lands a hit.</summary>
    public static void Engage(Creature mob, Player victim, uint damage = 1)
    {
        Assert.True(victim.Map!.Combat.Attack(mob, victim), "the mob could not attack the player");
        victim.Map!.Combat.DealDamage(mob, victim, damage);
    }

    /// <summary>Put <paramref name="players"/> at the given offsets east of <paramref name="anchor"/> and wait until the anchor sees them all.</summary>
    public static async Task StandAsync(SocialWorld world, string anchor, params (string Name, float Dx)[] players)
    {
        await world.Host.OnWorldAsync(() =>
        {
            Player a = Online(world, anchor);
            foreach ((string name, float dx) in players) Online(world, name).Relocate(a.X + dx, a.Y, a.Z, 0f, world.Host.World.NowMs);
        });
        await world.Host.WaitForWorldAsync(() =>
        {
            Player a = Online(world, anchor);
            return players.All(p => a.VisibleObjects.Contains(Online(world, p.Name).Guid));
        }, "the characters to be in sight");
    }

    public static PlayerbotSocialRecorder RecorderOf(SocialWorld world) => PartyTestHost.Feature(world.Host).Social;

    public static PlayerbotBrain BrainOf(SocialWorld world, Guid bot) => PartyTestHost.Feature(world.Host).FindBrain(bot)!;

    public static Task UntilAsync(Func<bool> condition, string what, int seconds = 20) => PlayerbotSocialTriggerWorldTests.UntilAsync(condition, what, seconds);

    /// <summary>The first world time (a whole second, as a pass runs) at which <paramref name="yes"/> holds; throws when none does in a long search.</summary>
    public static long NowWhere(Func<long, bool> yes)
    {
        for (long second = 1; second < 200_000; second++)
            if (yes(second * 1000L)) return second * 1000L;
        throw new InvalidOperationException("no world time gives the wanted roll");
    }

    /// <summary>The world time of the first 15-second bucket (see <see cref="PlayerbotHelpLane"/>) in which <paramref name="yes"/> holds; throws when none does in a long search.</summary>
    public static long BucketWhere(Func<uint, bool> yes)
    {
        for (uint bucket = 1; bucket < 100_000; bucket++)
            if (yes(bucket)) return bucket * 15_000L;
        throw new InvalidOperationException("no bucket gives the wanted roll");
    }
}
