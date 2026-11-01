using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Stratholme;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Timmy the Cruel (vmangos boss_timmy_the_cruel.cpp): the spawner guardsman's death summons him once; his claw and enrage timers.</summary>
public sealed class StratholmeTimmyTests
{
    private const uint Guardsman = 10418, Timmy = 10808;

    private static DungeonScriptTestKit Rig(IEnumerable<CreatureSpawn> spawns, uint[] spawnEntries, ICreatureSpellCaster? caster = null)
        => new(map => new StratholmeInstance(map), [Guardsman, Timmy], spawnEntries, [],
            new CreatureAiContent([], [new CreatureAiText(6150, "Timmy hungers.", 1, 0, 0)]),
            aiServices: new CreatureAiServices { Spells = caster }, extraSpawns: spawns);

    private static CreatureSpawn GuardsmanSpawn(uint guid)
        => Spawn(guid, Guardsman, -10f + guid % 10, -383.07f, 61.78f, mapId: InstanceFixture.Dungeon);

    private static Creature[] TimmysIn(DungeonScriptTestKit rig) => [.. rig.Creatures.Creatures.Where(c => c.Entry == Timmy)];

    [Fact]
    public void SpawnerGuardsmanDeath_SummonsTimmyOnce_AtTheRetailPlace_AndRemovesItsCorpse()
    {
        using DungeonScriptTestKit rig = Rig([GuardsmanSpawn(CrimsonGuardsmanAI.TimmySpawnerGuid)], []);
        Creature guard = rig.Creature(Guardsman);
        var ai = new CrimsonGuardsmanAI(guard);
        Assert.Empty(TimmysIn(rig));

        rig.Kill(Guardsman);
        ai.OnDeath(rig.Player);

        Creature timmy = Assert.Single(TimmysIn(rig));
        Assert.Equal(CrimsonGuardsmanAI.TimmyX, timmy.X, 0.01f);
        Assert.Equal(CrimsonGuardsmanAI.TimmyY, timmy.Y, 0.01f);
        Assert.Equal(CrimsonGuardsmanAI.TimmyZ, timmy.Z, 0.01f);
        Assert.Same(timmy, ((StratholmeInstance)rig.Script).Timmy);

        ai.OnDeath(rig.Player); // a second death report never summons again
        Assert.Single(TimmysIn(rig));

        rig.Tick(100);
        // A database spawn's forced despawn leaves the same Creature object registered (it respawns later), dead with its corpse gone.
        Assert.Equal(CreatureDeathState.Dead, guard.DeathState);
        Assert.False(guard.IsAlive);
        Assert.Single(TimmysIn(rig));
    }

    [Fact]
    public void OtherGuardsmanDeath_SummonsNoTimmy()
    {
        using DungeonScriptTestKit rig = Rig([GuardsmanSpawn(54071)], []);
        var ai = new CrimsonGuardsmanAI(rig.Creature(Guardsman));

        rig.Kill(Guardsman);
        ai.OnDeath(rig.Player);

        Assert.Empty(TimmysIn(rig));
    }

    private static (DungeonScriptTestKit Rig, Creature Boss) FightingTimmy(ICreatureSpellCaster caster)
    {
        DungeonScriptTestKit rig = Rig([], [Timmy], caster);
        Creature boss = rig.Creature(Timmy);
        rig.Player.Relocate(boss.X + 1, boss.Y, boss.Z, 0, 0);
        Assert.True(rig.Creatures.AttackStart(boss, rig.Player));
        boss.Combat.Threat.AddThreat(rig.Player, 100);
        return (rig, boss);
    }

    [Fact]
    public void RavenousClaw_FirstAtSevenSeconds_ThenEveryTwelve_OnTheManualClock()
    {
        var caster = new DungeonTestCaster();
        (DungeonScriptTestKit rig, Creature boss) = FightingTimmy(caster);
        using (rig)
        {
            var ai = new TimmyTheCruelAI(boss);
            ai.OnRespawn();
            int Claws() => caster.Casts.Count(c => c.Spell == TimmyTheCruelAI.SpellRavenousClaw);

            ai.OnUpdate(6_900);
            Assert.Equal(0, Claws());
            ai.OnUpdate(100);
            Assert.Equal(1, Claws());
            Assert.All(caster.Casts.Where(c => c.Spell == TimmyTheCruelAI.SpellRavenousClaw), c => Assert.Same(rig.Player, c.Target));

            ai.OnUpdate(11_900);
            Assert.Equal(1, Claws());
            ai.OnUpdate(100);
            Assert.Equal(2, Claws());
        }
    }

    [Fact]
    public void Enrage_AppliesOnceUnderTenPercentHealth_NotBefore()
    {
        var caster = new AuraTrackingCaster();
        (DungeonScriptTestKit rig, Creature boss) = FightingTimmy(caster);
        using (rig)
        {
            var ai = new TimmyTheCruelAI(boss);
            ai.OnRespawn();

            ai.OnUpdate(100);
            Assert.DoesNotContain(caster.Casts, c => c.Spell == TimmyTheCruelAI.SpellEnrage);

            boss.Health = (uint)(boss.MaxHealth / 10) - 1; // just under 10 percent
            Assert.True(boss.HealthPercent < 10f);
            ai.OnUpdate(100);
            ai.OnUpdate(100);

            Assert.Single(caster.Casts, c => c.Spell == TimmyTheCruelAI.SpellEnrage);
        }
    }

    [Fact]
    public void DungeonBossTable_MapsTimmyAndTheGuardsmanOnStratholme()
    {
        Assert.Equal("TimmyTheCruelAI", DungeonBossAis.Describe(329, Timmy));
        Assert.Equal("CrimsonGuardsmanAI", DungeonBossAis.Describe(329, Guardsman));
    }
}
