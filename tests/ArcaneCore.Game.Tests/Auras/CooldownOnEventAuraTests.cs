using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// Spell.dbc Attributes 0x02000000 (SPELL_ATTR_COOLDOWN_ON_EVENT, TrinityCore DISABLED_WHILE_ACTIVE) on a self aura: Stealth, Shadowmeld,
/// Prowl and the other "until used / until it fades" spells. The client keeps the button disabled while the aura lives; the cooldown only
/// starts when the aura ends, and the client learns that from SMSG_COOLDOWN_EVENT. Without it the button stays grey forever.
/// </summary>
public sealed class CooldownOnEventAuraTests : IDisposable
{
    private const uint StealthLike = 945001;
    private const uint PlainBuff = 945002;
    private const uint ShortStealth = 945003;
    private const uint Hit = 945004;
    private const AuraType Probe = SpellHandlerModuleTests.FixtureAura;
    private const uint CooldownMs = 10_000;

    private readonly SpellTestKit _kit;
    private readonly Player _player;
    private readonly Player _enemy;
    private readonly FakeSession _session;

    public CooldownOnEventAuraTests()
    {
        _kit = new SpellTestKit(
            Stealthy(StealthLike, 60_000),
            Stealthy(ShortStealth, 3_000),
            Spell(PlainBuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, Probe)) with
            {
                Duration = new SpellDuration(60_000, 0, 60_000),
                SpellVisual = 1,
                RecoveryTime = CooldownMs,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(Hit, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
            {
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (_player, _session) = _kit.AddPlayer(1);
        (_enemy, _) = _kit.AddPlayer(2, 2);
        _kit.Spellbook.Teach(_player, StealthLike, ShortStealth, PlainBuff);
    }

    public void Dispose() => _kit.Dispose();

    private static SpellInfo Stealthy(uint id, int durationMs) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, Probe)) with
    {
        Attributes = SpellAttributes.CooldownOnEvent,
        AuraInterruptFlags = SpellAuraInterruptFlags.Damage,
        Duration = new SpellDuration(durationMs, 0, durationMs),
        SpellVisual = 1,
        RecoveryTime = CooldownMs,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private SpellCastResult Cast(uint spell) => _kit.System.CastSpell(_player, spell, SpellCastTargets.ForSelf(), triggered: false);

    private bool Ready(uint spell) => _kit.System.IsSpellReady(_player, _kit.Store.Get(spell)!);

    private List<uint> CooldownEvents(uint spell)
        => Packets(_session, WorldOpcode.SmsgCooldownEvent)
            .Where(p => BinaryPrimitives.ReadUInt32LittleEndian(p) == spell)
            .Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p))
            .ToList();

    private void AssertCooldownStartedNow(uint spell)
    {
        Assert.Single(CooldownEvents(spell));
        Assert.False(Ready(spell));
        _kit.Advance(CooldownMs - 1_000, step: 1000);
        Assert.False(Ready(spell));
        _kit.Advance(1_000, step: 1000);
        Assert.True(Ready(spell));
        Assert.Equal(SpellCastResult.CastOk, Cast(spell));
    }

    [Fact]
    public void CastingIt_StartsNoCooldown_AndSendsNoEventYet()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(StealthLike));

        Assert.True(_kit.System.HasAura(_player, StealthLike));
        Assert.Empty(CooldownEvents(StealthLike));
        Assert.True(Ready(StealthLike)); // the client alone keeps the button disabled while the aura lives
    }

    [Fact]
    public void CancellingTheAura_SendsCooldownEvent_AndStartsTheNormalCooldown()
    {
        Cast(StealthLike);
        _session.Clear();

        _kit.System.CancelAura(_player, StealthLike);

        Assert.False(_kit.System.HasAura(_player, StealthLike));
        AssertCooldownStartedNow(StealthLike);
    }

    [Fact]
    public void AStealthBreakByDamage_StartsTheCooldown()
    {
        Cast(StealthLike);
        _session.Clear();

        _kit.System.OnDamageTaken(_player, _enemy, 10, periodic: false);

        Assert.False(_kit.System.HasAura(_player, StealthLike));
        AssertCooldownStartedNow(StealthLike);
    }

    [Fact]
    public void AnInterruptFlagBreak_StartsTheCooldown()
    {
        Cast(StealthLike);
        _session.Clear();

        _kit.System.RemoveAurasWithInterruptFlags(_player, SpellAuraInterruptFlags.Damage);

        AssertCooldownStartedNow(StealthLike);
    }

    [Fact]
    public void ADispel_StartsTheCooldown()
    {
        Cast(StealthLike);
        _session.Clear();

        _kit.System.RemoveAuras(_player, StealthLike, AuraRemoveMode.Dispel);

        AssertCooldownStartedNow(StealthLike);
    }

    [Fact]
    public void TheAuraExpiring_StartsTheCooldown()
    {
        Cast(ShortStealth);
        _session.Clear();

        _kit.Advance(3_500, step: 500);

        Assert.False(_kit.System.HasAura(_player, ShortStealth));
        Assert.Single(CooldownEvents(ShortStealth));
        Assert.False(Ready(ShortStealth));
        _kit.Advance(CooldownMs, step: 1000);
        Assert.True(Ready(ShortStealth));
    }

    [Fact]
    public void ADeathRemoval_StartsTheCooldown()
    {
        Cast(StealthLike);
        _session.Clear();

        _kit.System.RemoveAuras(_player, StealthLike, AuraRemoveMode.Death);

        Assert.Single(CooldownEvents(StealthLike));
        Assert.False(Ready(StealthLike));
    }

    [Fact]
    public void ARecastBeforeTheCooldownEnds_IsRefused()
    {
        Cast(StealthLike);
        _kit.System.CancelAura(_player, StealthLike);
        _kit.Advance(4_000, step: 1000);

        Assert.Equal(SpellCastResult.NotReady, Cast(StealthLike));
    }

    [Fact]
    public void AnAuraWithoutTheAttribute_SendsNoCooldownEvent()
    {
        Cast(PlainBuff);
        _session.Clear();

        _kit.System.CancelAura(_player, PlainBuff);

        Assert.Empty(CooldownEvents(PlainBuff));
    }
}
