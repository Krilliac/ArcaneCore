using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class NaxxramasPartTwoTests
{
    private sealed class Raid(uint entry) : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeCaster Caster { get; } = new();
        public Map Map { get; private set; } = null!;
        public NaxxramasInstance Instance { get; private set; } = null!;
        public CreatureMapSystem Creatures { get; private set; } = null!;
        public Creature Boss { get; private set; } = null!;
        public Player Tank { get; private set; } = null!;

        public Raid Initialize(bool activateThaddius = true, bool attackBoss = true)
        {
            Map = World.GetMap(533);
            Instance = Assert.IsType<NaxxramasInstance>(InstanceScriptRegistry.Default.Create(Map));
            Instance.Initialize();
            Map.AddUpdater(Instance);
            uint[] entries = [entry, 16803, 16137, 16124, 16127, 16125, 16148,
                16126, 16150, 16065, 16062, 16064, 16063, 16697, 15929, 15930, 15928, 15989, 15990, 16441];
            Creatures = new CreatureMapSystem(Map, Content([.. entries.Distinct().Select(e => Template(e))], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Caster, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 533);
            Tank.Relocate(0, 0, 270, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(Template(entry), 1, 0, 270, 0);
            if (entry == 15928 && activateThaddius)
            {
                Instance.RecordConstructAddDeath(15929);
                Instance.RecordConstructAddDeath(15930);
                Instance.Update(14000);
            }
            if (attackBoss && (entry != 15928 || activateThaddius)) Boss.AI!.AttackStart(Tank);
            return this;
        }

        public void Dispose() => World.Dispose();
    }

    [Theory]
    [InlineData(16061u, 6u, "RazuviousAI")]
    [InlineData(16060u, 7u, "GothikAI")]
    [InlineData(16065u, 8u, "HorsemanAI")]
    [InlineData(16028u, 9u, "PatchwerkAI")]
    [InlineData(15931u, 10u, "GrobbulusAI")]
    [InlineData(15932u, 11u, "GluthAI")]
    [InlineData(15928u, 12u, "ThaddiusAI")]
    [InlineData(15989u, 13u, "SapphironAI")]
    [InlineData(15990u, 14u, "KelThuzadAI")]
    public void BossRegisters_AndCompletesOwnEncounter(uint entry, uint type, string ai)
    {
        using var raid = new Raid(entry).Initialize();
        Assert.Equal(ai, raid.Boss.AI!.GetType().Name);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(type));
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Assert.Equal(entry == 16065 ? EncounterState.InProgress : EncounterState.Done, raid.Instance.GetData(type));
    }

    [Fact]
    public void FourHorsemen_OnlyFourthDeathCompletesTheEncounter()
    {
        using var raid = new Raid(16065).Initialize();
        foreach (uint entry in new uint[] { 16065, 16062, 16064 })
        {
            Creature horseman = entry == 16065 ? raid.Boss : raid.Creatures.SpawnTemporary(Template(entry), 1, 0, 270, 0);
            raid.Map.Combat.Kill(raid.Tank, horseman);
            Assert.NotEqual(EncounterState.Done, raid.Instance.GetData(8));
        }
        Creature last = raid.Creatures.SpawnTemporary(Template(16063), 1, 0, 270, 0);
        raid.Map.Combat.Kill(raid.Tank, last);
        Assert.Equal(EncounterState.Done, raid.Instance.GetData(8));
    }

    [Fact]
    public void Patchwerk_HatefulStrikeRepeats_AndEnragesAtFivePercent()
    {
        using var raid = new Raid(16028).Initialize();
        raid.Boss.AI!.OnUpdate(1199);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 28308);
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28308);
        raid.Boss.Health = raid.Boss.MaxHealth / 25;
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28131);
    }

    [Fact]
    public void Patchwerk_HatefulStrikeChoosesHighestHealthEligibleOffTank()
    {
        using var raid = new Raid(16028).Initialize();
        Player low = TestWorld.CreatePlayer(2, 2, 0, new FakeSession(), 533);
        Player high = TestWorld.CreatePlayer(3, 3, 0, new FakeSession(), 533);
        low.Relocate(2, 0, 270, 0, 0); high.Relocate(3, 0, 270, 0, 0);
        raid.World.AddPlayer(low); raid.World.AddPlayer(high); raid.World.RunTick(0);
        low.Health = low.MaxHealth / 2;
        high.Health = high.MaxHealth;
        raid.Boss.Combat.Threat.AddThreat(low, 90);
        raid.Boss.Combat.Threat.AddThreat(high, 80);
        raid.Boss.AI!.OnUpdate(1200);
        Assert.Equal(high, Assert.Single(raid.Caster.Casts, c => c.Spell == 28308).Target);
    }

    [Theory]
    [InlineData(15931u, 28169u, 12000u)]
    [InlineData(15932u, 28374u, 105000u)]
    [InlineData(15928u, 28089u, 30000u)]
    [InlineData(15989u, 28529u, 1u)]
    [InlineData(15990u, 29423u, 1u)]
    public void SignaturePhaseSpellOccurs(uint entry, uint spell, uint elapsed)
    {
        using var raid = new Raid(entry).Initialize();
        raid.Boss.AI!.OnUpdate(elapsed);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == spell);
    }

    [Fact]
    public void InProgressStateIsResetOnLoad_ButCompletedQuartersStayDone()
    {
        using var raid = new Raid(16028).Initialize();
        raid.Instance.SetData(6, EncounterState.InProgress);
        raid.Instance.SetData(9, EncounterState.Done);
        string saved = raid.Instance.GetSaveData()!;
        raid.Instance.Initialize();
        raid.Instance.Load(saved);
        Assert.Equal(EncounterState.NotStarted, raid.Instance.GetData(6));
        Assert.Equal(EncounterState.Done, raid.Instance.GetData(9));
    }

    [Fact]
    public void FrostwyrmTeleport_RequiresAllFourWingEndBosses()
    {
        using var raid = new Raid(16028).Initialize();
        foreach (uint type in new uint[] { 2, 5, 8 }) raid.Instance.SetData(type, EncounterState.Done);
        Assert.False(raid.Instance.FrostwyrmUnlocked);
        raid.Instance.SetData(12, EncounterState.Done);
        Assert.True(raid.Instance.FrostwyrmUnlocked);
        raid.Instance.SetData(5, EncounterState.Fail);
        Assert.False(raid.Instance.FrostwyrmUnlocked);
    }

    [Fact]
    public void KelThuzadAreaTriggerStartsTheChannel()
    {
        using var raid = new Raid(15990).Initialize(attackBoss: false);
        raid.Instance.OnAreaTrigger(raid.Tank, 4112);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(14));
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 29423);
    }

    [Fact]
    public void Razuvious_SpawnsFourUnderstudies_AndCastsBothSignatureAttacks()
    {
        using var raid = new Raid(16061).Initialize();
        Assert.Equal(4, raid.Creatures.Creatures.Count(c => c.Entry == 16803));
        Creature understudy = raid.Creatures.Creatures.First(c => c.Entry == 16803);
        Assert.Equal([29060u, 29061u], new CharmService().CreatureSpells(understudy));
        raid.Boss.AI!.OnUpdate(15000);
        raid.Boss.AI.OnUpdate(15000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 29107);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 26613);
    }

    [Fact]
    public void Gothik_LandsAfterBalcony_ButOpensCentralGateOnlyAfterFourTeleports()
    {
        using var raid = new Raid(16060).Initialize();
        raid.Boss.AI!.OnUpdate(4000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28007);
        raid.Boss.AI.OnUpdate(270000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28025);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(7));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(7));
            raid.Boss.AI.OnUpdate(20000);
        }
        Assert.Equal(2, raid.Caster.Casts.Count(c => c.Spell == 28026));
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(EncounterState.Special, raid.Instance.GetData(7));
        int teleports = raid.Caster.Casts.Count(c => c.Spell is 28025 or 28026);
        raid.Boss.AI.OnUpdate(20000);
        Assert.Equal(teleports, raid.Caster.Casts.Count(c => c.Spell is 28025 or 28026));
    }

    [Fact]
    public void Gothik_OpensCentralGateAtThirtyPercentBeforeFourTeleports()
    {
        using var raid = new Raid(16060).Initialize();
        raid.Boss.AI!.OnUpdate(274000);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(7));
        raid.Boss.Health = raid.Boss.MaxHealth * 3 / 10;
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(EncounterState.Special, raid.Instance.GetData(7));
    }

    [Fact]
    public void Gothik_LiveSideDeathCreatesSpectralAddAtImportedDeadSideTrigger()
    {
        using var raid = new Raid(16060).Initialize();
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181170, GameObjectType.Door)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        objects.Summon(181170, 0, 0, 270, 0);
        raid.Creatures.SpawnTemporary(Template(16137), 0, 10, 270, 0);
        Creature live = raid.Creatures.SpawnTemporary(Template(16124), 0, -10, 270, 0);
        raid.Map.Combat.Kill(raid.Tank, live);
        Assert.Contains(raid.Creatures.Creatures, c => c.Entry == 16127 && c.Y == 10);
    }

    [Fact]
    public void Gothik_CombatGateOpensForGroundPhase_AndExitOpensOnDeath()
    {
        using var raid = new Raid(16060).Initialize();
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181124, GameObjectType.Door),
             GameObjectTestKit.GoTemplate(181170, GameObjectType.Door),
             GameObjectTestKit.GoTemplate(181125, GameObjectType.Door)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        GameObject entry = objects.Summon(181124, 0, 0, 270, 0)!;
        GameObject combat = objects.Summon(181170, 0, 0, 270, 0)!;
        GameObject exit = objects.Summon(181125, 0, 0, 270, 0)!;
        Assert.Equal(GameObjectState.Ready, entry.State);
        Assert.Equal(GameObjectState.Ready, combat.State);
        raid.Instance.SetData(7, EncounterState.InProgress);
        raid.Instance.SetData(7, EncounterState.Special);
        Assert.Equal(GameObjectState.Active, combat.State);
        Assert.Equal(GameObjectState.Ready, entry.State); // naxxramas.cpp SetData(TYPE_GOTHIK, SPECIAL) leaves the entry gate shut
        raid.Instance.SetData(7, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, entry.State);
        Assert.Equal(GameObjectState.Active, exit.State);
    }

    [Fact]
    public void Horsemen_CastTheirMarks_AndShieldAtHalfHealth()
    {
        using var raid = new Raid(16065).Initialize();
        raid.Boss.AI!.OnUpdate(12000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28833);
        raid.Boss.Health = raid.Boss.MaxHealth / 2;
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 29061);
    }

    [Theory]
    [InlineData(16065u, 28833u, 0u)]
    [InlineData(16062u, 28834u, 28881u)]
    [InlineData(16064u, 28832u, 28884u)]
    [InlineData(16063u, 28835u, 28883u)]
    public void EachHorsemanHasItsOwnMarkAndSpecial(uint entry, uint mark, uint special)
    {
        using var raid = new Raid(entry).Initialize();
        raid.Boss.AI!.OnUpdate(12000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == mark);
        if (entry == 16065)
            Assert.Contains(raid.Creatures.Creatures, c => c.Entry == 16697);
        else
            Assert.Contains(raid.Caster.Casts, c => c.Spell == special);
    }

    [Fact]
    public void Grobbulus_InjectsAndSprayHitSummonsFalloutSlime()
    {
        using var raid = new Raid(15931).Initialize();
        raid.Boss.AI!.OnUpdate(12000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28169);
        raid.Boss.AI.OnSpellHitTarget(raid.Tank, new SpellInfo { Id = 28157 });
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28218);
    }

    [Fact]
    public void Gluth_DecimateReducesPlayersToFivePercent_ButNotBelowCurrentHealth()
    {
        using var raid = new Raid(15932).Initialize();
        raid.Tank.Health = raid.Tank.MaxHealth;
        raid.Boss.AI!.OnUpdate(105000);
        Assert.Equal(Math.Max(1u, raid.Tank.MaxHealth / 20), raid.Tank.Health);
        raid.Boss.AI.OnUpdate(105000);
        Assert.Equal(Math.Max(1u, raid.Tank.MaxHealth / 20), raid.Tank.Health);
    }

    [Fact]
    public void Thaddius_BothAddsMustDieWithinTenSeconds_BeforeTeslaOverload()
    {
        using (var expired = new Raid(15928).Initialize(activateThaddius: false))
        {
            Creature first = expired.Creatures.SpawnTemporary(Template(15929), 1, 0, 270, 0);
            Creature second = expired.Creatures.SpawnTemporary(Template(15930), 1, 0, 270, 0);
            expired.Map.Combat.Kill(expired.Tank, first);
            expired.Instance.Update(10000);
            Assert.False(expired.Instance.ConstructAddsDefeated);
            // Stalagg was revived when the window ran out, so Feugen falling alone defeats nothing.
            expired.Map.Combat.Kill(expired.Tank, second);
            Assert.False(expired.Instance.ConstructAddsDefeated);
        }

        using var raid = new Raid(15928).Initialize(activateThaddius: false);
        Creature stalagg = raid.Creatures.SpawnTemporary(Template(15929), 1, 0, 270, 0);
        Creature feugen = raid.Creatures.SpawnTemporary(Template(15930), 1, 0, 270, 0);
        raid.Map.Combat.Kill(raid.Tank, stalagg);
        raid.Map.Combat.Kill(raid.Tank, feugen);
        Assert.True(raid.Instance.ConstructAddsDefeated);
        raid.Instance.Update(13999);
        Assert.NotEqual(EncounterState.Special, raid.Instance.GetData(12));
        raid.Instance.Update(1);
        Assert.Equal(EncounterState.Special, raid.Instance.GetData(12));
    }

    [Fact]
    public void Thaddius_AddsFakeDeathAtOneHealth_AndOverloadWakesBoss()
    {
        using var raid = new Raid(15928).Initialize(activateThaddius: false);
        Creature stalagg = raid.Creatures.SpawnTemporary(Template(15929), 1, 0, 270, 0);
        Creature feugen = raid.Creatures.SpawnTemporary(Template(15930), 1, 0, 270, 0);
        Assert.Equal(1u, stalagg.InvincibilityHpThreshold);
        raid.Map.Combat.DealDamage(raid.Tank, stalagg, stalagg.Health, direct: false);
        raid.Map.Combat.DealDamage(raid.Tank, feugen, feugen.Health, direct: false);
        Assert.Equal(1u, stalagg.Health);
        Assert.True(stalagg.IsAlive);
        Assert.True(raid.Instance.ConstructAddsDefeated);
        raid.Instance.Update(14000);
        Assert.Equal(EncounterState.Special, raid.Instance.GetData(12));
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28359);
        Assert.Equal(0u, (uint)(raid.Boss.UnitFlags & (UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer)));
    }

    [Fact]
    public void Sapphiron_FiveIceBoltsPrecedeAirBreath_ThenLands()
    {
        using var raid = new Raid(15989).Initialize();
        raid.Boss.AI!.OnUpdate(46000);
        for (int i = 0; i < 6; i++) raid.Boss.AI.OnUpdate(i == 0 ? 6000u : 3500u);
        raid.Boss.AI.OnUpdate(500);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28522);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28524);
        raid.Boss.AI.OnUpdate(10000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 18430);
    }

    [Fact]
    public void SapphironBirthObject_SpawnsDragonOnlyAfterTwentyTwoSeconds()
    {
        using var raid = new Raid(16028).Initialize();
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181356, GameObjectType.Goober)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        GameObject birth = objects.Summon(181356, 0, 0, 270, 0)!;
        Assert.False(raid.Instance.OnGameObjectUse(raid.Tank, birth));
        raid.Instance.Update(21999);
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 15989);
        raid.Instance.Update(1);
        Assert.Contains(raid.Creatures.Creatures, c => c.Entry == 15989);
    }

    [Fact]
    public void KelThuzad_ChannelAndAddsThenCastPhaseTwoAndGuardianSpells()
    {
        using var raid = new Raid(15990).Initialize();
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 29423);
        raid.Boss.AI!.OnUpdate(4000);
        Assert.Equal(9, raid.Caster.Casts.Count(c => c.Spell == 28421));
        raid.Boss.AI.OnUpdate(321000);
        raid.Boss.AI.OnUpdate(9999);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 28478);
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28478);
        raid.Boss.Health = raid.Boss.MaxHealth / 3;
        raid.Boss.AI.OnUpdate(4000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28453);
        raid.Boss.AI.OnUpdate(55000);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 28453);
    }

    [Fact]
    public void KelThuzad_FourStunnedGuardiansBreakShackles()
    {
        using var raid = new Raid(15990).Initialize();
        for (int i = 0; i < 4; i++)
        {
            Creature guardian = raid.Creatures.SpawnTemporary(Template(16441), i + 1, 0, 270, 0);
            guardian.UnitFlags |= UnitFlags.Stunned;
        }
        raid.Instance.StartGuardianChecks();
        raid.Instance.Update(2000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 29910);
    }

    [Theory]
    [InlineData(AuraRemoveMode.Expire, 28322u)]
    [InlineData(AuraRemoveMode.Dispel, 28206u)]
    public void Grobbulus_InjectionRemovalCreatesCorrectBurstAndPoisonCloud(AuraRemoveMode mode, uint burst)
    {
        SpellInfo Aura(uint id) => SpellTestKit.Spell(id,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(30000, 0, 30000) };
        using var kit = new SpellTestKit(Aura(28169), Aura(28322), Aura(28206), Aura(28240));
        Player target = TestWorld.CreatePlayer(99, 0, 0, new FakeSession(), 533);
        kit.World.AddPlayer(target);
        kit.World.RunTick(0);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(target, 28169, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(target, 28169));
        kit.System.RemoveAuras(target, 28169, mode);
        Assert.True(kit.System.HasAura(target, burst));
        Assert.True(kit.System.HasAura(target, 28240));
    }

    [Fact]
    public void Thaddius_SameChargeNearbyPlayersReceiveTheChargeBuff()
    {
        SpellInfo Charge(uint id) => SpellTestKit.Spell(id,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.PeriodicTriggerSpell, amplitude: 1000)) with
            { Duration = new SpellDuration(30000, 0, 30000) };
        SpellInfo Buff(uint id) => SpellTestKit.Spell(id,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(30000, 0, 30000) };
        using var kit = new SpellTestKit(Charge(28059), Charge(28084), Buff(29659), Buff(29660));
        Player first = TestWorld.CreatePlayer(101, 0, 0, new FakeSession(), 533);
        Player second = TestWorld.CreatePlayer(102, 1, 0, new FakeSession(), 533);
        kit.World.AddPlayer(first); kit.World.AddPlayer(second); kit.World.RunTick(0);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(first, 28059, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(second, 28059, SpellCastTargets.ForSelf(), triggered: true));
        kit.Advance(1000);
        Assert.True(kit.System.HasAura(first, 29659));
        Assert.True(kit.System.HasAura(second, 29659));
    }

    [Fact]
    public void KelThuzad_PhaseTwoFollowsTheVmangosOpeningSchedule()
    {
        using var raid = new Raid(15990).Initialize();
        Player second = TestWorld.CreatePlayer(2, 2, 0, new FakeSession(), 533);
        second.Relocate(2, 0, 270, 0, 0);
        second.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        raid.World.AddPlayer(second); raid.World.RunTick(0);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        raid.Boss.Combat.Threat.AddThreat(second, 1);
        raid.Boss.AI!.OnUpdate(325000);
        int before = raid.Caster.Casts.Count;
        uint[] order = [];
        foreach (uint step in new uint[] { 10000, 4000, 6000, 10000, 20000, 10000 })
        {
            raid.Boss.AI.OnUpdate(step);
            order = [.. order, .. raid.Caster.Casts.Skip(before).Select(c => c.Spell).Where(id => id is 27810 or 27819 or 28479 or 27808 or 28408)
                .Except(order)];
        }
        // vmangos EVENT_PHASE_TWO_START: fissure 14 s, detonate 20 s, volley 30 s, frost blast 50 s, chains 60 s.
        Assert.Equal([27810u, 27819u, 28479u, 27808u, 28408u], order);
        Assert.All(raid.Caster.Casts.Where(c => c.Spell is 27810 or 27808), c => Assert.Equal(second, c.Target));
    }

    [Fact]
    public void FourHorsemen_PullingOneBringsTheOtherThree()
    {
        using var raid = new Raid(16065).Initialize(attackBoss: false);
        Creature[] others = [.. new uint[] { 16062, 16064, 16063 }.Select(e => raid.Creatures.SpawnTemporary(Template(e), 1, 0, 270, 0))];
        Assert.All(others, c => Assert.False(c.Combat.IsInCombat));
        raid.Boss.AI!.AttackStart(raid.Tank);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(8));
        Assert.All(others, c => Assert.True(c.Combat.IsInCombat));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 250)]
    [InlineData(3, 1000)]
    [InlineData(4, 3000)]
    [InlineData(6, 6000)]
    public void HorsemenMarkStackDamageFollowsTheScriptTable(int stacks, int damage)
        => Assert.Equal(damage, NaxxramasAuraModule.MarkDamage(stacks));

    [Fact]
    public void HorsemenMark_SecondStackCastsMarkDamageFromTheHorseman()
    {
        SpellInfo mark = SpellTestKit.Spell(28833,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(75000, 0, 75000), StackAmount = 100 };
        SpellInfo damage = SpellTestKit.Spell(28836,
            SpellTestKit.Effect(SpellEffectName.SchoolDamage, 1)) with { School = SpellSchool.Shadow };
        using var kit = new SpellTestKit(mark, damage);
        Player target = TestWorld.CreatePlayer(99, 0, 0, new FakeSession(), 533);
        kit.World.AddPlayer(target);
        kit.World.RunTick(0);
        target.MaxHealth = 10_000;
        target.Health = 10_000;
        uint full = target.Health;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(target, 28833, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(full, target.Health);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(target, 28833, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(full - 250, target.Health);
    }
}
