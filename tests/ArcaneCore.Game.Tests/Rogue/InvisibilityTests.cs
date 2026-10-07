using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Rogue;

public sealed class InvisibilityTests
{
    private const uint Invisible = 950180, Detection = 950190, Weak = 950191, OtherType = 950181, Invalid = 950182;
    private static SpellInfo Aura(uint id, AuraType type, int amount, int channel = 0)
        => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: channel)) with
        { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 };

    [Fact]
    public void ApplyDetectAndRemovalImmediatelyChangeActualMapVisibility()
    {
        using var kit = Kit();
        var (viewer, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(2, 5);
        Assert.Contains(target.Guid, viewer.VisibleObjects);
        Cast(kit, target, Invisible);
        Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);
        Assert.Equal(0x40, target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40);
        Cast(kit, viewer, Weak);
        Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);
        Cast(kit, viewer, Detection);
        Assert.Contains(target.Guid, viewer.VisibleObjects);
        kit.System.RemoveAuras(viewer, Detection);
        Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);
        kit.System.RemoveAuras(target, Invisible);
        Assert.Contains(target.Guid, viewer.VisibleObjects);
        Assert.Equal(0, target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40);
    }

    [Fact]
    public void OverlappingTypesAndLastRemovalUseSurvivingAuraState()
    {
        using var kit = Kit();
        var (viewer, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(2, 5);
        Cast(kit, target, Invisible);
        Cast(kit, target, OtherType);
        Cast(kit, viewer, OtherType);
        Assert.True(Invisibility.CanDetect(kit.System, viewer, target));
        Assert.Contains(target.Guid, viewer.VisibleObjects);
        kit.System.RemoveAuras(viewer, OtherType);
        Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);
        kit.System.RemoveAuras(target, Invisible);
        Assert.Equal(2u, Invisibility.Mask(kit.System, target, AuraType.ModInvisibility));
        Assert.Equal(0x40, target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40);
        kit.System.RemoveAuras(target, OtherType);
        Assert.Equal(0u, Invisibility.Mask(kit.System, target, AuraType.ModInvisibility));
        Assert.Contains(target.Guid, viewer.VisibleObjects);
    }

    [Fact]
    public void RestoredInvisibilityAndDetectionRebuildVisibilityFromSavedAuras()
    {
        using var kit = Kit();
        var (viewer, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(2, 5);
        Cast(kit, target, Invisible);
        Cast(kit, viewer, Detection);
        var savedHidden = kit.System.CaptureState(target, 1_800_000_000_000);
        var savedDetect = kit.System.CaptureState(viewer, 1_800_000_000_000);
        kit.System.RemoveAuras(target, Invisible);
        kit.System.RemoveAuras(viewer, Detection);
        Assert.Single(kit.System.RestoreAuras(target, savedHidden.Auras, 1_800_000_000_000));
        Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);
        Assert.Equal(0x40, target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & Invisibility.PlayerGlow);
        Assert.Single(kit.System.RestoreAuras(viewer, savedDetect.Auras, 1_800_000_000_000));
        Assert.Contains(target.Guid, viewer.VisibleObjects);
    }

    [Fact]
    public void EnteringInvisibilityInterruptsFlagAuraAndLastRemovalClearsGlow()
    {
        const uint flag = 950183;
        using var kit = Kit();
        kit.System.Store = new SpellStore([.. kit.System.Store.All,
            Aura(flag, AuraType.Dummy, 1) with { AuraInterruptFlags = (SpellAuraInterruptFlags)AuraInterruptMask.StealthInvisibility }], [], []);
        var (target, _) = kit.AddPlayer(2);
        Cast(kit, target, flag);
        Assert.True(kit.System.HasAura(target, flag));
        Cast(kit, target, Invisible);
        Assert.False(kit.System.HasAura(target, flag));
        kit.System.RemoveAuras(target, Invisible);
        Assert.Equal(0, target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & Invisibility.PlayerGlow);
    }

    [Fact]
    public void CreatureDetectionAndWorldBossUseTheSameInvisibilityContract()
    {
        using var kit = Kit();
        var (target, _) = kit.AddPlayer(2, 5);
        var creature = new Creature(100, new CreatureTemplate { Entry = 100, Name = "detector" }, null, CreatureContent.Empty, new Random(1));
        var boss = new Creature(101, new CreatureTemplate { Entry = 101, Name = "boss detector", Rank = (uint)CreatureRank.WorldBoss },
            null, CreatureContent.Empty, new Random(1));
        var services = new StealthServices(kit.System, new StealthRegistry(), new StealthOptions());
        Cast(kit, target, Invisible);
        Assert.False(services.CanCreatureSee(creature, target, out _));
        Assert.True(services.CanCreatureSee(boss, target, out _));
        Cast(kit, creature, Detection);
        Assert.True(services.CanCreatureSee(creature, target, out _));
    }

    [Fact]
    public void InvalidTypeDoesNotWrapMaskAndDetectedInvisibilityStillRespectsStealth()
    {
        using var kit = Kit();
        var (viewer, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(2, 30);
        Cast(kit, target, Invalid);
        Assert.Equal(0u, Invisibility.Mask(kit.System, target, AuraType.ModInvisibility));
        Cast(kit, target, Invisible);
        Cast(kit, viewer, Detection);
        var registry = new StealthRegistry();
        registry.SetVisibility(target, StealthVisibility.Stealth);
        var rule = new StealthVisibilityRule(kit.System, registry);
        Assert.False(rule.CanSee(viewer, target, alreadyVisible: false, detect: true));
    }

    private static SpellTestKit Kit()
    {
        var kit = new SpellTestKit(Aura(Invisible, AuraType.ModInvisibility, 10),
            Aura(Detection, AuraType.ModInvisibilityDetection, 10), Aura(Weak, AuraType.ModInvisibilityDetection, 5),
            Aura(OtherType, AuraType.ModInvisibility, 10, 1), Aura(Invalid, AuraType.ModInvisibility, 10, 32));
        Invisibility.Register(kit.System);
        kit.World.GetMap(0).AddVisibilityRule(new StealthVisibilityRule(kit.System, new StealthRegistry()));
        return kit;
    }

    private static void Cast(SpellTestKit kit, Unit unit, uint id)
        => Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(unit, id, SpellCastTargets.ForSelf(), triggered: true));
}
