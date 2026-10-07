using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class DeathAuraLifecycleTests
{
    private const uint Mark = 962000;
    private const uint Other = 962001;
    private const uint MindVision = 962002;

    [Fact]
    public void CasterDeath_RemovesItsMarkAndHandlers_LeavesOtherCastersAndOrdinaryAuras()
    {
        using var kit = Kit();
        var (hunter, _) = kit.AddPlayer(1);
        var (otherHunter, _) = kit.AddPlayer(2, 1);
        var (target, _) = kit.AddPlayer(3, 2);
        Cast(kit, hunter, target, Mark);
        Cast(kit, otherHunter, target, Mark);
        Cast(kit, hunter, target, Other);
        SpellAuraHolder old = Assert.Single(kit.System.GetAuras(target), h => h.Spell.Id == Mark && h.CasterGuid == hunter.Guid);

        Die(kit, hunter);

        Assert.True(old.IsRemoved);
        Assert.Single(kit.System.GetAuras(target), h => h.Spell.Id == Mark && h.CasterGuid == otherHunter.Guid);
        Assert.True(kit.System.HasAura(target, Other));
    }

    [Fact]
    public void CasterDeath_OnlyMarkRemovalClearsTheTrackingFieldAndStatContribution()
    {
        using var kit = Kit();
        var (hunter, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(3, 2);
        int initial = target.GetInt32(UpdateFields.UnitFieldStat0);
        Cast(kit, hunter, target, Mark);
        Assert.Equal(initial + 7, target.GetInt32(UpdateFields.UnitFieldStat0));
        Assert.NotEqual(0u, target.GetUInt32(UpdateFields.UnitDynamicFlags) & UnitDynFlags.TrackUnit);

        Die(kit, hunter);

        Assert.False(kit.System.HasAura(target, Mark));
        Assert.Equal(initial, target.GetInt32(UpdateFields.UnitFieldStat0));
        Assert.Equal(0u, target.GetUInt32(UpdateFields.UnitDynamicFlags) & UnitDynFlags.TrackUnit);
    }

    [Fact]
    public void MarkRemoval_WaitsForTargetSettlement_ThenRemovesTheExactHolder()
    {
        using var kit = Kit();
        var (hunter, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(3, 2);
        Cast(kit, hunter, target, Mark);
        SpellAuraHolder holder = Assert.Single(kit.System.GetAuras(target));
        Guid operation = Guid.NewGuid();
        Assert.True(target.BeginQuestSettlement(operation));

        Die(kit, hunter);
        kit.Advance(100);
        Assert.False(holder.IsRemoved);
        Assert.True(target.EndQuestSettlement(operation));
        kit.Advance(100);

        Assert.True(holder.IsRemoved);
        Assert.False(kit.System.HasAura(target, Mark));
    }

    [Fact]
    public void DelayedDeathNotification_WaitsForCasterSettlementBeforeRemovingItsMark()
    {
        using var kit = Kit();
        var (hunter, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(3, 2);
        Cast(kit, hunter, target, Mark);
        hunter.Map!.FindUpdater<MapCombat>()!.Kill(null, hunter);
        Guid operation = Guid.NewGuid();
        Assert.True(hunter.BeginQuestSettlement(operation));

        kit.System.OnUnitDied(hunter);
        kit.Advance(100);
        Assert.True(kit.System.HasAura(target, Mark));
        Assert.True(hunter.EndQuestSettlement(operation));
        kit.Advance(100);

        Assert.False(kit.System.HasAura(target, Mark));
    }

    [Fact]
    public void DeathOfReplacementCaster_DoesNotRemoveAnOldOwnersMark()
    {
        using var kit = Kit();
        var (oldHunter, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(3, 2);
        Cast(kit, oldHunter, target, Mark);
        kit.System.RemoveUnit(oldHunter);
        kit.World.RemovePlayer(oldHunter);
        var (replacement, _) = kit.AddPlayer(1);

        Die(kit, replacement);

        Assert.True(kit.System.HasAura(target, Mark));
    }

    [Fact]
    public void OrphanedRestoredMark_KeepsProvenanceWithoutRegainingCasterDeathOwnership()
    {
        using var kit = Kit();
        var (hunter, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(3, 2);
        Cast(kit, hunter, target, Mark);
        SpellStateSnapshot saved = kit.System.CaptureState(target, 1_800_000_000_000);
        kit.System.RemoveAuras(target, Mark);
        SpellAuraHolder restored = Assert.Single(kit.System.RestoreAuras(target, saved.Auras, 1_800_000_000_000));
        Assert.False(SpellSystem.HasLiveCasterOwnership(restored));

        Die(kit, hunter);

        Assert.False(restored.IsRemoved);
        Assert.Equal(hunter.Guid, restored.CasterGuid);
    }

    [Fact]
    public void ReleasedDebt_DoesNotRemoveAReplacementHolderFromAnotherHunter()
    {
        using var kit = Kit();
        var (hunter, _) = kit.AddPlayer(1);
        var (otherHunter, _) = kit.AddPlayer(2, 1);
        var (target, _) = kit.AddPlayer(3, 2);
        Cast(kit, hunter, target, Mark);
        SpellAuraHolder old = Assert.Single(kit.System.GetAuras(target));
        Guid operation = Guid.NewGuid();
        Assert.True(target.BeginQuestSettlement(operation));
        Die(kit, hunter);
        Assert.True(target.EndQuestSettlement(operation));
        kit.System.RemoveAuras(target, Mark);
        Cast(kit, otherHunter, target, Mark);
        SpellAuraHolder fresh = Assert.Single(kit.System.GetAuras(target));

        kit.System.RemoveUnit(hunter);
        kit.World.RemovePlayer(hunter);
        kit.AddPlayer(1); // a replacement session must not gain the original holder's debt
        kit.Advance(100);

        Assert.True(old.IsRemoved);
        Assert.False(fresh.IsRemoved);
        Assert.Equal(otherHunter.Guid, fresh.CasterGuid);
    }

    [Fact]
    public void MarkOnAnotherMap_AndNonHunterStalkedAura_AreNotRemovedOnCasterDeath()
    {
        using var kit = Kit();
        var (hunter, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(3, 2);
        var (local, _) = kit.AddPlayer(4, 2);
        Cast(kit, hunter, target, Mark);
        Cast(kit, hunter, local, MindVision);
        kit.World.RemovePlayer(target);
        target.MapId = 1;
        kit.World.AddPlayer(target);
        kit.World.RunTick(0);

        Die(kit, hunter);

        Assert.True(kit.System.HasAura(target, Mark));
        Assert.True(kit.System.HasAura(local, MindVision));
    }

    [Fact]
    public void LivingCasterDeathNotification_DoesNotRemoveItsMark()
    {
        using var kit = Kit();
        var (hunter, _) = kit.AddPlayer(1);
        var (target, _) = kit.AddPlayer(3, 2);
        Cast(kit, hunter, target, Mark);
        kit.System.OnUnitDied(hunter);
        Assert.True(kit.System.HasAura(target, Mark));
    }

    private static SpellTestKit Kit()
    {
        var kit = new SpellTestKit(
            Targeted(Mark,
                Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.Unit, AuraType.ModStalked),
                Effect(SpellEffectName.ApplyAura, 7, SpellImplicitTarget.Unit, AuraType.ModStat, misc: 0))
                with { SpellFamilyName = 9, SpellFamilyFlags = 1UL << 10 },
            Targeted(Other, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.Unit, AuraType.Dummy)),
            Targeted(MindVision, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.Unit, AuraType.ModStalked))
                with { SpellFamilyName = 6, SpellFamilyFlags = 1UL << 26 });
        RangedHandlers.Register(kit.System);
        new StatAuras().Register(kit.System);
        var relations = new FakeRelations();
        relations.Hostile.Add(new ObjectGuid(3));
        relations.Hostile.Add(new ObjectGuid(4));
        kit.System.Relations = relations;
        return kit;
    }

    private static SpellInfo Targeted(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with
    {
        Duration = new SpellDuration(120_000, 0, 120_000), RangeIndex = 4, Range = new SpellRange(0, 100),
        StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1,
        Attributes = SpellAttributes.AuraIsDebuff,
    };

    private static void Cast(SpellTestKit kit, Player caster, Player target, uint spell)
        => Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), true));

    private static void Die(SpellTestKit kit, Player hunter)
    {
        hunter.Map!.FindUpdater<MapCombat>()!.Kill(null, hunter);
        kit.System.OnUnitDied(hunter);
    }
}
