using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Spell effect and targeting gaps against vmangos (lane Z12): the rank-upgrade supersede packet (Player.cpp:3640-3680), EffectSpawn
/// (SpellEffects.cpp:2990-2998), EffectTriggerSpell's caster-source rule (SpellEffects.cpp:1571,1576), EffectTeleportUnits with
/// TARGET_LOCATION_CASTER_DEST and the taxi guard (SpellEffects.cpp:1597-1636) and SPELL_ATTR_EX_REQUIRE_ALL_TARGETS (Spell.cpp:430-441).
/// The charge delay and path cap tests are in <see cref="ChargeEffectTests"/>.
/// </summary>
public sealed class SpellEffectGapTests
{
    private const uint TriggerCaster = 972001;
    private const uint CasterSourceOnly = 972002;
    private const uint CasterSourcePartial = 972003;
    private const uint TeleportToTarget = 972004;
    private const uint SpawnSpell = 972005;
    private const uint RequireAll = 972006;
    private const uint PlainTwoEffects = 972007;
    private const uint TriggerPartial = 972008;

    private static SpellEffectInfo Rage(int value) => Effect(SpellEffectName.Energize, value, SpellImplicitTarget.UnitCaster, misc: (int)PowerType.Rage);

    private static SpellInfo[] Spells() =>
    [
        // The triggered spell names the caster in all three effect slots, so every slot is caster-sourced (IsSpellWithCasterSourceTargetsOnly).
        Spell(CasterSourceOnly, Rage(10), Rage(10), Rage(10)),
        // The same with two slots empty: the reference refuses it (an empty slot has neither target).
        Spell(CasterSourcePartial, Rage(10)),
        Spell(TriggerCaster, Effect(SpellEffectName.TriggerSpell, 0, SpellImplicitTarget.Unit, trigger: CasterSourceOnly)),
        Spell(TriggerPartial, Effect(SpellEffectName.TriggerSpell, 0, SpellImplicitTarget.Unit, trigger: CasterSourcePartial)),
        Spell(TeleportToTarget, Effect(SpellEffectName.TeleportUnits, 0, SpellImplicitTarget.Unit, targetB: SpellImplicitTarget.LocationCasterDest)),
        Spell(SpawnSpell, Effect(SpellEffectName.Spawn, 0, SpellImplicitTarget.Unit)),
    ];

    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private static SpellTestKit NewKit(params SpellInfo[] extra)
    {
        var kit = new SpellTestKit([.. Spells(), .. extra]);
        kit.System.Units = new MapUnits();
        return kit;
    }

    // --- SMSG_SUPERCEDED_SPELL ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BuildSupercededSpell_WritesTheOldThenTheNewIdAsTwoU16()
        => Assert.Equal(new byte[] { 0x10, 0x27, 0x11, 0x27 }, SpellPackets.BuildSupercededSpell(10000, 10001));

    private static SpellRankChains TwoRanks(uint first, uint second)
        => new([
            new SkillLineAbilityRecord(1, 26, first, 0, 0, 0, second, 0, 0, 0),
            new SkillLineAbilityRecord(2, 26, second, 0, 0, 0, 0, 0, 0, 0)]);

    [Fact]
    public void LearningAHigherRank_SendsSupercededSpell_AndNoLearnedSpell()
    {
        using var kit = new SpellTestKit();
        kit.System.RankChains = TwoRanks(InstantHeal, CastBolt);
        (Player player, FakeSession session) = kit.AddPlayer(1);
        Assert.True(kit.System.LearnSpell(player, InstantHeal));
        Assert.Empty(Packets(session, WorldOpcode.SmsgSupercededSpell)); // a first rank replaces nothing
        session.Clear();

        Assert.True(kit.System.LearnSpell(player, CastBolt));

        Assert.Equal(new byte[] { (byte)InstantHeal, 0, (byte)CastBolt, 0 }, Packets(session, WorldOpcode.SmsgSupercededSpell).Single());
        // vmangos Player::AddSpell returns false for a superseding rank, so Player::LearnSpell sends no SMSG_LEARNED_SPELL (Player.cpp:3743,3774-3782).
        Assert.Empty(Packets(session, WorldOpcode.SmsgLearnedSpell));
    }

