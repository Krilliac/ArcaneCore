using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Two explicit-target rules of Spell::CheckCast: AttributesEx3 ONLY_ON_PLAYER (vmangos Spell.cpp:5509-5510, strict, non-triggered,
/// not an area spell, BAD_TARGETS) and AttributesEx ONLY_PEACEFUL_TARGETS (Spell.cpp:5487-5488, strict, non-triggered,
/// TARGET_AFFECTING_COMBAT).
/// </summary>
public sealed class CastCheckTargetRulesTests
{
    private const uint Soulstone = 971001;
    private const uint Sap = 971002;
    private const uint AreaOnlyPlayer = 971003;

    private static SpellInfo[] Spells() =>
    [
        Spell(Soulstone, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with
        {
            AttributesEx3 = 0x100,
            Duration = new SpellDuration(60_000, 0, 60_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(Sap, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
        {
            AttributesEx = (SpellAttributesEx)0x100,
            Duration = new SpellDuration(60_000, 0, 60_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(AreaOnlyPlayer, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.LocationCasterSrc,
            targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 20 }) with
        {
            AttributesEx3 = 0x100,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
    ];

    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private sealed class Scene : IDisposable
    {
        private uint _next = 991_000;

        public Scene()
        {
            Kit = new SpellTestKit(Spells());
            Kit.System.Units = new MapUnits();
            Kit.System.CombatRules = SpellCombatRules.Neutral;
            Relations = new FakeRelations();
            Kit.System.Relations = Relations;
            GeneralCastChecks.Install(Kit.System);
            (Caster, _) = Kit.AddPlayer(1);
            Caster.Level = 60;
            Caster.MaxHealth = Caster.Health = 1000;
        }

        public SpellTestKit Kit { get; }

        public FakeRelations Relations { get; }

        public Player Caster { get; }

        public Creature AddCreature(float x)
        {
            uint entry = ++_next;
            var template = new CreatureTemplate
            {
                Entry = entry, Name = $"Target {entry}", MinLevel = 20, MaxLevel = 20,
                MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [1], Faction = 14,
            };
            var creature = new Creature(entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
            Map map = Kit.World.GetMap(0);
            creature.Relocate(x, 0, Caster.Z, 0, 0);
            creature.MapId = 0;
            map.AddObject(creature);
            map.Combat.Track(creature);
            Kit.World.RunTick(0);
            creature.Level = 20;
            creature.MaxHealth = creature.Health = 100;
            return creature;
        }

        public SpellCastResult Cast(uint spell, Unit target, bool triggered = false)
            => Kit.System.CastSpell(Caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void OnlyOnPlayer_FriendlyCreatureTarget_FailsBadTargets_FriendlyPlayerSucceeds()
    {
        using var s = new Scene();
        Creature npc = s.AddCreature(3);
        Player friend = s.Kit.AddPlayer(2, 4).Player;
        friend.MaxHealth = friend.Health = 100;

        Assert.Equal(SpellCastResult.BadTargets, s.Cast(Soulstone, npc));
        Assert.Equal(SpellCastResult.CastOk, s.Cast(Soulstone, friend));
    }

    [Fact]
    public void OnlyOnPlayer_TriggeredCastOnACreature_IsNotRefused()
    {
        using var s = new Scene();
        Creature npc = s.AddCreature(3);

        Assert.Equal(SpellCastResult.CastOk, s.Cast(Soulstone, npc, triggered: true));
    }

    [Fact]
    public void OnlyOnPlayer_AreaSpell_IsNotRefusedByTheExplicitTargetRule()
    {
        using var s = new Scene();
        Creature npc = s.AddCreature(3);

        Assert.NotEqual(SpellCastResult.BadTargets, s.Cast(AreaOnlyPlayer, npc));
    }

    [Fact]
    public void OnlyPeacefulTargets_SapOnACreatureInCombat_FailsTargetAffectingCombat_IdleCreatureSucceeds()
    {
        using var s = new Scene();
        Creature busy = s.AddCreature(3);
        Creature idle = s.AddCreature(4);
        s.Relations.Hostile.Add(busy.Guid);
        s.Relations.Hostile.Add(idle.Guid);
        busy.UnitFlags |= UnitFlags.InCombat;

        Assert.Equal(SpellCastResult.TargetAffectingCombat, s.Cast(Sap, busy));
        Assert.Equal(SpellCastResult.CastOk, s.Cast(Sap, idle));
    }

    [Fact]
    public void OnlyPeacefulTargets_TriggeredCastOnACreatureInCombat_IsNotRefused()
    {
        using var s = new Scene();
        Creature busy = s.AddCreature(3);
        s.Relations.Hostile.Add(busy.Guid);
        busy.UnitFlags |= UnitFlags.InCombat;

        Assert.Equal(SpellCastResult.CastOk, s.Cast(Sap, busy, triggered: true));
    }
}
