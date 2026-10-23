using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task ABotWhoseBrainWasReplacedAfterAGroup_RecordsAPlayersKill_ExactlyOnce()
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
        Assert.Same(feature.Social, await world.Host.OnWorldAsync(() => feature.FindBrain(victim)!.Social)); // the replacement records too

        await using WorldTestClient killer = await world.AddPlayerAsync("Rbpvpkiller", SocialTestKit.Orc);
        ObjectGuid killerGuid = await world.Host.PlayerStateAsync("Rbpvpkiller", p => p.Guid);
        await world.Host.OnWorldAsync(() =>
        {
            Slay(Online(world, "Rbpvpkiller"), Online(world, "Rbvictim"));
            Assert.False(Online(world, "Rbvictim").IsAlive);
        });

        await world.Host.WaitForWorldAsync(() => RelationOf(world, victim, killerGuid) is not null, "the kill to be remembered");
        await Task.Delay(300);
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

        await world.Host.WaitForWorldAsync(() => RelationOf(world, victim, ownerGuid) is not null, "the pet's kill to be remembered against its owner");
        await Task.Delay(300);
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

        await world.Host.WaitForWorldAsync(() => RelationOf(world, slayer, victimGuid) is not null && RelationOf(world, victim, slayerGuid) is not null,
            "both sides to remember the kill");
        await Task.Delay(300);
        Assert.Equal((short)Defaults.AffinityPerKilledThem, RelationOf(world, slayer, victimGuid)!.Affinity);
        Assert.Equal((short)Defaults.AffinityPerKilledBy, RelationOf(world, victim, slayerGuid)!.Affinity);
        Assert.Single(MemoryOf(world).Snapshot(slayer));
        Assert.Single(MemoryOf(world).Snapshot(victim));
    }

    [Fact]
    public async Task ABotKilledOnTheMapItWasJustTeleportedTo_BeforeItsNextThink_IsRecordedOnce()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(world.Host);
        Guid victim = await world.AddBotAsync("Tpvictim");
        // Seven more bots and no think time beyond the first think of a tick: one bot thinks per tick, so the victim has ticks without a think.
        foreach (string name in new[] { "Tpfilla", "Tpfillb", "Tpfillc", "Tpfilld", "Tpfille", "Tpfillf", "Tpfillg" }) await world.AddBotAsync(name);
        feature.ThinkBudgetMs = 0;
        // The killer is a real player (no bot of its own).
        await using WorldTestClient killer = await world.AddPlayerAsync("Tpkiller", SocialTestKit.Orc);
        ObjectGuid killerGuid = await world.Host.PlayerStateAsync("Tpkiller", p => p.Guid);
        (uint map, float x, float y, float z) = await world.Host.PlayerStateAsync("Tpkiller", p => (p.MapId, p.X, p.Y, p.Z));
        uint elsewhere = map == 0 ? 1u : 0u;
        TeleportService teleports = world.Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
        long ThinksOf() => feature.FindBrain(victim)!.ThinkCount;

        // The bot goes to the other continent and thinks there, then comes back to the killer's map and is killed on arrival, provided it has
        // not thought since the teleport back was issued (otherwise the round trip is made again).
        bool killedBeforeAThink = false;
        for (int attempt = 0; attempt < 20 && !killedBeforeAThink; attempt++)
        {
            Assert.True(await world.Host.OnWorldAsync(() => teleports.TeleportTo(Online(world, "Tpvictim"), elsewhere, x, y, z, 0f)), "teleport refused");
            await world.Host.WaitForWorldAsync(() => Online(world, "Tpvictim") is { IsInWorld: true } away && away.MapId == elsewhere, "the bot on the other map");
            long there = await world.Host.OnWorldAsync(ThinksOf);
            await world.Host.WaitForWorldAsync(() => ThinksOf() > there, "a think on the other map");

            long mark = 0;
            Assert.True(await world.Host.OnWorldAsync(() =>
            {
                mark = ThinksOf();
                return teleports.TeleportTo(Online(world, "Tpvictim"), map, x + 2f, y, z, 0f);
            }), "teleport refused");
            await world.Host.WaitForWorldAsync(() =>
            {
                if (ThinksOf() != mark) return true; // it thought first: not this time
                if (Online(world, "Tpvictim") is not { IsInWorld: true } arrived || arrived.MapId != map) return false;
                Slay(Online(world, "Tpkiller"), arrived);
                Assert.False(arrived.IsAlive);
                killedBeforeAThink = true;
                return true;
            }, "the bot back on the killer's map");
        }

        Assert.True(killedBeforeAThink, "every round trip had a think before the kill");

        await world.Host.WaitForWorldAsync(() => RelationOf(world, victim, killerGuid) is not null, "the kill on the new map to be remembered");
        await Task.Delay(300);
        Assert.Equal((short)Defaults.AffinityPerKilledBy, RelationOf(world, victim, killerGuid)!.Affinity);
        Assert.Single(MemoryOf(world).Snapshot(victim));
    }
}
