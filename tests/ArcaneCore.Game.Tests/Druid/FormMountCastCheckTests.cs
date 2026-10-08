using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// vmangos Spell::CheckCast: the mount spell form gate (Spell.cpp:6370-6391), dismount on cast (:5680-5692) and the target rule of
/// auras that shapeshifting or mounting cancels (:5446-5451).
/// </summary>
public sealed class FormMountCastCheckTests : IDisposable
{
    private const uint MountSpell = 900701;
    private const uint Fireball = 900702;
    private const uint AllowedWhileMounted = 900703;
    private const uint PassiveSpell = 900704;
    private const uint WaterWalking = 546;
    private const uint MountCancelled = 900705;
    private const uint AreaCancelled = 900706;

    private readonly SpellTestKit _kit;
    private readonly Player _player;
    private readonly Player _other;
    private readonly FakeSession _session;

    public FormMountCastCheckTests()
    {
        _kit = new SpellTestKit(
            Spell(MountSpell, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Mounted, misc: 12345)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(Fireball, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
            {
                Duration = new SpellDuration(5000, 0, 5000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(AllowedWhileMounted, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
            {
                Attributes = (SpellAttributes)0x01000000u,
                Duration = new SpellDuration(5000, 0, 5000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(PassiveSpell, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
            {
                Attributes = SpellAttributes.Passive,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(WaterWalking, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.WaterWalk)) with
            {
                AuraInterruptFlags = (SpellAuraInterruptFlags)0x8000u,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                Duration = new SpellDuration(600000, 0, 600000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(MountCancelled, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.WaterWalk)) with
            {
                AuraInterruptFlags = SpellAuraInterruptFlags.MountCancels,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                Duration = new SpellDuration(600000, 0, 600000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(AreaCancelled, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.WaterWalk) with { TargetB = SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc }) with
            {
                AuraInterruptFlags = (SpellAuraInterruptFlags)0x8000u,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                Duration = new SpellDuration(600000, 0, 600000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (_player, _session) = _kit.AddPlayer(1);
        (_other, _) = _kit.AddPlayer(2, 3, 0);
        _kit.System.RegisterCastCheck(new MountFormCastCheck());
        _kit.System.RegisterCastCheck(new DismountOnCastCheck());
        _kit.System.RegisterCastCheck(new ShiftedOrMountedTargetCastCheck(() => ShapeshiftFormCatalog.Retail));
    }

    public void Dispose() => _kit.Dispose();

    private SpellCastResult Cast(Player caster, uint spell, Unit? target = null)
    {
        _kit.Advance(1500);
        return _kit.System.CastSpell(caster, spell, target is null ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid), triggered: false);
    }

    private static void SetForm(Unit unit, byte form) => unit.SetByte(UpdateFields.UnitFieldBytes1, 2, form);

    private static void Mount(Unit unit) => unit.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 14337);

    private static bool IsMounted(Unit unit) => unit.GetUInt32(UpdateFields.UnitFieldMountdisplayid) != 0;

    // --- the mount form gate -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]    // cat
    [InlineData(3)]    // travel
    [InlineData(31)]   // moonkin
    [InlineData(5)]    // bear
    [InlineData(16)]   // ghost wolf
    public void MountSpell_InAShapeshiftedForm_FailsNotShapeshift(byte form)
    {
        SetForm(_player, form);

        Assert.Equal(SpellCastResult.NotShapeshift, Cast(_player, MountSpell));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]   // battle stance
    [InlineData(30)]   // stealth
    [InlineData(28)]   // shadowform
    public void MountSpell_InNoFormAStanceStealthOrShadowform_IsAllowed(byte form)
    {
        SetForm(_player, form);

        Assert.Equal(SpellCastResult.CastOk, Cast(_player, MountSpell));
    }

    [Fact]
    public void AMountSpellInADisallowedForm_IsRefusedForTriggeredCastsToo()
    {
        SetForm(_player, 1);

        Assert.Equal(SpellCastResult.NotShapeshift, _kit.System.CastSpell(_player, MountSpell, SpellCastTargets.ForSelf(), triggered: true));
    }

    // --- dismount on cast ----------------------------------------------------------------------------------------

    [Fact]
    public void CastingWhileMounted_Dismounts_ThenCasts_AndSendsTheDismountResult()
    {
        _kit.System.CastSpell(_player, MountSpell, SpellCastTargets.ForSelf(), triggered: true);
        Mount(_player);
        _session.Clear();

        Assert.Equal(SpellCastResult.CastOk, Cast(_player, Fireball));

        Assert.False(IsMounted(_player));
        Assert.False(_kit.System.HasAura(_player, MountSpell));                       // RemoveSpellsCausingAura(MOUNTED)
        Assert.True(_kit.System.HasAura(_player, Fireball));
        Assert.Single(Packets(_session, WorldOpcode.SmsgDismountresult));
    }

    [Fact]
    public void AllowWhileMountedPassiveAndTriggeredCasts_DoNotDismount()
    {
        Mount(_player);

        Assert.Equal(SpellCastResult.CastOk, Cast(_player, AllowedWhileMounted));
        Assert.True(IsMounted(_player));

        _kit.System.CastLearnedPassive(_player, PassiveSpell);
        Assert.True(IsMounted(_player));

        _kit.System.CastSpell(_player, Fireball, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(IsMounted(_player));

        Assert.Equal(SpellCastResult.CastOk, Cast(_player, Fireball));                // a plain cast does
        Assert.False(IsMounted(_player));
    }

    // --- the shifted or mounted target ---------------------------------------------------------------------------

    [Fact]
    public void WaterWalking_OnADruidInCat_FailsBadTargets_OnAWarriorInBattleStance_IsOk()
    {
        SetForm(_other, 1);
        Assert.Equal(SpellCastResult.BadTargets, Cast(_player, WaterWalking, _other));

        SetForm(_other, 17);
        Assert.Equal(SpellCastResult.CastOk, Cast(_player, WaterWalking, _other));
        _kit.System.RemoveAuras(_other, WaterWalking);

        SetForm(_other, 0);
        Assert.Equal(SpellCastResult.CastOk, Cast(_player, WaterWalking, _other));
    }

    [Fact]
    public void WaterWalking_OnASpirit_FailsBadTargets_OnAPriestInShadowform_IsOk()
    {
        // The client's SpellShapeshiftForm rows (patch.MPQ): Spirit of Redemption 32 has flags1 0 (shapeshifted),
        // Shadowform 28 has 0x9 (Stance, so not shapeshifted in vmangos Unit::IsShapeShifted).
        SetForm(_other, 32);
        Assert.Equal(SpellCastResult.BadTargets, Cast(_player, WaterWalking, _other));

        SetForm(_other, 28);
        Assert.Equal(SpellCastResult.CastOk, Cast(_player, WaterWalking, _other));
    }

    [Fact]
    public void AMountCancelledAura_OnAMountedTarget_FailsBadTargets()
    {
        Mount(_other);

        Assert.Equal(SpellCastResult.BadTargets, Cast(_player, MountCancelled, _other));

        _other.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
        Assert.Equal(SpellCastResult.CastOk, Cast(_player, MountCancelled, _other));
    }

    [Fact]
    public void AnAreaAura_IsNotRefusedByTheShiftedTargetRule()
    {
        SetForm(_other, 1);

        Assert.NotEqual(SpellCastResult.BadTargets, Cast(_player, AreaCancelled, _other));
    }
}
