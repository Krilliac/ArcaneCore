using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// character_aura save and load rules of vmangos: Player::SaveAura (Player.cpp:16618-16675) and Player::LoadAura
/// (Player.cpp:15356-15425). Only the SQLite-free in-memory snapshot is exercised here (CaptureState/RestoreAuras); the
/// character_aura table and its provider theories are unchanged by this slice (hosted CI runs the MariaDB and PostgreSQL ones).
/// </summary>
public sealed class AuraPersistenceFidelityTests
{
    private const uint Dot = 945001;
    private const uint Deserter = 945002;
    private const uint LeaveWorldBuff = 945003;
    private const uint EnterWorldBuff = 945004;
    private const uint CharmAura = 945005;
    private const uint NoChargeBuff = 945006;
    private const uint Plain = 945007;
    private const long Saved = 1_800_000_000_000;

    private static SpellInfo Timed(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit Kit() => new(
        Timed(Dot, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with { Attributes = SpellAttributes.AuraIsDebuff },
        Timed(Deserter, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with { AttributesEx4 = 0x4, Attributes = SpellAttributes.AuraIsDebuff },
        Timed(LeaveWorldBuff, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with { AuraInterruptFlags = SpellAuraInterruptFlags.LeaveWorld },
        Timed(EnterWorldBuff, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with { AuraInterruptFlags = (SpellAuraInterruptFlags)0x00400000 },
        Timed(CharmAura, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.ModCharm)),
        Timed(NoChargeBuff, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.Dummy)),
        Timed(Plain, Effect(SpellEffectName.ApplyAura, 7, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with { StackAmount = 3, ProcCharges = 2 });

    private static PersistedAura Saved_(uint spell, ObjectGuid caster, int remaining) => new()
    {
        SpellId = spell,
        CasterGuid = caster,
        CasterLevel = 60,
        MaxDurationMs = 60_000,
        RemainingMs = remaining,
        EffectMask = 0b001,
        Amounts = [1, 0, 0],
        PeriodicTimers = [0, 0, 0],
        SavedAtUnixMs = Saved,
    };

    [Fact]
    public void HarmfulAura_DoesNotLoseOfflineTime_ByDefault()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        SpellAuraHolder dot = Assert.Single(kit.System.RestoreAuras(player, [Saved_(Dot, new ObjectGuid(2), 40_000)], Saved + 3_600_000));

        Assert.Equal(40_000, dot.Duration); // HEAD subtracted the hour offline and dropped the aura
    }

    [Fact]
    public void Deserter_LikeAuraWithExpiresOffline_LosesOfflineTime_AndExpires()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        SpellAuraHolder kept = Assert.Single(kit.System.RestoreAuras(player, [Saved_(Deserter, player.Guid, 40_000)], Saved + 10_000));
        Assert.Equal(30_000, kept.Duration);

        kit.System.RemoveAuras(player, Deserter);
        Assert.Empty(kit.System.RestoreAuras(player, [Saved_(Deserter, player.Guid, 40_000)], Saved + 40_000)); // exactly used up: dropped (duration <= offline)
    }

    [Fact]
    public void CmangosSwitch_RestoresTheHarmfulOfflineCountdown()
    {
        using SpellTestKit kit = Kit();
        kit.System.AuraOptions = new AuraOptions { HarmfulAurasExpireOffline = true };
        (Player player, _) = kit.AddPlayer(1);

        SpellAuraHolder dot = Assert.Single(kit.System.RestoreAuras(player, [Saved_(Dot, new ObjectGuid(2), 40_000)], Saved + 10_000));

        Assert.Equal(30_000, dot.Duration);
    }

    [Fact]
    public void AurasCancelledByLeavingOrEnteringTheWorld_AreNotCaptured()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, LeaveWorldBuff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, EnterWorldBuff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, NoChargeBuff, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(3, kit.System.GetAuras(player).Count);

        SpellStateSnapshot state = kit.System.CaptureState(player, Saved);

        Assert.Equal([NoChargeBuff], state.Auras.Select(a => a.SpellId).ToArray());
    }

    [Fact]
    public void CharmAura_IsNotCaptured()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, CharmAura, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Single(kit.System.GetAuras(player));

        Assert.Empty(kit.System.CaptureState(player, Saved).Auras);
    }

    [Fact]
    public void ChargesAreZeroed_ForASpellWithoutProcCharges_AndKept_ForOneWith()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        IReadOnlyList<SpellAuraHolder> holders = kit.System.RestoreAuras(player,
        [
            Saved_(NoChargeBuff, player.Guid, 30_000) with { Charges = 5 },
            Saved_(Plain, player.Guid, 30_000) with { Charges = 2, StackAmount = 9 },
        ], Saved);

        Assert.Equal(0, holders.Single(h => h.Spell.Id == NoChargeBuff).Charges);
        SpellAuraHolder plain = holders.Single(h => h.Spell.Id == Plain);
        Assert.Equal(2, plain.Charges);
        Assert.Equal(3, plain.StackAmount); // clamped to the spell's StackAmount
    }
}