    [Fact]
    public void LearningATalentLinkedRank_WithNoRawForwardLink_SendsNoSupercededSpell_AndAnnouncesTheSpell()
    {
        // Pyroblast ranks 1 and 2: SkillLineAbility.dbc has forward_spellid 0 on both; only TalentAbilityRankLinks joins them in
        // RankChains. The reference (GetSpellBookSuccessorSpellId) sees no successor, so the client keeps both ranks.
        const uint pyroblast1 = 11366;
        const uint pyroblast2 = 12505;
        using var kit = new SpellTestKit();
        kit.System.RankChains = new SpellRankChains([
            new SkillLineAbilityRecord(1, 8, pyroblast1, 0, 0, 0, 0, 0, 0, 0),
            new SkillLineAbilityRecord(2, 8, pyroblast2, 0, 0, 0, 0, 0, 0, 0)]);
        Assert.Equal(pyroblast1, kit.System.RankChains.Previous(pyroblast2)); // the chain relation does link them
        (Player player, FakeSession session) = kit.AddPlayer(1);
        Assert.True(kit.System.LearnSpell(player, pyroblast1));
        session.Clear();

        Assert.True(kit.System.LearnSpell(player, pyroblast2));

        Assert.Empty(Packets(session, WorldOpcode.SmsgSupercededSpell));
        Assert.Single(Packets(session, WorldOpcode.SmsgLearnedSpell));
    }

    [Fact]
    public void LearningAHigherRank_WithoutTheLowerRank_SendsNoSupercededSpell()
    {
        using var kit = new SpellTestKit();
        kit.System.RankChains = TwoRanks(InstantHeal, CastBolt);
        (Player player, FakeSession session) = kit.AddPlayer(1);

        Assert.True(kit.System.LearnSpell(player, CastBolt));

        Assert.Empty(Packets(session, WorldOpcode.SmsgSupercededSpell));
    }

    // --- EffectSpawn -------------------------------------------------------------------------------------------------------------

    private static Creature AddCreature(SpellTestKit kit, float x)
    {
        const uint entry = 972900;
        var template = new CreatureTemplate
        {
            Entry = entry, Name = "Spawner", MinLevel = 20, MaxLevel = 20,
            MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [1], Faction = 14,
        };
        var creature = new Creature(entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        Map map = kit.World.GetMap(0);
        creature.Relocate(x, 0, 83.5f, 0, 0);
        creature.MapId = 0;
        map.AddObject(creature);
        map.Combat.Track(creature);
        kit.World.RunTick(0);
        return creature;
    }

    [Fact]
    public void SpawnEffect_ClearsTheSpawningFlagOfACreatureTarget_AndIgnoresAPlayer()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Creature creature = AddCreature(kit, 3);
        creature.UnitFlags |= UnitFlags.Spawning | UnitFlags.NotSelectable;
        caster.UnitFlags |= UnitFlags.Spawning;

        kit.System.CastSpell(caster, SpawnSpell, SpellCastTargets.ForUnit(creature.Guid), triggered: true);
        Assert.Equal(0u, (uint)(creature.UnitFlags & UnitFlags.Spawning));
        Assert.NotEqual(0u, (uint)(creature.UnitFlags & UnitFlags.NotSelectable)); // only the spawning flag goes

        kit.System.CastSpell(caster, SpawnSpell, SpellCastTargets.ForUnit(caster.Guid), triggered: true);
        Assert.NotEqual(0u, (uint)(caster.UnitFlags & UnitFlags.Spawning)); // EffectSpawn returns for a non-creature
    }

    [Fact]
    public void SpawnEffect_ForcesAStealthGroupCreatureTargetBackToVisibilityOn()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Creature creature = AddCreature(kit, 3);
        var registry = new ArcaneCore.Game.Stealth.StealthRegistry();
        ArcaneCore.Game.Stealth.StealthServices.Install(
            kit.World.GetMap(0), new ArcaneCore.Game.Stealth.StealthServices(kit.System, registry, ArcaneCore.Game.Stealth.StealthOptions.Default));
        registry.SetVisibility(creature, ArcaneCore.Game.Stealth.StealthVisibility.Stealth);

        kit.System.CastSpell(caster, SpawnSpell, SpellCastTargets.ForUnit(creature.Guid), triggered: true);

