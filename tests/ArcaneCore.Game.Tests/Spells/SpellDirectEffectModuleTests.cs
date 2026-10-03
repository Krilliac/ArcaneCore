using System.Buffers.Binary;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// SPELL_EFFECT_INSTAKILL, HEAL_MAX_HEALTH, THREAT and DISPEL_MECHANIC (vmangos Spell::EffectInstaKill
/// SpellEffects.cpp:268, EffectHealMaxHealth :3516, EffectThreat :3503, EffectDispelMechanic :5507).
/// </summary>
public sealed class SpellDirectEffectModuleTests
{
    private const uint Kill = 910001;
    private const uint FullHeal = 910002;
    private const uint Taunt = 910003;
    private const uint NegativeThreat = 910004;
    private const uint DispelRoot = 910005;
    private const uint Rooted = 910006;
    private const uint SlowedByEffect = 910007;
    private const uint Unrelated = 910008;
    private const uint HealingBoost = 910009;
    private const uint HealingPenalty = 910010;
    private const uint HealingBonusTaken = 910011;
    private const uint HolyThreatMod = 910012;
    private const uint ThreatNoNew = 910013;
    private const uint ThreatNoHarmful = 910014;
    private const uint Holy = 910015;

    private const uint MechanicRoot = 7;
    private const uint MechanicSnare = 11;

