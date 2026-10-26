using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// How a PvP kill reaches the bot social memory (<see cref="PlayerbotPvpKillFeed"/>): every map of the world is heard, so a kill is recorded
/// exactly once whatever the bot did just before (its brain was replaced after a group, it changed maps and has not thought since), a pet's
/// kill counts for the player behind it, and a kill between two bots is one event on each side.
/// </summary>
public sealed class PlayerbotPvpKillRoutingWorldTests
{
    private static readonly PlayerbotSocialMemoryOptions Defaults = new();

    private static readonly CreatureTemplate PetTemplate = new()
    {
        Entry = 990_530, Name = "pvp routing pet", CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 1000, MaxLevelHealth = 1000,
    };

    private static PlayerbotSocialMemory MemoryOf(SocialWorld world) => PartyTestHost.Feature(world.Host).Social.Memory;

    private static PlayerbotRelation? RelationOf(SocialWorld world, Guid bot, ObjectGuid other)
        => MemoryOf(world).TryGet(bot, other.Value, out PlayerbotRelation relation) ? relation : null;

    private static Player Online(SocialWorld world, string name) => world.Host.World.FindOnlinePlayer(name)!;

    private static void Slay(Unit killer, Player victim) => victim.Map!.Combat.DealDamage(killer, victim, victim.Health);

    [Fact]
    public async Task ABotWhoseBrainWasReplacedAfterAGroup_SharesTheFeaturesRecorder_AndItsKillIsRecordedOnce()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(o =>
        {
            o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
            o.Party.MasterTimeoutSeconds = 1;
        }, frozen: true);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(world.Host);
        Guid victim = await world.AddBotAsync("Rbvictim");
        PlayerbotBrain first = (await world.Host.OnWorldAsync(() => feature.FindBrain(victim)))!;

        // The real path: a real player groups the bot (its party AI drives it) and goes; a fresh brain takes over.
        WorldTestClient master = await world.Host.EnterWorldAsync("RBPVPMASTER", "Rbpvpmaster");
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Rbvictim"));
        await world.Host.WaitForWorldAsync(() => feature.IsPartyDriven(victim), "the party AI drives the grouped bot");
        await master.DisposeAsync();
        await world.Host.WaitForWorldAsync(() => !feature.IsPartyDriven(victim) && feature.FindBrain(victim) is { } b && !ReferenceEquals(b, first),
            "a fresh brain drives the bot again");
        // The replacement is what this test is about: the fresh brain is not the first one and records through the feature's recorder. (The kill
        // below is heard by the feed whichever brain the bot has, so on its own it would pass without any replacement.)
        PlayerbotBrain replacement = (await world.Host.OnWorldAsync(() => feature.FindBrain(victim)))!;
        Assert.NotSame(first, replacement);
        Assert.Same(feature.Social, await world.Host.OnWorldAsync(() => replacement.Social));

        await using WorldTestClient killer = await world.AddPlayerAsync("Rbpvpkiller", SocialTestKit.Orc);
        ObjectGuid killerGuid = await world.Host.PlayerStateAsync("Rbpvpkiller", p => p.Guid);
        await world.Host.OnWorldAsync(() =>
        {
            Slay(Online(world, "Rbpvpkiller"), Online(world, "Rbvictim"));
            Assert.False(Online(world, "Rbvictim").IsAlive);
        });

