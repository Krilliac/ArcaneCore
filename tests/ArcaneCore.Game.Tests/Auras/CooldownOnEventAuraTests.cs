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
    private const uint StealthRank1 = 945010; // real Stealth/Prowl shape: no own recovery, 10 s category 38
    private const uint StealthRank2 = 945011;
    private const uint OtherCategorySpell = 945012;
    private const uint FriendStealth = 945014; // aura on an explicit friendly target
    private const uint CategoryMs = 10_000;
    private const uint Category = 38;
    private const AuraType Probe = SpellHandlerModuleTests.FixtureAura;
    private const uint CooldownMs = 10_000;

    private readonly SpellTestKit _kit;
    private readonly Player _player;
    private readonly Player _enemy;
    private readonly FakeSession _session;
    private readonly FakeSession _enemySession;

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
            Category38(StealthRank1),
            Category38(StealthRank2),
            Category38(OtherCategorySpell) with { Attributes = SpellAttributes.None },
            Category38(FriendStealth) with
            {
                Effects = [Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, Probe)],
            },
            Spell(Hit, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
            {
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (_player, _session) = _kit.AddPlayer(1);
        (_enemy, _enemySession) = _kit.AddPlayer(2, 2);
        _kit.Spellbook.Teach(_player, StealthLike, ShortStealth, PlainBuff, StealthRank1, StealthRank2, OtherCategorySpell, FriendStealth);
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

    private static SpellInfo Category38(uint id) => Stealthy(id, 60_000) with
    {
        RecoveryTime = 0,
        Category = Category,
        CategoryRecoveryTime = CategoryMs,
    };

    private SpellCastResult Cast(uint spell)
    {
        SpellCastResult result = _kit.System.CastSpell(_player, spell, SpellCastTargets.ForSelf(), triggered: false);
        FlushSpellBatch();
        return result;
    }

    /// <summary>With spell batching on, a held go resolves at the next batch boundary; advancing one window always crosses it (0: no-op).</summary>
    private void FlushSpellBatch() => _kit.Advance(_kit.System.SpellBatchWindowMs);

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

    [Fact]
    public void ACategoryOnlyCooldown_BlocksOtherSpellsOfTheCategory_UntilItExpires()
    {
        Cast(StealthRank1);
        _kit.System.CancelAura(_player, StealthRank1);

        Assert.False(Ready(StealthRank1));
        Assert.False(Ready(StealthRank2));
        Assert.False(Ready(OtherCategorySpell));
        Assert.Equal(SpellCastResult.NotReady, Cast(StealthRank2));
        _kit.Advance(CategoryMs - 1_000, step: 1000);
        Assert.False(Ready(StealthRank2));
        _kit.Advance(1_000, step: 1000);
        Assert.True(Ready(StealthRank2));
        Assert.True(Ready(OtherCategorySpell));
        Assert.Equal(SpellCastResult.CastOk, Cast(StealthRank2));
    }

    [Fact]
    public void AnotherRankEnding_WhileTheCategoryIsHeldByTheFirst_AlsoTellsTheClientAboutTheCategoryOwner()
    {
        Cast(StealthRank1);
        _kit.System.CancelAura(_player, StealthRank1);
        _session.Clear();
        _kit.System.RemoveAuras(_player, StealthRank2, AuraRemoveMode.Cancel); // no-op: not active
        Assert.Empty(CooldownEvents(StealthRank2));

        // rank 2 is applied while rank 1's category cooldown runs (a triggered cast ignores the cooldown), then ends
        _kit.System.CastSpell(_player, StealthRank2, SpellCastTargets.ForSelf(), triggered: true);
        _kit.System.CancelAura(_player, StealthRank2);

        var order = Packets(_session, WorldOpcode.SmsgCooldownEvent).Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p)).ToList();
        Assert.Equal([StealthRank1, StealthRank2], order); // the owner first, then the spell that ended
    }

    [Fact]
    public void TheOwnSpellEnding_SendsOnlyOneEvent_WhenItHoldsTheCategoryItself()
    {
        Cast(StealthRank1);
        _session.Clear();

        _kit.System.CancelAura(_player, StealthRank1);

        Assert.Single(Packets(_session, WorldOpcode.SmsgCooldownEvent));
    }

    [Fact]
    public void ReplacingTheAuraByTheSameSpell_StartsNoCooldownAndSendsNoEvent()
    {
        Cast(StealthLike);
        _session.Clear();

        _kit.System.RemoveAuras(_player, StealthLike, AuraRemoveMode.Stack);

        Assert.Empty(CooldownEvents(StealthLike));
        Assert.True(Ready(StealthLike));
    }

    [Fact]
    public void WhenCasterAndTargetDiffer_TheCasterGetsTheCooldownAndTheEvent_NotTheTarget()
    {
        FakeSession enemySession = _enemySession;
        _kit.Spellbook.Teach(_enemy, FriendStealth);
        enemySession.Clear();
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_enemy, FriendStealth, SpellCastTargets.ForUnit(_player.Guid), triggered: false));
        FlushSpellBatch();
        Assert.True(_kit.System.HasAura(_player, FriendStealth));
        _session.Clear();

        _kit.System.RemoveAuras(_player, FriendStealth, AuraRemoveMode.Dispel);

        Assert.Contains(Packets(enemySession, WorldOpcode.SmsgCooldownEvent), p => BinaryPrimitives.ReadUInt32LittleEndian(p) == FriendStealth);
        Assert.Empty(CooldownEvents(FriendStealth)); // nothing for the target's client
        Assert.False(_kit.System.IsSpellReady(_enemy, _kit.Store.Get(FriendStealth)!));
        Assert.True(Ready(FriendStealth));
    }

    [Fact]
    public void ACategoryOnlyCooldown_IsListedInInitialSpells_AfterARelog()
    {
        Cast(StealthRank1);
        _kit.System.CancelAura(_player, StealthRank1);
        _kit.Advance(3_000, step: 1000);
        SpellStateSnapshot snapshot = _kit.System.CaptureState(_player, nowUnixMs: 1_000_000);
        _kit.System.RemoveUnit(_player); // logout: the state and its owner map are dropped
        _kit.System.RestoreCooldowns(_player, snapshot.Cooldowns, nowUnixMs: 1_000_000);

        IReadOnlyList<InitialSpellCooldown> listed = _kit.System.GetActiveCooldowns(_player);

        InitialSpellCooldown entry = Assert.Single(listed, c => c.Category == Category);
        Assert.Equal(StealthRank1, entry.SpellId); // the lowest known spell of the category, on-event spells first
        Assert.Equal(0u, entry.CooldownMs);
        Assert.InRange(entry.CategoryCooldownMs, 6_000u, 7_000u);

        // 5875 SMSG_INITIAL_SPELLS cooldown entry: u16 spell, u16 item, u16 category, u32 recovery, u32 category recovery.
        byte[] packet = SpellPackets.BuildInitialSpells([StealthRank1], listed);
        int at = 1 + 2 + 4 + 2; // flag, spell count, one spell (u16 id + u16 slot), cooldown count
        Assert.Equal(unchecked((ushort)StealthRank1), BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(at))); // synthetic id wraps to u16
        Assert.Equal(Category, (uint)BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(at + 4)));
        Assert.Equal(entry.CategoryCooldownMs, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(at + 10)));
    }

    [Fact]
    public void ACategoryOnlyCooldown_KeepsTheRecordedOwner_WhenAnotherRankStartedItFirst()
    {
        _kit.System.CastSpell(_player, StealthRank2, SpellCastTargets.ForSelf(), triggered: true);
        _kit.System.CancelAura(_player, StealthRank2);

        Assert.Equal(StealthRank2, Assert.Single(_kit.System.GetActiveCooldowns(_player), c => c.Category == Category).SpellId);
    }
}
