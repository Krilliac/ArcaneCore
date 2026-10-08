using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.MoltenCore;
using ArcaneCore.Game.Instances.Scripts.Onyxia;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class RaidBossTests
{
    private sealed class MinimumRandom : Random
    {
        public override long NextInt64(long minValue, long maxValue) => minValue;
    }
    private sealed class FailableCaster(FakeCaster inner) : ICreatureSpellCaster
    {
        public bool Fail { get; set; }
        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
            => Fail ? CreatureCastResult.AlreadyCasting : inner.Cast(caster, spellId, target, triggered);
        public bool IsCasting(Creature caster) => Fail;
        public bool HasAura(Unit unit, uint spellId) => inner.HasAura(unit, spellId);
        public void Interrupt(Creature caster) => inner.Interrupt(caster);
        public void OnCreatureRemoved(Creature creature) => inner.OnCreatureRemoved(creature);
        public event Action<Unit, Unit, SpellInfo>? SpellHit { add => inner.SpellHit += value; remove => inner.SpellHit -= value; }
    }
    private sealed class Arena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public ScriptedInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Spells { get; } = new();
        public FailableCaster Caster { get; }
        public Player Player { get; }
        public Arena(bool onyxia = false, Random? random = null, IEnumerable<CreatureSpawn>? spawns = null)
        {
            Map = World.GetMap(0);
            Raid = onyxia ? new OnyxiaInstance(Map) : new MoltenCoreInstance(Map);
            Map.AddUpdater(Raid);
            uint[] entries = [.. MoltenCoreInstance.BossEntries, 12099, 11662, 11672, 11663, 11664, 12143, 13148, 10184, 11262, 12758, 12129];
            var content = new CreatureContent(entries.Select(e => Template(e, b => { b.MinLevelHealth = b.MaxLevelHealth = 10000; })), spawns ?? [], [], [], []);
            Caster = new FailableCaster(Spells);
            Creatures = new CreatureMapSystem(Map, content, random: random ?? new Random(1), aiServices: new CreatureAiServices { Spells = Caster });
            Map.AddUpdater(Creatures);
            (Player, _) = CreatureAiTestSupport.AddPlayer(World, 1, 0, 0);
            Player.MaxHealth = Player.Health = 100000;
        }
        public Creature Spawn(uint entry) => Creatures.SpawnTemporary(Creatures.Content.FindTemplate(entry)!, 5, 0, 83.5f, 0);
        public Creature Engage(uint entry)
        {
            Creature boss = Spawn(entry);
            Assert.True(boss.AI!.AttackStart(Player));
            Spells.Casts.Clear();
            return boss;
        }
        public void Dispose() => World.Dispose();
    }

    [Theory]
    // mangos-classic molten_core.h TYPE_* (ClassicDB order), not vmangos' different encounter order.
    [InlineData(12118u, 0u)]
    [InlineData(11982u, 1u)]
    [InlineData(12259u, 2u)]
    [InlineData(12057u, 3u)]
    [InlineData(12264u, 4u)]
    [InlineData(12056u, 5u)]
    [InlineData(11988u, 6u)]
    [InlineData(12098u, 7u)]
    public void BossLifecycle_UsesClassicDbEncounterIndex(uint entry, uint type)
    {
        using var a = new Arena();
        Creature boss = a.Engage(entry);
        Assert.IsType<MoltenCoreBossAI>(boss.AI);
        Assert.Equal(EncounterState.InProgress, a.Raid.GetData(type));
        boss.AI!.OnEvade();
        Assert.Equal(EncounterState.Fail, a.Raid.GetData(type));
        a.Map.Combat.Kill(a.Player, boss);
        Assert.Equal(EncounterState.Done, a.Raid.GetData(type));
    }
    [Theory]
    [InlineData(12118u, 19460u, 6000u)]
    [InlineData(11982u, 19408u, 10000u)]
    [InlineData(12259u, 19716u, 10000u)]
    [InlineData(12057u, 19492u, 15000u)]
    [InlineData(12056u, 19659u, 30000u)]
    [InlineData(12264u, 19712u, 6000u)]
    [InlineData(12098u, 19781u, 2000u)]
    [InlineData(11988u, 20228u, 7000u)]
    [InlineData(11662u, 19777u, 10000u)]
    [InlineData(11672u, 19820u, 7000u)]
    public void EachCombatAi_CastsItsReferenceAbility(uint entry, uint spell, uint time)
    {
        using var a = new Arena();
        Creature boss = a.Engage(entry);
        boss.AI!.OnUpdate(time);
        Assert.Contains(a.Spells.Casts, c => c.Spell == spell);
    }
    [Fact]
    public void Lucifron_DoomDeadlineAndReset_AreWorldTime()
    {
        using var a = new Arena(); Creature boss = a.Engage(12118);
        boss.AI!.OnUpdate(9999); Assert.DoesNotContain(a.Spells.Casts, c => c.Spell == 19702);
        boss.AI.OnUpdate(1); Assert.Single(a.Spells.Casts, c => c.Spell == 19702);
        boss.AI.OnUpdate(19999); Assert.Single(a.Spells.Casts, c => c.Spell == 19702);
        boss.AI.OnUpdate(1); Assert.Equal(2, a.Spells.Casts.Count(c => c.Spell == 19702));
        boss.AI.OnEvade(); a.Spells.Casts.Clear(); boss.AI.OnUpdate(9999);
        Assert.DoesNotContain(a.Spells.Casts, c => c.Spell == 19702);
    }
    [Fact]
    public void FailedCast_IsRetriedWithoutSpendingItsCooldown()
    {
        using var a = new Arena(); Creature boss = a.Engage(12118); a.Caster.Fail = true;
        boss.AI!.OnUpdate(10000); Assert.Empty(a.Spells.Casts);
        a.Caster.Fail = false; boss.AI.OnUpdate(1); Assert.Single(a.Spells.Casts, c => c.Spell == 19702);
        boss.AI.OnUpdate(19999); Assert.Single(a.Spells.Casts, c => c.Spell == 19702);
        boss.AI.OnUpdate(1); Assert.Equal(2, a.Spells.Casts.Count(c => c.Spell == 19702));
    }
    [Fact]
    public void SulfuronPriest_HealsTheLargestAbsoluteDeficit_NotTheLowestPercentage()
    {
        using var a = new Arena();
        Creature priest = a.Engage(11662);
        Creature large = a.Spawn(12098); large.Health = 7000; // 3000 missing, 70% health
        Creature small = a.Spawn(11662); small.MaxHealth = 100; small.Health = 10; // 90 missing, 10% health
        priest.AI!.OnUpdate(30000);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 19775 && ReferenceEquals(c.Target, large));
        Assert.DoesNotContain(a.Spells.Casts, c => c.Spell == 19775 && ReferenceEquals(c.Target, small));
    }
    [Fact]
    public void Geddon_ArmageddonOnlyAtTwoPercent_AndStopsMelee()
    {
        using var a = new Arena(); Creature boss = a.Engage(12056);
        boss.Health = 201; boss.AI!.OnUpdate(1); Assert.DoesNotContain(a.Spells.Casts, c => c.Spell == 20478);
        boss.Health = 200; boss.AI.OnUpdate(1); boss.AI.OnUpdate(1);
        Assert.Single(a.Spells.Casts, c => c.Spell == 20478); Assert.False(boss.AI.MeleeEnabled); Assert.False(boss.Combat.IsMeleeAttacking);
        boss.AI.OnEvade(); Assert.True(boss.AI.MeleeEnabled);
    }
    [Fact]
    public void Golemagg_QuakesAtTenPercent_AndKillsCoreRagersOnDeath()
    {
        using var a = new Arena(); Creature boss = a.Engage(11988); Creature add = a.Spawn(11672);
        boss.Health = 1001; boss.AI!.OnUpdate(1); Assert.DoesNotContain(a.Spells.Casts, c => c.Spell == 19798);
        boss.Health = 1000; boss.AI.OnUpdate(1); Assert.Single(a.Spells.Casts, c => c.Spell == 19798);
        boss.AI.OnUpdate(2999); Assert.Single(a.Spells.Casts, c => c.Spell == 19798);
        boss.AI.OnUpdate(1); Assert.Equal(2, a.Spells.Casts.Count(c => c.Spell == 19798));
        a.Map.Combat.Kill(a.Player, boss); Assert.Contains(a.Spells.Casts, c => c.Spell == 3617 && ReferenceEquals(c.Target, add));
    }
    [Fact]
    public void Garr_FireswornDeathErupts_AndEnrageHitStacks()
    {
        using var a = new Arena(); Creature garr = a.Engage(12057); Creature add = a.Spawn(12099);
        a.Map.Combat.Kill(a.Player, add);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 19497 && c.Triggered);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 19515 && c.Triggered);
        garr.AI!.OnSpellHit(add, new SpellInfo { Id = 19515 });
        Assert.Contains(a.Spells.Casts, c => c.Spell == 19516);
    }
    [Fact]
    public void Shazzrah_SuccessfulGateResetsThreatAndExplodes()
    {
        using var a = new Arena(); Creature boss = a.Engage(12264);
        boss.Combat.Threat.AddThreat(a.Player, 100);
        boss.AI!.OnReceiveAiEvent(1000, boss, boss, 0);
        Assert.Equal(0, boss.Combat.Threat.GetThreat(a.Player));
        Assert.Contains(a.Spells.Casts, c => c.Spell == 19712);
    }
    [Fact]
    public void Runes_RequireBossDeaths_SaveDouses_AndSummonDomoOnce()
    {
        using var a = new Arena(); var raid = (MoltenCoreInstance)a.Raid;
        Assert.False(raid.DouseRune(176956));
        for (uint i = 1; i <= 7; i++) { raid.SetData(i, EncounterState.Done); Assert.True(raid.DouseRune(MoltenCoreInstance.RuneEntries[(int)i - 1])); }
        Assert.True(raid.RunesDoused); raid.Update(1); raid.Update(1);
        Assert.Single(a.Creatures.Creatures, c => c.Template.Entry == 12018);
        Assert.Equal(8, a.Creatures.Creatures.Count(c => c.Template.Entry is 11663 or 11664));
        var restored = new MoltenCoreInstance(a.Map); restored.Load(raid.GetSaveData()!); Assert.True(restored.RunesDoused);
    }
    [Fact]
    public void Majordomo_IsNotRecreatedAfterHisCorpseGoesWhileRagnarosRemains()
    {
        using var a = new Arena();
        for (uint i = 1; i <= 7; i++) a.Raid.SetData(i, EncounterState.Special);
        a.Raid.SetData(8, EncounterState.Done);
        a.Spawn(11502);
        a.Raid.Update(1);
        Assert.DoesNotContain(a.Creatures.Creatures, c => c.Entry == 12018);
    }
    [Fact]
    public void Majordomo_CannotDieFromDamage_AndSurrendersOnlyAfterEightDistinctAdds()
    {
        using var a = new Arena(); Creature domo = a.Engage(12018); var ai = Assert.IsType<MajordomoAI>(domo.AI);
        Assert.Equal(1u, domo.InvincibilityHpThreshold);
        a.Map.Combat.DealDamage(a.Player, domo, 20000); Assert.Equal(1u, domo.Health);
        var adds = new List<Creature>();
        for (int i = 0; i < 8; i++) adds.Add(a.Creatures.SummonCorpseDespawn(domo, 11664, 5, 0, 83.5f, 0)!);
        for (int i = 0; i < 7; i++) a.Map.Combat.Kill(a.Player, adds[i]);
        ai.OnSummonedCreatureJustDied(adds[0]); Assert.False(ai.Defeated);
        a.Map.Combat.Kill(a.Player, adds[7]); Assert.True(ai.Defeated);
        Assert.NotEqual(EncounterState.Done, a.Raid.GetData(8));
        ai.OnMovementInform(MovementGeneratorType.Point, 100);
        Assert.Equal(EncounterState.Done, a.Raid.GetData(8)); Assert.Equal(1080u, domo.FactionTemplate);
    }
    [Fact]
    public void Ragnaros_SubmergesAtThreeMinutes_AndEmergesAfterNinetySeconds()
    {
        using var a = new Arena(); Creature boss = a.Engage(11502); var ai = Assert.IsType<RagnarosAI>(boss.AI);
        ai.OnUpdate(179999); Assert.Equal(RagnarosAI.RagnarosPhase.Emerged, ai.Phase);
        ai.OnUpdate(1); Assert.Equal(RagnarosAI.RagnarosPhase.Submerging, ai.Phase); Assert.False(ai.MeleeEnabled); Assert.False(boss.Combat.IsMeleeAttacking);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 21108);
        ai.OnUpdate(3000); Assert.Equal(RagnarosAI.RagnarosPhase.Submerged, ai.Phase);
        ai.OnUpdate(89999); Assert.Equal(RagnarosAI.RagnarosPhase.Submerged, ai.Phase);
        ai.OnUpdate(1); Assert.Equal(RagnarosAI.RagnarosPhase.Emerging, ai.Phase);
        ai.OnUpdate(500); Assert.Equal(RagnarosAI.RagnarosPhase.Emerged, ai.Phase); Assert.True(ai.MeleeEnabled);
    }
    [Fact]
    public void Ragnaros_LastSonDeathAcceleratesEmerge_AndWipeRemovesSons()
    {
        using var a = new Arena(); Creature boss = a.Engage(11502); var ai = Assert.IsType<RagnarosAI>(boss.AI);
        ai.OnUpdate(180000); ai.OnUpdate(3000);
        Creature son = a.Creatures.SummonCorpseDespawn(boss, 12143, 5, 0, 83.5f, 0)!;
        Assert.Equal(1, ai.LivingSons); a.Map.Combat.Kill(a.Player, son); ai.OnUpdate(1000);
        Assert.Equal(RagnarosAI.RagnarosPhase.Emerging, ai.Phase);
        ai.OnEvade(); Assert.DoesNotContain(son, a.Creatures.Creatures); Assert.Equal(RagnarosAI.RagnarosPhase.Emerged, ai.Phase);
    }
    [Fact]
    public void Onyxia_PhasesWaitForMovementCompletion_ThenFlightAndFinalFear()
    {
        using var a = new Arena(true); Creature boss = a.Engage(10184); var ai = Assert.IsType<OnyxiaAI>(boss.AI);
        boss.Health = 6501; ai.OnUpdate(1); Assert.Equal(OnyxiaAI.OnyxiaPhase.Ground, ai.Phase);
        boss.Health = 6500; ai.OnUpdate(1); Assert.Equal(OnyxiaAI.OnyxiaPhase.ToLiftoff, ai.Phase);
        ai.OnUpdate(60000); Assert.Equal(OnyxiaAI.OnyxiaPhase.ToLiftoff, ai.Phase); Assert.False(ai.MeleeEnabled); Assert.False(boss.Combat.IsMeleeAttacking);
        ai.OnMovementInform(MovementGeneratorType.Point, 9); ai.OnUpdate(3500);
        Assert.Equal(OnyxiaAI.OnyxiaPhase.FlyingNorth, ai.Phase);
        ai.OnMovementInform(MovementGeneratorType.Point, 10); ai.OnUpdate(1);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 18392);
        boss.Health = 4000; ai.OnUpdate(1); Assert.Equal(OnyxiaAI.OnyxiaPhase.Landing, ai.Phase);
        ai.OnMovementInform(MovementGeneratorType.Point, 11); ai.OnUpdate(2000); ai.OnUpdate(1);
        Assert.Equal(OnyxiaAI.OnyxiaPhase.Final, ai.Phase); Assert.True(ai.MeleeEnabled);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 18431);
        ai.OnEvade(); Assert.Equal(OnyxiaAI.OnyxiaPhase.Ground, ai.Phase);
        Assert.Equal(0u, (uint)(boss.Movement.Flags & MovementFlags.Flying));
    }
    [Fact]
    public void Onyxia_FirstWhelpWaveHasTwenty_AndEvadeDespawnsIt()
    {
        using var a = new Arena(true); Creature boss = a.Engage(10184); var ai = Assert.IsType<OnyxiaAI>(boss.AI);
        boss.Health = 6000; ai.OnUpdate(1); ai.OnMovementInform(MovementGeneratorType.Point, 9); ai.OnUpdate(3500);
        // Keep her en route so movement choices do not affect the independent whelp timer.
        for (int i = 0; i < 10; i++) ai.OnUpdate(3000);
        Assert.Equal(20, a.Creatures.Creatures.Count(c => c.Template.Entry == 11262));
        ai.OnEvade(); Assert.DoesNotContain(a.Creatures.Creatures, c => c.Template.Entry == 11262);
        Assert.Equal(EncounterState.Fail, a.Raid.GetData(0));
    }
    [Fact]
    public void Onyxia_DeepBreathCrossesToOppositePointOnlyAfterSpellHit()
    {
        using var a = new Arena(true, new MinimumRandom()); Creature boss = a.Engage(10184); var ai = (OnyxiaAI)boss.AI!;
        boss.Health = 6000; ai.OnUpdate(1); ai.OnMovementInform(MovementGeneratorType.Point, 9); ai.OnUpdate(3500);
        ai.OnMovementInform(MovementGeneratorType.Point, 10); a.Spells.Casts.Clear();
        ai.OnUpdate(25000); Assert.Contains(a.Spells.Casts, c => c.Spell == 17086); Assert.Equal(4, ai.FlightPoint);
        Assert.DoesNotContain(a.Spells.Casts, c => c.Spell == 22191);
        ai.OnSpellHit(boss, new SpellInfo { Id = 17086 }); Assert.Contains(a.Spells.Casts, c => c.Spell == 22191);
        ai.OnMovementInform(MovementGeneratorType.Point, 4);
        ai.OnUpdate(25000); Assert.Contains(a.Spells.Casts, c => c.Spell == 18351); Assert.Equal(0, ai.FlightPoint);
    }
    [Fact]
    public void Onyxia_WhelpSpawnerWaitsForTrigger_ThenSummonsExactlyOnce()
    {
        using var a = new Arena(true);
        var template = GameObjects.GameObjectTestKit.GoTemplate(176510, GameObjectType.Generic);
        var objects = new GameObjectMapSystem(a.Map, new GameObjectContent([template], [], [], [], [])); a.Map.AddUpdater(objects);
        objects.Summon(176510, 1, 0, 83.5f, 0);
        a.Raid.Update(1); Assert.DoesNotContain(a.Creatures.Creatures, c => c.Entry == 11262);
        a.Spawn(12758); a.Raid.Update(1); a.Raid.Update(1);
        Assert.Single(a.Creatures.Creatures, c => c.Entry == 11262);
    }
    [Fact]
    public void Onyxia_DeadWardersRespawnAtPull()
    {
        using var a = new Arena(true, spawns: [CreatureTestSupport.Spawn(100, 12129, 5, 0)]); Creature warder = Assert.Single(a.Creatures.Creatures, c => c.Entry == 12129);
        a.Map.Combat.Kill(a.Player, warder); Assert.False(warder.IsAlive);
        a.Raid.SetData(0, EncounterState.InProgress); Assert.True(warder.IsAlive);
    }
    [Fact]
    public void Majordomo_GossipHasThreeSteps_AndIntroductionCannotSummonTwice()
    {
        using var a = new Arena(); a.Raid.SetData(8, EncounterState.Done);
        Creature domo = a.Spawn(12018); var ai = (MajordomoAI)domo.AI!;
        INpcGossipScript gossip = ai;
        var first = gossip.Hello(a.Player, null!); Assert.Equal(4995u, first!.NpcTextId);
        Assert.Equal(5011u, gossip.SelectReply(a.Player, null!, 1, 1).NextMenu!.NpcTextId);
        Assert.Equal(5012u, gossip.SelectReply(a.Player, null!, 1, 2).NextMenu!.NpcTextId);
        Assert.True(gossip.SelectReply(a.Player, null!, 1, 3).Close); Assert.True(ai.Summoning);
        Assert.False(ai.StartSummonEvent(a.Player));
        foreach (uint delay in new uint[] { 5000, 1000, 11500, 8000 }) ai.OnUpdate(delay);
        Creature rag = Assert.Single(a.Creatures.Creatures, c => c.Entry == 11502);
        Assert.False(rag.AI!.AggroesOnSight);
        Assert.False(rag.AI.AttackStart(a.Player));
        Assert.NotEqual(0u, (uint)(rag.UnitFlags & UnitFlags.NonAttackable2));
        foreach (uint delay in new uint[] { 8700, 11700, 8700, 16500 }) ai.OnUpdate(delay);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 19773 && ReferenceEquals(c.Target, domo));
        Assert.False(ai.StartSummonEvent(a.Player)); Assert.Single(a.Creatures.Creatures, c => c.Entry == 11502);
        var ragAi = (RagnarosAI)rag.AI!;
        ragAi.OnSpellHitTarget(domo, new SpellInfo { Id = 19773 }); ragAi.OnUpdate(10000); ragAi.OnUpdate(3000);
        Assert.Equal(0u, (uint)(rag.UnitFlags & UnitFlags.NonAttackable2));
    }
}
