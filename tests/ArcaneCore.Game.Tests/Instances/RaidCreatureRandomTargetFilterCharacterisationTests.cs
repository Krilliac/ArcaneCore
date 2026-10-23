using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.MoltenCore;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// RaidCreatureAI.RandomTarget(predicate) must keep applying the caller's filter to the threat list (not only the alive and
/// same-map checks) when the random-target pickers move into CombatActionHelpers. Magmadar's Magma Spit (19411) wants a
/// non-mana target and Lava Bomb (20474) a mana target (mangos-classic boss_magmadarAI), so one lone threat target decides
/// which of the two fires and the other must find no target and stay silent.
/// </summary>
public sealed class RaidCreatureRandomTargetFilterCharacterisationTests
{
    [Theory]
    [InlineData(PowerType.Rage, 19411u, 20474u)]
    [InlineData(PowerType.Mana, 20474u, 19411u)]
    public void Magmadar_RandomTargetPredicate_SelectsByPowerType_AndAnEmptyMatchCastsNothing(PowerType power, uint expected, uint absent)
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var raid = new MoltenCoreInstance(map);
        map.AddUpdater(raid);
        var content = new ArcaneCore.Kernel.WorldData.Creatures.CreatureContent(
            MoltenCoreInstance.BossEntries.Select(e => Template(e, b => { b.MinLevelHealth = b.MaxLevelHealth = 10000; })), [], [], [], []);
        var spells = new FakeCaster();
        var creatures = new CreatureMapSystem(map, content, random: new Random(1), aiServices: new CreatureAiServices { Spells = spells });
        map.AddUpdater(creatures);
        (Player player, _) = CreatureAiTestKit.AddPlayer(world, 1, 0, 0);
        player.MaxHealth = player.Health = 100000;
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)power);

        Creature boss = creatures.SpawnTemporary(creatures.Content.FindTemplate(11982)!, 5, 0, 83.5f, 0);
        Assert.True(boss.AI!.AttackStart(player));
        spells.Casts.Clear();

        boss.AI.OnUpdate(18000);

        (uint Spell, Unit? Target, bool Triggered) cast = Assert.Single(spells.Casts, c => c.Spell == expected);
        Assert.Same(player, cast.Target);
        Assert.DoesNotContain(spells.Casts, c => c.Spell == absent);
    }
}