    [Fact]
    public void ModuleIsDiscovered_AndTheEffectsHaveHandlers()
    {
        using var kit = Kit();

        Assert.Contains(typeof(DirectCombatEffects), SpellHandlerModules.BuiltIn);
        Assert.Contains(typeof(DirectCombatEffects), kit.System.Modules);
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Instakill));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.HealMaxHealth));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Threat));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.DispelMechanic));
    }

    [Fact]
    public void Instakill_KillsTheTargetAndLogsVictimAndSpell()
    {
        using var kit = Kit();
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, Kill, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(0u, target.Health);
        // vmangos SpellEffects.cpp:274-279 (build > 1.11.2): victim guid (8 bytes, not packed), spell id; to the caster too.
        byte[] log = Assert.Single(Packets(casterSession, WorldOpcode.SmsgSpellinstakilllog));
        Assert.Equal(12, log.Length);
        Assert.Equal(target.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(log));
        Assert.Equal(Kill, BinaryPrimitives.ReadUInt32LittleEndian(log.AsSpan(8)));
    }

    [Fact]
    public void Instakill_OnADeadTarget_DoesNothing()
    {
        using var kit = Kit();
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Health = 0;
        casterSession.Clear();

        kit.System.CastSpell(caster, Kill, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Empty(Packets(casterSession, WorldOpcode.SmsgSpellinstakilllog));
    }

    [Fact]
    public void HealMaxHealth_HealsByTheCastersMaximumHealth_NotTheTargets()
    {
        using var kit = Kit();
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.MaxHealth = 80;
        target.MaxHealth = 500;
        target.Health = 10;

        kit.System.CastSpell(caster, FullHeal, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(90u, target.Health); // 10 + caster max 80 (vmangos :3524)
        Assert.Single(Packets(casterSession, WorldOpcode.SmsgSpellheallog));
    }

    [Fact]
    public void HealMaxHealth_IsClampedToTheTargetsMaximum()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.MaxHealth = 800;
        target.MaxHealth = 100;
        target.Health = 10;

        kit.System.CastSpell(caster, FullHeal, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(100u, target.Health);
    }

    [Fact]
    public void HealMaxHealth_AppliesHealingDonePercentOfTheCaster_AndHealingTakenOfTheTarget()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.MaxHealth = 100;
        target.MaxHealth = 1000;

        // +25% done (aura 136 ModHealingDonePercent): vmangos :3529-3535 multiplies (100 + amount) / 100 per aura.
        kit.System.CastSpell(caster, HealingBoost, SpellCastTargets.ForSelf(), triggered: true);
        target.Health = 1;
        kit.System.CastSpell(caster, FullHeal, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(126u, target.Health);

        // Taken: the strongest negative and the strongest positive ModHealingPct (aura 118) both apply (:3539-3548).
        kit.System.CastSpell(caster, HealingPenalty, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.System.CastSpell(caster, HealingBonusTaken, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        target.Health = 1;
        kit.System.CastSpell(caster, FullHeal, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(126u, target.Health); // 1 + 100 * 1.25 * 0.5 * 2
    }

    [Fact]
    public void HealMaxHealth_OnADeadTarget_DoesNothing()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Health = 0;

        kit.System.CastSpell(caster, FullHeal, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(0u, target.Health);
    }

    [Fact]
    public void Threat_AddsTheEffectValueToTheCastersThreatOnACreature()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        Creature wolf = MakeCreature(kit);

        kit.System.CastSpell(caster, Taunt, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);

        Assert.Equal(150f, wolf.Combat.Threat.GetThreat(caster));
    }

    [Fact]
    public void Threat_CanBeNegative_AndNeverDropsBelowZero()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        Creature wolf = MakeCreature(kit);
        wolf.Combat.Threat.AddThreat(caster, 100f);

        kit.System.CastSpell(caster, NegativeThreat, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        Assert.Equal(60f, wolf.Combat.Threat.GetThreat(caster)); // Feint-shaped: -40

        kit.System.CastSpell(caster, NegativeThreat, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        kit.System.CastSpell(caster, NegativeThreat, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        Assert.Equal(0f, wolf.Combat.Threat.GetThreat(caster));
    }

    [Fact]
    public void Threat_OnAPlayer_IsIgnored_OnlyCreaturesHaveThreatLists()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, Taunt, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Empty(target.Combat.Threat.Entries);
    }

    [Fact]
    public void Threat_IsScaledByTheCastersModThreatAuraOfTheSpellSchool()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        Creature wolf = MakeCreature(kit);

        // Aura 10 ModThreat -50% on the Holy school (misc value = school mask): vmangos Unit::ApplyTotalThreatModifier :7409-7420.
        kit.System.CastSpell(caster, HolyThreatMod, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(caster, Holy, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        Assert.Equal(75f, wolf.Combat.Threat.GetThreat(caster));

        // A physical (school 0) threat spell is not scaled by a holy-only modifier.
        kit.System.CastSpell(caster, Taunt, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        Assert.Equal(225f, wolf.Combat.Threat.GetThreat(caster));
    }

    [Fact]
    public void Threat_SpellAttributes_NoThreatNeverCreatesAnEntry_NoHarmfulThreatSkipsTheEffect()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        Creature wolf = MakeCreature(kit);

        // SPELL_ATTR_EX_NO_THREAT (0x400): ThreatManager::addThreat passes noNew (ThreatManager.cpp:424).
        kit.System.CastSpell(caster, ThreatNoNew, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        Assert.Empty(wolf.Combat.Threat.Entries);
        wolf.Combat.Threat.AddThreat(caster, 10f);
        kit.System.CastSpell(caster, ThreatNoNew, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        Assert.Equal(60f, wolf.Combat.Threat.GetThreat(caster));

        // SPELL_ATTR_EX4_NO_HARMFUL_THREAT (0x10): Unit::AddThreat returns early (Unit.cpp:7426).
        wolf.Combat.Threat.Clear();
        kit.System.CastSpell(caster, ThreatNoHarmful, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        Assert.Empty(wolf.Combat.Threat.Entries);
    }

    [Fact]
    public void DispelMechanic_RemovesEveryAuraWithThatMechanic_SpellLevelOrEffectLevel()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(caster, Rooted, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.System.CastSpell(caster, SlowedByEffect, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.System.CastSpell(caster, Unrelated, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(3, kit.System.GetAuras(target).Count);

        // Escape Artist-shaped: EffectMiscValue names the mechanic (vmangos :5511, SpellAuraHolder::HasMechanic :7435).
        kit.System.CastSpell(caster, DispelRoot, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal([SlowedByEffect, Unrelated], kit.System.GetAuras(target).Select(h => h.Spell.Id).Order());
        Assert.False(kit.System.HasAura(target, Rooted));
    }

    /// <summary>A wolf spawned into the kit's map (threat needs both units in the same map, vmangos Unit::AddThreat IsInMap).</summary>
    private static Creature MakeCreature(SpellTestKit kit)
    {
        CreatureTemplate template = CreatureTestSupport.Template(CreatureTestSupport.WolfEntry, t =>
        {
            t.MinLevelHealth = 100;
            t.MaxLevelHealth = 100;
        });
        CreatureSpawn spawn = CreatureTestSupport.Spawn(77, CreatureTestSupport.WolfEntry, 3, 0);
        var system = new CreatureMapSystem(kit.World.GetMap(0), CreatureTestSupport.Content([template], [spawn]), null, random: new Random(1));
        kit.World.GetMap(0).AddUpdater(system);
        kit.World.RunTick(50);
        Creature creature = Assert.Single(system.Creatures);
        kit.System.Units = new FixedResolver(creature);
        return creature;
    }

    private static SpellTestKit Kit() => new(
        Spell(Kill, Effect(SpellEffectName.Instakill, 0, SpellImplicitTarget.UnitEnemy)),
        Spell(FullHeal, Effect(SpellEffectName.HealMaxHealth, 0, SpellImplicitTarget.UnitFriend)),
        Spell(Taunt, Effect(SpellEffectName.Threat, 150, SpellImplicitTarget.UnitEnemy)),
        Spell(NegativeThreat, Effect(SpellEffectName.Threat, -40, SpellImplicitTarget.UnitEnemy)),
        Spell(Holy, Effect(SpellEffectName.Threat, 150, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Holy },
        Spell(HolyThreatMod, Effect(SpellEffectName.ApplyAura, -50, aura: AuraType.ModThreat, misc: 1 << (int)SpellSchool.Holy))
            with { Duration = new SpellDuration(-1, 0, -1) },
        Spell(ThreatNoNew, Effect(SpellEffectName.Threat, 50, SpellImplicitTarget.UnitEnemy)) with { AttributesEx = (SpellAttributesEx)0x400 },
        Spell(ThreatNoHarmful, Effect(SpellEffectName.Threat, 50, SpellImplicitTarget.UnitEnemy)) with { AttributesEx4 = 0x10 },
        Spell(HealingBoost, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModHealingDonePercent)) with { Duration = new SpellDuration(-1, 0, -1) },
        Spell(HealingPenalty, Effect(SpellEffectName.ApplyAura, -50, SpellImplicitTarget.UnitFriend, AuraType.ModHealingPct)) with { Duration = new SpellDuration(-1, 0, -1) },
        Spell(HealingBonusTaken, Effect(SpellEffectName.ApplyAura, 100, SpellImplicitTarget.UnitFriend, AuraType.ModHealingPct)) with { Duration = new SpellDuration(-1, 0, -1) },
        Spell(DispelRoot, Effect(SpellEffectName.DispelMechanic, 0, SpellImplicitTarget.UnitFriend, misc: (int)MechanicRoot)),
        Spell(Rooted, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with
        {
            Mechanic = MechanicRoot,
            Duration = new SpellDuration(-1, 0, -1),
        },
        Spell(SlowedByEffect, WithMechanic(Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy), MechanicSnare)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
        },
        Spell(Unrelated, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
        });

    private static SpellEffectInfo WithMechanic(SpellEffectInfo effect, uint mechanic) => effect with { Mechanic = mechanic };

    private sealed class FixedResolver(Unit unit) : ISpellUnitResolver
    {
        private readonly MapPlayerResolver _players = new();

        public Unit? Find(Unit reference, ObjectGuid guid) => guid == unit.Guid ? unit : _players.Find(reference, guid);
    }
}
