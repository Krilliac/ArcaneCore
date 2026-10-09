using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;
using ArcaneCore.Game.Instances.Scripts.ZulGurub;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class RemainingRaidBossTests
{
    private sealed class Raid(uint mapId, uint entry, params uint[] extraEntries) : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeCaster Caster { get; } = new();
        public Map Map { get; private set; } = null!;
        public InstanceData Data { get; private set; } = null!;
        public CreatureMapSystem Creatures { get; private set; } = null!;
        public Creature Boss { get; private set; } = null!;
        public Player Tank { get; private set; } = null!;

        public void Start(bool aggro = true)
        {
            Map = World.GetMap(mapId);
            Data = Assert.IsAssignableFrom<InstanceData>(InstanceScriptRegistry.Default.Create(Map));
            Data.Initialize();
            Map.AddUpdater(Data);
            Creatures = new CreatureMapSystem(Map, Content([Template(entry), .. extraEntries.Select(e => Template(e))], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Caster, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), mapId);
            float z = mapId == 309 ? 50 : 450;
            Tank.Relocate(0, 0, z, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(Template(entry), 1, 0, z, 0);
            if (aggro) Boss.AI!.AttackStart(Tank);
        }

        public Creature Spawn(uint otherEntry)
            => Creatures.SpawnTemporary(Template(otherEntry), 2, 0, mapId == 309 ? 50 : 450, 0);

        public void Dispose() => World.Dispose();
    }

    [Theory]
    [InlineData(309u, 14517u, "JeklikAI", 0u)]
    [InlineData(309u, 14507u, "VenoxisAI", 1u)]
    [InlineData(309u, 14510u, "MarliAI", 2u)]
    [InlineData(309u, 14509u, "ThekalAI", 3u)]
    [InlineData(309u, 14515u, "ArlokkAI", 4u)]
    [InlineData(309u, 11382u, "MandokirAI", 5u)]
    [InlineData(509u, 15340u, "MoamAI", 2u)]
    [InlineData(509u, 15370u, "BuruAI", 3u)]
    [InlineData(509u, 15369u, "AyamissAI", 4u)]
    [InlineData(509u, 15339u, "OssirianAI", 5u)]
    public void Boss_IsSelected_AndTracksCombat(uint map, uint entry, string ai, uint slot)
    {
        using var raid = new Raid(map, entry);
        raid.Start();
        Assert.Equal(ai, raid.Boss.AI!.GetType().Name);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(slot));
    }

    [Theory]
    [InlineData(309u, 11347u, "LorKhanAI")]
    [InlineData(309u, 11348u, "ZathAI")]
    [InlineData(309u, 11380u, "JindoAI")]
    [InlineData(309u, 15114u, "GahzrankaAI")]
    [InlineData(309u, 15082u, "GrilekAI")]
    [InlineData(309u, 15083u, "HazzarahAI")]
    [InlineData(309u, 15084u, "RenatakiAI")]
    [InlineData(309u, 15085u, "WushoolayAI")]
    [InlineData(509u, 15341u, "RajaxxAI")]
    public void AdditionalBoss_HasDedicatedAi(uint map, uint entry, string ai)
    {
        using var raid = new Raid(map, entry);
        raid.Start();
        Assert.Equal(ai, raid.Boss.AI!.GetType().Name);
    }

    [Theory]
    [InlineData(14517u, 49u, 24085u)]
    [InlineData(14507u, 50u, 23849u)]
    [InlineData(15370u, 19u, 24721u)]
    public void HealthPhase_UsesReferenceThresholdAndSpell(uint entry, uint percent, uint spell)
    {
        uint map = entry == 15370 ? 509u : 309u;
        using var raid = new Raid(map, entry);
        raid.Start();
        raid.Boss.Health = raid.Boss.MaxHealth * percent / 100;
        raid.Boss.AI!.OnUpdate(1);
        if (entry == 15370)
        {
            Assert.DoesNotContain(raid.Caster.Casts, cast => cast.Spell == spell);
            raid.Boss.AI.OnUpdate(1999); // ScriptDev2 waits two seconds before Buru transforms.
        }
        Assert.Contains(raid.Caster.Casts, cast => cast.Spell == spell);
    }

    [Theory]
    [InlineData(309u, 14517u, 23918u, 12000u)]
    [InlineData(309u, 14507u, 23858u, 7500u)]
    [InlineData(309u, 14510u, 24099u, 15000u)]
    [InlineData(309u, 14515u, 24210u, 12000u)]
    [InlineData(309u, 11380u, 24306u, 6000u)]
    [InlineData(309u, 11382u, 16856u, 1000u)]
    [InlineData(309u, 15114u, 16099u, 8000u)]
    [InlineData(309u, 15082u, 6524u, 16000u)]
    [InlineData(309u, 15083u, 24684u, 10000u)]
    [InlineData(309u, 15084u, 24649u, 8000u)]
    [InlineData(309u, 15085u, 25033u, 10000u)]
    [InlineData(509u, 15341u, 25599u, 18000u)]
    [InlineData(509u, 15340u, 15550u, 9000u)]
    [InlineData(509u, 15370u, 96u, 5000u)]
    [InlineData(509u, 15369u, 25748u, 5000u)]
    [InlineData(509u, 15339u, 25189u, 20000u)]
    public void Boss_CastsItsReferenceOpeningMechanic(uint map, uint entry, uint spell, uint delay)
    {
        using var raid = new Raid(map, entry);
        raid.Start();
        raid.Caster.Casts.Clear();
        raid.Boss.AI!.OnUpdate(delay);
        Assert.Contains(raid.Caster.Casts, cast => cast.Spell == spell);
    }

    [Fact]
    public void ThekalTrio_FakesDeath_AndThekalRevivesAsKillableTiger()
    {
        using var raid = new Raid(309, 14509, 11347, 11348);
        raid.Start();
        Creature lorkhan = raid.Spawn(11347), zath = raid.Spawn(11348);
        lorkhan.AI!.AttackStart(raid.Tank);
        zath.AI!.AttackStart(raid.Tank);
        foreach (Creature creature in new[] { lorkhan, zath, raid.Boss })
            raid.Map.Combat.DealDamage(raid.Tank, creature, creature.Health + 100);
        Assert.Equal(EncounterState.Special, raid.Data.GetData(3));
        Assert.Equal(EncounterState.Special, raid.Data.GetData(6));
        Assert.Equal(EncounterState.Special, raid.Data.GetData(7));
        raid.Boss.AI!.OnUpdate(10000);
        raid.Boss.AI.OnUpdate(5000);
        Assert.Contains(raid.Caster.Casts, cast => cast.Spell == 24169);
        Assert.Equal(0u, raid.Boss.InvincibilityHpThreshold);
    }

    [Fact]
    public void ThekalTiger_KeepsBothZealotsDown_AndHisDeathDespawnsThem()
    {
        using var raid = new Raid(309, 14509, 11347, 11348);
        raid.Start();
        Creature lorkhan = raid.Spawn(11347), zath = raid.Spawn(11348);
        lorkhan.AI!.AttackStart(raid.Tank);
        zath.AI!.AttackStart(raid.Tank);
        foreach (Creature creature in new[] { lorkhan, zath })
            raid.Map.Combat.DealDamage(raid.Tank, creature, creature.Health + 100);
        lorkhan.AI.OnUpdate(9000);
        zath.AI.OnUpdate(9000);
        raid.Map.Combat.DealDamage(raid.Tank, raid.Boss, raid.Boss.Health + 100);

        // boss_thekalAI::OnFakeingDeath: both zealots are down, so Thekal rises after one second, not ten.
        raid.Boss.AI!.OnUpdate(1000);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(3));
        Assert.Contains(raid.Caster.Casts, cast => cast.Spell == 24171);

        // CanPreventAddsResurrect: Thekal leaving SPECIAL must not let the zealots get back up.
        lorkhan.AI.OnUpdate(20000);
        zath.AI.OnUpdate(20000);
        Assert.True(Assert.IsAssignableFrom<ThekalCompanionAI>(lorkhan.AI).FakeDeath);
        Assert.True(Assert.IsAssignableFrom<ThekalCompanionAI>(zath.AI).FakeDeath);
        Assert.Equal(EncounterState.Special, raid.Data.GetData(6));
        Assert.Equal(EncounterState.Special, raid.Data.GetData(7));

        raid.Boss.AI.OnUpdate(5000);
        Assert.Equal(0u, raid.Boss.InvincibilityHpThreshold);
        raid.Map.Combat.DealDamage(raid.Tank, raid.Boss, raid.Boss.Health + 100);
        Assert.False(raid.Boss.IsAlive);
        Assert.Equal(EncounterState.Done, raid.Data.GetData(3));
        raid.World.RunTick(1500);
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry is 11347 or 11348 && c.IsAlive);
    }

    [Fact]
    public void ThekalEvade_StandsUpAFakeDeadZealot()
    {
        using var raid = new Raid(309, 14509, 11347);
        raid.Start();
        Creature lorkhan = raid.Spawn(11347);
        lorkhan.AI!.AttackStart(raid.Tank);
        raid.Map.Combat.DealDamage(raid.Tank, lorkhan, lorkhan.Health + 100);
        var zealot = Assert.IsAssignableFrom<ThekalCompanionAI>(lorkhan.AI);
        Assert.True(zealot.FakeDeath);
        raid.Boss.AI!.EnterEvadeMode();
        Assert.False(zealot.FakeDeath);
        Assert.Equal(StandState.Stand, lorkhan.StandState);
        Assert.Equal(1u, lorkhan.InvincibilityHpThreshold);
    }

    [Fact]
    public void Rajaxx_HisOwnPullDoesNotStartTheArmy_AndACaptainEvadeFailsIt()
    {
        using var raid = new Raid(509, 15341, 15391);
        raid.Start();
        // ruins_of_ahnqiraj.cpp OnCreatureEnterCombat has no Rajaxx case: only Andorov's dialogue starts the waves.
        Assert.Equal(EncounterState.NotStarted, raid.Data.GetData(1));
        Creature captain = raid.Spawn(15391);
        raid.Data.SetData(1, EncounterState.InProgress);
        Assert.True(captain.Combat.IsInCombat);
        captain.AI!.EnterEvadeMode();
        Assert.Equal(EncounterState.Fail, raid.Data.GetData(1));
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Assert.Equal(EncounterState.Done, raid.Data.GetData(1));
    }

    [Fact]
    public void Ossirian_FirstCrystalStandsBeforeThePull_AndFiveMoreRiseTenSecondsIn()
    {
        using var raid = new Raid(509, 15339, 15590);
        raid.Start(aggro: false);
        for (int i = 0; i < 8; i++) raid.Spawn(15590).Relocate(20 * i, 0, 450, 0, 0);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(180619, GameObjectType.Button)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        raid.Data.Update(1);
        raid.Data.Update(1);
        Assert.Single(objects.GameObjects);
        raid.Boss.AI!.AttackStart(raid.Tank);
        raid.Boss.AI.OnUpdate(9999);
        Assert.Single(objects.GameObjects);
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(6, objects.GameObjects.Count());
        Assert.Equal(6, objects.GameObjects.Select(g => g.X).Distinct().Count());
        raid.Boss.AI.OnUpdate(60000);
        Assert.Equal(6, objects.GameObjects.Count());
    }

    [Fact]
    public void OssirianCrystal_UsesNearbyTrigger_AndConsumesObject()
    {
        using var raid = new Raid(509, 15339, 15590);
        raid.Start();
        raid.Spawn(15590);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(180619, GameObjectType.Button)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        GameObject crystal = objects.Summon(180619, 2, 0, 450, 0)!;
        raid.Data.OnObjectUsed(raid.Tank, crystal);
        Assert.Contains(raid.Caster.Casts, c => c.Spell is 25177 or 25178 or 25180 or 25181 or 25183);
        Assert.Empty(objects.GameObjects);
    }

    [Fact]
    public void Ossirian_WeaknessDropsSupreme_AndTheNextTriggerCreatesAnotherCrystal()
    {
        using var raid = new Raid(509, 15339, 15590);
        raid.Start();
        Creature first = raid.Spawn(15590);
        Creature second = raid.Spawn(15590);
        second.Relocate(20, 0, 450, 0, 0);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(180619, GameObjectType.Button)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        raid.Data.Update(1);
        GameObject firstCrystal = Assert.Single(objects.GameObjects);
        raid.Caster.RaiseHit(first, raid.Boss, new SpellInfo { Id = 25177 });
        Assert.Contains(raid.Caster.RemovedAuras, aura => aura.Unit == raid.Boss && aura.Spell == 25176);
        raid.Data.OnObjectUsed(raid.Tank, firstCrystal);
        GameObject next = Assert.Single(objects.GameObjects);
        Assert.Equal(second.X, next.X);
    }

    [Fact]
    public void Jeklik_AtThirtyFourPercent_SummonsBothBatRiders_AndCleansThemOnDeath()
    {
        using var raid = new Raid(309, 14517, 14750);
        raid.Start();
        raid.Boss.Health = raid.Boss.MaxHealth * 34 / 100;
        raid.Boss.AI!.OnUpdate(1);
        Assert.Equal(2, raid.Creatures.Creatures.Count(c => c.Entry == 14750));
        Assert.Single(raid.Caster.Casts, c => c.Spell == 23968);
        raid.Boss.AI.OnUpdate(14999);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 23968);
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(2, raid.Caster.Casts.Count(c => c.Spell == 23968));
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 14750);
    }

    [Fact]
    public void Marli_AggroHatchesFourRealEggs_AndWipeRestoresThem()
    {
        using var raid = new Raid(309, 14510, 15041);
        raid.Start(aggro: false);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(179985, GameObjectType.Button)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        for (int i = 0; i < 4; i++) objects.Summon(179985, i, 2, 50, 0);
        raid.Boss.AI!.AttackStart(raid.Tank);
        Assert.Equal(4, raid.Creatures.Creatures.Count(c => c.Entry == 15041));
        Assert.All(objects.GameObjects, egg => Assert.Equal(GameObjectState.Active, egg.State));
        raid.Boss.AI.OnReachedHome();
        Assert.All(objects.GameObjects, egg => Assert.Equal(GameObjectState.Ready, egg.State));
    }

    [Fact]
    public void ArlokkGong_SummonsTheClassicDbBossOnlyOnce()
    {
        using var raid = new Raid(309, 14834, 14515);
        raid.Start();
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(180526, GameObjectType.Goober)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        GameObject gong = objects.Summon(180526, 0, 0, 50, 0)!;
        raid.Data.OnObjectUsed(raid.Tank, gong);
        raid.Data.OnObjectUsed(raid.Tank, gong);
        Creature arlokk = Assert.Single(raid.Creatures.Creatures, c => c.Entry == 14515);
        Assert.Equal(-11540.7f, arlokk.X);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(4));
    }

    [Fact]
    public void Mandokir_PullSummonsChainedSpirits_AndOhganDeathEnragesHim()
    {
        using var raid = new Raid(309, 11382, 15117, 14988);
        raid.Start();
        Assert.Equal(19, raid.Creatures.Creatures.Count(c => c.Entry == 15117));
        Creature ohgan = raid.Spawn(14988);
        raid.Map.Combat.Kill(raid.Tank, ohgan);
        Assert.Single(raid.Caster.Casts, cast => cast.Spell == 23537);
        raid.Boss.AI!.OnEvade();
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 15117);
    }

    [Fact]
    public void Rajaxx_SevenCaptainsAdvanceOnWaveDeaths()
    {
        uint[] captains = [15391, 15392, 15389, 15390, 15386, 15388, 15385];
        using var raid = new Raid(509, 15339, [.. captains, 15341]);
        raid.Start();
        Creature[] wave = [.. captains.Select(raid.Spawn)];
        Creature rajaxx = raid.Spawn(15341);
        raid.Data.SetData(1, EncounterState.InProgress);
        Assert.True(wave[0].Combat.IsInCombat);
        for (int i = 0; i < wave.Length; i++)
        {
            raid.Map.Combat.Kill(raid.Tank, wave[i]);
            raid.Data.Update(1);
            if (i + 1 < wave.Length) Assert.True(wave[i + 1].Combat.IsInCombat);
        }
        Assert.True(rajaxx.Combat.IsInCombat);
    }

    [Fact]
    public void Andorov_GossipAndDialogue_StartTheRajaxxWaves()
    {
        using var raid = new Raid(509, 15348, 15471, 15473, 15391);
        raid.Start();
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Creature andorov = Assert.Single(raid.Creatures.Creatures, c => c.Entry == 15471);
        var ai = Assert.IsType<AndorovAI>(andorov.AI);
        var npc = new NpcInfo(andorov.Guid, andorov.Entry, 0, NpcFlags.Gossip,
            raid.Map.MapId, andorov.X, andorov.Y, andorov.Z, andorov.BoundingRadius,
            true, false, false, false, 0);
        Assert.Equal("Let's find out.", Assert.Single(ai.Hello(raid.Tank, npc)!.Items).Text);
        ai.SelectReply(raid.Tank, npc, 1, 1001);
        ai.OnMovementInform(MovementGeneratorType.Point, 3);
        ai.OnMovementInform(MovementGeneratorType.Point, 4);
        ai.OnUpdate(1);
        ai.OnUpdate(7000);
        ai.OnUpdate(4000);
        ai.OnUpdate(6000);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(1));
    }

    [Theory]
    [InlineData(24728u, 24681u, 24729u)]
    [InlineData(25684u, 25681u, 25682u)]
    public void SummonSpell_ExecutesItsScriptedFollowup(uint source, uint first, uint second)
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(source, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)),
            SpellTestKit.Spell(first, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
                with { Duration = new SpellDuration(10000, 0, 10000) },
            SpellTestKit.Spell(second, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
                with { Duration = new SpellDuration(10000, 0, 10000) },
            SpellTestKit.Spell(25683, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
                with { Duration = new SpellDuration(10000, 0, 10000) });
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, source, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, first));
        Assert.True(kit.System.HasAura(player, second));
        if (source == 25684) Assert.True(kit.System.HasAura(player, 25683));
    }

    [Fact]
    public void Jindo_CastsBrainWashTotemHexAndShadeWithinTheirFirstWindows()
    {
        using var raid = new Raid(309, 11380);
        raid.Start();
        raid.Boss.AI!.OnUpdate(50000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 24262);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 17172);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 24308);
    }

    [Fact]
    public void Venoxis_ChangesToParasiticSerpentsBelowTwentyFivePercent()
    {
        using var raid = new Raid(309, 14507);
        raid.Start();
        raid.Boss.Health = raid.Boss.MaxHealth / 2;
        raid.Boss.AI!.OnUpdate(1);
        raid.Caster.Casts.Clear();
        raid.Boss.Health = raid.Boss.MaxHealth * 24 / 100;
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 23867);
    }

    [Fact]
    public void Moam_OnlyEruptsAtFullMana_AndCancelsEnergizeWhenFiendsDie()
    {
        using var raid = new Raid(509, 15340, 15527);
        raid.Start();
        raid.Boss.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        raid.Boss.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        raid.Boss.AI!.OnUpdate(1);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 25672);
        raid.Boss.SetUInt32(UpdateFields.UnitFieldPower1, 100);
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 25672);
        Creature fiend = raid.Spawn(15527);
        raid.Boss.AI.OnJustSummoned(fiend);
        raid.Boss.AI.OnSummonedCreatureJustDied(fiend);
        Assert.Contains(raid.Caster.RemovedAuras, a => a.Unit == raid.Boss && a.Spell == 25685);
    }

    [Fact]
    public void BuruEgg_DeathExplodesSummonsHatchlingAndClearsSpeed()
    {
        using var raid = new Raid(509, 15370, 15514);
        raid.Start();
        Creature egg = raid.Spawn(15514);
        uint health = raid.Boss.Health;
        raid.Map.Combat.Kill(raid.Tank, egg);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 19593);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 1881);
        Assert.Contains(raid.Caster.RemovedAuras, a => a.Unit == raid.Boss && a.Spell == 1557);
        Assert.Equal(health - raid.Boss.MaxHealth * 15 / 100, raid.Boss.Health);
    }

    [Fact]
    public void Ayamiss_StartsAirborneAndLandsAtSeventyPercent()
    {
        using var raid = new Raid(509, 15369);
        raid.Start();
        Assert.False(raid.Boss.AI!.MeleeEnabled);
        Assert.True(raid.Boss.Movement.Flags.HasFlag(MovementFlags.Flying));
        raid.Boss.Health = raid.Boss.MaxHealth * 70 / 100;
        raid.Boss.AI.OnUpdate(1);
        Assert.True(raid.Boss.AI.MeleeEnabled);
        Assert.False(raid.Boss.Movement.Flags.HasFlag(MovementFlags.Flying));
    }
}
