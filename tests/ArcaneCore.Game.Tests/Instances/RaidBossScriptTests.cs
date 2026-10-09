using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackwingLair;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class RaidBossScriptTests
{
    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid) => reference.Map?.FindObject(guid) as Unit;
    }

    private sealed class ObjectCaster(SpellSystem spells) : IGameObjectSpells
    {
        public List<SpellCastResult> Results { get; } = [];
        public bool Cast(GameObject source, uint spellId, Unit target, Unit? unitCaster)
        {
            SpellCastResult result = spells.CastForGameObject(source, spellId, target, unitCaster);
            Results.Add(result);
            return result == SpellCastResult.CastOk;
        }
        public float? MaxRange(uint spellId) => spells.Store.Get(spellId)?.Range.Max;
        public bool IsChanneling(Unit unit) => false;
        public void StartRitualAnimation(Player helper, uint animSpellId, GameObject ritual) { }
        public bool CastRitualSpell(GameObject ritual, uint spellId, Unit caster, ObjectGuid summonTarget) => false;
        public void StartCreatingSpellCooldown(Player owner, uint spellId) { }
    }

    private sealed class Raid : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeCaster Caster { get; } = new();
        public Map Map { get; }
        public InstanceData Data { get; }
        public CreatureMapSystem Creatures { get; }
        public Creature Boss { get; }
        public Player Tank { get; }

        public Raid(uint map, uint entry, bool engage = true, params uint[] extraEntries)
        {
            Map = World.GetMap(map);
            Data = Assert.IsAssignableFrom<InstanceData>(InstanceScriptRegistry.Default.Create(Map));
            Data.Initialize();
            Map.AddUpdater(Data);
            Creatures = new CreatureMapSystem(Map, Content([Template(entry), .. extraEntries.Select(e => Template(e))], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Caster, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), map);
            float z = map == 309 ? 50 : 450;
            Tank.Relocate(0, 0, z, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(Template(entry), 1, 0, z, 0);
            if (engage) Boss.AI!.AttackStart(Tank);
        }

        public void Dispose() => World.Dispose();
    }

    [Theory]
    [InlineData(469u, 12017u, 2u, "BroodlordAI")]
    [InlineData(469u, 11983u, 3u, "FiremawAI")]
    [InlineData(469u, 11981u, 5u, "FlamegorAI")]
    [InlineData(469u, 13020u, 1u, "VaelastraszAI")]
    [InlineData(469u, 14601u, 4u, "EbonrocAI")]
    [InlineData(469u, 14020u, 6u, "ChromaggusAI")]
    [InlineData(509u, 15348u, 0u, "KurinnaxxAI")]
    public void Spawn_SelectsRaidAi_AndDeathCompletesEncounter(uint map, uint entry, uint encounter, string ai)
    {
        using var raid = new Raid(map, entry);
        Assert.Equal(ai, raid.Boss.AI!.GetType().Name);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(encounter));
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Assert.Equal(EncounterState.Done, raid.Data.GetData(encounter));
        Assert.Contains("3", raid.Data.GetSaveData()!);
    }

    [Theory]
    [InlineData(12435u, 0u)]
    [InlineData(13020u, 1u)]
    [InlineData(12017u, 2u)]
    [InlineData(11983u, 3u)]
    [InlineData(14601u, 4u)]
    [InlineData(11981u, 5u)]
    [InlineData(14020u, 6u)]
    [InlineData(10162u, 7u)]
    public void BlackwingBossWipe_FailsItsOwnEncounter(uint entry, uint slot)
    {
        using var raid = new Raid(469, entry);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(slot));
        raid.Boss.AI!.OnReachedHome();
        Assert.Equal(EncounterState.Fail, raid.Data.GetData(slot));
    }

    [Fact]
    public void Razorgore_EggsAreUnique_PhaseTwoEnablesAbilities_AndWipeResets()
    {
        using var raid = new Raid(469, 12435);
        var bwl = Assert.IsType<BlackwingLairInstance>(raid.Data);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(177807, GameObjectType.Goober)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        GameObject first = objects.Summon(177807, 1, 0, 450, 0)!;
        GameObject last = objects.Summon(177807, 2, 0, 450, 0)!;
        Assert.Equal(2, bwl.EggCount);
        Assert.True(bwl.DestroyEgg(first));
        Assert.False(bwl.DestroyEgg(first));
        Assert.Equal(EncounterState.InProgress, bwl.GetData(0));
        raid.Boss.AI!.OnUpdate(40000);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 19632);
        Assert.True(bwl.DestroyEgg(last));
        Assert.Equal(EncounterState.Special, bwl.GetData(0));
        raid.Boss.AI.OnUpdate(8000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 19632);
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Assert.Equal(EncounterState.Done, bwl.GetData(0));

        using var wiped = new Raid(469, 12435);
        var failed = Assert.IsType<BlackwingLairInstance>(wiped.Data);
        var wipeObjects = new GameObjectMapSystem(wiped.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(177807, GameObjectType.Goober)], [], [], [], []));
        wiped.Map.AddUpdater(wipeObjects);
        GameObject usedEgg = wipeObjects.Summon(177807, 1, 0, 450, 0)!;
        wipeObjects.Summon(177807, 2, 0, 450, 0);
        Assert.True(failed.DestroyEgg(usedEgg));
        wiped.Map.Combat.DealDamage(wiped.Tank, wiped.Boss, wiped.Boss.Health + 1000, direct: false);
        Assert.True(wiped.Boss.IsAlive);
        Assert.Equal(1u, wiped.Boss.Health);
        wiped.Boss.AI!.OnUpdate(1);
        Assert.Equal(EncounterState.Fail, failed.GetData(0));
        Assert.Equal(0, failed.BrokenEggCount);
        wipeObjects.Update(wiped.Map, 1);
        failed.Update(30000);
        Assert.Equal(2, failed.EggCount);
        Assert.Equal(2, wipeObjects.GameObjects.Count(go => go.Entry == 177807));
    }

    [Fact]
    public void DestroyEggSpell_UsesItsExplicitGameObjectTarget()
    {
        using var raid = new Raid(469, 12435);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(177807, GameObjectType.Goober)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        GameObject egg = objects.Summon(177807, 1, 0, 450, 0)!;
        var spell = SpellTestKit.Spell(19873,
            SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.GameObject));
        var spells = new SpellSystem(new SpellStore([spell], [], []), () => 0, units: new MapUnits());
        SpellScriptDispatcher.Install(spells, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = egg.Guid };
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(raid.Boss, 19873, targets, triggered: true));
        Assert.Equal(EncounterState.Special, raid.Data.GetData(0));
    }

    [Fact]
    public void RazorgoreOrb_UnlocksAfterGrethokDies_AndCastsPossessOnTheBoss()
    {
        using var raid = new Raid(469, 12435);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(177808, GameObjectType.Goober)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        GameObject orb = objects.Summon(177808, 1, 0, 450, 0)!;
        Creature controller = raid.Creatures.SpawnTemporary(Template(12557), 2, 0, 450, 0);
        Assert.True((orb.Flags & GameObjectFlags.NoInteract) != 0);
        Assert.True(raid.Data.OnGameObjectUse(raid.Tank, orb));
        var casts = new List<(uint Spell, ObjectGuid Target)>();
        raid.Data.CastPlayerTargetSpell = (_, spell, target) => casts.Add((spell, target));
        raid.Map.Combat.Kill(raid.Tank, controller);
        Assert.True((orb.Flags & GameObjectFlags.NoInteract) == 0);
        Assert.True(raid.Data.OnGameObjectUse(raid.Tank, orb));
        Assert.Equal((19832u, raid.Boss.Guid), Assert.Single(casts));
    }

    [Fact]
    public void Vaelastrasz_AreaIntroAndSpeechStartTheFight_AndAdrenalineResets()
    {
        using var raid = new Raid(469, 13020, engage: false);
        var vael = Assert.IsType<VaelastraszAI>(raid.Boss.AI);
        raid.Data.OnAreaTrigger(raid.Tank, 3626);
        Assert.Equal(EncounterState.Special, raid.Data.GetData(1));
        Assert.False(vael.BeginSpeech());
        vael.OnUpdate(1000);
        vael.OnUpdate(1000);
        vael.OnUpdate(14000);
        vael.OnUpdate(2000);
        Assert.True(vael.BeginSpeech());
        vael.OnUpdate(10000);
        vael.OnUpdate(16000);
        Assert.Equal(EncounterState.Special, raid.Data.GetData(1));
        vael.OnUpdate(10000);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(1));
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 23513);
        vael.OnUpdate(15000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 23620);
        vael.OnReachedHome();
        Assert.Equal(EncounterState.Fail, raid.Data.GetData(1));
    }

    [Fact]
    public void Vaelastrasz_IntroNefariusCorruptsHimAndLeaves_AndTheLastSpeechLineMakesHimHostile()
    {
        using var raid = new Raid(469, 13020, engage: false, 10162);
        var vael = Assert.IsType<VaelastraszAI>(raid.Boss.AI);
        var bwl = Assert.IsType<BlackwingLairInstance>(raid.Data);
        Assert.True(vael.BeginIntro());
        vael.OnUpdate(1000);
        Creature nefarius = Assert.Single(raid.Creatures.Creatures, c => c.Entry == 10162);
        Assert.False(bwl.IsEncounterVictor(nefarius.Guid));
        vael.OnUpdate(1000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 23642 && ReferenceEquals(c.Target, raid.Boss));
        vael.OnUpdate(14000);
        vael.OnUpdate(2000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 19484 && ReferenceEquals(c.Target, raid.Boss));
        raid.Creatures.Update(raid.Map, 25000);
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 10162);

        Assert.True(vael.BeginSpeech());
        vael.OnUpdate(10000);
        vael.OnUpdate(16000);
        Assert.NotEqual(VaelastraszAI.FactionHostile, raid.Boss.FactionTemplate);
        vael.OnUpdate(10000);
        Assert.Equal(VaelastraszAI.FactionHostile, raid.Boss.FactionTemplate);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(1));
    }

    [Fact]
    public void Ebonroc_ShadowOnVictim_FollowsReferenceTimerAndResets()
    {
        using var raid = new Raid(469, 14601);
        raid.Boss.AI!.OnUpdate(44999);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 23340);
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 23340 && ReferenceEquals(c.Target, raid.Tank));
        raid.Boss.AI.OnEvade();
        raid.Caster.Casts.Clear();
        raid.Boss.AI.OnUpdate(44999);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 23340);
    }

    [Fact]
    public void Chromaggus_BreathsAreDistinctAndPersist_EnragesThenResets()
    {
        using var raid = new Raid(469, 14020);
        var boss = Assert.IsType<ChromaggusAI>(raid.Boss.AI);
        Assert.NotEqual(boss.LeftBreath, boss.RightBreath);
        Assert.Equal(boss.LeftBreath, raid.Data.GetData(9));
        Assert.Equal(boss.RightBreath, raid.Data.GetData(10));
        boss.OnUpdate(30000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == boss.LeftBreath);
        boss.OnUpdate(30000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == boss.RightBreath);
        raid.Boss.Health = raid.Boss.MaxHealth / 10;
        boss.OnUpdate(1);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 23537);
        boss.OnReachedHome();
        Assert.Equal(EncounterState.Fail, raid.Data.GetData(6));
        string save = raid.Data.GetSaveData()!;
        raid.Data.Initialize(); raid.Data.Load(save);
        Assert.Equal(boss.LeftBreath, raid.Data.GetData(9));
        Assert.Equal(boss.RightBreath, raid.Data.GetData(10));
    }

    [Fact]
    public void Chromaggus_OnePersistedBreathIsNeverOverwrittenByFallbackSelection()
    {
        using var raid = new Raid(469, 14020);
        raid.Data.SetData(9, 23308);
        raid.Data.SetData(10, 0);
        var replacement = new ChromaggusAI(raid.Boss);
        Assert.Equal(23308u, replacement.LeftBreath);
        Assert.NotEqual(23309u, replacement.RightBreath);
        Assert.Equal(23308u, raid.Data.GetData(9));
    }

    [Fact]
    public void HourglassSand_RemovesBronzeAffliction()
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(23170, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
                with { Duration = new SpellDuration(30000, 0, 30000) },
            SpellTestKit.Spell(23645, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)));
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 23170, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, 23170));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 23645, SpellCastTargets.ForSelf(), triggered: true));
        Assert.False(kit.System.HasAura(player, 23170));
    }

    [Fact]
    public void BronzeAffliction_TimeStopPassesOnlyOneTickInFour_AndADirectTimeStopIsUntouched()
    {
        SpellInfo timeStop = SpellTestKit.Spell(23171, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0));
        SpellInfo bronze = SpellTestKit.Spell(23170, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy));
        using var kit = new SpellTestKit(timeStop, bronze);
        (Player player, _) = kit.AddPlayer(1);
        var script = new BronzeAfflictionScript();
        var tick = new SpellCastCheckContext(kit.System, player, timeStop, SpellCastTargets.ForSelf(), player, Triggered: true, Strict: false,
            TriggeringSpell: bronze);
        int passed = Enumerable.Range(0, 400).Count(_ => script.OnCheckCast(tick) == SpellCastResult.CastOk);
        Assert.InRange(passed, 60, 140); // vmangos SpellAuras.cpp case 23170: urand(0, 3) < 1
        var direct = tick with { TriggeringSpell = null };
        Assert.All(Enumerable.Range(0, 50), _ => Assert.Equal(SpellCastResult.CastOk, script.OnCheckCast(direct)));
    }

    [Fact]
    public void BlackwingGossip_UsesSuppliedScriptText_AndRequiresEveryVictorChoice()
    {
        using var raid = new Raid(469, 10162, engage: false);
        var gossip = new BlackwingLairGossip((_, id) => id is >= -3469002 and <= -3469000 ? $"choice {id}" : null);
        var npc = new NpcInfo(raid.Boss.Guid, raid.Boss.Entry, 0, NpcFlags.Gossip, 469,
            raid.Boss.X, raid.Boss.Y, raid.Boss.Z, 0.5f, true, false, false, false, 0);
        Assert.Single(gossip.Hello(raid.Tank, npc)!.Items);
        Assert.False(gossip.SelectReply(raid.Tank, npc, 1, 1003).Close);
        Assert.Single(gossip.SelectReply(raid.Tank, npc, 1, 1001).NextMenu!.Items);
        Assert.Single(gossip.SelectReply(raid.Tank, npc, 1, 1002).NextMenu!.Items);
        Assert.True(gossip.SelectReply(raid.Tank, npc, 1, 1003).Close);
        raid.Boss.AI!.OnUpdate(1000);
        raid.Boss.AI.OnUpdate(7000);
        Assert.NotEqual(VictorNefariusAI.FactionMonster, raid.Boss.FactionTemplate);
        raid.Boss.AI.OnUpdate(4000);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(7));
        Assert.Equal(VictorNefariusAI.FactionMonster, raid.Boss.FactionTemplate);
        raid.Boss.AI.OnReachedHome();
        Assert.Equal(EncounterState.Fail, raid.Data.GetData(7));
        Assert.Equal(raid.Boss.Template.Faction, raid.Boss.FactionTemplate);
    }

    [Fact]
    public void BlackwingGossip_FallsBackToClassicDbText_AndOnlyTheThroneVictorOffersTheEvent()
    {
        using var raid = new Raid(469, 10162, engage: false);
        var gossip = new BlackwingLairGossip();
        var throne = new NpcInfo(raid.Boss.Guid, raid.Boss.Entry, 0, NpcFlags.Gossip, 469,
            raid.Boss.X, raid.Boss.Y, raid.Boss.Z, 0.5f, true, false, false, false, 0);
        Assert.Equal("I've made no mistakes.", Assert.Single(gossip.Hello(raid.Tank, throne)!.Items).Text);

        // Vaelastrasz's intro summon (vmangos/MC aNefariusSpawnLoc, z 412) is not the encounter's Victor.
        Creature summoned = raid.Creatures.SpawnTemporary(Template(10162), -7466.16f, -1040.80f, 412.053f, 2.14675f);
        var intro = new NpcInfo(summoned.Guid, summoned.Entry, 0, NpcFlags.Gossip, 469,
            summoned.X, summoned.Y, summoned.Z, 0.5f, true, false, false, false, 0);
        Assert.Empty(gossip.Hello(raid.Tank, intro)!.Items);
        Assert.False(gossip.SelectReply(raid.Tank, intro, 1, 1001).Close);
        Assert.Null(gossip.SelectReply(raid.Tank, intro, 1, 1001).NextMenu);
    }

    [Fact]
    public void VictorNefarius_DrakonidDeathsTriggerDragonSpawnAfterFiveSeconds()
    {
        using var raid = new Raid(469, 10162, engage: false, 14265, 14302, 11583);
        var victor = Assert.IsType<VictorNefariusAI>(raid.Boss.AI);
        Assert.True(victor.BeginIntro());
        victor.OnUpdate(1000); victor.OnUpdate(7000); victor.OnUpdate(4000);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(7));
        for (int i = 0; i < 42; i++)
        {
            Creature drakonid = raid.Creatures.SpawnTemporary(Template(14265), 2, 0, 450, 0);
            raid.Map.Combat.Kill(raid.Tank, drakonid);
        }
        var bwl = Assert.IsType<BlackwingLairInstance>(raid.Data);
        Assert.Equal(42, bwl.DrakonidDeaths);
        Assert.Equal(EncounterState.Special, bwl.GetData(7));
        bwl.Update(4999);
        Assert.DoesNotContain(raid.Creatures.Creatures, c => c.Entry == 11583);
        bwl.Update(1);
        Creature dragon = Assert.Single(raid.Creatures.Creatures, c => c.Entry == 11583);
        raid.Creatures.Update(raid.Map, 2500);
        dragon.AI!.OnReachedHome();
        Assert.Equal(EncounterState.Fail, bwl.GetData(7));
        Assert.Contains(raid.Creatures.Creatures, c => c.Entry == 10162 && c.IsAlive);
    }

    [Fact]
    public void Nefarian_LandsThenCallsClass_RaisesBonesAndCompletesOnDeath()
    {
        using var raid = new Raid(469, 11583, engage: false, 14605);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(179804, GameObjectType.Generic)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        objects.Summon(179804, 3, 0, 450, 0);
        var nef = Assert.IsType<NefarianAI>(raid.Boss.AI);
        Assert.False(nef.AttackStart(raid.Tank));
        nef.OnMovementInform(MovementGeneratorType.Point, 1);
        nef.OnMovementInform(MovementGeneratorType.Point, 2);
        nef.OnUpdate(3999);
        Assert.Equal(EncounterState.NotStarted, raid.Data.GetData(7));
        nef.OnUpdate(1);
        Assert.Equal(EncounterState.InProgress, raid.Data.GetData(7));
        nef.OnUpdate(35000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell is >= 23397 and <= 23436);
        raid.Boss.Health = raid.Boss.MaxHealth / 10;
        nef.OnUpdate(1);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 23362);
        Assert.Contains(raid.Creatures.Creatures, c => c.Entry == 14605);
        raid.Map.Combat.Kill(raid.Tank, raid.Boss);
        Assert.Equal(EncounterState.Done, raid.Data.GetData(7));
    }

    [Fact]
    public void Broodlord_CleaveAtEightSeconds_AndKnockAwayHalvesOnlyTheHitTargetsThreat()
    {
        using var raid = new Raid(469, 12017);
        raid.Boss.AI!.OnUpdate(7999);
        Assert.Empty(raid.Caster.Casts);
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(15284u, Assert.Single(raid.Caster.Casts).Spell);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 100);
        float before = raid.Boss.Combat.Threat.GetThreat(raid.Tank);
        raid.Caster.RaiseHit(raid.Boss, raid.Tank, new SpellInfo { Id = 18670 });
        Assert.Equal(before / 2, raid.Boss.Combat.Threat.GetThreat(raid.Tank));
        raid.Boss.AI.OnReachedHome();
        Assert.Equal(EncounterState.Fail, raid.Data.GetData(2));
    }

    [Theory]
    [InlineData(11983u, 23341u, 5000u)]
    [InlineData(11981u, 23342u, 10000u)]
    public void Drakes_CastTheirDistinctMechanic_AndWingBuffetReducesThreat(uint entry, uint spell, uint delay)
    {
        using var raid = new Raid(469, entry);
        raid.Boss.AI!.OnUpdate(delay - 1);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == spell);
        raid.Boss.AI.OnUpdate(1);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == spell);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 100);
        float before = raid.Boss.Combat.Threat.GetThreat(raid.Tank);
        raid.Caster.RaiseHit(raid.Boss, raid.Tank, new SpellInfo { Id = 23339 });
        Assert.Equal(before / 2, raid.Boss.Combat.Threat.GetThreat(raid.Tank));
    }

    [Fact]
    public void Kurinnaxx_EnragesOnceAtThirtyPercent_AndRetriesAfterReset()
    {
        using var raid = new Raid(509, 15348);
        raid.Boss.Health = raid.Boss.MaxHealth * 30 / 100;
        raid.Boss.AI!.OnUpdate(1);
        raid.Boss.AI.OnUpdate(1);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 26527);
        raid.Boss.AI.OnEvade();
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(2, raid.Caster.Casts.Count(c => c.Spell == 26527));
    }

    [Fact]
    public void Broodlord_LeashesBelowThePlatform_AndReloadKeepsCompletedDoorOpen()
    {
        using var raid = new Raid(469, 12017);
        raid.Boss.Relocate(1, 0, 448.59f, 0, 0);
        raid.Boss.AI!.OnUpdate(8000);
        Assert.True(raid.Boss.IsEvading);
        Assert.Empty(raid.Caster.Casts);

        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(179365, GameObjectType.Door)], [], [], [], []));
        raid.Map.AddUpdater(objects);
        GameObject door = objects.Summon(179365, 1, 0, 450, 0)!;
        raid.Data.SetData(2, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, door.State);
        raid.Data.SetData(2, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, door.State); // duplicate notification must not toggle it closed
        raid.Data.SetData(3, EncounterState.InProgress);
        string saved = raid.Data.GetSaveData()!;
        raid.Data.Initialize();
        raid.Data.Load(saved);
        Assert.Equal(EncounterState.NotStarted, raid.Data.GetData(3));
        objects.Remove(door);
        Assert.Equal(GameObjectState.Active, objects.Summon(179365, 1, 0, 450, 0)!.State);
    }

    [Fact]
    public void SandTrap_ScriptSummonsAtThreatTarget_ActivatesAtFourSeconds_AndConsumesItsCharge()
    {
        using var raid = new Raid(509, 15348);
        var objectSpells = new FakeObjectSpells();
        // ClassicDB z2815: radius=0, spell=25656, charges=1. A proximity scan alone never triggers this object.
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(180647, GameObjectType.Trap, (3, 25656), (4, 1))], [], [], [], []))
        { Spells = objectSpells };
        raid.Map.AddUpdater(objects);
        var spells = new SpellSystem(new SpellStore(
            [SpellTestKit.Spell(26524, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0))], [], []), () => 10000);
        SpellScriptDispatcher.Install(spells, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(raid.Boss, 26524, SpellCastTargets.ForSelf(), triggered: true));
        GameObject trap = Assert.Single(objects.GameObjects);
        Assert.Equal(raid.Tank.X, trap.X);
        Assert.Equal(raid.Tank.Y, trap.Y);
        Assert.Equal(raid.Tank.Z, trap.Z);
        objects.Update(raid.Map, 3999);
        Assert.Empty(objectSpells.Casts);
        objects.Update(raid.Map, 1);
        Assert.Equal(25656u, Assert.Single(objectSpells.Casts).Spell);
        Assert.Empty(objects.GameObjects);
        objects.Update(raid.Map, 10000);
        Assert.Single(objectSpells.Casts);
    }

    [Fact]
    public void SandTrap_MissingTemplateDoesNotFabricateAnObject()
    {
        using var raid = new Raid(509, 15348);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent([], [], [], [], []));
        raid.Map.AddUpdater(objects);
        var spells = new SpellSystem(new SpellStore(
            [SpellTestKit.Spell(26524, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0))], [], []), () => 10000);
        SpellScriptDispatcher.Install(spells, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(raid.Boss, 26524, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Empty(objects.GameObjects);
    }

    [Fact]
    public void SandTrap_DestinationAreaDamagesThePlayerAtTheTrap_NotAtTheDistantBoss()
    {
        using var raid = new Raid(509, 15348);
        raid.Tank.Relocate(30, 0, 450, 0, 0);
        var relations = new FakeRelations();
        relations.Hostile.Add(raid.Boss.Guid);
        var spells = new SpellSystem(new SpellStore(
            [SpellTestKit.Spell(26524, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)),
             // Spell.sql 25656 uses TARGET_LOCATION_CASTER_DEST + enemies at destination.
             SpellTestKit.Spell(25656, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 7,
                 SpellImplicitTarget.LocationCasterDest, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc) with { Radius = 6 })],
            [], []), () => 10000, units: new MapUnits(), random: new Random(1)) { Relations = relations };
        SpellScriptDispatcher.Install(spells, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        var adapter = new ObjectCaster(spells);
        var objects = new GameObjectMapSystem(raid.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(180647, GameObjectType.Trap, (3, 25656), (4, 1))], [], [], [], []))
        { Spells = adapter };
        raid.Map.AddUpdater(objects);
        uint health = raid.Tank.Health;
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(raid.Boss, 26524, SpellCastTargets.ForSelf(), triggered: true));
        objects.Update(raid.Map, 4000);
        Assert.Equal(SpellCastResult.CastOk, Assert.Single(adapter.Results));
        Assert.Equal(health - 7, raid.Tank.Health);
    }

    [Fact]
    public void Hakkar_PowerDownRemovesExactlyOneStack_ThenRemovesTheLastHolder()
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(24692, SpellTestKit.Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.Dummy)) with
            { StackAmount = 5, Duration = new SpellDuration(-1, 0, -1) },
            SpellTestKit.Spell(24693, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)));
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 24692, SpellCastTargets.ForSelf(), triggered: true));
        }

        Assert.Equal(5, Assert.Single(kit.System.GetAuras(player)).StackAmount);
        for (int expected = 4; expected >= 0; expected--)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 24693, SpellCastTargets.ForSelf(), triggered: true));
            if (expected == 0)
            {
                Assert.Empty(kit.System.GetAuras(player));
            }
            else
            {
                SpellAuraHolder aura = Assert.Single(kit.System.GetAuras(player));
                Assert.Equal(expected, aura.StackAmount);
                Assert.Equal(expected * 10, aura.Auras[0]!.Amount);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShadowFlame_TriggersTheDamageOverTimeOnlyWithoutOnyxiaScaleCloak(bool cloak)
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(22539, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)),
            SpellTestKit.Spell(22682, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) },
            SpellTestKit.Spell(22683, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) });
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        if (cloak)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 22683, SpellCastTargets.ForSelf(), triggered: true));
        }

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 22539, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(!cloak, kit.System.HasAura(player, 22682));
    }

    [Fact]
    public void Hakkar_DeadPriestsDisableTheirAspects_AndBloodSiphonStartsAtNinetySeconds()
    {
        using var raid = new Raid(309, 14834);
        Assert.Equal("HakkarAI", raid.Boss.AI!.GetType().Name);
        Assert.Equal(5, raid.Caster.Casts.Count(c => c.Spell == 24692));
        for (uint priest = 0; priest < 5; priest++)
        {
            raid.Data.SetData(priest, EncounterState.Done);
        }

        raid.Caster.Casts.Clear();
        raid.Boss.AI.OnUpdate(89999);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell is >= 24686 and <= 24690);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 24324);
        raid.Boss.AI.OnUpdate(1);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 24324);
        Assert.Equal("3 3 3 3 3 0 0 0", raid.Data.GetSaveData());
    }

    [Fact]
    public void ZulGurub_APriestsDeathCompletesHisSlot_AndPowersHakkarDownOnce()
    {
        // mangos-classic zulgurub.cpp SetData(TYPE_JEKLIK, DONE) -> RemoveHakkarPowerStack: Hakkar casts 24693 on himself.
        using var raid = new Raid(309, 14834);
        raid.Caster.Casts.Clear();
        Creature jeklik = raid.Creatures.SpawnTemporary(Template(14517), 5, 0, 50, 0);
        raid.Map.Combat.Kill(raid.Tank, jeklik);
        Assert.Equal(EncounterState.Done, raid.Data.GetData(0));
        Assert.Equal(EncounterState.NotStarted, raid.Data.GetData(1));
        Assert.Single(raid.Caster.Casts, c => c.Spell == 24693);

        Creature again = raid.Creatures.SpawnTemporary(Template(14517), 5, 0, 50, 0);
        raid.Map.Combat.Kill(raid.Tank, again);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 24693); // a repeated DONE must not strip a second stack

        Creature other = raid.Creatures.SpawnTemporary(Template(11830), 5, 0, 50, 0); // any entry that is not a high priest
        raid.Map.Combat.Kill(raid.Tank, other);
        Assert.Equal("3 0 0 0 0 0 0 0", raid.Data.GetSaveData());
    }

    [Fact]
    public void Hakkar_InsanityRestoresCapturedThreatAfterTheAuraEnds()
    {
        using var raid = new Raid(309, 14834);
        raid.Boss.Combat.Threat.AddThreat(raid.Tank, 100);
        float before = raid.Boss.Combat.Threat.GetThreat(raid.Tank);
        raid.Boss.AI!.OnUpdate(17000);
        Assert.Contains(raid.Caster.Casts, c => c.Spell == 24327 && c.Target == raid.Tank);
        raid.Caster.Auras.Add((raid.Tank, 24327));
        raid.Boss.Combat.Threat.ModifyThreatPercent(raid.Tank, -100);
        raid.Boss.AI.OnUpdate(4000);
        Assert.Equal(0, raid.Boss.Combat.Threat.GetThreat(raid.Tank));
        raid.Caster.Auras.Remove((raid.Tank, 24327));
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(before, raid.Boss.Combat.Threat.GetThreat(raid.Tank));
    }

    [Theory]
    [InlineData(false, 24322u)]
    [InlineData(true, 24323u)]
    public void Hakkar_BloodSiphonSelectsPoisonDamageOrHealing(bool poisoned, uint expected)
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(24324, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)),
            SpellTestKit.Spell(24322, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) },
            SpellTestKit.Spell(24323, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) },
            SpellTestKit.Spell(24321, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(10000, 0, 10000) });
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        if (poisoned)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 24321, SpellCastTargets.ForSelf(), triggered: true));
        }

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 24324, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, expected));
        Assert.False(kit.System.HasAura(player, poisoned ? 24322u : 24323u));
    }
}
