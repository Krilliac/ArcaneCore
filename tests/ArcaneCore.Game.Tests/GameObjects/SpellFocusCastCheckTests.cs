using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// GO7a: the spell focus requirement of Spell::CheckItems (D:\refs\vmangos\src\game\Spells\Spell.cpp:7230-7243,
/// GridNotifiers.h:586-606 GameObjectFocusCheck, Object.cpp:1738-1752 IsWithinDist).
/// </summary>
public sealed class SpellFocusCastCheckTests
{
    private const uint FocusEntry = 300;
    private const uint ChestEntry = 301;
    private const uint ForgeSpell = 970001;
    private const uint AnvilSpell = 970002;
    private const uint PassiveForge = 970003;
    private const uint PlainSpell = 970004;

    private sealed class Rig : IDisposable
    {
        public Rig(uint focusRadius = 5, bool enabled = true, params GameObjectSpawn[] extraSpawns)
        {
            Kit = new SpellTestKit(
                Spell(ForgeSpell, Effect(SpellEffectName.Dummy, 0)) with { RequiresSpellFocus = 4, StartRecoveryTime = 0, StartRecoveryCategory = 0 },
                Spell(AnvilSpell, Effect(SpellEffectName.Dummy, 0)) with { RequiresSpellFocus = 5, StartRecoveryTime = 0, StartRecoveryCategory = 0 },
                Spell(PassiveForge, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
                {
                    RequiresSpellFocus = 4,
                    Attributes = SpellAttributes.Passive,
                    Duration = new SpellDuration(-1, 0, -1),
                    StartRecoveryTime = 0,
                    StartRecoveryCategory = 0,
                },
                Spell(PlainSpell, Effect(SpellEffectName.Dummy, 0)) with { StartRecoveryTime = 0, StartRecoveryCategory = 0 });
            GameObjectTemplate[] templates =
            [
                GoTemplate(FocusEntry, GameObjectType.SpellFocus, (0, 4), (1, focusRadius)),
                GoTemplate(ChestEntry, GameObjectType.Chest, (0, 4), (1, focusRadius)),
            ];
            var content = new GameObjectContent(templates, [GoSpawn(1, FocusEntry, 0, 0), .. extraSpawns], [], [], []);
            Systems = new GameObjectMapSystem(Kit.World.GetMap(0), content);
            Kit.World.GetMap(0).AddUpdater(Systems);
            Kit.System.RegisterCastCheck(new SpellFocusCastCheck(map => ReferenceEquals(map, Kit.World.GetMap(0)) ? Systems : null, () => enabled));
            (Caster, _) = Kit.AddPlayer(1, 0, 0);
            Kit.World.RunTick(50);
            Focus = Systems.GameObjects.Single(g => g.Spawn!.Guid == 1);
            Caster.Relocate(Focus.X, Focus.Y, Focus.Z, 0, 0);
        }

        public SpellTestKit Kit { get; }

        public GameObjectMapSystem Systems { get; }

        public Player Caster { get; }

        public GameObject Focus { get; }

        /// <summary>The 3D centre distance below which the focus object reaches the caster.</summary>
        public float Reach(uint radius) => radius + Focus.BoundingRadius + Caster.BoundingRadius;

        public void StandAt(float distance, float dz = 0) => Caster.Relocate(Focus.X + distance, Focus.Y, Focus.Z + dz, 0, 0);

        public SpellCastResult Cast(uint spell, bool triggered = false)
            => Kit.System.CastSpell(Caster, spell, SpellCastTargets.ForUnit(Caster.Guid), triggered);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void NoFocusObjectInRange_FailsRequiresSpellFocus_AndInRangeSucceeds()
    {
        using var rig = new Rig();
        rig.StandAt(30);
        Assert.Equal(SpellCastResult.RequiresSpellFocus, rig.Cast(ForgeSpell));
        rig.StandAt(2);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ForgeSpell));
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(PlainSpell));
    }

    [Fact]
    public void Distance_IsTheStrictThreeDimensionalCentreDistance_WithBothBoundingRadii_AndNotClamped()
    {
        using var rig = new Rig();
        float reach = rig.Reach(5);
        rig.StandAt(reach - 0.02f);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ForgeSpell));
        rig.StandAt(reach + 0.02f);
        Assert.Equal(SpellCastResult.RequiresSpellFocus, rig.Cast(ForgeSpell));
        rig.StandAt(1, dz: 8); // 2D inside the radius, 3D outside
        Assert.Equal(SpellCastResult.RequiresSpellFocus, rig.Cast(ForgeSpell));

        using var zero = new Rig(focusRadius: 0);
        float zeroReach = zero.Reach(0);
        zero.StandAt(zeroReach - 0.02f);
        Assert.Equal(SpellCastResult.CastOk, zero.Cast(ForgeSpell));
        zero.StandAt(zeroReach + 0.1f); // the previous Math.Max(1, data1) clamp would still have accepted this
        Assert.True(zeroReach + 0.1f < 1.0f);
        Assert.Equal(SpellCastResult.RequiresSpellFocus, zero.Cast(ForgeSpell));
    }

    [Fact]
    public void OnlyASpawnedSpellFocusObjectOfTheRightFocusIdCounts()
    {
        using var rig = new Rig(extraSpawns: GoSpawn(2, ChestEntry, 0, 0));
        rig.StandAt(1);
        Assert.Equal(SpellCastResult.RequiresSpellFocus, rig.Cast(AnvilSpell)); // focus id 5: none
        rig.Systems.Despawn(rig.Focus);
        Assert.Equal(SpellCastResult.RequiresSpellFocus, rig.Cast(ForgeSpell));   // the chest has data0 = 4 but is not a spell focus
        rig.Systems.ForceRespawn(rig.Focus);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ForgeSpell));
    }

    [Fact]
    public void TriggeredCastsAreChecked_PassiveSpellsAreNot()
    {
        using var rig = new Rig();
        rig.StandAt(30);
        Assert.Equal(SpellCastResult.RequiresSpellFocus, rig.Cast(ForgeSpell, triggered: true));
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(PassiveForge, triggered: true));
    }

    [Fact]
    public void TheConfigSwitchDisablesTheRequirement()
    {
        using var rig = new Rig(enabled: false);
        rig.StandAt(30);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ForgeSpell));
    }

    [Fact]
    public void AMapWithoutAGameObjectSystem_FindsNoFocusObject()
    {
        using var rig = new Rig();
        var check = new SpellFocusCastCheck(_ => null);
        SpellInfo spell = rig.Kit.Store.Get(ForgeSpell)!;
        var context = new SpellCastCheckContext(rig.Kit.System, rig.Caster, spell, SpellCastTargets.ForUnit(rig.Caster.Guid), rig.Caster, false, true);
        Assert.Equal(SpellCastResult.RequiresSpellFocus, check.Check(context));
        Assert.Equal(SpellCheckPhase.Items, check.Phase);
        Assert.True(check.Order > SpellCastCheckOrder.Equipment);
    }

    [Fact]
    public void FindSpellFocus_PicksTheLowestSpawnGuid_AndHasSpellFocusNearbyAgrees()
    {
        using var rig = new Rig(extraSpawns: GoSpawn(0, FocusEntry, 0, 0));
        rig.StandAt(1);
        Assert.Equal(0u, rig.Systems.FindSpellFocus(rig.Caster, 4)!.Spawn!.Guid);
        Assert.True(rig.Systems.HasSpellFocusNearby(rig.Caster, 4));
        Assert.False(rig.Systems.HasSpellFocusNearby(rig.Caster, 9));
    }

    [Fact]
    public void NonPlayerCaster_IsNotChecked_AsInRetail()
    {
        // Spell.cpp:7104-7106: CheckItems returns SPELL_CAST_OK for a non-Player caster before the focus block.
        using var rig = new Rig();
        var creature = new ArcaneCore.Game.Creatures.Creature(
            9001, CreatureTestSupport.Template(CreatureTestSupport.WolfEntry), null, ArcaneCore.Kernel.WorldData.Creatures.CreatureContent.Empty, new Random(1));
        var check = new SpellFocusCastCheck(map => rig.Systems);
        SpellInfo forge = rig.Kit.Store.Get(ForgeSpell)!;
        var context = new SpellCastCheckContext(rig.Kit.System, creature, forge, SpellCastTargets.ForUnit(creature.Guid), creature, false, true);
        Assert.Equal(SpellCastResult.CastOk, check.Check(context));
    }
}
