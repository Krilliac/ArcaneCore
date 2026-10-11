using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// The hazards of creatures beyond a solo bot's strength (kind <c>strong</c>, <see cref="PlayerbotRisk.ScanHazards"/>) next to the
/// shared hazard machinery (review of 07aa8286): they never push a death or retreat place out, they last only while the creature is
/// seen alive, and a bot ringed by them is not held in place for good.
/// </summary>
public sealed class PlayerbotStrongHazardTests
{
    /// <summary>The live Defias Cutpurse (94) at level 5 against a level 2 bot of 70 health.</summary>
    private static readonly CreatureTemplate Cutpurse = RiskTestWorld.Template(991394, level: 5, health: 102, minDamage: 7, maxDamage: 10);

    private static async Task<RiskTestWorld> StartLevelTwoAsync()
    {
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = 2;
            player.MaxHealth = 70;
            player.Health = 70;
            return true;
        });
        return world;
    }

    private static Task<Creature[]> RingAsync(RiskTestWorld world, int count, Func<int, float> distance) => world.OnWorldAsync(() =>
    {
        var ring = new Creature[count];
        for (int i = 0; i < count; i++)
        {
            float angle = i * MathF.Tau / count;
            ring[i] = world.Spawn(Cutpurse, MathF.Cos(angle) * distance(i), MathF.Sin(angle) * distance(i));
        }

        return ring;
    });

    /// <summary>A death place stays a hazard however many strong creatures the bot sees (the shared cap used to drop the oldest).</summary>
    [Fact]
    public async Task SixtyFourStrongCreatures_NeverPushADeathPlaceOut()
    {
        await using RiskTestWorld world = await StartLevelTwoAsync();
        Vector3 deathSpot = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var at = new Vector3(player.X - 150, player.Y, player.Z);
            world.Brain.Risk.Hazards.Add(player.MapId, at, PlayerbotHazards.DeathYards, world.Host.World.NowMs, 300, "death");
            return at;
        });
        Creature[] ring = await RingAsync(world, 64, i => 60f + (i % 4) * 10f);
        await world.SeeAsync(ring);
        await world.OnWorldAsync(() =>
        {
            world.Brain.Risk.ScanHazards(world.Player);
            IReadOnlyList<PlayerbotHazard> active = world.Brain.Risk.Hazards.Active(world.Player.MapId, world.Host.World.NowMs);
            Assert.Contains(active, h => h.Kind == "death" && h.At == deathSpot);
            Assert.Equal(64, active.Count(h => h.Kind == "strong"));
            return true;
        });
    }

    /// <summary>
    /// A strong creature's hazard goes with it: killed (or out of sight), it is gone at the next look; and when the looks stop (the
    /// bot in a bot-led group, its brain not updated), it lapses within a few scan periods instead of DangerMemorySeconds.
    /// </summary>
    [Fact]
    public async Task AStrongHazard_LastsOnlyWhileItsCreatureIsSeenAlive()
    {
        await using RiskTestWorld world = await StartLevelTwoAsync();
        (Creature first, Creature second) = await world.OnWorldAsync(() => (world.Spawn(Cutpurse, 0, 50), world.Spawn(Cutpurse, 0, -50)));
        await world.SeeAsync(first, second);
        await world.OnWorldAsync(() =>
        {
            world.Brain.Risk.ScanHazards(world.Player);
            Assert.Equal(2, world.Brain.Risk.Hazards.Active(world.Player.MapId, world.Host.World.NowMs).Count(h => h.Kind == "strong"));
            world.Creatures.KillCreature(first);
            return true;
        });
        await world.Host.World.AdvanceClockAsync(2 * PlayerbotRisk.HazardScanMs); // past the first look's phase
        await world.OnWorldAsync(() =>
        {
            world.Brain.Risk.ScanHazards(world.Player);
            PlayerbotHazard left = Assert.Single(world.Brain.Risk.Hazards.Active(world.Player.MapId, world.Host.World.NowMs),
                h => h.Kind == "strong");
            Assert.Equal(second.Guid, left.Creature);
            return true;
        });

        // No more looks: the hazard lapses on its own.
        await world.Host.World.AdvanceClockAsync(PlayerbotRisk.StrongHazardLifeMs + 100);
        await world.OnWorldAsync(() =>
        {
            Assert.DoesNotContain(world.Brain.Risk.Hazards.Active(world.Player.MapId, world.Host.World.NowMs), h => h.Kind == "strong");
            return true;
        });
    }

    /// <summary>
    /// A level 2 bot inside a ring of twelve level-5 creatures 35 yards round it: every way out passes through one's reach, so every
    /// plan is refused at first. After <see cref="PlayerbotRisk.StrongRefusalsBeforeRelief"/> refusals the strong hazards are let go
    /// for <see cref="PlayerbotRisk.StrongReliefMs"/> and the bot moves; a death place is still kept out of meanwhile.
    /// </summary>
    [Fact]
    public async Task ABotRingedByStrongCreatures_IsNotHeldForGood()
    {
        await using RiskTestWorld world = await StartLevelTwoAsync();
        Creature[] ring = await RingAsync(world, 12, _ => 35f);
        await world.SeeAsync(ring);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            PlayerbotRisk risk = world.Brain.Risk;
            risk.ScanHazards(player);
            Assert.Equal(12, risk.Hazards.Active(player.MapId, world.Host.World.NowMs).Count(h => h.Kind == "strong"));
            PlayerbotNavigation.Guard(player, risk);
            try
            {
                Vector3 Out(int direction)
                {
                    float angle = (direction % 8) * MathF.Tau / 8f + 0.13f;
                    return new Vector3(player.X + MathF.Cos(angle) * 80f, player.Y + MathF.Sin(angle) * 80f, player.Z);
                }

                Assert.False(PlayerbotNavigation.TryPlan(player, Out(0), world.Options, out _), "the ring did not hold the first plan");
                // A death place due east of the bot: never walked through, relief or not.
                var deathSpot = new Vector3(player.X + 50f, player.Y, player.Z);
                risk.Hazards.Add(player.MapId, deathSpot, PlayerbotHazards.DeathYards, world.Host.World.NowMs, 300, "death");
                int attempts = 1;
                PlayerbotRoute? route = null;
                while (attempts < 3 * PlayerbotRisk.StrongRefusalsBeforeRelief && route is null)
                {
                    PlayerbotNavigation.TryPlan(player, Out(attempts), world.Options, out route);
                    attempts++;
                }

                Assert.True(route is not null, $"no way out of the ring after {attempts} plans");
                Assert.False(PlayerbotNavigation.TryPlan(player, deathSpot + new Vector3(30f, 0, 0), world.Options, out PlayerbotRoute? east)
                    && east!.Points.Any(p => Vector2.Distance(new(p.X, p.Y), new(deathSpot.X, deathSpot.Y)) < PlayerbotHazards.DeathYards - 1f),
                    "a route through the death place was given during the relief");
            }
            finally
            {
                PlayerbotNavigation.Guard(player, null);
            }

            return true;
        });
    }

    /// <summary>
    /// A bot-led group's AI and a party bot's AI keep no strong hazards (the group goes for creatures too strong for one member, its
    /// leader chose the way; a party bot follows its master), though the same creature is one for the bot's own brain.
    /// </summary>
    [Fact]
    public async Task GroupAndPartyAIs_KeepNoStrongHazards()
    {
        await using RiskTestWorld world = await StartLevelTwoAsync();
        Creature cutpurse = await world.OnWorldAsync(() => world.Spawn(Cutpurse, 0, 50));
        await world.SeeAsync(cutpurse);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var group = new PlayerbotGroupAI(world.Session, world.Options);
            var party = new PlayerbotPartyAI(world.Session, world.Options);
            Assert.False(group.Risk.AvoidsStrongCreatures);
            Assert.False(party.Risk.AvoidsStrongCreatures);
            group.Risk.ScanHazards(player);
            party.Risk.ScanHazards(player);
            world.Brain.Risk.ScanHazards(player);
            uint now = world.Host.World.NowMs;
            Assert.Empty(group.Risk.Hazards.Active(player.MapId, now));
            Assert.Empty(party.Risk.Hazards.Active(player.MapId, now));
            Assert.Single(world.Brain.Risk.Hazards.Active(player.MapId, now), h => h.Kind == "strong");
            return true;
        });
    }

    /// <summary>The allocation-free strength test of the scan gives the model's own verdict.</summary>
    [Theory]
    [InlineData((byte)2, 70u, 5, 102u, 7f, 10f, 0u)]   // the live Cutpurse against a level 2 bot: beyond
    [InlineData((byte)2, 70u, 4, 80u, 3f, 5f, 0u)]     // a Defias Thug: not
    [InlineData((byte)10, 400u, 5, 102u, 7f, 10f, 0u)] // the Cutpurse against a level 10 bot: not
    [InlineData((byte)10, 400u, 13, 300u, 5f, 8f, 1u)] // an elite three levels up: beyond
    [InlineData((byte)10, 400u, 10, 3000u, 30f, 40f, 0u)]
    public void BeyondStrengthAlone_MatchesTheModel(byte botLevel, uint health, int level, uint creatureHealth, float min, float max, uint rank)
    {
        var options = new PlayerbotRiskOptions();
        float dps = PlayerbotRiskModel.CreatureDps(min, max, 2000, (byte)level, rank is 1 or 2 or 3);
        var enemy = new RiskEnemy(1, (byte)level, rank, creatureHealth, dps, RiskJoin.Target);
        float botDps = PlayerbotRiskModel.PriorBotDps(botLevel);
        var facts = new PlayerbotEngagementFacts
        {
            BotLevel = botLevel, BotHealth = health, BotMaxHealth = health, BotDps = botDps, Enemies = [enemy],
        };
        Assert.Equal(PlayerbotRiskModel.BeyondStrength(facts, options),
            PlayerbotRiskModel.BeyondStrengthAlone(botLevel, health, botDps, enemy, options));
    }
}