        Assert.Equal(ArcaneCore.Game.Stealth.StealthVisibility.On, registry.VisibilityOf(creature));
    }

    // --- EffectTriggerSpell ------------------------------------------------------------------------------------------------------

    [Fact]
    public void CasterSourceTargetsOnly_NeedsAllThreeSlotsNamed_EachCasterSourced()
    {
        using SpellTestKit kit = NewKit();
        Assert.True(SpellCasterSourceTargets.IsCasterSourceTargetsOnly(kit.Store.Get(CasterSourceOnly)!));
        Assert.False(SpellCasterSourceTargets.IsCasterSourceTargetsOnly(kit.Store.Get(CasterSourcePartial)!));
        Assert.False(SpellCasterSourceTargets.IsCasterSourceTargetsOnly(kit.Store.Get(TriggerCaster)!));
        Assert.True(SpellCasterSourceTargets.IsCasterSourceTarget((SpellImplicitTarget)27));
        Assert.True(SpellCasterSourceTargets.IsCasterSourceTarget((SpellImplicitTarget)44));
        Assert.False(SpellCasterSourceTargets.IsCasterSourceTarget(SpellImplicitTarget.UnitEnemy));
    }

    [Fact]
    public void TriggerSpell_OfACasterSourcedSpell_IsCastByTheTarget()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, TriggerCaster, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(30u, SpellSystem.GetPower(target, PowerType.Rage));
        Assert.Equal(0u, SpellSystem.GetPower(caster, PowerType.Rage));
    }

    [Fact]
    public void TriggerSpell_OfAnOrdinarySpell_IsStillCastByTheCaster()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, TriggerPartial, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(10u, SpellSystem.GetPower(caster, PowerType.Rage));
        Assert.Equal(0u, SpellSystem.GetPower(target, PowerType.Rage));
    }

    [Fact]
    public void TriggerSpell_OfDisarm6608_RealShape_DisarmsTheTarget_NotTheCaster()
    {
        // 25852 (effect 64 trigger spell at the unit) triggers 6608: Spell.dbc gives it one named slot (effect 6 aura, TargetA = caster, B and C empty),
        // which fails the literal caster-source rule, so it is pinned by id (mangos-classic SpellEffects.cpp:1608-1612).
        const uint trigger25852 = 972010, disarm = 6608;
        using SpellTestKit kit = NewKit(
            Spell(trigger25852, Effect(SpellEffectName.TriggerSpell, 0, SpellImplicitTarget.Unit, trigger: disarm)),
            Spell(disarm, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModDisarm)) with
            {
                Duration = new SpellDuration(10000, 0, 10000),
                SpellVisual = 1,
            });
        Assert.False(SpellCasterSourceTargets.IsCasterSourceTargetsOnly(kit.Store.Get(disarm)!)); // the literal rule alone would disarm the caster
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, trigger25852, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.True(kit.System.HasAura(target, disarm));
        Assert.False(kit.System.HasAura(caster, disarm));
    }

    // --- EffectTeleportUnits -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TeleportUnits_ToCasterDest_MovesTheCasterToTheTarget_KeepingItsFacing()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 30, 10);
        float facing = caster.Orientation;

        kit.System.CastSpell(caster, TeleportToTarget, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(30f, caster.X, 3);
        Assert.Equal(10f, caster.Y, 3);
        Assert.Equal(facing, caster.Orientation, 3);
        Assert.Equal(30f, target.X, 3); // the target stays
    }

    [Fact]
    public void TeleportUnits_OnATargetOnATaxi_MovesNobody()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 30, 10);
        target.UnitFlags |= UnitFlags.TaxiFlight;

        kit.System.CastSpell(caster, TeleportToTarget, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(0f, caster.X, 3);
        Assert.Equal(30f, target.X, 3);
    }

    // --- SPELL_ATTR_EX_REQUIRE_ALL_TARGETS ---------------------------------------------------------------------------------------

    [Fact]
    public void RequireAllTargets_IsTheSpellAttributeExBit26()
        => Assert.Equal(0x04000000u, (uint)SpellAttributesExCombat.RequireAllTargets);

    [Fact]
    public void RequireAllTargets_WhenTheMainTargetMisses_NoUnitTakesAnEffect_ButWithoutTheAttributeTheOtherEffectLands()
    {
        // Effect 0 hits the explicit target, effect 1 energizes the caster: the caster is a second unit entry of the same cast,
        // and a self cast skips the hit roll, so only the main target can miss.
        SpellInfo requireAll = Spell(RequireAll,
            Effect(SpellEffectName.SchoolDamage, 40, SpellImplicitTarget.Unit),
            Effect(SpellEffectName.Energize, 50, SpellImplicitTarget.UnitCaster, misc: (int)PowerType.Rage)) with
        {
            AttributesEx = (SpellAttributesEx)SpellAttributesExCombat.RequireAllTargets,
        };
        SpellInfo plain = Spell(PlainTwoEffects,
            Effect(SpellEffectName.SchoolDamage, 40, SpellImplicitTarget.Unit),
            Effect(SpellEffectName.Energize, 50, SpellImplicitTarget.UnitCaster, misc: (int)PowerType.Rage));
        using SpellTestKit kit = NewKit(requireAll, plain);
        kit.System.CombatRules = new MissEverything();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        // The control: the same cast without the attribute still energizes the caster although the target was missed.
        kit.System.CastSpell(caster, PlainTwoEffects, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(50u, SpellSystem.GetPower(caster, PowerType.Rage));
        SpellSystem.SetPower(caster, PowerType.Rage, 0);

        kit.System.CastSpell(caster, RequireAll, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(0u, SpellSystem.GetPower(caster, PowerType.Rage)); // the caster's own effect is dropped as well
    }

    /// <summary>A combat rule set whose hit roll always misses.</summary>
    private sealed class MissEverything : ISpellCombatRules
    {
        public SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => SpellMissInfo.Miss;

        public bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => false;

        public float CritMultiplier(SpellInfo spell) => 1.0f;

        public uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage) => 0;

        public uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage) => damage;
    }
}
