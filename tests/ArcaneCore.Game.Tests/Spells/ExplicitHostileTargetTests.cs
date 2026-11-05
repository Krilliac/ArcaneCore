using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>A client-requested harmful spell aimed at an explicit target must name an attackable unit (vmangos Spell.cpp:5604-5610).</summary>
public sealed class ExplicitHostileTargetTests
{
    private const uint Bolt = 900420;
    private const uint Relay = 900421;

    [Fact]
    public void HostileSpellAtUnflaggedSameTeamPlayer_IsBadTargets_AndDealsNoDamage()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3);
        kit.Spellbook.Teach(caster, Bolt);
        kit.System.Relations = CombatHookRelations.Instance;   // the production relations (also the kit default)
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

    [Fact]
    public void ClientCast_WhoseTriggerEffectFiresAHarmfulSpellAtTheClientsTarget_IsRefused()
    {
        // The exploit: a client-castable spell whose own selector is not a hostile one (here any unit) names an unattackable unit and its
        // TriggerSpell effect re-aims a harmful spell at that same unit with a server-built block. The block must inherit the client origin.
        using var kit = new SpellTestKit(
            BoltSpell(),
            Spell(Relay, Effect(SpellEffectName.TriggerSpell, 0, SpellImplicitTarget.Unit, trigger: Bolt)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (Player caster, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3);
        kit.Spellbook.Teach(caster, Relay);
        kit.System.Relations = CombatHookRelations.Instance;
        var wire = new PacketWriter();
        wire.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
        wire.WritePackedGuid(ally.Guid.Value);
        var reader = new PacketReader(wire.ToArray());
        SpellCastTargets fromClient = SpellCastTargets.Read(ref reader);
        uint before = ally.Health;

        // RED without the inheritance (ForUnit(guid) in EffectTriggerSpell): the triggered Bolt lands and the ally loses health.
        kit.System.HandleCastRequest(caster, Relay, fromClient);

        Assert.Equal(before, ally.Health);
    }

    [Fact]
    public void ForUnit_WithAParentCast_InheritsClientOriginOnlyForTheParentsOwnUnit()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player named, _) = kit.AddPlayer(2, 3);
        (Player other, _) = kit.AddPlayer(3, 4);
        var wire = new PacketWriter();
        wire.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
        wire.WritePackedGuid(named.Guid.Value);
        var reader = new PacketReader(wire.ToArray());
        var parent = new SpellCast(kit.Store.Get(Bolt)!, caster, SpellCastTargets.Read(ref reader), false, 0, 0, 0);

        Assert.True(SpellCastTargets.ForUnit(named.Guid, parent).FromClient);
        Assert.False(SpellCastTargets.ForUnit(other.Guid, parent).FromClient);
        Assert.False(SpellCastTargets.ForUnit(named.Guid).FromClient);
    }

    // --- the production relations: who really is attackable -------------------------------------------------------------------------

    [Fact]
    public void FreeForAllSameTeamPlayers_CanCastHostileSpellsAtEachOther()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player rival, _) = kit.AddPlayer(2, 3);
        kit.Spellbook.Teach(caster, Bolt);
        kit.System.Relations = CombatHookRelations.Instance;
        caster.Flags |= PlayerFlags.FfaPvp;
        rival.Flags |= PlayerFlags.FfaPvp;

        // RED without the free-for-all rule in CombatHooks.IsFriendly: two Humans are friends and the cast is BAD_TARGETS (an arena is unusable).
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(rival.Guid)));
    }

    [Fact]
    public void FreeForAllGroupMates_StayFriends()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player mate, _) = kit.AddPlayer(2, 3);
        kit.Spellbook.Teach(caster, Bolt);
        kit.System.Relations = CombatHookRelations.Instance;
        caster.Flags |= PlayerFlags.FfaPvp;
        mate.Flags |= PlayerFlags.FfaPvp;
        CombatHooks.RegisterSameGroup(kit.World, (a, b) => true);

        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(mate.Guid)));
    }

    [Fact]
    public void OneSidedFreeForAll_DoesNotOpenSameTeamAttacks()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3);
        kit.Spellbook.Teach(caster, Bolt);
        kit.System.Relations = CombatHookRelations.Instance;
        caster.Flags |= PlayerFlags.FfaPvp;

        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(ally.Guid)));
    }

    [Fact]
    public void AMindControlledAlly_IsAnEnemyToItsFormerFriends_AndTheCharmerIsItsFriend()
    {
        using var kit = NewKit();
        (Player friend, _) = kit.AddPlayer(1);
        (Player controlled, _) = kit.AddPlayer(2, 3);
        (Player charmer, _) = kit.AddHostilePlayer(3, 6);   // an Orc, PvP-flagged
        kit.Spellbook.Teach(friend, Bolt);
        kit.System.Relations = CombatHookRelations.Instance;
        CharmService.SetCharmerGuid(controlled, charmer.Guid);

        // Team (race) alone would not decide this (the base already resolves the controlling player): the Human friend and the Human under control are the same team, so the friend cannot hit it.
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(friend, Bolt, SpellCastTargets.ForUnit(controlled.Guid)));
        Assert.True(CombatHooks.Default.IsFriendly(controlled, charmer));
        Assert.False(CombatHooks.Default.IsFriendly(controlled, friend));
    }

    [Fact]
    public void AMindControlledPlayer_CanCastHostileSpellsAtItsFormerFriends_ThroughThePetBar()
    {
        using var kit = NewKit();
        (Player friend, _) = kit.AddPlayer(1);
        (Player controlled, _) = kit.AddPlayer(2, 3);
        (Player charmer, _) = kit.AddHostilePlayer(3, 6);
        SpellTestKit.FlagForPvp(friend);   // the charmer acts for the Horde side, so the Human it aims at must be attackable by it
        kit.Spellbook.Teach(controlled, Bolt);
        kit.System.Relations = CombatHookRelations.Instance;
        CharmService.SetCharmerGuid(controlled, charmer.Guid);
        var wire = new PacketWriter();
        wire.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
        wire.WritePackedGuid(friend.Guid.Value);
        var reader = new PacketReader(wire.ToArray());
        SpellCastTargets fromClient = SpellCastTargets.Read(ref reader);

        // Team alone would not decide this (the base already resolves the controlling player): the controlled Human and the friend are the same team and the pet-bar cast is BAD_TARGETS.
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(controlled, Bolt, fromClient));
    }

    [Fact]
    public void DestinationSelectorAtAnUnflaggedAlly_IsBadTargets_ButAtAnAttackableFoeIsNot()
    {
        using var kit = new SpellTestKit(
            Spell(Relay, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.LocationCasterTargetPosition)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (Player caster, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3);
        (Player foe, _) = kit.AddHostilePlayer(3, 6);
        kit.Spellbook.Teach(caster, Relay);
        kit.System.Relations = CombatHookRelations.Instance;

        // TARGET_LOCATION_CASTER_TARGET_POSITION needs no unit target of its own, so it used to skip the gate.
        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, Relay, SpellCastTargets.ForUnit(ally.Guid)));
        Assert.NotEqual(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, Relay, SpellCastTargets.ForUnit(foe.Guid)));
    }

    [Fact]
    public void ProductionRelations_AllowTheAttackableTargets()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player foe, _) = kit.AddHostilePlayer(2, 3);          // the opposing team, PvP-flagged
        (Player dueller, _) = kit.AddPlayer(3, 6);             // the same team, in a started duel
        kit.Spellbook.Teach(caster, Bolt);
        kit.System.Relations = CombatHookRelations.Instance;
        caster.Duel = new DuelInfo(caster, dueller) { StartTimeSeconds = 1 };
        dueller.Duel = new DuelInfo(caster, caster) { StartTimeSeconds = 1 };

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(foe.Guid)));
        kit.Advance(2000);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(dueller.Guid)));
    }

    [Fact]
    public void DestinationSelectorAtADeadAttackableTarget_IsBadTargets_UnlessTheSpellCanTargetTheDead()
    {
        const uint DeadRelay = 900424;
        SpellInfo relay = Spell(Relay, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.LocationCasterTargetPosition)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        using var kit = new SpellTestKit(relay, relay with { Id = DeadRelay, AttributesEx2 = SpellAttributesEx2.AllowDeadTarget });
        (Player caster, _) = kit.AddPlayer(1);
        (Player foe, _) = kit.AddHostilePlayer(2, 3);
        kit.Spellbook.Teach(caster, Relay, DeadRelay);
        kit.System.Relations = CombatHookRelations.Instance;
        foe.Health = 0;
        Assert.False(foe.IsAlive);

        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, Relay, SpellCastTargets.ForUnit(foe.Guid)));
        Assert.NotEqual(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, DeadRelay, SpellCastTargets.ForUnit(foe.Guid)));
    }

    [Fact]
    public void ADestinationSpellAimedAtTheCaster_IsNotRefusedByTheGate()
    {
        using var kit = new SpellTestKit(
            Spell(Relay, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.LocationCasterTargetPosition)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (Player caster, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(caster, Relay);
        kit.System.Relations = CombatHookRelations.Instance;

        // The self-cast exemption: the caster is never "attackable" by itself, yet naming itself is not a hostile aim.
        Assert.NotEqual(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, Relay, SpellCastTargets.ForUnit(caster.Guid)));
    }

    [Fact]
    public void AHostileSpellThatCanTargetTheDead_IsNotRefusedAtADeadAttackableTarget_ButOneThatCannotIsTargetsDead()
    {
        const uint DeadBolt = 900423;
        using var kit = new SpellTestKit(
            BoltSpell(),
            BoltSpell() with { Id = DeadBolt, AttributesEx2 = SpellAttributesEx2.AllowDeadTarget });
        (Player caster, _) = kit.AddPlayer(1);
        (Player foe, _) = kit.AddHostilePlayer(2, 3);
        kit.Spellbook.Teach(caster, Bolt, DeadBolt);
        kit.System.Relations = CombatHookRelations.Instance;
        foe.Health = 0;
        Assert.False(foe.IsAlive);

        Assert.Equal(SpellCastResult.TargetsDead, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(foe.Guid)));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, DeadBolt, SpellCastTargets.ForUnit(foe.Guid)));
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

    private static SpellTestKit NewKit() => new(BoltSpell());

    private static SpellInfo BoltSpell() =>
        Spell(Bolt, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
}
