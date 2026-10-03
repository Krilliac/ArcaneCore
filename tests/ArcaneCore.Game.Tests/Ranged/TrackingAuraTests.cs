using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// Tracking auras and Hunter's Mark (vmangos Aura::HandleAuraTrackCreatures / TrackResources /
/// TrackStealthed, SpellAuras.cpp:2909-2940, HandleAuraModStalked 4217-4226, Spell.cpp:6436-6447).
/// Spells are synthetic; the real ids and MiscValues need the client's Spell.dbc.
/// </summary>
public sealed class TrackingAuraTests
{
    private const uint TrackBeasts = 920001;
    private const uint TrackHumanoids = 920002;
    private const uint FindMinerals = 920003;
    private const uint TrackHidden = 920004;
    private const uint Mark = 920005;
    private const uint SelfMark = 920006;
    private const uint WellFed = 920007;
    private const uint MindVision = 920008;
    private const uint WarlockLookalike = 920009;
    private const uint MountedTracker = 920010;

    private const uint DynTrackUnit = 0x2;

    private static SpellInfo Tracker(uint id, AuraType type, int misc) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, type, misc: misc))
        with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
        Attributes = SpellAttributes.AllowWhileMounted, // SpellEntry.cpp:153-157: a tracker carries one of the two attribute bits
    };

    private static SpellInfo StalkedSpell(uint id, uint family, ulong familyFlags) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.Unit, AuraType.ModStalked)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        RangeIndex = 4,
        Range = new SpellRange(0, 100),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
        SpellVisual = 1,
        SpellFamilyName = family,
        SpellFamilyFlags = familyFlags,
    };

    private static SpellTestKit Kit() => new(
        Tracker(TrackBeasts, AuraType.TrackCreatures, 1),
        Tracker(TrackHumanoids, AuraType.TrackCreatures, 7),
        Tracker(FindMinerals, AuraType.TrackResources, 2),
        Tracker(TrackHidden, AuraType.TrackStealthed, 0),
        Tracker(WellFed, AuraType.TrackCreatures, 3) with { Attributes = SpellAttributes.None }, // a tracking aura on a non-tracker spell (food, elixir)
        Tracker(MountedTracker, AuraType.TrackCreatures, 4) with { Attributes = SpellAttributes.None, AttributesEx = SpellAttributesEx.NoAutocastAi },
        StalkedSpell(MindVision, 6, 1UL << 26),         // SPELLFAMILY_PRIEST, CF_PRIEST_MIND_VISION
        StalkedSpell(WarlockLookalike, 5, 1UL << 26),   // the same flag bit in another family
        Spell(Mark, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.Unit, AuraType.ModStalked)) with
        {
            Duration = new SpellDuration(120_000, 0, 120_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 100),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellVisual = 1,
        });

    private static SpellTestKit WithHandlers()
    {
        SpellTestKit kit = Kit();
        RangedHandlers.Register(kit.System);
        return kit;
    }

    private static void Track(SpellTestKit kit, Player player, uint spell)
        => Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true));

    [Fact]
    public void TrackCreatures_SetsTheBitOfTheMiscValue_AndRemovalClearsOnlyThatBit()
    {
        using var kit = WithHandlers();
        (Player player, _) = kit.AddPlayer(1);

        Track(kit, player, TrackBeasts);
        Assert.Equal(0b1u, player.GetUInt32(UpdateFields.PlayerTrackCreatures));

        player.SetFlag(UpdateFields.PlayerTrackCreatures, 1u << 20); // a bit of another source stays
        kit.System.RemoveAuras(player, TrackBeasts);
        Assert.Equal(1u << 20, player.GetUInt32(UpdateFields.PlayerTrackCreatures));

        Track(kit, player, TrackHumanoids);
        Assert.Equal((1u << 20) | (1u << 6), player.GetUInt32(UpdateFields.PlayerTrackCreatures));
    }

    [Fact]
    public void TrackResources_UsesItsOwnField()
    {
        using var kit = WithHandlers();
        (Player player, _) = kit.AddPlayer(1);

        Track(kit, player, FindMinerals);

        Assert.Equal(0b10u, player.GetUInt32(UpdateFields.PlayerTrackResources));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerTrackCreatures));
    }

    [Fact]
    public void ANewTrackerReplacesTheOldOne_OfAnyTrackingKind()
    {
        using var kit = WithHandlers();
        (Player player, _) = kit.AddPlayer(1);

        Track(kit, player, TrackBeasts);
        Track(kit, player, TrackHumanoids);
        Assert.False(kit.System.HasAura(player, TrackBeasts));
        Assert.Equal(1u << 6, player.GetUInt32(UpdateFields.PlayerTrackCreatures));

        Track(kit, player, FindMinerals);
        Assert.False(kit.System.HasAura(player, TrackHumanoids));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerTrackCreatures));
        Assert.Equal(0b10u, player.GetUInt32(UpdateFields.PlayerTrackResources));

        Track(kit, player, TrackHidden);
        Assert.False(kit.System.HasAura(player, FindMinerals));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerTrackResources));
    }

    [Fact]
    public void TrackStealthed_SetsOneBitOfThePlayerFlagsByte_AndKeepsTheOthers()
    {
        using var kit = WithHandlers();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.PlayerFieldBytes, 0, 0x08); // release timer bit

        Track(kit, player, TrackHidden);
        Assert.Equal(0x0A, player.GetByte(UpdateFields.PlayerFieldBytes, 0));

        kit.System.RemoveAuras(player, TrackHidden);
        Assert.Equal(0x08, player.GetByte(UpdateFields.PlayerFieldBytes, 0));
    }

    [Fact]
    public void NonPlayers_NeverGetThePlayerFields()
    {
        using var kit = WithHandlers();
        var creature = new TestUnit(1, 0, 0);
        SpellInfo spell = kit.Store.Get(TrackBeasts)!;
        var holder = new SpellAuraHolder(spell, creature, ObjectGuid.Player(1), 1, AuraCasterOwner.Orphaned(), -1);
        var aura = new SpellAura(0, AuraType.TrackCreatures, 0, 0, 1);
        holder.SetAura(aura);

        // The creature's value array ends before PLAYER_TRACK_CREATURES: touching the field would throw.
        TrackingAuras.Apply(kit.System, holder, aura, apply: true);
        TrackingAuras.Apply(kit.System, holder, aura, apply: false);
    }

    [Fact]
    public void HuntersMark_SetsTheTrackUnitDynamicFlag_UntilItEnds()
    {
        using var kit = WithHandlers();
        (Player hunter, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 20);
        var relations = new FakeRelations();
        relations.Hostile.Add(target.Guid);
        kit.System.Relations = relations;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, Mark, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        Assert.True(target.HasFlag(UpdateFields.UnitDynamicFlags, DynTrackUnit));

        kit.System.RemoveAuras(target, Mark);
        Assert.False(target.HasFlag(UpdateFields.UnitDynamicFlags, DynTrackUnit));
    }

    [Fact]
    public void HuntersMark_NeedsAnAttackableUnit()
    {
        using var kit = WithHandlers();
        (Player hunter, _) = kit.AddPlayer(1);
        (Player friend, _) = kit.AddPlayer(2, 20);
        kit.System.Relations = new FakeRelations(); // nobody hostile

        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(hunter, Mark, SpellCastTargets.ForUnit(friend.Guid), triggered: false));
        // vmangos reads only the explicit unit target (Spell.cpp:6438): a self-mask cast has none.
        Assert.Equal(SpellCastResult.BadImplicitTargets, kit.System.CastSpell(hunter, Mark, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal(SpellCastResult.BadImplicitTargets, kit.System.CastSpell(hunter, Mark, SpellCastTargets.ForUnit(ObjectGuid.Player(99)), triggered: false));
        Assert.False(friend.HasFlag(UpdateFields.UnitDynamicFlags, DynTrackUnit));
    }

    [Fact]
    public void MindVision_IsExemptFromTheAttackableRule_ButOnlyInTheMindVisionFamily()
    {
        using var kit = WithHandlers();
        (Player priest, _) = kit.AddPlayer(1);
        (Player friend, _) = kit.AddPlayer(2, 20);
        kit.System.Relations = new FakeRelations(); // nobody hostile

        // Spell.cpp:6444-6447 exempts SPELLFAMILY_PRIEST / CF_PRIEST_MIND_VISION.
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(priest, MindVision, SpellCastTargets.ForUnit(friend.Guid), triggered: false));
        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(priest, WarlockLookalike, SpellCastTargets.ForUnit(friend.Guid), triggered: false));
    }

    [Fact]
    public void ATrackingAuraOnANonTrackerSpell_NeitherReplacesNorIsReplacedByATracker()
    {
        using var kit = WithHandlers();
        (Player player, _) = kit.AddPlayer(1);

        Track(kit, player, TrackBeasts);
        Track(kit, player, WellFed); // SpellEntry.cpp:153-157: no tracker attribute, so no exclusivity
        Assert.True(kit.System.HasAura(player, TrackBeasts));
        Assert.True(kit.System.HasAura(player, WellFed));

        Track(kit, player, TrackHumanoids); // a real tracker replaces the tracker but not the food buff
        Assert.False(kit.System.HasAura(player, TrackBeasts));
        Assert.True(kit.System.HasAura(player, WellFed));
        Assert.True(kit.System.HasAura(player, TrackHumanoids));
    }

    [Fact]
    public void TheNoAutocastAiAttribute_AloneMakesATracker()
    {
        using var kit = WithHandlers();
        (Player player, _) = kit.AddPlayer(1);

        Track(kit, player, TrackBeasts);
        Track(kit, player, MountedTracker);

        Assert.False(kit.System.HasAura(player, TrackBeasts));
        Assert.True(kit.System.HasAura(player, MountedTracker));
    }
}
