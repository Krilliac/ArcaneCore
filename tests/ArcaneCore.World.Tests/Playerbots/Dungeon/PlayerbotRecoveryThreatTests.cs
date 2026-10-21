using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Dungeon;

/// <summary>
/// What <see cref="PlayerbotRecovery.Threats(Player)"/> counts round a bot: a civilian or a creature that does not attack on sight is no
/// threat (it held the bot's recovery for nothing), unless it is attacking the bot; a hostile that attacks it still counts.
/// </summary>
public sealed class PlayerbotRecoveryThreatTests
{
    private const uint Civilian = 994601, Passive = 994602, Fighter = 994603, Aggressive = 994604;

    private static Creature Spawn(
        DungeonBotHost host,
        uint entry,
        float dx,
        bool civilian,
        Func<Creature, CreatureAI> ai,
        CreatureReactState reactState = CreatureReactState.Aggressive)
    {
        var creature = new Creature(entry, new CreatureTemplate
        {
            Entry = entry, Name = "creature " + entry, CreatureType = 1, Faction = 14, MinLevel = 1, MaxLevel = 1,
            MinLevelHealth = 20, MaxLevelHealth = 20, Civilian = civilian,
        }, null, CreatureContent.Empty, new Random((int)entry));
        creature.ReactState = reactState;
        creature.AI = ai(creature);
        creature.Relocate(host.Player.X + dx, host.Player.Y, host.Player.Z, 0, host.Host.World.NowMs);
        host.Player.Map!.AddObject(creature);
        return creature;
    }

    private static async Task<DungeonBotHost> Ghost(Creature[] spawned, Func<DungeonBotHost, Creature[]> spawn)
    {
        var clock = new TestDeathClock(1_000);
        DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() => DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await host.KillAndReleaseAsync();
        Creature[] made = await host.OnWorldAsync(() => spawn(host));
        await host.AcknowledgeUntilAsync(p => made.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees them all");
        Array.Copy(made, spawned, made.Length);
        return host;
    }

    [Fact]
    public async Task ACivilian_AndACreatureThatDoesNotAggroOnSight_AreNoThreat_SoTheBotIsNotCamped()
    {
        var made = new Creature[2];
        await using DungeonBotHost host = await Ghost(made, h =>
        [
            Spawn(h, Civilian, 3f, civilian: true, c => new AggressorAI(c)),
            Spawn(h, Passive, -3f, civilian: false, c => new ReactorAI(c), CreatureReactState.Defensive),
        ]);
        await host.OnWorldAsync(() =>
        {
            Assert.Empty(PlayerbotRecovery.Threats(host.Player));
            Assert.False(PlayerbotRecovery.Camped(host.Player));
        });
    }

    [Fact]
    public async Task AHostileThatIsAttackingTheBot_StillCounts_PassiveOrCivilianOrNot()
    {
        var made = new Creature[3];
        await using DungeonBotHost host = await Ghost(made, h =>
        [
            Spawn(h, Fighter, 3f, civilian: false, c => new ReactorAI(c), CreatureReactState.Passive), // does not aggro on sight, passive react state, but fights the bot
            Spawn(h, Civilian, 2f, civilian: true, c => new ReactorAI(c), CreatureReactState.Defensive), // civilian, defensive react state, but fights the bot
            Spawn(h, Aggressive, -3f, civilian: false, c => new AggressorAI(c)),
        ]);
        await host.OnWorldAsync(() =>
        {
            Assert.NotNull(host.Player.Map!.FindUpdater<CreatureMapSystem>());
            Assert.Single(PlayerbotRecovery.Threats(host.Player)); // the aggressive one; the passive/civilian ones have not engaged
            host.Player.Combat.AttackersInternal.Add(made[0]);
            Assert.Equal(2, PlayerbotRecovery.Threats(host.Player).Count);
            host.Player.Combat.AttackersInternal.Add(made[1]);
            Assert.Equal(3, PlayerbotRecovery.Threats(host.Player).Count);
            Assert.True(PlayerbotRecovery.Camped(host.Player));
        });
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }
}
