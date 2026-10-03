using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Capturing and restoring cooldowns and auras across logout (SpellSystem.Persistence), including
/// the caster-ownership rule on reload (docs/integration/aura-caster-ownership.md).
/// </summary>
public sealed class SpellPersistenceTests
{
    private const uint Dot = 900500;
    private const uint Buff = 900501;
    private const uint CategorySpell = 900502;
    private const long Saved = 1_800_000_000_000;

    [Fact]
    public void CaptureState_SavesRunningCooldownsAndSaveableAuras_Only()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(player, CooldownSpell, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, CategorySpell, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, Passive, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, Buff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(enemy, Dot, SpellCastTargets.ForUnit(player.Guid), triggered: true);
        kit.Advance(2500);

        SpellStateSnapshot state = kit.System.CaptureState(player, Saved);

        Assert.Contains(new PersistedCooldown(SpellCooldownKind.Spell, CooldownSpell, Saved + 7_500), state.Cooldowns);
        Assert.Contains(new PersistedCooldown(SpellCooldownKind.Category, 77, Saved + 17_500), state.Cooldowns);
        Assert.Equal(2, state.Auras.Count);
        Assert.DoesNotContain(state.Auras, a => a.SpellId == Passive);
        PersistedAura buff = state.Auras.Single(a => a.SpellId == Buff);
        Assert.Equal((player.Guid, 27_500, 30_000, (byte)0b001, Saved), (buff.CasterGuid, buff.RemainingMs, buff.MaxDurationMs, buff.EffectMask, Saved));
        Assert.Equal(15, buff.Amounts[0]);
        PersistedAura dot = state.Auras.Single(a => a.SpellId == Dot);
        Assert.Equal(enemy.Guid, dot.CasterGuid);
        Assert.Equal(500, dot.PeriodicTimers[0]); // 3000 amplitude, 2500 elapsed
        Assert.Equal(SpellStateSnapshot.Empty, kit.System.CaptureState(TestWorld.CreatePlayer(99, 0, 0, new FakeSession(99)), Saved));
    }

    [Fact]
    public void RestoreCooldowns_SubtractsWallClockTime_AndDropsExpiredAndUnknownRows()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        int restored = kit.System.RestoreCooldowns(player,
        [
            new PersistedCooldown(SpellCooldownKind.Spell, CooldownSpell, Saved + 10_000),
            new PersistedCooldown(SpellCooldownKind.Spell, 424242, Saved + 10_000),
            new PersistedCooldown(SpellCooldownKind.Category, 77, Saved + 6_000),
            new PersistedCooldown(SpellCooldownKind.Spell, InstantHeal, Saved + 1_000),
        ], Saved + 4_000);

