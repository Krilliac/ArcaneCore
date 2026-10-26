using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>A client-requested harmful spell aimed at an explicit target must name an attackable unit (vmangos Spell.cpp:5604-5610).</summary>
public sealed class ExplicitHostileTargetTests
{
    private const uint Bolt = 900420;

    [Fact]
    public void HostileSpellAtUnflaggedSameTeamPlayer_IsBadTargets_AndDealsNoDamage()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3);
        kit.Spellbook.Teach(caster, Bolt);
        kit.System.Relations = CombatHookRelations.Instance;   // the production relations; the kit default opens the gate
        uint before = ally.Health;

        // RED on origin/main: this assertion fails (CastOk) because CheckCast never tests hostility for a UnitEnemy selector.
        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(ally.Guid)));
        Assert.Equal(before, ally.Health);
    }

    [Fact]
    public void HostileSpellAtAttackableTarget_StillCasts()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player foe, _) = kit.AddPlayer(2, 3);
        kit.Spellbook.Teach(caster, Bolt);
        kit.System.Relations = new HostileSetRelations(foe.Guid);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(foe.Guid)));
    }

    [Fact]
    public void TriggeredCast_WithAClientNamedTarget_IsCheckedLikeAnyOther()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3);
        kit.System.Relations = CombatHookRelations.Instance;
        uint before = ally.Health;

        // The target block as an item-use packet carries it: the second on-use spell of an item is cast triggered with the client's block.
        var wire = new PacketWriter();
        wire.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
        wire.WritePackedGuid(ally.Guid.Value);
        var reader = new PacketReader(wire.ToArray());
        SpellCastTargets fromClient = SpellCastTargets.Read(ref reader);
        Assert.True(fromClient.FromClient);

        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(caster, Bolt, fromClient, triggered: true));
        Assert.Equal(before, ally.Health);
    }

    [Fact]
    public void TriggeredCast_WithAServerChosenTarget_IsNotGated()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3);
        kit.System.Relations = CombatHookRelations.Instance;

        // A proc or script trigger names its own target; the explicit-target gate is for what a client can name.
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Bolt, SpellCastTargets.ForUnit(ally.Guid), triggered: true));
    }

    [Fact]
    public void TriggeredClientCast_WhoseTargetStopsBeingAttackable_IsRefusedWhenItLands()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player foe, _) = kit.AddPlayer(2, 3);
        var relations = new SwitchRelations();
        kit.System.Relations = relations;
        // The target is attackable when the cast is prepared and not when it lands (a triggered cast has no cast time: it lands inside
        // the same call, after the prepared notification). SpellCast's copy of the block must keep FromClient for the landing re-check.
        kit.System.RegisterObserver(new Switcher(relations));
        var wire = new PacketWriter();
        wire.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
        wire.WritePackedGuid(foe.Guid.Value);
        var reader = new PacketReader(wire.ToArray());
        SpellCastTargets fromClient = SpellCastTargets.Read(ref reader);
        uint before = foe.Health;

        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(caster, Bolt, fromClient, triggered: true));
        Assert.Equal(before, foe.Health);
    }

    [Fact]
    public void TriggeredServerCast_WhoseTargetStopsBeingAttackable_StillLands_ADocumentedLimit()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player foe, _) = kit.AddPlayer(2, 3);
        var relations = new SwitchRelations();
        kit.System.Relations = relations;
        kit.System.RegisterObserver(new Switcher(relations));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Bolt, SpellCastTargets.ForUnit(foe.Guid), triggered: true));
    }

    private sealed class SwitchRelations : ISpellTargetRelations
    {
        public bool Attackable { get; set; } = true;

        public bool IsHostile(Unit caster, Unit target) => Attackable;

        public bool IsFriendly(Unit caster, Unit target) => !Attackable;
    }

    private sealed class Switcher(SwitchRelations relations) : ISpellCastObserver
    {
        public void OnPrepared(SpellCast cast) => relations.Attackable = false;
    }

    /// <summary>Only the listed units are attackable; the explicit-target gate takes its default (<see cref="ISpellTargetRelations.IsHostile"/>).</summary>
    private sealed class HostileSetRelations(ObjectGuid hostile) : ISpellTargetRelations
    {
        public bool IsHostile(Unit caster, Unit target) => target.Guid == hostile;

        public bool IsFriendly(Unit caster, Unit target) => !IsHostile(caster, target);
    }

    private static SpellTestKit NewKit() => new(
        Spell(Bolt, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });
}
