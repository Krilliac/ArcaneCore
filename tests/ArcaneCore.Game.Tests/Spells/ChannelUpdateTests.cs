using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// What ends a running channel on the world tick, besides its timer.
/// vmangos Spell::update (Spell.cpp:4000-4147): a player channel cancels on a jump and, with the turning channel interrupt
/// flag, on a turn; the channel ends when no target it needs is left in a state the spell can target
/// (HasValidUnitPresentInTargetList, :1957-1990). vmangos SpellAuraHolder::UpdateHolder (SpellAuras.cpp:7338-7376): the
/// channel is interrupted when its channel target is beyond the spell's maximum range times 1.33 (hostile) or plus 1.25 yd.
/// </summary>
public sealed class ChannelUpdateTests
{
    private const uint Drain = 900801;
    private const uint SelfAuraChannel = 900802;
    private const uint TurnChannel = 900803;
    private const uint FarDrain = 900804;
    private const uint FriendChannel = 900805;
    private const uint ChannelNoDistLimit = 0x008;

    private static SpellInfo DrainLike(uint id) => Spell(id,
        Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 1000)) with
    {
        AttributesEx = SpellAttributesEx.IsChanneled,
        Duration = new SpellDuration(5000, 0, 5000),
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
    };

    private static SpellTestKit NewKit() => new(
        DrainLike(Drain),
        // Its only aura sits on the caster, so it needs no target (vmangos needTargetsMask == 0).
        Spell(SelfAuraChannel,
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy),
            Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(5000, 0, 5000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
        },
        Spell(TurnChannel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            ChannelInterruptFlags = SpellAuraInterruptFlags.Turning,
            Duration = new SpellDuration(5000, 0, 5000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
        },
        DrainLike(FarDrain) with { CustomFlags = ChannelNoDistLimit },
        Spell(FriendChannel, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitFriend, AuraType.PeriodicHeal, amplitude: 1000)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(5000, 0, 5000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
        });

    private static bool IsChannelling(SpellTestKit kit, Unit caster, uint spellId)
        => caster.GetUInt32(UpdateFields.UnitChannelSpell) == spellId
            && kit.System.GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Casting } cast && cast.Spell.Id == spellId;

    private static void Kill(Player killer, Unit victim, SpellTestKit kit)
    {
        victim.Map!.FindUpdater<MapCombat>()!.Kill(killer, victim);
        kit.System.OnUnitDied(victim);
    }

    private static CombatTestUnit AddNpc(SpellTestKit kit, Player caster, uint factionTemplate, float x)
    {
        caster.Map!.Combat.Hooks = new FactionCombatHooks(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
            new FactionTemplateRecord(11, 11, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
            new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 8, HostileMask: 2),
        ]));
        var npc = new CombatTestUnit { FactionTemplate = factionTemplate, MaxHealth = 100, Health = 50 };
        npc.Relocate(x, 0, caster.Z, 0, 0);
        caster.Map!.AddObject(npc);
        npc.Map!.Combat.Track(npc);
        kit.World.RunTick(0);
        kit.System.Units = new MapUnitResolver();
        return npc;
    }

    // --- finding 18: a dead channel target -------------------------------------------------

    [Fact]
    public void Channel_EndsWhenItsTargetDies_AndTheObjectStillExists()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 10, 0);
        (Player killer, _) = kit.AddPlayer(3, 12, 0);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Drain, SpellCastTargets.ForUnit(enemy.Guid), triggered: false));
        Assert.True(IsChannelling(kit, caster, Drain));

        Kill(killer, enemy, kit);
        Assert.NotNull(kit.System.Units.Find(caster, enemy.Guid)); // the corpse is still in the world
        session.Clear();
        kit.Advance(100);

        Assert.False(IsChannelling(kit, caster, Drain));
        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
        Assert.Equal(0u, caster.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Packets(session, WorldOpcode.MsgChannelUpdate).Single());
    }

    [Fact]
    public void Channel_ThatNeedsNoTarget_KeepsRunning_WhenTheTargetDies()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 10, 0);
        (Player killer, _) = kit.AddPlayer(3, 12, 0);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, SelfAuraChannel, SpellCastTargets.ForUnit(enemy.Guid), triggered: false));

        Kill(killer, enemy, kit);
        kit.Advance(1000);

        Assert.True(IsChannelling(kit, caster, SelfAuraChannel));
    }

    // --- finding 35: the channel target runs out of range ----------------------------------

    [Fact]
    public void HostileChannel_BreaksPastMaxRangeTimes133()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        CombatTestUnit enemy = AddNpc(kit, caster, 14, 10);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Drain, SpellCastTargets.ForUnit(enemy.Guid), triggered: false));

        // Combat distance 42 - 3 = 39 yd: past 30 + 1.25 but inside 30 x 1.33 = 39.9 yd.
        enemy.Relocate(42, 0, enemy.Z, 0, kit.Now);
        kit.Advance(100);
        Assert.True(IsChannelling(kit, caster, Drain));

        // 44 - 3 = 41 yd: beyond 39.9 yd.
        enemy.Relocate(44, 0, enemy.Z, 0, kit.Now);
        kit.Advance(100);
        Assert.False(IsChannelling(kit, caster, Drain));
        Assert.False(kit.System.HasAura(enemy, Drain));
    }

    [Fact]
    public void FriendlyChannel_BreaksPastMaxRangePlusTheCasterLeeway()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        CombatTestUnit friend = AddNpc(kit, caster, 11, 10);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, FriendChannel, SpellCastTargets.ForUnit(friend.Guid), triggered: false));

        // Combat distance 34 - 3 = 31 yd: inside 30 + 1.25.
        friend.Relocate(34, 0, friend.Z, 0, kit.Now);
        kit.Advance(100);
        Assert.True(IsChannelling(kit, caster, FriendChannel));

        // 35 - 3 = 32 yd: past 31.25 yd, though a hostile target would still be inside 39.9 yd.
        friend.Relocate(35, 0, friend.Z, 0, kit.Now);
        kit.Advance(100);
        Assert.False(IsChannelling(kit, caster, FriendChannel));
    }

    [Fact]
    public void Channel_WithNoDistanceLimit_IgnoresRange()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        CombatTestUnit enemy = AddNpc(kit, caster, 14, 10);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, FarDrain, SpellCastTargets.ForUnit(enemy.Guid), triggered: false));

        enemy.Relocate(80, 0, enemy.Z, 0, kit.Now);
        kit.Advance(100);

        Assert.True(IsChannelling(kit, caster, FarDrain));
    }

    // --- finding 36: jump and turn ----------------------------------------------------------

    [Fact]
    public void PlayerChannel_CancelsOnAJump_WithoutMoving()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, ChannelSpell, SpellCastTargets.ForSelf(), triggered: false));

        caster.ApplyMovement(new MovementInfo
        {
            Flags = MovementFlags.Jumping, X = caster.X, Y = caster.Y, Z = caster.Z, Orientation = caster.Orientation,
        }, kit.Now);
        kit.Advance(100);

        Assert.False(IsChannelling(kit, caster, ChannelSpell));
    }

    [Fact]
    public void TurningChannel_CancelsWhenTheCasterTurns()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, TurnChannel, SpellCastTargets.ForSelf(), triggered: false));

        kit.Advance(100);
        Assert.True(IsChannelling(kit, caster, TurnChannel));

        caster.ApplyMovement(new MovementInfo { X = caster.X, Y = caster.Y, Z = caster.Z, Orientation = caster.Orientation + 1.0f }, kit.Now);
        kit.Advance(100);

        Assert.False(IsChannelling(kit, caster, TurnChannel));
    }

    [Fact]
    public void ChannelWithoutTheTurningFlag_SurvivesATurn()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, ChannelSpell, SpellCastTargets.ForSelf(), triggered: false));

        caster.ApplyMovement(new MovementInfo { X = caster.X, Y = caster.Y, Z = caster.Z, Orientation = caster.Orientation + 1.0f }, kit.Now);
        kit.Advance(100);

        Assert.True(IsChannelling(kit, caster, ChannelSpell));
    }

    private sealed class MapUnitResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid) => reference.Map?.Combat.FindUnit(guid) ?? reference.Map?.FindPlayer(guid);
    }
}