        Assert.Equal(2, restored);
        Assert.False(kit.System.IsSpellReady(player, kit.Store.Get(CooldownSpell)!));
        Assert.False(kit.System.IsSpellReady(player, kit.Store.Get(CategorySpell)!));
        Assert.True(kit.System.IsSpellReady(player, kit.Store.Get(InstantHeal)!));
        kit.Advance(2000);
        Assert.True(kit.System.IsSpellReady(player, kit.Store.Get(CategorySpell)!));
        kit.Advance(4000);
        Assert.True(kit.System.IsSpellReady(player, kit.Store.Get(CooldownSpell)!));
    }

    [Fact]
    public void RestoreAuras_HarmfulAurasLoseOfflineTime_BeneficialAndPermanentKeepIt()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PersistedAura buff = Aura(Buff, player.Guid, remaining: 20_000, amount: 15);
        PersistedAura dot = Aura(Dot, new ObjectGuid(2), remaining: 9_000, amount: 4) with { PeriodicTimers = [1_200, 0, 0] };
        PersistedAura expiredDot = Aura(Dot, new ObjectGuid(3), remaining: 3_000, amount: 4);
        PersistedAura permanent = Aura(Buff, new ObjectGuid(4), remaining: -1, amount: 1) with { MaxDurationMs = -1, SpellId = PartyAura };

        IReadOnlyList<SpellAuraHolder> holders = kit.System.RestoreAuras(player,
            [buff, dot, expiredDot, Aura(424242, player.Guid, 5_000, 1), Aura(Passive, player.Guid, 5_000, 1), permanent], Saved + 5_000);

        Assert.Equal(3, holders.Count);
        SpellAuraHolder restoredBuff = holders.Single(h => h.Spell.Id == Buff);
        Assert.Equal(20_000, restoredBuff.Duration);
        Assert.Equal(15, restoredBuff.Auras[0]!.Amount);
        SpellAuraHolder restoredDot = holders.Single(h => h.Spell.Id == Dot);
        Assert.Equal(4_000, restoredDot.Duration);
        Assert.Equal(1_200, restoredDot.Auras[0]!.PeriodicTimer);
        Assert.True(holders.Single(h => h.Spell.Id == PartyAura).IsPermanent);
        Assert.NotEqual(SpellAuraHolder.NoSlot, restoredBuff.Slot);
        Assert.Equal(Buff, player.GetUInt32(UpdateFields.UnitFieldAura + restoredBuff.Slot));
    }

    [Fact]
    public void RestoreAuras_KeepsStacksAndCharges_ClampedToTheSpell_AndSkipsNonAuraEffects()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        SpellAuraHolder holder = Assert.Single(kit.System.RestoreAuras(player,
            [Aura(Buff, player.Guid, 10_000, 45) with { StackAmount = 9, Charges = 2, EffectMask = 0b111, Amounts = [45, 7, 7] }], Saved));

        Assert.Equal(3, holder.StackAmount);
        Assert.Equal(2, holder.Charges);
        Assert.Equal(45, holder.Auras[0]!.Amount);
        Assert.Null(holder.Auras[1]);

        Assert.Empty(kit.System.RestoreAuras(player, [Aura(Buff, player.Guid, 10_000, 1) with { EffectMask = 0b010 }], Saved));
    }

    [Fact]
    public void RestoredForeignAura_NeverRegainsTheCaster_EvenWhenTheSameGuidIsOnline()
    {
        using var kit = Kit();
        var sink = new AttributionSink();
        kit.System.Damage = sink;
        (Player caster, _) = kit.AddPlayer(2, 2); // the original caster, online with the saved GUID
        (Player player, _) = kit.AddPlayer(1);

        SpellAuraHolder dot = Assert.Single(kit.System.RestoreAuras(player, [Aura(Dot, caster.Guid, 9_000, 4)], Saved));

        Assert.Equal(caster.Guid, dot.CasterGuid); // provenance kept
        Assert.False(SpellSystem.HasLiveCasterOwnership(dot));
        Assert.Same(player, kit.System.ResolveAuraActor(dot));
        kit.Advance(3000);
        Assert.Equal(56u, player.Health);
        Assert.Same(player, Assert.Single(sink.Casters));

        // The caster casts the same DoT again: it replaces the orphan instead of stacking onto it.
        kit.System.CastSpell(caster, Dot, SpellCastTargets.ForUnit(player.Guid), triggered: true);
        SpellAuraHolder fresh = Assert.Single(kit.System.GetAuras(player));
        Assert.NotSame(dot, fresh);
        Assert.True(dot.IsRemoved);
        Assert.Equal(1, fresh.StackAmount);
        Assert.Same(caster, kit.System.ResolveAuraActor(fresh));
    }

    [Fact]
    public void RestoredSelfCastAura_GetsAFreshToken_KeepsAttribution_AndStacksWithANewCast()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        SpellAuraHolder buff = Assert.Single(kit.System.RestoreAuras(player, [Aura(Buff, player.Guid, 10_000, 15)], Saved));

        Assert.True(SpellSystem.HasLiveCasterOwnership(buff));
        Assert.Same(player, kit.System.ResolveAuraActor(buff));
        kit.System.CastSpell(player, Buff, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Same(buff, Assert.Single(kit.System.GetAuras(player)));
        Assert.Equal(2, buff.StackAmount);
        Assert.Equal(30, buff.Auras[0]!.Amount);
    }

    [Fact]
    public void CaptureThenRestore_RoundTripsThroughANewPlayerObject()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, Buff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, CooldownSpell, SpellCastTargets.ForSelf(), triggered: true);
        SpellStateSnapshot state = kit.System.CaptureState(player, Saved);
        kit.System.RemoveUnit(player);
        kit.World.RemovePlayer(player);

        (Player again, _) = kit.AddPlayer(1);
        kit.System.RestoreCooldowns(again, state.Cooldowns, Saved + 1_000);
        kit.System.RestoreAuras(again, state.Auras, Saved + 1_000);

        Assert.Single(kit.System.GetActiveCooldowns(again));
        SpellAuraHolder buff = Assert.Single(kit.System.GetAuras(again));
        Assert.Equal(30_000, buff.Duration);
        Assert.Same(again, kit.System.ResolveAuraActor(buff));
    }

    private const uint PartyAura = 900503;

    private static SpellTestKit Kit() => new(
        Spell(Dot, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000))
            with { Duration = new SpellDuration(12_000, 0, 12_000), RangeIndex = 4, Range = new SpellRange(0, 30), SpellVisual = 1, StartRecoveryCategory = 0 },
        Spell(Buff, Effect(SpellEffectName.ApplyAura, 15, aura: AuraType.Dummy), Effect(SpellEffectName.Dummy, 7))
            with { Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1, StackAmount = 3, ProcCharges = 1, StartRecoveryCategory = 0 },
        Spell(CategorySpell, Effect(SpellEffectName.Dummy, 0)) with { Category = 77, CategoryRecoveryTime = 20_000, StartRecoveryCategory = 0 },
        Spell(PartyAura, Effect(SpellEffectName.ApplyAreaAuraParty, 1, aura: AuraType.Dummy) with { Radius = 30 })
            with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 });

    private static PersistedAura Aura(uint spell, ObjectGuid caster, int remaining, int amount) => new()
    {
        SpellId = spell,
        CasterGuid = caster,
        CasterLevel = 60,
        MaxDurationMs = remaining < 0 ? -1 : 30_000,
        RemainingMs = remaining,
        EffectMask = 0b001,
        Amounts = [amount, 0, 0],
        PeriodicTimers = [0, 0, 0],
        SavedAtUnixMs = Saved,
    };

    private sealed class AttributionSink : IDamageSink
    {
        private readonly HealthOnlyDamageSink _inner = new();

        public List<Unit> Casters { get; } = [];

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        {
            Casters.Add(caster);
            return _inner.DealSpellDamage(caster, victim, spell, damage, periodic);
        }

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => _inner.Heal(caster, target, spell, amount);
    }
}