        Assert.NotNull(RelationOf(world, victim, killerGuid)); // recorded synchronously by the kill itself
        Assert.Equal((short)Defaults.AffinityPerKilledBy, RelationOf(world, victim, killerGuid)!.Affinity); // one KilledBy, not two
    }

    [Fact]
    public async Task APetsKill_CountsForThePlayerBehindIt_Once_AndThePetHasNoRelation()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid victim = await world.AddBotAsync("Petvictim");
        await using WorldTestClient owner = await world.AddPlayerAsync("Petowner", SocialTestKit.Orc);
        ObjectGuid ownerGuid = await world.Host.PlayerStateAsync("Petowner", p => p.Guid);
        ulong petGuid = 0;

        await world.Host.OnWorldAsync(() =>
        {
            Player bot = Online(world, "Petvictim");
            var system = new CreatureMapSystem(bot.Map!, new CreatureContent([PetTemplate], [], [], [], []), random: new Random(1));
            Creature pet = system.SpawnTemporary(PetTemplate, bot.X + 2f, bot.Y, bot.Z, 0f);
            pet.SetOwnerGuid(ownerGuid); // UNIT_FIELD_SUMMONEDBY: DuelRules.ControllingPlayer resolves the pet (or a totem) to its owner
            petGuid = pet.Guid.Value;
            Assert.Same(Online(world, "Petowner"), ArcaneCore.Game.Combat.DuelRules.ControllingPlayer(pet));
            Slay(pet, bot);
            Assert.False(bot.IsAlive);
        });

        PlayerbotRelation byOwner = RelationOf(world, victim, ownerGuid)!;
        Assert.Equal((short)Defaults.AffinityPerKilledBy, byOwner.Affinity);
        Assert.True(byOwner.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
        Assert.False(MemoryOf(world).TryGet(victim, petGuid, out _));
        Assert.Single(MemoryOf(world).Snapshot(victim));
    }

    [Fact]
    public async Task AKillBetweenTwoBots_IsOneEventOnEachSide()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid slayer = await world.AddBotAsync("Twoslayer");
        Guid victim = await world.AddBotAsync("Twovictim", race: SocialTestKit.Orc);
        ObjectGuid slayerGuid = await world.Host.PlayerStateAsync("Twoslayer", p => p.Guid);
        ObjectGuid victimGuid = await world.Host.PlayerStateAsync("Twovictim", p => p.Guid);

        await world.Host.OnWorldAsync(() => Slay(Online(world, "Twoslayer"), Online(world, "Twovictim")));

        Assert.Equal((short)Defaults.AffinityPerKilledThem, RelationOf(world, slayer, victimGuid)!.Affinity);
        Assert.Equal((short)Defaults.AffinityPerKilledBy, RelationOf(world, victim, slayerGuid)!.Affinity);
        Assert.Single(MemoryOf(world).Snapshot(slayer));
        Assert.Single(MemoryOf(world).Snapshot(victim));
    }

    [Fact]
    public async Task ABotKilledOnTheMapItWasJustTeleportedTo_WithoutThinkingThere_IsRecordedOnce()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(world.Host);
        Guid victim = await world.AddBotAsync("Tpvictim");
        // The killer is a real player (no bot of its own).
        await using WorldTestClient killer = await world.AddPlayerAsync("Tpkiller", SocialTestKit.Orc);
        ObjectGuid killerGuid = await world.Host.PlayerStateAsync("Tpkiller", p => p.Guid);
        (uint map, float x, float y, float z) = await world.Host.PlayerStateAsync("Tpkiller", p => (p.MapId, p.X, p.Y, p.Z));
        uint elsewhere = map == 0 ? 1u : 0u;
        TeleportService teleports = world.Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;

        // The bot has thought on its first map, then a scripted controller takes it over: a controlled bot is not thought for, so it cannot think
        // on either map (no race with the think rotation). It goes to the other continent and back, and is killed on arrival.
        await world.Host.WaitForWorldAsync(() => feature.FindBrain(victim)!.ThinkCount > 0, "a think on the first map");
        Assert.True(await feature.SetControllerAsync(victim, new IdleController()));
        long thinks = await world.Host.OnWorldAsync(() => feature.FindBrain(victim)!.ThinkCount);

        Assert.True(await world.Host.OnWorldAsync(() => teleports.TeleportTo(Online(world, "Tpvictim"), elsewhere, x, y, z, 0f)), "teleport refused");
        await world.Host.WaitForWorldAsync(() => Online(world, "Tpvictim") is { IsInWorld: true } away && away.MapId == elsewhere, "the bot on the other map");
        Assert.True(await world.Host.OnWorldAsync(() => teleports.TeleportTo(Online(world, "Tpvictim"), map, x + 2f, y, z, 0f)), "teleport refused");
        await world.Host.WaitForWorldAsync(() =>
        {
            if (Online(world, "Tpvictim") is not { IsInWorld: true } arrived || arrived.MapId != map) return false;
            Slay(Online(world, "Tpkiller"), arrived);
            Assert.False(arrived.IsAlive);
            return true;
        }, "the bot back on the killer's map");

        Assert.Equal(thinks, await world.Host.OnWorldAsync(() => feature.FindBrain(victim)!.ThinkCount)); // it never thought on the new map
        Assert.Equal((short)Defaults.AffinityPerKilledBy, RelationOf(world, victim, killerGuid)!.Affinity);
        Assert.Single(MemoryOf(world).Snapshot(victim));
    }

    private sealed class IdleController : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId) { }
    }

    [Fact]
    public async Task AKillOnAnInstanceMapCreatedAfterTheAttach_IsRecordedOnce_AndTheMapIsDroppedWhenItUnloads()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(world.Host);
        Guid victim = await world.AddBotAsync("Inbot");
        await using WorldTestClient killer = await world.AddPlayerAsync("Inkiller", SocialTestKit.Orc);
        ObjectGuid killerGuid = await world.Host.PlayerStateAsync("Inkiller", p => p.Guid);
        PlayerbotPvpKillFeed feed = feature.PvpKills!;
        int before = feed.MapCount;
        // A controlled bot is not thought for: it cannot release its spirit or repop between the kill and the move back.
        Assert.True(await feature.SetControllerAsync(victim, new IdleController()));
        Map? instance = null;
        Map? home = null;

        await world.Host.OnWorldAsync(() =>
        {
            Player bot = Online(world, "Inbot");
            Player player = Online(world, "Inkiller");
            home = bot.Map!;
            Assert.Same(home, player.Map);
            instance = world.Host.World.GetMap(home.MapId, 4242); // created after the feed attached
            foreach (Player mover in new[] { bot, player })
            {
                home.RemovePlayer(mover);
                instance.AddPlayer(mover);
                Assert.Same(instance, mover.Map);
            }

            Slay(player, bot);
            Assert.False(bot.IsAlive);
        });

        Assert.Equal(before + 1, feed.MapCount); // the new map is heard
        Assert.Equal((short)Defaults.AffinityPerKilledBy, RelationOf(world, victim, killerGuid)!.Affinity); // one KilledBy (-20), not two
        Assert.Single(MemoryOf(world).Snapshot(victim));

        // Everyone leaves, the instance is asked to unload and the feed lets go of it.
        await world.Host.OnWorldAsync(() =>
        {
            foreach (Player mover in new[] { Online(world, "Inbot"), Online(world, "Inkiller") })
            {
                instance!.RemovePlayer(mover);
                home!.AddPlayer(mover);
            }

            world.Host.World.UnloadMap(instance!);
        });
        await world.Host.WaitForWorldAsync(() => feed.MapCount == before, "the unloaded instance to leave the feed");
        Assert.True(instance!.IsUnloaded);
    }

    [Fact]
    public async Task AMapHandedToTheFeedTwice_IsHeardOnce_AndADisposedFeedHearsNothing()
    {
        // A map can reach the feed twice (it is created while the feed enumerates the world's maps): the second Add must not subscribe again.
        // The paths that do it race the constructor, so the test hands the map over twice itself.
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient killer = await world.AddPlayerAsync("Dupkiller", SocialTestKit.Orc);
        await using WorldTestClient victim = await world.AddPlayerAsync("Dupvictim");
        await using WorldTestClient later = await world.AddPlayerAsync("Duplater");
        int heard = 0;

        PlayerbotPvpKillFeed feed = await world.Host.OnWorldAsync(() => new PlayerbotPvpKillFeed(world.Host.World, (_, _) => heard++));
        try
        {
            await world.Host.OnWorldAsync(() =>
            {
                Map map = Online(world, "Dupvictim").Map!;
                feed.Add(map);
                feed.Add(map);
                Slay(Online(world, "Dupkiller"), Online(world, "Dupvictim"));
                Assert.False(Online(world, "Dupvictim").IsAlive);
            });
            Assert.Equal(1, heard);
        }
        finally
        {
            feed.Dispose();
        }

        feed.Dispose(); // harmless
        Assert.Equal(0, feed.MapCount);
        await world.Host.OnWorldAsync(() =>
        {
            Slay(Online(world, "Dupkiller"), Online(world, "Duplater"));
            Assert.False(Online(world, "Duplater").IsAlive); // the kill happened, and nobody heard it
        });
        Assert.Equal(1, heard);
    }

    [Fact]
    public async Task AFeedDisposedWithAMapThatCannotBeDetached_StillDetachesTheOthers_AndRecordsTheFault()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient killer = await world.AddPlayerAsync("Faultkiller", SocialTestKit.Orc);
        await using WorldTestClient victim = await world.AddPlayerAsync("Faultvictim");
        int heard = 0;
        long faultsBefore = FaultsAt("playerbot.pvp-feed");

        PlayerbotPvpKillFeed feed = await world.Host.OnWorldAsync(() =>
        {
            var feed = new PlayerbotPvpKillFeed(world.Host.World, (_, _) => heard++);
            // A map built without DefaultMapUpdaters has no MapCombat: Combat throws. Put it first in the feed's set (a HashSet hands a freed slot
            // to the next map added: the world's maps leave in reverse order, so the first slot is the last freed), so Dispose meets it before
            // the real maps and a Dispose that stops at the first failure leaves them subscribed.
            var broken = new Map(0, 9_999, world.Host.World, NullLogger.Instance);
            Map[] real = [.. world.Host.World.Maps];
            for (int i = real.Length - 1; i >= 0; i--) feed.Remove(real[i]);
            Assert.ThrowsAny<Exception>(() => feed.Add(broken));
            foreach (Map map in real) feed.Add(map);
            Assert.Equal(real.Length + 1, feed.MapCount);
            return feed;
        });

        feed.Dispose();

        Assert.Equal(0, feed.MapCount);
        Assert.True(FaultsAt("playerbot.pvp-feed") > faultsBefore, "the failed detach is in the fault ledger");
        await world.Host.OnWorldAsync(() =>
        {
            Slay(Online(world, "Faultkiller"), Online(world, "Faultvictim"));
            Assert.False(Online(world, "Faultvictim").IsAlive);
        });
        Assert.Equal(0, heard); // the real maps were detached although the broken one failed first
    }

    private static long FaultsAt(string site) => FaultLedger.Shared.Snapshot().Where(f => f.Site == site).Sum(f => f.Count);

    [Fact]
    public async Task AKillAfterTheFeatureWasDetachedAndAttachedAgain_IsRecordedOnce()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(world.Host);
        FeatureHost features = world.Host.WorldServices.GetRequiredService<FeatureHost>();
        Guid victim = await world.AddBotAsync("Cyclevictim");
        await using WorldTestClient killer = await world.AddPlayerAsync("Cyclekiller", SocialTestKit.Orc);
        ObjectGuid killerGuid = await world.Host.PlayerStateAsync("Cyclekiller", p => p.Guid);
        PlayerbotPvpKillFeed first = feature.PvpKills!;

        FeatureResult detached = await features.DetachAsync(nameof(ManagedPlayerbotFeature));
        Assert.True(detached.Ok, detached.Detail);
        Assert.Equal(0, first.MapCount); // the detach took the first feed off every map
        FeatureResult attached = await features.AttachAsync(nameof(ManagedPlayerbotFeature));
        Assert.True(attached.Ok, attached.Detail);
        await world.Host.WaitForWorldAsync(() => feature.FindBrain(victim) is not null, "the bot driven again");
        PlayerbotPvpKillFeed second = feature.PvpKills!;
        Assert.NotSame(first, second);
        Assert.Equal(0, first.MapCount);
        Assert.True(second.MapCount > 0);

        await world.Host.OnWorldAsync(() => Slay(Online(world, "Cyclekiller"), Online(world, "Cyclevictim")));

        Assert.Equal((short)Defaults.AffinityPerKilledBy, RelationOf(world, victim, killerGuid)!.Affinity); // one KilledBy, not one per attach
        Assert.Single(MemoryOf(world).Snapshot(victim));
    }

    [Fact]
    public async Task APlayerDeathWithNoBotInvolved_AllocatesNothingInTheRecorder()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(world.Host);
        await using WorldTestClient killer = await world.AddPlayerAsync("Allockiller", SocialTestKit.Orc);
        await using WorldTestClient victim = await world.AddPlayerAsync("Allocvictim");
        const int deathsPerWindow = 1000;

        await world.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialRecorder recorder = feature.Social;
            Player slayer = Online(world, "Allockiller");
            Player dead = Online(world, "Allocvictim");
            Action window = () =>
            {
                for (int i = 0; i < deathsPerWindow; i++) recorder.NotePvpKill(slayer, dead);
            };
            for (int i = 0; i < 1000; i++) recorder.NotePvpKill(slayer, dead); // warm-up: JIT, tiering

            // A closure plus its delegate cost over 100 bytes per death; no window may allocate. Runtime cast-cache rebuilds on this thread are
            // excused by the probe (see the note in AllocationProbe).
            AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, $"{deathsPerWindow} player deaths with no bot involved");
        });
    }
}
