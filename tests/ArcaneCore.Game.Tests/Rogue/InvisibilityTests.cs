using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>vmangos Unit.cpp:6401-6439,6502-6541 and SpellAuras.cpp:3711-3779.</summary>
public sealed class InvisibilityTests
{
    private const uint Invisibility = 911001;
    private const uint SecondInvisibility = 911002;
    private const uint StrongInvisibility = 911003;
    private const uint DetectWeak = 911004;
    private const uint DetectStrong = 911005;
    private const uint Stealth = 911006;

    [Fact]
    public void Invisibility_HidesUntilSameTypeOrSufficientDetection_AndRestoresOnLastRemoval()
    {
        static SpellInfo Permanent(uint id, AuraType aura, int amount, int type) =>
            Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: aura, misc: type)) with
            { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0 };

        using var kit = new SpellTestKit(
            Permanent(Invisibility, AuraType.ModInvisibility, 10, 0),
            Permanent(SecondInvisibility, AuraType.ModInvisibility, 10, 0),
            Permanent(StrongInvisibility, AuraType.ModInvisibility, 20, 0),
            Permanent(DetectWeak, AuraType.ModInvisibilityDetection, 9, 0),
            Permanent(DetectStrong, AuraType.ModInvisibilityDetection, 20, 0),
            Permanent(Stealth, AuraType.ModStealth, 5, 0));
        var registry = new StealthRegistry();
        kit.System.RegisterAura(AuraType.ModInvisibility, InvisibilityAuras.InvisibilityHandler(registry));
        kit.System.RegisterAura(AuraType.ModInvisibilityDetection, InvisibilityAuras.DetectionHandler());
        kit.System.RegisterAura(AuraType.ModStealth, StealthAuras.Handler(registry));
        var map = kit.World.GetMap(0);
        map.AddVisibilityRule(new StealthVisibilityRule(kit.System, registry));
        (Player viewer, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 5);
        Assert.Contains(target.Guid, viewer.VisibleObjects);

        kit.System.CastSpell(target, Invisibility, SpellCastTargets.ForSelf(), triggered: true);
        Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);
        Assert.Equal(0x40, target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40);

        kit.System.CastSpell(viewer, DetectWeak, SpellCastTargets.ForSelf(), triggered: true);
        Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);
        kit.System.CastSpell(viewer, DetectStrong, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Contains(target.Guid, viewer.VisibleObjects);
        kit.System.RemoveAuras(viewer, DetectStrong);
        Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);

        kit.System.CastSpell(viewer, StrongInvisibility, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Contains(target.Guid, viewer.VisibleObjects);
        kit.System.CastSpell(target, SecondInvisibility, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.RemoveAuras(target, Invisibility);
        Assert.Equal(0x40, target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40);
        kit.System.RemoveAuras(target, SecondInvisibility);
        Assert.Equal(0, target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40);
        Assert.Equal(StealthVisibility.On, registry.VisibilityOf(target));

        kit.System.CastSpell(target, Invisibility, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(target, Stealth, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(StealthVisibility.Stealth, registry.VisibilityOf(target));
        kit.System.RemoveAuras(target, Stealth);
        Assert.Equal(StealthVisibility.Invisibility, registry.VisibilityOf(target));
        kit.System.RemoveAuras(target, Invisibility);
        Assert.Equal(StealthVisibility.On, registry.VisibilityOf(target));
    }

    [Fact]
    public void AnInvisiblePetOrCharmedUnit_IsAlwaysVisibleToItsOwnerOrCharmer_ButHiddenFromEveryoneElse()
    {
        // vmangos Unit::IsVisibleForOrDetect (Unit.cpp:6359-6361): "always seen by owner", ahead of every stealth and invisibility test.
        static SpellInfo Permanent(uint id, AuraType aura, int amount, int type) =>
            Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: aura, misc: type)) with
            { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0 };

        using var kit = new SpellTestKit(Permanent(Invisibility, AuraType.ModInvisibility, 10, 0));
        var registry = new StealthRegistry();
        kit.System.RegisterAura(AuraType.ModInvisibility, InvisibilityAuras.InvisibilityHandler(registry));
        var rule = new StealthVisibilityRule(kit.System, registry);
        (Player owner, _) = kit.AddPlayer(1);
        (Player stranger, _) = kit.AddPlayer(2, 5);
        var pet = new CombatTestUnit(10);
        pet.Spawn(kit.World.GetMap(0), 1f, 0, 83.5f, MathF.PI);
        pet.SetUInt64(UpdateFields.UnitFieldSummonedby, owner.Guid.Value);
        kit.System.CastSpell(pet, Invisibility, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(rule.CanSee(owner, pet, alreadyVisible: false, detect: true));
        Assert.False(rule.CanSee(stranger, pet, alreadyVisible: false, detect: true));

        // A charmer wins over the owner field the same way (GetCharmerOrOwnerGuid).
        pet.SetUInt64(UpdateFields.UnitFieldSummonedby, 0);
        pet.SetUInt64(UpdateFields.UnitFieldCharmedby, stranger.Guid.Value);
        Assert.True(rule.CanSee(stranger, pet, alreadyVisible: false, detect: true));
        Assert.False(rule.CanSee(owner, pet, alreadyVisible: false, detect: true));
    }

    [Fact]
    public void ACreature_DoesNotSeeAnInvisiblePlayerAtPointBlank_UntilItSharesTheTypeOrDetectsIt()
    {
        static SpellInfo Permanent(uint id, AuraType aura, int amount, int type) =>
            Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: aura, misc: type)) with
            { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0 };

        using var kit = new SpellTestKit(
            Permanent(Invisibility, AuraType.ModInvisibility, 10, 0),
            Permanent(DetectStrong, AuraType.ModInvisibilityDetection, 20, 0));
        var registry = new StealthRegistry();
        kit.System.RegisterAura(AuraType.ModInvisibility, InvisibilityAuras.InvisibilityHandler(registry));
        kit.System.RegisterAura(AuraType.ModInvisibilityDetection, InvisibilityAuras.DetectionHandler());
        var services = new StealthServices(kit.System, registry, StealthOptions.Default);
        (Player target, _) = kit.AddPlayer(1);
        var mob = new CombatTestUnit(10);
        mob.Spawn(kit.World.GetMap(0), 1f, 0, 83.5f, MathF.PI);

        Assert.True(services.CanCreatureSee(mob, target, out _));
        kit.System.CastSpell(target, Invisibility, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(services.CanCreatureSee(mob, target, out _));

        kit.System.CastSpell(mob, DetectStrong, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(services.CanCreatureSee(mob, target, out _));
        kit.System.RemoveAuras(mob, DetectStrong);
        Assert.False(services.CanCreatureSee(mob, target, out _));

        kit.System.RemoveAuras(target, Invisibility);
        Assert.True(services.CanCreatureSee(mob, target, out _));
    }
}
