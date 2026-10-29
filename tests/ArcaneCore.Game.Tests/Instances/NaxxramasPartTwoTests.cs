using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
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

        public Raid Initialize(bool activateThaddius = true, bool attackBoss = true, IEnumerable<CreatureSpawn>? spawns = null)
        {
            // Map 533 is a raid (map_template map_type 2): SetInCombatWithZone only works in a dungeon.
            WorldMaps.Of(World).Load(new MapContent(
                [new MapTemplate(533, 0, MapType.Raid, 3456, 40, 7, 0, 3005.68f, -3447.77f, "Naxxramas", "instance_naxxramas")], [], [], [], []));
            Map = World.GetMap(533);
            Instance = Assert.IsType<NaxxramasInstance>(InstanceScriptRegistry.Default.Create(Map));
            Instance.Initialize();
            Map.AddUpdater(Instance);
            uint[] entries = [entry, 16803, 16137, 16124, 16127, 16125, 16148,
                16126, 16150, 16065, 16062, 16064, 16063, 16697, 15929, 15930, 15928, 15989, 15990, 16441];
            Creatures = new CreatureMapSystem(Map, Content([.. entries.Distinct().Select(e => Template(e))], spawns ?? []),
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
            if (attackBoss && entry == 15990) Instance.OnAreaTrigger(Tank, 4112); // Kel'Thuzad starts from his trigger, not a pull
            else if (attackBoss && (entry != 15928 || activateThaddius)) Boss.AI!.AttackStart(Tank);
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
        // Maexxna and Loatheb come from the Arachnid/Plague part; the Four Horsemen and Thaddius from this one's own paths.
        foreach (uint type in new uint[] { 2, 5 }) raid.Instance.SetData(type, EncounterState.Done);
        foreach (uint horseman in NaxxramasInstance.HorsemenEntries) raid.Instance.RecordHorsemanDeath(horseman);
        Assert.False(raid.Instance.WingsCleared);
        Assert.True(raid.Instance.BlocksAreaTriggerTeleport(raid.Tank, 4156));
        raid.Instance.SetData(12, EncounterState.Done);
        Assert.True(raid.Instance.WingsCleared);
        Assert.False(raid.Instance.BlocksAreaTriggerTeleport(raid.Tank, 4156));
        raid.Instance.SetData(5, EncounterState.Fail);
        Assert.True(raid.Instance.BlocksAreaTriggerTeleport(raid.Tank, 4156));
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
    public void Gothik_LiveSideDeathStartsTheAnchorChainFromACorpseTrigger()
    {
        // vmangos boss_gothikAI::SummonedCreatureJustDied: a 15 s sub-boss trigger on the corpse casts To Anchor 1 (27892 for a trainee) at the
        // nearest high anchor of the living side. The rest of the chain is NaxxramasEncounterSpellTests (it needs the real spell system).
        using var raid = new Raid(16060).Initialize();
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181170, GameObjectType.Door)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        objects.Summon(181170, 0, 0, 270, 0);
        Creature spectralAnchor = raid.Creatures.SpawnTemporary(Template(16137), 0, 10, 270, 0);
        Creature livingAnchor = raid.Creatures.SpawnTemporary(Template(16137), 0, -20, 270, 0);
        Creature live = raid.Creatures.SpawnTemporary(Template(16124), 0, -10, 270, 0);
        raid.Map.Combat.Kill(raid.Tank, live);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 27892 && c.Target == livingAnchor && c.Triggered);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Target == spectralAnchor);
        Assert.Contains(raid.Creatures.Creatures, c => c.Entry == 16137 && c.Y == -10); // the corpse trigger
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 16127); // nothing is raised directly
    }

    [Fact]
    public void Gothik_LiveSideDeathStartsTheChain_WhenTheTriggersRunTheSpellAnchorScript()
    {
        // Spec 533-spell_anchor check 3: the inert GothikAnchorAI on 16137 must not disturb the trigger lookup.
        using var raid = new Raid(16060).Initialize();
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(181170, GameObjectType.Door)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        objects.Summon(181170, 0, 0, 270, 0);
        Creature trigger = raid.Creatures.SpawnTemporary(Template(16137) with { ScriptName = GothikAnchorAI.ScriptName }, 0, -20, 270, 0);
        Assert.IsType<GothikAnchorAI>(trigger.AI);
        Creature live = raid.Creatures.SpawnTemporary(Template(16125), 0, -10, 270, 0);
        raid.Map.Combat.Kill(raid.Tank, live);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 27928 && c.Target == trigger);
        Assert.Null(trigger.Combat.Victim);
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
        // vmangos boss_four_horsemen_shared::Reset m_uiMarkTimer = 20000.
        raid.Boss.AI!.OnUpdate(19999);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 28833);
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28833);
        raid.Boss.Health = raid.Boss.MaxHealth / 2;
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 29061);
    }

    [Theory]
    [InlineData(16065u, 28833u, 0u, 12000u)]
    [InlineData(16062u, 28834u, 28881u, 0u)]
    [InlineData(16064u, 28832u, 28884u, 30000u)]
    [InlineData(16063u, 28835u, 28883u, 12000u)]
    public void EachHorsemanHasItsOwnMarkAndSpecial(uint entry, uint mark, uint special, uint firstSpecial)
    {
        using var raid = new Raid(entry).Initialize();
        // vmangos Aggro EVENT_BOSS_ABILITY: Blaumeux and Zeliek 12 s, Korth'azz 30 s; Mograine's Righteous Fire is cast on aggro.
        if (firstSpecial > 1)
        {
            raid.Boss.AI!.OnUpdate(firstSpecial - 1);
            Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == special);
            Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 16697);
            raid.Boss.AI.OnUpdate(1);
        }
        raid.Boss.AI!.OnUpdate(30000 - firstSpecial);
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
        // The overload sets SPECIAL and puts Thaddius in combat with the zone; his Aggro then sets IN_PROGRESS (boss_thaddius.cpp:117-123).
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(12));
        Assert.True(raid.Boss.Combat.IsInCombat);
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
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(12)); // SPECIAL, then his Aggro (zone combat) sets IN_PROGRESS
        Assert.True(raid.Boss.Combat.IsInCombat);
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
        raid.Boss.AI!.OnUpdate(325000);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        raid.Boss.Combat.Threat.AddThreat(second, 1);
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
    // --- movement and melee state (intake review: a bare MeleeEnabled set left the swing and the chase running) -------------

    private static void AssertStandsWithoutSwinging(Creature creature)
    {
        Assert.False(creature.AI!.CombatMovement);
        Assert.False(creature.AI.MeleeEnabled);
        Assert.False(creature.Combat.IsMeleeAttacking);
        Assert.NotEqual(MovementGeneratorType.Chase, creature.Motion.CurrentType);
    }

    private static void AssertChasesAndSwings(Creature creature)
    {
        Assert.True(creature.AI!.CombatMovement);
        Assert.True(creature.AI.MeleeEnabled);
        Assert.True(creature.Combat.IsMeleeAttacking);
        Assert.Equal(MovementGeneratorType.Chase, creature.Motion.CurrentType);
    }

    [Fact]
    public void Gothik_OnTheBalcony_NeitherChasesNorSwings_UntilTheGroundPhase()
    {
        // mangos-classic boss_gothik.cpp Reset/Aggro SetCombatMovement(false) + SetMeleeEnabled(false); HandleGroundPhase turns both on.
        using var raid = new Raid(16060).Initialize();
        Assert.Same(raid.Tank, raid.Boss.Combat.Victim);
        AssertStandsWithoutSwinging(raid.Boss);
        raid.World.RunTick(1000);
        AssertStandsWithoutSwinging(raid.Boss);
        raid.Boss.AI!.OnUpdate(274000);
        AssertChasesAndSwings(raid.Boss);
    }

    [Fact]
    public void Sapphiron_AirPhaseStopsChaseAndSwing_LandingResumesBoth()
    {
        using var raid = new Raid(15989).Initialize();
        AssertChasesAndSwings(raid.Boss);
        raid.Boss.AI!.OnUpdate(46000);
        AssertStandsWithoutSwinging(raid.Boss);
        for (int i = 0; i < 6; i++) raid.Boss.AI.OnUpdate(i == 0 ? 6000u : 3500u);
        raid.Boss.AI.OnUpdate(500);
        AssertStandsWithoutSwinging(raid.Boss);
        raid.Boss.AI.OnUpdate(10000);
        AssertChasesAndSwings(raid.Boss);
    }

    [Fact]
    public void ThaddiusAdd_FakeDeathStopsChaseAndSwing_ReviveResumesBoth()
    {
        using var raid = new Raid(15928).Initialize(activateThaddius: false);
        Creature stalagg = raid.Creatures.SpawnTemporary(Template(15929), 1, 0, 270, 0);
        raid.Creatures.SpawnTemporary(Template(15930), 1, 0, 270, 0);
        stalagg.AI!.AttackStart(raid.Tank);
        AssertChasesAndSwings(stalagg);
        raid.Map.Combat.DealDamage(raid.Tank, stalagg, stalagg.Health, direct: false);
        Assert.Equal(1u, stalagg.Health);
        AssertStandsWithoutSwinging(stalagg);
        Assert.True(((ThaddiusAddAI)stalagg.AI).IsFakingDeath);
        raid.World.RunTick(1000);
        AssertStandsWithoutSwinging(stalagg);
        // Feugen did not fall within ten seconds: Stalagg gets up and fights again.
        raid.Instance.Update(10000);
        Assert.False(((ThaddiusAddAI)stalagg.AI).IsFakingDeath);
        Assert.Equal(stalagg.MaxHealth, stalagg.Health);
        Assert.True(stalagg.AI.CombatMovement);
        Assert.True(stalagg.AI.MeleeEnabled);
    }

    [Fact]
    public void ThaddiusAdd_CombatScriptRunsFromTheFakeDeathUntilReviveOrReset()
    {
        // boss_thaddius.cpp: JustPreventedDeath SetCombatScriptStatus(true) (:275); Revive (:235) and Reset (:208) set it false.
        using var raid = new Raid(15928).Initialize(activateThaddius: false);
        Creature stalagg = raid.Creatures.SpawnTemporary(Template(15929), 1, 0, 270, 0);
        raid.Creatures.SpawnTemporary(Template(15930), 1, 0, 270, 0);
        var ai = (ThaddiusAddAI)stalagg.AI!;
        ai.AttackStart(raid.Tank);
        Assert.False(ai.CombatScriptRunning);

        raid.Map.Combat.DealDamage(raid.Tank, stalagg, stalagg.Health, direct: false);
        Assert.True(ai.IsFakingDeath);
        Assert.True(ai.CombatScriptRunning);
        ai.Revive();
        Assert.False(ai.CombatScriptRunning);

        raid.Map.Combat.DealDamage(raid.Tank, stalagg, stalagg.Health, direct: false);
        Assert.True(ai.CombatScriptRunning);
        ai.EnterEvadeMode(); // Reset: evade drops the fake death with the rest of the script state
        Assert.False(ai.IsFakingDeath);
        Assert.False(ai.CombatScriptRunning);
    }

    private static readonly CreatureSpawn[] ConstructAddSpawns =
    [
        Spawn(53311, 15929, 2, 0, 270, 533, respawnSeconds: 604800), Spawn(53312, 15930, 3, 0, 270, 533, respawnSeconds: 604800),
    ];

    private static void Tick(Raid raid, int ms)
    {
        for (int elapsed = 0; elapsed < ms; elapsed += 100) raid.World.RunTick(100);
    }

    private static Creature Add(Raid raid, uint entry) => raid.Creatures.Creatures.Single(c => c.Entry == entry);

    private static Player AddPlayer2(Raid raid)
    {
        Player player = TestWorld.CreatePlayer(2, 0, 0, new FakeSession(), 533);
        player.Relocate(0, 1, 270, 0, 0);
        raid.World.AddPlayer(player);
        raid.World.RunTick(0);
        return player;
    }

    private static void AssertAddsRespawnedAtFullHealthAndIdle(Raid raid)
    {
        foreach (uint entry in new uint[] { 15929, 15930 })
        {
            Creature add = Add(raid, entry);
            Assert.True(add.IsAlive);
            Assert.Equal(add.MaxHealth, add.Health);
            Assert.False(((ThaddiusAddAI)add.AI!).IsFakingDeath);
            Assert.False(add.Combat.IsInCombat);
        }
    }

    [Fact]
    public void ThaddiusAdds_OnePlatformDeadWhileTheOtherFights_NoFailNoHeal()
    {
        // cmangos has no per-add wipe rule: a feigning add whose own enemies died, while its partner still fights, changes nothing.
        using var raid = new Raid(15928).Initialize(activateThaddius: false, spawns: ConstructAddSpawns);
        raid.World.RunTick(0);
        Player second = AddPlayer2(raid);
        Creature stalagg = Add(raid, 15929), feugen = Add(raid, 15930);
        stalagg.AI!.AttackStart(second);
        feugen.AI!.AttackStart(raid.Tank);
        feugen.Health = feugen.MaxHealth / 2;
        uint feugenHealth = feugen.Health;
        raid.Map.Combat.DealDamage(second, stalagg, stalagg.Health, direct: false);
        Assert.True(((ThaddiusAddAI)stalagg.AI).IsFakingDeath);

        second.Health = 0;
        Tick(raid, 5000);

        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        Assert.Equal(feugenHealth, feugen.Health);
        Assert.True(((ThaddiusAddAI)stalagg.AI).IsFakingDeath);
        Assert.True(stalagg.IsAlive);
        Assert.True(feugen.IsAlive);
    }

    [Fact]
    public void Thaddius_WipeAfterTheOverloadWhileEngaged_FailsDespawnsTheAddsAndRespawnsThemIn30s()
    {
        // naxxramas.cpp SetData(TYPE_THADDIUS, FAIL) :594-617: both adds are set to a 30 s respawn and despawned; Thaddius is re-flagged
        // at home (boss_thaddius.cpp:109-115, :125-129).
        using var raid = new Raid(15928).Initialize(activateThaddius: true, spawns: ConstructAddSpawns);
        raid.World.RunTick(0);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        raid.Tank.Health = 0;

        Tick(raid, 5000);

        Assert.Equal(EncounterState.Fail, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        Assert.All(raid.Creatures.Creatures.Where(c => c.Entry is 15929 or 15930), c => Assert.False(c.IsAlive));
        Assert.False(raid.Instance.ConstructAddsDefeated);
        Assert.NotEqual(0u, (uint)(raid.Boss.UnitFlags & UnitFlags.NotSelectable));
        Assert.NotEqual(0u, (uint)(raid.Boss.UnitFlags & UnitFlags.ImmuneToPlayer));

        Tick(raid, 31_000);
        AssertAddsRespawnedAtFullHealthAndIdle(raid);
    }

    [Fact]
    public void Thaddius_KilledThenPlayersLeave_StaysDoneAndTheAddsAreGone()
    {
        // boss_thaddiusAI::JustDied (boss_thaddius.cpp:131-145) despawns both adds; nothing later can fail a finished encounter.
        using var raid = new Raid(15928).Initialize(activateThaddius: true, spawns: ConstructAddSpawns);
        raid.World.RunTick(0);
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Assert.Equal(EncounterState.Done, raid.Instance.GetData(NaxxramasInstance.Thaddius));

        raid.Tank.Health = 0;
        Tick(raid, 5000);

        Assert.Equal(EncounterState.Done, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        Assert.All(raid.Creatures.Creatures.Where(c => c.Entry is 15929 or 15930), c => Assert.False(c.IsAlive));
    }

    [Fact]
    public void ThaddiusAdds_BothFakingWhenTheRaidDiesBeforeTheOverload_FailsAndRespawnsThem()
    {
        using var raid = new Raid(15928).Initialize(activateThaddius: false, spawns: ConstructAddSpawns);
        raid.World.RunTick(0);
        Creature stalagg = Add(raid, 15929), feugen = Add(raid, 15930);
        stalagg.AI!.AttackStart(raid.Tank);
        feugen.AI!.AttackStart(raid.Tank);
        raid.Map.Combat.DealDamage(raid.Tank, stalagg, stalagg.Health, direct: false);
        raid.Map.Combat.DealDamage(raid.Tank, feugen, feugen.Health, direct: false);
        Assert.True(raid.Instance.ConstructAddsDefeated);

        raid.Tank.Health = 0; // the wipe: nobody is left to fight when the Teslas overload
        Tick(raid, 15_000);

        Assert.Equal(EncounterState.Fail, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        Assert.All(raid.Creatures.Creatures.Where(c => c.Entry is 15929 or 15930), c => Assert.False(c.IsAlive));

        Tick(raid, 31_000);
        AssertAddsRespawnedAtFullHealthAndIdle(raid);
    }

    [Fact]
    public void ThaddiusAdds_OneFakingWhenThePartnerEvades_FailsAndRespawnsBoth()
    {
        using var raid = new Raid(15928).Initialize(activateThaddius: false, spawns: ConstructAddSpawns);
        raid.World.RunTick(0);
        Creature stalagg = Add(raid, 15929), feugen = Add(raid, 15930);
        stalagg.AI!.AttackStart(raid.Tank);
        raid.Map.Combat.DealDamage(raid.Tank, stalagg, stalagg.Health, direct: false);
        Assert.True(((ThaddiusAddAI)stalagg.AI).IsFakingDeath);
        Assert.True(feugen.Combat.IsInCombat);

        raid.Tank.Health = 0; // Feugen has nobody to fight: it evades home, which fails the encounter
        Tick(raid, 5000);

        Assert.Equal(EncounterState.Fail, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        Assert.All(raid.Creatures.Creatures.Where(c => c.Entry is 15929 or 15930), c => Assert.False(c.IsAlive));

        Tick(raid, 31_000);
        AssertAddsRespawnedAtFullHealthAndIdle(raid);
    }

    [Fact]
    public void ThaddiusAdd_ReviveOnAnAddThatIsNotFaking_DoesNothing()
    {
        // boss_thaddiusAddsAI::Revive is only reached from the revive timer of a feigning add; a living partner is not healed.
        using var raid = new Raid(15928).Initialize(activateThaddius: false);
        Creature feugen = raid.Creatures.SpawnTemporary(Template(15930), 1, 0, 270, 0);
        feugen.AI!.AttackStart(raid.Tank);
        feugen.Health = feugen.MaxHealth / 2;
        uint health = feugen.Health;

        ((ThaddiusAddAI)feugen.AI).Revive();

        Assert.Equal(health, feugen.Health);
    }

    [Fact]
    public void Thaddius_OverloadWithNoLivingPlayer_FailsTheEncounter()
    {
        // The Tesla overload puts Thaddius in combat with the zone; with no living player that yields nobody. ArcaneCore-only fail: the
        // reference leaves him attackable but never fighting (a soft-lock). Here he stays flagged unselectable and immune to players, so
        // nobody can kill him for free; precedent FrostwyrmBossAIs.StartPhaseTwo (Kel'Thuzad).
        using var raid = new Raid(15928).Initialize(activateThaddius: false);
        raid.Instance.RecordConstructAddDeath(15929);
        raid.Instance.RecordConstructAddDeath(15930);
        raid.Tank.Health = 0;
        raid.World.RunTick(100);

        raid.Instance.Update(14000);

        Assert.Equal(EncounterState.Fail, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        Assert.False(raid.Boss.Combat.IsInCombat);
        Assert.NotEqual(0u, (uint)(raid.Boss.UnitFlags & UnitFlags.NotSelectable));
        Assert.NotEqual(0u, (uint)(raid.Boss.UnitFlags & UnitFlags.ImmuneToPlayer));
        Player late = AddPlayer2(raid);
        Assert.False(raid.Map.Combat.Hooks.CanAttack(late, raid.Boss)); // a player cannot hit him
    }

    [Fact]
    public void ThaddiusAdds_FailBeforeTheDeferredDespawn_IgnoresAnAddBroughtToOneHealthInTheWindow()
    {
        // SetData(FAIL) only schedules the despawn; an add dropped to 1 HP before it runs must not re-arm the revive or overload timers.
        using var raid = new Raid(15928).Initialize(activateThaddius: false, spawns: ConstructAddSpawns);
        raid.World.RunTick(0);
        Creature stalagg = Add(raid, 15929);
        stalagg.AI!.AttackStart(raid.Tank);
        raid.Instance.SetData(NaxxramasInstance.Thaddius, EncounterState.Fail);

        raid.Map.Combat.DealDamage(raid.Tank, stalagg, stalagg.Health, direct: false);
        Tick(raid, 20_000); // past the 10 s revive window and before the 30 s respawn

        Assert.False(raid.Instance.ConstructAddsDefeated);
        Assert.All(raid.Creatures.Creatures.Where(c => c.Entry is 15929 or 15930), c => Assert.False(c.IsAlive));
        Tick(raid, 11_000);
        AssertAddsRespawnedAtFullHealthAndIdle(raid);
    }

    [Fact]
    public void ThaddiusAdds_FakingMoreThanTenSecondsApart_TheFirstRevivesThenEvadesAndFailsTheEncounter()
    {
        using var raid = new Raid(15928).Initialize(activateThaddius: false, spawns: ConstructAddSpawns);
        raid.World.RunTick(0);
        Creature stalagg = Add(raid, 15929);
        stalagg.AI!.AttackStart(raid.Tank);
        raid.Map.Combat.DealDamage(raid.Tank, stalagg, stalagg.Health, direct: false);
        Assert.True(((ThaddiusAddAI)stalagg.AI).IsFakingDeath);

        Tick(raid, 10_500); // Feugen never fell: Stalagg gets up
        Assert.False(((ThaddiusAddAI)stalagg.AI).IsFakingDeath);
        Assert.Equal(stalagg.MaxHealth, stalagg.Health);
        Assert.False(raid.Instance.ConstructAddsDefeated);

        raid.Tank.Health = 0; // the raid dies: the revived add evades home
        Tick(raid, 5000);

        Assert.Equal(EncounterState.Fail, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        Assert.All(raid.Creatures.Creatures.Where(c => c.Entry is 15929 or 15930), c => Assert.False(c.IsAlive));
        Tick(raid, 31_000);
        AssertAddsRespawnedAtFullHealthAndIdle(raid);
    }

    [Fact]
    public void Thaddius_PulledBeforeTheAddsFell_LeavesCombatFlaggedWithoutCallingAssistance_AndTheRealEngageStillDoes()
    {
        using var raid = new Raid(15928).Initialize(activateThaddius: false);
        raid.Creatures.SpawnTemporary(Template(15929), 1, 0, 270, 0);
        raid.Creatures.SpawnTemporary(Template(15930), 1, 0, 270, 0);

        raid.Boss.AI!.AttackStart(raid.Tank);

        Assert.False(raid.Boss.Combat.IsInCombat);
        Assert.NotEqual(0u, (uint)(raid.Boss.UnitFlags & UnitFlags.NotSelectable));
        Assert.NotEqual(0u, (uint)(raid.Boss.UnitFlags & UnitFlags.ImmuneToPlayer));
        Assert.False(raid.Boss.CalledAssistance);

        raid.Instance.RecordConstructAddDeath(15929);
        raid.Instance.RecordConstructAddDeath(15930);
        raid.Instance.Update(14000); // the overload pulls him for real

        Assert.True(raid.Boss.Combat.IsInCombat);
        Assert.Equal(0u, (uint)(raid.Boss.UnitFlags & (UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer)));
        Assert.True(raid.Boss.CalledAssistance);
    }

    [Fact]
    public void ThaddiusAdd_HitFromOutsideItsAggroRadius_StartsTheEncounterAndPullsThePartner()
    {
        // vmangos CreatureAI::AttackedBy -> AttackStart -> Aggro: the encounter starts, the door shuts, the other add joins.
        using var raid = new Raid(15928).Initialize(activateThaddius: false);
        Creature stalagg = raid.Creatures.SpawnTemporary(Template(15929), 60, 0, 270, 0);
        Creature feugen = raid.Creatures.SpawnTemporary(Template(15930), 70, 0, 270, 0);
        Assert.False(stalagg.Combat.IsInCombat);
        raid.Map.Combat.DealDamage(raid.Tank, stalagg, 10);
        Assert.Same(raid.Tank, stalagg.Combat.Victim);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        Assert.True(feugen.Combat.IsInCombat);
    }

    [Fact]
    public void KelThuzad_TriggerStartsTheChannelOutOfCombat_AndPhaseTwoChasesAndSwings()
    {
        // naxxramas.cpp DoHandleAreaTrigger only sets IN_PROGRESS; boss_kelthuzad.cpp "does not enter combat until phase 2".
        using var raid = new Raid(15990).Initialize(attackBoss: false);
        raid.Instance.OnAreaTrigger(raid.Tank, 4112);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(NaxxramasInstance.KelThuzad));
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 29423);
        Assert.Null(raid.Boss.Combat.Victim);
        Assert.False(raid.Boss.Combat.IsInCombat);
        AssertStandsWithoutSwinging(raid.Boss);
        Assert.NotEqual(0u, (uint)(raid.Boss.UnitFlags & UnitFlags.NotSelectable));
        raid.World.RunTick(1000);
        raid.Boss.AI!.OnUpdate(60000);
        Assert.Null(raid.Boss.Combat.Victim);
        Assert.False(raid.Boss.AI.AttackStart(raid.Tank)); // no pull, no retaliation in phase one
        Assert.Null(raid.Boss.Combat.Victim);
        raid.Boss.AI.OnUpdate(265000);
        Assert.Same(raid.Tank, raid.Boss.Combat.Victim);
        Assert.Equal(0u, (uint)(raid.Boss.UnitFlags & (UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer)));
        AssertChasesAndSwings(raid.Boss);
    }

    [Fact]
    public void KelThuzad_PhaseOneFailsWhenNoLivingPlayerIsLeft()
    {
        using var raid = new Raid(15990).Initialize();
        raid.Map.Combat.Kill(null, raid.Tank);
        raid.Boss.AI!.OnUpdate(1000);
        Assert.Equal(EncounterState.Fail, raid.Instance.GetData(NaxxramasInstance.KelThuzad));
        Assert.Null(raid.Boss.Combat.Victim);
    }

    [Fact]
    public void AreaTriggers_IgnoreGameMastersAndTheDead()
    {
        // mangos-classic AreaTrigger_at_naxxramas: IsGameMaster() || !IsAlive() returns before DoHandleAreaTrigger.
        using var raid = new Raid(15990).Initialize(attackBoss: false);
        raid.Creatures.SpawnTemporary(Template(15928), 5, 0, 270, 0);
        raid.Tank.Flags |= PlayerFlags.Gm;
        raid.Instance.OnAreaTrigger(raid.Tank, 4112);
        raid.Instance.OnAreaTrigger(raid.Tank, 4113);
        Assert.Equal(EncounterState.NotStarted, raid.Instance.GetData(NaxxramasInstance.KelThuzad));
        Assert.Equal(EncounterState.NotStarted, raid.Instance.GetData(NaxxramasInstance.Thaddius));
        raid.Tank.Flags &= ~PlayerFlags.Gm;
        raid.Map.Combat.Kill(null, raid.Tank);
        raid.Instance.OnAreaTrigger(raid.Tank, 4112);
        raid.Instance.OnAreaTrigger(raid.Tank, 4113);
        Assert.Equal(EncounterState.NotStarted, raid.Instance.GetData(NaxxramasInstance.KelThuzad));
        Assert.Equal(EncounterState.NotStarted, raid.Instance.GetData(NaxxramasInstance.Thaddius));
    }

    private static readonly CreatureSpawn[] HorsemenSpawns =
    [
        Spawn(53301, 16065, 2, 0, 270, 533, respawnSeconds: 604800), Spawn(53302, 16062, 3, 0, 270, 533, respawnSeconds: 604800),
        Spawn(53303, 16064, 4, 0, 270, 533, respawnSeconds: 604800), Spawn(53304, 16063, 5, 0, 270, 533, respawnSeconds: 604800),
    ];

    [Fact]
    public void FourHorsemen_WipeRespawnsTheDeadAndSendsTheRestHome_SoFourDeathsStillComplete()
    {
        // vmangos instance_naxxramas.cpp SetData(TYPE_FOUR_HORSEMEN, FAIL) respawns dead horsemen and resets the death counter.
        using var raid = new Raid(16028).Initialize(attackBoss: false, spawns: HorsemenSpawns);
        raid.World.RunTick(0);
        Creature Horseman(uint entry) => raid.Creatures.Creatures.Single(c => c.Entry == entry);
        Horseman(16065).AI!.AttackStart(raid.Tank);
        Assert.All(NaxxramasInstance.HorsemenEntries, e => Assert.True(Horseman(e).Combat.IsInCombat));
        raid.Map.Combat.Kill(raid.Tank, Horseman(16065));
        raid.Map.Combat.Kill(raid.Tank, Horseman(16062));
        raid.Instance.SetData(NaxxramasInstance.Horsemen, EncounterState.Fail);
        Assert.All(NaxxramasInstance.HorsemenEntries, e => Assert.True(Horseman(e).IsAlive));
        Assert.All(NaxxramasInstance.HorsemenEntries, e => Assert.False(Horseman(e).Combat.IsInCombat));
        foreach (uint entry in NaxxramasInstance.HorsemenEntries.Take(3))
        {
            raid.Map.Combat.Kill(raid.Tank, Horseman(entry));
            Assert.NotEqual(EncounterState.Done, raid.Instance.GetData(NaxxramasInstance.Horsemen));
        }
        raid.Map.Combat.Kill(raid.Tank, Horseman(NaxxramasInstance.HorsemenEntries[3]));
        Assert.Equal(EncounterState.Done, raid.Instance.GetData(NaxxramasInstance.Horsemen));
    }

    [Fact]
    public void FourHorsemen_ADeadHorsemanLoadedBeforeTheEncounterIsDoneRespawns()
    {
        // vmangos instance_naxxramas::OnCreatureCreate: a dead horseman respawns while TYPE_FOUR_HORSEMEN is not DONE.
        using var raid = new Raid(16028).Initialize(attackBoss: false, spawns: HorsemenSpawns.Take(1));
        raid.World.RunTick(0);
        Creature blaumeux = raid.Creatures.Creatures.Single(c => c.Entry == 16065);
        raid.Map.Combat.Kill(raid.Tank, blaumeux);
        Assert.False(blaumeux.IsAlive);
        raid.Instance.OnCreatureCreate(blaumeux); // what a grid reload or a restart with a saved respawn time delivers
        raid.Instance.Update(1);
        Assert.True(blaumeux.IsAlive);
    }

    [Fact]
    public void Horsemen_MarkHalvesEveryThreatEntry()
    {
        using var raid = new Raid(16065).Initialize();
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        float before = raid.Boss.Combat.Threat.Entries.Single(e => e.Target == raid.Tank).Threat;
        raid.Boss.AI!.OnUpdate(20000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 28833);
        Assert.Equal(before / 2, raid.Boss.Combat.Threat.Entries.Single(e => e.Target == raid.Tank).Threat, 3);
    }

    [Fact]
    public void Patchwerk_HatefulStrikeUsesCombatReach_NotAFixedFiveYards()
    {
        // vmangos DoHatefulStrike -> CanReachWithMeleeSpellAttack: Patchwerk's reach 3 + player 1.5 + 4/3 = 5.83 yd.
        using var raid = new Raid(16028).Initialize();
        raid.Boss.SetFloat(UpdateFields.UnitFieldCombatreach, 3f);
        Player offTank = TestWorld.CreatePlayer(2, 0, 0, new FakeSession(), 533);
        Player outside = TestWorld.CreatePlayer(3, 0, 0, new FakeSession(), 533);
        offTank.Relocate(1 + 5.6f, 0, 270, 0, 0);
        outside.Relocate(1 + 6.2f, 0, 270, 0, 0);
        raid.World.AddPlayer(offTank); raid.World.AddPlayer(outside); raid.World.RunTick(0);
        raid.Tank.Health = raid.Tank.MaxHealth / 4;
        offTank.Health = offTank.MaxHealth / 2;
        outside.Health = outside.MaxHealth;
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 1000);
        raid.Boss.Combat.Threat.AddThreat(outside, 90);
        raid.Boss.Combat.Threat.AddThreat(offTank, 80);
        raid.Boss.AI!.OnUpdate(1200);
        Assert.Equal(offTank, Assert.Single(raid.Caster.Casts, c => c.Spell == 28308).Target);
    }
}
