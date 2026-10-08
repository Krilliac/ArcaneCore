using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.CharmPossessTests;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// The control primitives behind charm and possession (docs/areas/unit-control.md): the mover a client may move (vmangos
/// Player::GetConfirmedMover, HandleSetActiveMoverOpcode), the camera view point, Eyes of the Beast's return, and a player charmed by a
/// creature (vmangos PlayerControlledAI).
/// </summary>
public sealed class UnitControlTests
{
    private static PetTestKit Kit() => new(ControlSpells());

    private static Creature Mob(PetTestKit kit, float x = 8, float y = 5)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, x, y, 83.5f, 0);

    private static SpellCastResult CastAt(PetTestKit kit, Unit caster, uint spell, Unit target)
        => kit.Spells.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    [Fact]
    public void TheConfirmedMover_FollowsTheClient_AndAPossessedPlayerMovesNothing()
    {
        using PetTestKit kit = Kit();
        (Player priest, _) = kit.AddPlayer(1, 5, 5);
        (Player victim, _) = kit.AddPlayer(2, 9, 5);
        victim.UnitFlags |= UnitFlags.Pvp;
        Creature mob = Mob(kit);
        CharmService charms = kit.Service.Charms;

        Assert.Same(priest, priest.GetMover());
        Assert.Same(priest, priest.GetConfirmedMover());

        CastAt(kit, priest, PossessSpell, mob);
        Assert.Same(mob, priest.GetMover());
        Assert.Same(priest, priest.GetConfirmedMover()); // the client has not switched yet: it may still move itself

        Assert.False(charms.HandleSetActiveMover(priest, victim.Guid)); // not the server's mover
        Assert.True(charms.HandleSetActiveMover(priest, mob.Guid));
        Assert.Same(mob, priest.GetConfirmedMover());

        kit.Spells.System.RemoveAuras(mob, PossessSpell);
        Assert.Same(priest, priest.GetMover());
        Assert.Same(priest, priest.GetConfirmedMover());

        CastAt(kit, priest, PossessSpell, victim);
        Assert.Null(victim.GetConfirmedMover());
        Assert.Same(victim, priest.GetMover());
    }

    [Fact]
    public void MoveNotActiveMover_GivesUpOnlyTheMoverTheClientNamed()
    {
        using PetTestKit kit = Kit();
        (Player priest, _) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        CharmService charms = kit.Service.Charms;
        CastAt(kit, priest, PossessSpell, mob);
        Assert.True(charms.HandleSetActiveMover(priest, mob.Guid));

        // vmangos HandleMoveNotActiveMoverOpcode: the old mover must be the client's, and not the server's current mover (unless it is the player)
        Assert.Null(CharmService.HandleMoveNotActiveMover(priest, priest.Guid));
        Assert.Null(CharmService.HandleMoveNotActiveMover(priest, mob.Guid));

        kit.Spells.System.RemoveAuras(mob, PossessSpell);
        Assert.Same(mob, CharmService.HandleMoveNotActiveMover(priest, mob.Guid));
        Assert.True(priest.ClientMoverGuid.IsEmpty);
        Assert.Same(priest, priest.GetConfirmedMover());
    }

    [Fact]
    public void ThePossessorsCamera_SeesWhatIsAroundThePossessedUnit()
    {
        using PetTestKit kit = Kit();
        (Player priest, _) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        Creature farAway = Mob(kit, 300, 5);
        kit.Run(200);
        Assert.DoesNotContain(farAway.Guid, priest.VisibleObjects);

        CastAt(kit, priest, PossessSpell, mob);
        Assert.Same(mob, priest.ViewPoint);
        mob.Relocate(290, 5, 83.5f, 0, kit.Spells.Now);
        kit.Run(200);

        Assert.Contains(mob.Guid, priest.VisibleObjects);     // the camera's own body
        Assert.Contains(farAway.Guid, priest.VisibleObjects); // seen from the camera, 290 yd from the priest

        kit.Spells.System.RemoveAuras(mob, PossessSpell);
        kit.Run(200);
        Assert.Same(priest, priest.ViewPoint);
        Assert.DoesNotContain(farAway.Guid, priest.VisibleObjects);
    }

    [Fact]
    public void EyesOfTheBeastEnds_ThePetReturnsWhenTheClientTakesItsOwnBodyBack_OrIsDismissedFarAway()
    {
        using PetTestKit kit = Kit();
        ArcaneCore.Game.Spells.Utility.Targets.PetTargets.Install(kit.Spells.System);
        (Player hunter, _) = kit.AddPlayer(1, 5, 5);
        kit.Cast(hunter, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        CharmService charms = kit.Service.Charms;

        kit.Cast(hunter, EyesOfTheBeast);
        Assert.True(charms.HandleSetActiveMover(hunter, pet.Guid));
        kit.Spells.System.RemoveAuras(pet, EyesOfTheBeast);

        // vmangos: the possessed state stays until the client names its own mover again
        Assert.NotEqual(0u, (uint)(pet.UnitFlags & UnitFlags.Possessed));
        Assert.True(charms.HandleSetActiveMover(hunter, hunter.Guid));
        Assert.Equal(0u, (uint)(pet.UnitFlags & UnitFlags.Possessed));
        Assert.Same(pet, kit.Creatures.FindCreature(pet.Guid)); // close: it comes back

        // a second time, far away: the pet is dismissed at the mover swap
        kit.Cast(hunter, EyesOfTheBeast);
        Assert.True(charms.HandleSetActiveMover(hunter, pet.Guid));
        pet.Relocate(5 + 110, 5, 83.5f, 0, kit.Spells.Now);
        kit.Spells.System.RemoveAuras(pet, EyesOfTheBeast);
        Assert.True(charms.HandleSetActiveMover(hunter, hunter.Guid));
        Assert.Null(kit.Creatures.FindCreature(pet.Guid));
        Assert.True(hunter.PetGuid.IsEmpty);
    }

    [Fact]
    public void APlayerCharmedByACreature_LosesControl_FightsForIt_AndIsFreedWhenItLeavesCombat()
    {
        using PetTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1, 5, 5);
        Creature controller = Mob(kit);
        Creature enemy = Mob(kit, 6, 5);
        uint raceFaction = player.FactionTemplate;
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, CastAt(kit, controller, CharmSpell, player));
        Assert.Equal(controller.Guid, player.CharmerGuid);
        Assert.Equal(controller.FactionTemplate, player.FactionTemplate);
        Assert.Null(player.GetConfirmedMover());
        byte[] control = Assert.Single(Packets(session, WorldOpcode.SmsgClientControlUpdate));
        var r = new PacketReader(control);
        Assert.Equal(player.Guid.Value, r.ReadPackedGuid());
        Assert.Equal(0, r.ReadByte());

        // the controller fights the enemy: the charmed player joins (vmangos PlayerControlledAI::UpdateAI with a creature controller)
        kit.Map.Combat.Attack(controller, enemy);
        controller.Combat.Threat.AddThreat(enemy, 10);
        kit.Map.Combat.SetInCombatState(controller, 0);
        kit.Run(100);
        Assert.Same(enemy, player.Combat.Victim);

        // the controller leaves combat: the charm ends
        kit.Map.Combat.CombatStop(controller);
        controller.Combat.Threat.Clear();
        kit.Run(100);
        Assert.True(player.CharmerGuid.IsEmpty);
        Assert.Equal(raceFaction, player.FactionTemplate);
        Assert.Same(player, player.GetConfirmedMover());
        Assert.False(kit.Spells.System.HasAura(player, CharmSpell));
    }

    [Fact]
    public void ThePossessor_MayCancelItsOwnChanneledPossession_ThoughTheSpellIsNegative()
    {
        SpellInfo mindControl = Spell(920_101, Effect(SpellEffectName.ApplyAura, 60, SpellImplicitTarget.UnitEnemy, aura: AuraType.ModPossess)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            Duration = new SpellDuration(60_000, 0, 60_000),
            AttributesEx = SpellAttributesEx.IsChanneled,
        };
        using var kit = new PetTestKit([.. ControlSpells(), mindControl]);
        (Player priest, _) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        kit.Spells.Spellbook.Teach(priest, 920_101);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(priest, 920_101, SpellCastTargets.ForUnit(mob.Guid), triggered: false));
        Assert.Equal(priest.Guid, mob.CharmerGuid);

        kit.Spells.System.CancelAura(priest, 920_101); // vmangos HandleCancelAuraOpcode: "except own aura spells" while remote controlling

        Assert.True(mob.CharmerGuid.IsEmpty);
        Assert.True(priest.CharmGuid.IsEmpty);
        Assert.Same(priest, priest.GetMover());
    }

    [Fact]
    public void AFearedPossessedCreature_IsNoLongerMovedByItsPossessor()
    {
        SpellInfo fear = Spell(920_100, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, aura: AuraType.ModFear)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            Duration = new SpellDuration(5_000, 0, 5_000),
        };
        using var kit = new PetTestKit([.. ControlSpells(), fear]);
        (Player priest, FakeSession session) = kit.AddPlayer(1, 5, 5);
        (Player other, _) = kit.AddPlayer(2, 5, 7);
        Creature mob = Mob(kit);
        CastAt(kit, priest, PossessSpell, mob);
        session.Clear();

        CastAt(kit, other, 920_100, mob); // vmangos Unit::SetFeared -> UpdateControl

        var r = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgClientControlUpdate)));
        Assert.Equal(mob.Guid.Value, r.ReadPackedGuid());
        Assert.Equal(0, r.ReadByte());
    }
}
