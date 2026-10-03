using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// SPELL_AURA_MOD_SCALE (vmangos Aura::HandleAuraModScale, SpellAuras.cpp:2948) and the tracking auras
/// MOD_TRACK_CREATURES / MOD_TRACK_RESOURCES (HandleAuraTrackCreatures :2909, HandleAuraTrackResources :2923).
/// </summary>
public sealed class VisualAuraModuleTests
{
    private const uint Grow = 930301;
    private const uint Shrink = 930302;
    private const uint Giant = 930303;
    private const uint TrackHumanoids = 930304;
    private const uint TrackBeasts = 930305;
    private const uint FindMinerals = 930306;
    private const uint PlainTrack = 930307;
    private const uint PlainTrack2 = 930308;
    private const uint Collapse = 930309;

    private const uint MountedOk = 0x01000000;   // SPELL_ATTR_ALLOW_WHILE_MOUNTED (SpellDefines.h:854)
    private const uint NoAutocastAi = 0x00020000; // SPELL_ATTR_EX_NO_AUTOCAST_AI (SpellDefines.h:887)

    [Fact]
    public void ModuleIsDiscovered_AndTheAuraTypesHaveHandlers()
    {
        using var kit = Kit();

        Assert.Contains(typeof(VisualAuras), kit.System.Modules);
        Assert.True(kit.System.HasAuraHandler(AuraType.ModScale));
        Assert.True(kit.System.HasAuraHandler(AuraType.TrackCreatures));
        Assert.True(kit.System.HasAuraHandler(AuraType.TrackResources));
    }

    [Fact]
    public void ModScale_MultipliesTheObjectScale_AndKeepsReachAndRadiusInProportion()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.0f);
        player.SetFloat(UpdateFields.UnitFieldBoundingradius, 0.4f);
        player.SetFloat(UpdateFields.UnitFieldCombatreach, 1.5f);

        kit.System.CastSpell(player, Grow, SpellCastTargets.ForSelf(), triggered: true);

        // ApplyPercentModFloatValue (Object.h:248): * (100 + 50) / 100; UpdateModelData (Unit.cpp:9364) scales radius and reach with it.
        Assert.Equal(1.5f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);
        Assert.Equal(0.6f, player.GetFloat(UpdateFields.UnitFieldBoundingradius), 4);
        Assert.Equal(2.25f, player.GetFloat(UpdateFields.UnitFieldCombatreach), 4);

        kit.System.CastSpell(player, Giant, SpellCastTargets.ForSelf(), triggered: true); // another +100%
        Assert.Equal(3.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);

        kit.System.RemoveAuras(player, Grow);
        Assert.Equal(2.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);
        kit.System.RemoveAuras(player, Giant);
        Assert.Equal(1.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);
        Assert.Equal(0.4f, player.GetFloat(UpdateFields.UnitFieldBoundingradius), 4);
        Assert.Equal(1.5f, player.GetFloat(UpdateFields.UnitFieldCombatreach), 4);
    }

    [Fact]
    public void ModScale_Negative_ShrinksTheUnit_AndMinus100NeverZeroesIt()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.0f);

        kit.System.CastSpell(player, Shrink, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(0.5f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);
        kit.System.RemoveAuras(player, Shrink);
        Assert.Equal(1.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);

        kit.System.CastSpell(player, Collapse, SpellCastTargets.ForSelf(), triggered: true);
        // vmangos turns -100 into -99.9 (Object.h:250), so the scale stays above zero.
        Assert.InRange(player.GetFloat(UpdateFields.ObjectFieldScaleX), 0.0009f, 0.0011f);
        kit.System.RemoveAuras(player, Collapse);
        Assert.Equal(1.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
    }

    [Fact]
    public void Tracking_SetsAndClearsTheMiscValueBitOfTheTrackField()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, TrackHumanoids, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(1u << 6, player.GetUInt32(UpdateFields.PlayerTrackCreatures));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerTrackResources));

        kit.System.CastSpell(player, FindMinerals, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(1u << 0, player.GetUInt32(UpdateFields.PlayerTrackResources));
        // Find Minerals is a tracker too: only one tracking spell at a time, so Track Humanoids is gone.
        Assert.False(kit.System.HasAura(player, TrackHumanoids));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerTrackCreatures));

        kit.System.RemoveAuras(player, FindMinerals);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerTrackResources));
    }

    [Fact]
    public void Tracking_ANewTracker_ReplacesTheOldOne_FromAnyCaster()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, TrackHumanoids, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, TrackBeasts, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(1u << 0, player.GetUInt32(UpdateFields.PlayerTrackCreatures));
        Assert.True(kit.System.HasAura(player, TrackBeasts));
        Assert.False(kit.System.HasAura(player, TrackHumanoids));
    }

    [Fact]
    public void Tracking_WithoutTheTrackerAttributes_IsNotExclusive()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        // vmangos GetSpellSpecific (SpellEntry.cpp:152-157) names SPELL_TRACKER only for spells with
        // EX_NO_AUTOCAST_AI or ALLOW_WHILE_MOUNTED ("exclude Well Fed, some other always allowed cases").
        kit.System.CastSpell(player, PlainTrack, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, PlainTrack2, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal((1u << 2) | (1u << 3), player.GetUInt32(UpdateFields.PlayerTrackCreatures));
        Assert.True(kit.System.HasAura(player, PlainTrack));
        Assert.True(kit.System.HasAura(player, PlainTrack2));
    }

    [Fact]
    public void Tracking_AffectsOnlyTheUnitItIsOn()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        // The track fields belong to the unit the aura is on.
        (Player other, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(other, TrackHumanoids, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(0u, caster.GetUInt32(UpdateFields.PlayerTrackCreatures));
        Assert.Equal(1u << 6, other.GetUInt32(UpdateFields.PlayerTrackCreatures));
    }

    private static SpellTestKit Kit()
    {
        SpellInfo Perm(uint id, SpellEffectInfo effect, uint attributes = 0, uint ex = 0) => Spell(id, effect) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
            Attributes = (SpellAttributes)attributes,
            AttributesEx = (SpellAttributesEx)ex,
        };

        return new SpellTestKit(
            Perm(Grow, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModScale)),
            Perm(Giant, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModScale)),
            Perm(Shrink, Effect(SpellEffectName.ApplyAura, -50, aura: AuraType.ModScale)),
            Perm(Collapse, Effect(SpellEffectName.ApplyAura, -100, aura: AuraType.ModScale)),
            Perm(TrackHumanoids, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.TrackCreatures, misc: 7), MountedOk),
            Perm(TrackBeasts, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.TrackCreatures, misc: 1), ex: NoAutocastAi),
            Perm(FindMinerals, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.TrackResources, misc: 1), MountedOk),
            Perm(PlainTrack, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.TrackCreatures, misc: 3)),
            Perm(PlainTrack2, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.TrackCreatures, misc: 4)));
    }
}
