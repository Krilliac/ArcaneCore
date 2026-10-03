using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Other players' area and channel updates cannot mutate a held settlement target.</summary>
public sealed class SpellSettlementFreezeTests
{
    private const uint Party = 900610;
    private const uint PartyRoot = 900611;
    private const uint OtherChannel = 900612;
    private const uint HostileChannel = 900613;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartyAreaAura_HeldMemberRetainsChildUntilRangeOrPartyCleanupCanRun(bool leaveParty)
    {
        using var kit = Kit();
        var groups = new FakeGroups();
        kit.System.Groups = groups;
        (Player caster, _) = kit.AddPlayer(1);
        (Player member, _) = kit.AddPlayer(2, 5);
        groups.Parties.Add([caster.Guid, member.Guid]);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Party, SpellCastTargets.ForSelf(), triggered: true));
        kit.Advance(100);
        SpellAuraHolder child = Assert.Single(kit.System.GetAuras(member));
        uint[] fields = member.Values.ToArray();
        Guid operation = Guid.NewGuid();
        Assert.True(member.BeginQuestSettlement(operation));

        // Merely becoming pending must not be treated as leaving the source's radius.
        kit.Advance(100);
        Assert.Same(child, Assert.Single(kit.System.GetAuras(member)));
        Assert.False(child.IsRemoved);
        Assert.Equal(fields, member.Values.ToArray());

        if (leaveParty)
        {
            groups.Parties[0].Remove(member.Guid);
        }
        else
        {
            caster.Relocate(40, 0, caster.Z, 0, kit.Now);
        }

        kit.Advance(500);
        Assert.Same(child, Assert.Single(kit.System.GetAuras(member)));
        Assert.False(child.IsRemoved);
        Assert.Equal(fields, member.Values.ToArray());

        Assert.True(member.EndQuestSettlement(operation));
        kit.Advance(100);
        Assert.True(child.IsRemoved);
        Assert.Empty(kit.System.GetAuras(member));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartyAreaAura_SourceRemovalOrExpiryDefersHeldChildFieldsAndRootHandler(bool expire)
    {
        using var kit = Kit();
        var groups = new FakeGroups();
        kit.System.Groups = groups;
        (Player caster, _) = kit.AddPlayer(1);
        (Player member, _) = kit.AddPlayer(2, 5);
        groups.Parties.Add([caster.Guid, member.Guid]);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, PartyRoot, SpellCastTargets.ForSelf(), triggered: true));
        kit.Advance(100);
        SpellAuraHolder source = Assert.Single(kit.System.GetAuras(caster));
        SpellAuraHolder child = Assert.Single(kit.System.GetAuras(member));
        Assert.Same(source, child.AreaParent);
        Assert.True(member.IsRooted);
        int duration = child.Duration;
        uint[] fields = member.Values.ToArray();
        Guid operation = Guid.NewGuid();
        Assert.True(member.BeginQuestSettlement(operation));

        if (expire)
        {
            kit.Advance(1000);
        }
        else
        {
            kit.System.RemoveAuras(caster, PartyRoot);
            kit.Advance(100);
        }

        Assert.True(source.IsRemoved);
        Assert.Same(child, Assert.Single(kit.System.GetAuras(member)));
        Assert.False(child.IsRemoved);
        Assert.Same(source, child.AreaParent); // Retains the invalid parent for deferred cleanup.
        Assert.Equal(duration, child.Duration);
        Assert.True(member.IsRooted);
        Assert.Equal(fields, member.Values.ToArray());

        Assert.True(member.EndQuestSettlement(operation));
        kit.Advance(100);
        Assert.True(child.IsRemoved);
        Assert.Empty(kit.System.GetAuras(member));
        Assert.False(member.IsRooted);
        Assert.Equal(0u, member.GetUInt32(UpdateFields.UnitFieldAura + child.Slot));
    }

    [Fact]
    public void PartyAreaAura_OldChildCleanupCannotRemoveSameGuidReplacementSource()
    {
        using var kit = Kit();
        var groups = new FakeGroups();
        kit.System.Groups = groups;
        (Player caster, _) = kit.AddPlayer(1);
        (Player oldMember, _) = kit.AddPlayer(2, 5);
        groups.Parties.Add([caster.Guid, oldMember.Guid]);
        kit.System.CastSpell(caster, Party, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        SpellAuraHolder oldChild = Assert.Single(kit.System.GetAuras(oldMember));
        kit.System.RemoveUnit(oldMember);
        kit.World.RemovePlayer(oldMember);
        (Player replacement, _) = kit.AddPlayer(2, 5);
        Assert.Equal(oldMember.Guid, replacement.Guid);
        Assert.NotSame(oldMember, replacement);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(replacement, Party, SpellCastTargets.ForSelf(), triggered: true));
        SpellAuraHolder fresh = Assert.Single(kit.System.GetAuras(replacement));
        Assert.True(fresh.IsAreaSource);
        Assert.Same(replacement, kit.System.ResolveAuraActor(fresh));
        Guid operation = Guid.NewGuid();
        Assert.True(replacement.BeginQuestSettlement(operation));
        uint[] fields = replacement.Values.ToArray();

        kit.System.RemoveAuras(caster, Party);
        kit.System.RemoveUnit(oldMember); // Delayed departure callback still holds the old instance.
        kit.World.RemovePlayer(oldMember);
        kit.Advance(100);

        Assert.True(oldChild.IsRemoved);
        Assert.Same(replacement, kit.World.FindOnlinePlayer(replacement.Guid));
        Assert.Same(fresh, Assert.Single(kit.System.GetAuras(replacement)));
        Assert.False(fresh.IsRemoved);
        Assert.Equal(fields, replacement.Values.ToArray());
        Assert.True(replacement.EndQuestSettlement(operation));
        kit.Advance(100);
        Assert.Same(fresh, Assert.Single(kit.System.GetAuras(replacement)));
        Assert.Same(replacement, kit.System.ResolveAuraActor(fresh));
    }

    [Fact]
    public void ChannelPushback_HeldOtherTargetRetainsAuraDurationAndFields()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession targetSession) = kit.AddPlayer(2, 5);
        (Player attacker, _) = kit.AddPlayer(3, 10);
        kit.Spellbook.Teach(caster, OtherChannel);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, OtherChannel, SpellCastTargets.ForUnit(target.Guid)));
        SpellCast cast = kit.System.GetState(caster.Guid)!.CurrentCast!;
        SpellAuraHolder holder = Assert.Single(kit.System.GetAuras(target));
        Assert.Equal(SpellCastState.Casting, cast.State);
        Assert.Equal(8000, holder.Duration);
        Guid operation = Guid.NewGuid();
        Assert.True(target.BeginQuestSettlement(operation));
        uint[] fields = target.Values.ToArray();
        targetSession.Clear();

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);

        Assert.Equal(6000, cast.Timer);
        Assert.Equal(1, cast.PushbackCount);
        Assert.Equal(8000, holder.Duration);
        Assert.Same(holder, Assert.Single(kit.System.GetAuras(target)));
        Assert.Equal(fields, target.Values.ToArray());
        Assert.Empty(Packets(targetSession, WorldOpcode.SmsgUpdateAuraDuration));
        kit.Advance(100);
        Assert.Equal(8000, holder.Duration);

        Assert.True(target.EndQuestSettlement(operation));
        kit.Advance(100);
        Assert.Equal(7900, holder.Duration); // Resumes without catching up held time.
        targetSession.Clear();
        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);
        Assert.Equal(5900, holder.Duration);
        Assert.Single(Packets(targetSession, WorldOpcode.SmsgUpdateAuraDuration));
    }

    [Fact]
    public void ChannelPushback_SameGuidReplacementCannotShortenDepartedCastersOrphanAura()
    {
        using var kit = Kit();
        (Player oldCaster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 5);
        (Player attacker, _) = kit.AddPlayer(3, 10);
        kit.Spellbook.Teach(oldCaster, HostileChannel);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(oldCaster, HostileChannel, SpellCastTargets.ForUnit(target.Guid)));
        SpellAuraHolder orphan = Assert.Single(kit.System.GetAuras(target));
        kit.System.RemoveUnit(oldCaster);
        kit.World.RemovePlayer(oldCaster);
        (Player replacement, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(replacement, HostileChannel);
        kit.System.CombatRules = new FixedRules { Miss = SpellMissInfo.Resist };
        // A missed new channel leaves the departed caster's existing target holder in place.
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(replacement, HostileChannel, SpellCastTargets.ForUnit(target.Guid)));
        SpellCast freshCast = kit.System.GetState(replacement.Guid)!.CurrentCast!;
        Assert.Same(orphan, Assert.Single(kit.System.GetAuras(target)));
        Assert.Same(target, kit.System.ResolveAuraActor(orphan));

        kit.System.OnDamageTaken(replacement, attacker, 5, periodic: false);

        Assert.Equal(6000, freshCast.Timer);
        Assert.Equal(8000, orphan.Duration);
        Assert.False(orphan.IsRemoved);
        Assert.Same(target, kit.System.ResolveAuraActor(orphan));
        Assert.False(SpellSystem.HasLiveCasterOwnership(orphan));
    }

    private static SpellTestKit Kit() => new(
        Spell(Party, Effect(SpellEffectName.ApplyAreaAuraParty, 0, aura: AuraType.Dummy) with { Radius = 30 }) with
        {
            Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0,
        },
        Spell(PartyRoot, Effect(SpellEffectName.ApplyAreaAuraParty, 0, aura: AuraType.ModRoot) with { Radius = 30 }) with
        {
            Duration = new SpellDuration(1000, 0, 1000), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0,
        },
        Channel(OtherChannel, SpellImplicitTarget.UnitFriend),
        Channel(HostileChannel, SpellImplicitTarget.UnitEnemy) with { DamageClass = SpellDamageClass.Magic });

    private static SpellInfo Channel(uint id, SpellImplicitTarget target) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, target, AuraType.Dummy)) with
    {
        AttributesEx = SpellAttributesEx.IsChanneled, Duration = new SpellDuration(8000, 0, 8000), SpellVisual = 1,
        RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
        ChannelInterruptFlags = (SpellAuraInterruptFlags)SpellChannelInterruptFlags.Delay,
    };
}
