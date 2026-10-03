using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>The pet opcodes: vmangos PetHandler.cpp, Unit::HandlePetCommand, Player::PetSpellInitialize.</summary>
public sealed class PetControlTests
{
    private static byte[] Action(ObjectGuid pet, uint action, ActionType type, ObjectGuid target = default)
    {
        var w = new PacketWriter(20);
        w.WriteUInt64(pet.Value);
        w.WriteUInt32(ActionButton.Make(action, type).Packed);
        w.WriteUInt64(target.Value);
        return w.ToArray();
    }

    private static (PetTestKit Kit, Player Owner, FakeSession Session, Creature Pet) Summoned()
    {
        var kit = new PetTestKit();
        (Player owner, FakeSession session) = kit.AddPlayer(1, 5, 5);
        kit.Cast(owner, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        return (kit, owner, session, pet);
    }

    private static Creature Enemy(PetTestKit kit, float x, float y)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, x, y, 83.5f, 0);

    [Fact]
    public void SummoningAPet_SendsItsActionBar_AndDismissingClearsIt()
    {
        (PetTestKit kit, Player owner, FakeSession session, Creature pet) = Summoned();
        using (kit)
        {
            byte[] bar = Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells));
            var r = new PacketReader(bar);
            Assert.Equal(pet.Guid.Value, r.ReadUInt64());
            Assert.Equal(0u, r.ReadUInt32());
            Assert.Equal((byte)ReactState.Defensive, r.ReadByte());   // a player's pet is defensive (SpellEffects.cpp:2400)
            Assert.Equal((byte)CommandState.Follow, r.ReadByte());
            Assert.Equal(0, r.ReadByte());
            Assert.Equal(0, r.ReadByte());                            // enabled
            uint[] words = new uint[10];
            for (int i = 0; i < words.Length; i++)
            {
                words[i] = r.ReadUInt32();
            }

            Assert.Equal(
                [0x07000002u, 0x07000001u, 0x07000000u, 0x81000000u, 0x81000000u, 0x81000000u, 0x81000000u, 0x06000002u, 0x06000001u, 0x06000000u],
                words);
            Assert.Equal(0, r.ReadByte()); // spells
            Assert.Equal(0, r.ReadByte()); // cooldowns
            Assert.Equal(0, r.Remaining);

            CharmInfo charm = pet.Summon!.Charm!;
            Assert.Equal(pet.Guid.Entry, charm.PetNumber);            // Pet::Create: the pet number is in the GUID
            Assert.Equal(0u, pet.GetUInt32(UpdateFields.UnitFieldPetnumber)); // SetPetNumber(n, false)
            Assert.Equal(1000u, pet.GetUInt32(UpdateFields.UnitFieldPetnextlevelexp));

            session.Clear();
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, (uint)CommandState.Dismiss, ActionType.Command)));

            Assert.Empty(kit.Creatures.Creatures);
            Assert.True(owner.PetGuid.IsEmpty);
            Assert.Equal(new byte[8], Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells)));
        }
    }

    [Fact]
    public void GuardiansAndMiniPets_GetACharmInfoButNoActionBar()
    {
        using var kit = new PetTestKit();
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        session.Clear();

        kit.Cast(owner, GuardianSpell);
        kit.Cast(owner, CritterSpell);

        Assert.Empty(Packets(session, WorldOpcode.SmsgPetSpells));
        Creature guardian = kit.Map.Pets!.GuardiansOf(owner).Single();
        Creature critter = kit.Map.Pets!.MiniPetOf(owner)!;

        // vmangos Pet::Pet: a guardian is always aggressive, a mini pet always passive and never attackable
        Assert.Equal(ReactState.Aggressive, guardian.Summon!.Charm!.ReactState);
        Assert.Equal(ReactState.Passive, critter.Summon!.Charm!.ReactState);
        Assert.Equal(UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc, critter.UnitFlags & (UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc));
        Assert.Equal(0u, (uint)(guardian.UnitFlags & UnitFlags.ImmuneToPlayer));
        Assert.Equal(PetConstants.MiniPetFollowAngle, critter.Summon.FollowAngle);
        Assert.Equal(0x38, critter.GetByte(UpdateFields.UnitFieldBytes2, 1));
        Assert.Equal(guardian.Guid.Entry, guardian.Summon.Charm!.PetNumber);
    }

    [Fact]
    public void ReactStates_AreSetByTheirButtons_AndPassiveStopsTheAttack()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            CharmInfo charm = pet.Summon!.Charm!;
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, 2, ActionType.Reaction)));
            Assert.Equal(ReactState.Aggressive, charm.ReactState);

            Creature enemy = Enemy(kit, 6, 5);
            kit.Map.Combat.Attack(pet, enemy);
            Assert.Same(enemy, pet.Combat.Victim);

            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, 0, ActionType.Reaction)));
            Assert.Equal(ReactState.Passive, charm.ReactState);
            Assert.Null(pet.Combat.Victim);

            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, 1, ActionType.Reaction)));
            Assert.Equal(ReactState.Defensive, charm.ReactState);

            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, 7, ActionType.Reaction))); // unknown: ignored
            Assert.Equal(ReactState.Defensive, charm.ReactState);
        }
    }

    [Fact]
    public void OnlyTheOwnerCanCommandAPet_AndADeadOrDisabledPetIgnoresIt()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            (Player stranger, _) = kit.AddPlayer(2, 50, 50);
            kit.Controller.HandleAction(stranger, PetPackets.ReadAction(Action(pet.Guid, 2, ActionType.Reaction)));
            Assert.Equal(ReactState.Defensive, pet.Summon!.Charm!.ReactState);

            pet.Summon.Charm.Enabled = false; // vmangos Pet::IsEnabled: a greyed bar
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, 2, ActionType.Reaction)));
            Assert.Equal(ReactState.Defensive, pet.Summon.Charm.ReactState);

            pet.Summon.Charm.Enabled = true;
            pet.Health = 0;
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, 2, ActionType.Reaction)));
            Assert.Equal(ReactState.Defensive, pet.Summon.Charm.ReactState);
        }
    }

    [Fact]
    public void StayAndFollowCommands_SetTheCharmFlagsAndTheMovement()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            CharmInfo charm = pet.Summon!.Charm!;
            kit.Controller.HandleCommand(pet, CommandState.Follow, null);
            Assert.Equal(CommandState.Follow, charm.CommandState);
            Assert.Equal(MovementGeneratorType.Follow, pet.Motion.CurrentType);
            Assert.Same(owner, pet.Motion.TargetedUnit);
            Assert.True(charm.IsReturning);
            Assert.True(charm.IsCommandFollow);
            Assert.False(charm.IsAtStay);

            kit.Controller.HandleCommand(pet, CommandState.Stay, null);
            Assert.Equal(CommandState.Stay, charm.CommandState);
            Assert.NotEqual(MovementGeneratorType.Follow, pet.Motion.CurrentType);
            Assert.True(charm.IsAtStay);
            Assert.False(charm.IsReturning);
            Assert.False(charm.IsCommandFollow);
            Assert.Equal((pet.X, pet.Y, pet.Z), charm.StayPosition);
        }
    }

    [Fact]
    public void AttackCommand_NeedsAValidTarget_AndStartsTheFight()
    {
        (PetTestKit kit, Player owner, FakeSession session, Creature pet) = Summoned();
        using (kit)
        {
            session.Clear();
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, (uint)CommandState.Attack, ActionType.Command)));
            Assert.Equal([(byte)PetFeedback.NothingToAttack], Assert.Single(Packets(session, WorldOpcode.SmsgPetActionFeedback)));

            // the owner itself is no valid attack target (vmangos IsValidAttackTarget)
            session.Clear();
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, (uint)CommandState.Attack, ActionType.Command, owner.Guid)));
            Assert.Equal([(byte)PetFeedback.CantAttackTarget], Assert.Single(Packets(session, WorldOpcode.SmsgPetActionFeedback)));

            Creature enemy = Enemy(kit, 6, 5);
            session.Clear();
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, (uint)CommandState.Attack, ActionType.Command, enemy.Guid)));

            Assert.Same(enemy, pet.Combat.Victim);
            CharmInfo charm = pet.Summon!.Charm!;
            Assert.True(charm.IsCommandAttack);
            Assert.False(charm.IsAtStay || charm.IsFollowing || charm.IsCommandFollow || charm.IsReturning);

            // 90% an AI reaction (aggro growl), 10% the pet's attack talk: one of the two goes to the owner
            Assert.Equal(1, Packets(session, WorldOpcode.SmsgAiReaction).Count + Packets(session, WorldOpcode.SmsgPetActionSound).Count);
        }
    }

    [Fact]
    public void StopAttack_AndAbandon_AndNameQuery_AndPetInfo()
    {
        (PetTestKit kit, Player owner, FakeSession session, Creature pet) = Summoned();
        using (kit)
        {
            Creature enemy = Enemy(kit, 6, 5);
            kit.Map.Combat.Attack(pet, enemy);
            kit.Controller.HandleStopAttack(owner, pet.Guid);
            Assert.Null(pet.Combat.Victim);

            // CMSG_REQUEST_PET_INFO resends the bar
            session.Clear();
            kit.Controller.HandleRequestPetInfo(owner);
            Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells));

            // CMSG_PET_NAME_QUERY answers only for the right pet number
            uint number = pet.Summon!.Charm!.PetNumber;
            session.Clear();
            kit.Controller.HandleNameQuery(owner, number + 1, pet.Guid);
            Assert.Empty(Packets(session, WorldOpcode.SmsgPetNameQueryResponse));
            kit.Controller.HandleNameQuery(owner, number, pet.Guid);
            var r = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgPetNameQueryResponse)));
            Assert.Equal(number, r.ReadUInt32());
            Assert.Equal(pet.Template.Name, r.ReadCString());
            Assert.Equal(0u, r.ReadUInt32());

            // CMSG_PET_ABANDON dismisses a summoned pet
            kit.Controller.HandleAbandon(owner, pet.Guid);
            Assert.Null(kit.Creatures.FindCreature(pet.Guid));
            Assert.True(owner.PetGuid.IsEmpty);
        }
    }

    [Fact]
    public void SetAction_MovesCommandsAndReactions_NeverRemovesThem_AndPlacesLearnedSpells()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            CharmInfo charm = pet.Summon!.Charm!;
            Assert.True(charm.LearnSpell(PetBiteSpell));
            Assert.Equal(PetBiteSpell, charm.GetButton(3).Action); // the first free spell slot
            Assert.Equal((byte)ActionType.Disabled, charm.GetButton(3).Type);

            byte[] SetAction(params (uint Position, uint Data)[] actions)
            {
                var w = new PacketWriter(24);
                w.WriteUInt64(pet.Guid.Value);
                foreach ((uint position, uint data) in actions)
                {
                    w.WriteUInt32(position);
                    w.WriteUInt32(data);
                }

                return w.ToArray();
            }

            // swap attack (slot 0) and stay (slot 2): two actions, each the other's button
            uint attack = charm.GetButton(0).Packed;
            uint stay = charm.GetButton(2).Packed;
            kit.Controller.HandleSetAction(owner, PetPackets.ReadSetAction(SetAction((0, stay), (2, attack))));
            Assert.Equal(stay, charm.GetButton(0).Packed);
            Assert.Equal(attack, charm.GetButton(2).Packed);

            // a single command or reaction action is refused (PetHandler.cpp:236-243)
            kit.Controller.HandleSetAction(owner, PetPackets.ReadSetAction(SetAction((1, attack))));
            Assert.Equal(charm.GetButton(1).Packed, ActionButton.Make(1, ActionType.Command).Packed);

            // a swap whose second half does not match the bar is refused (vmangos checks both)
            kit.Controller.HandleSetAction(owner, PetPackets.ReadSetAction(SetAction((0, attack), (1, stay))));
            Assert.Equal(stay, charm.GetButton(0).Packed);

            // an invalid position is refused
            kit.Controller.HandleSetAction(owner, PetPackets.ReadSetAction(SetAction((10, ActionButton.Make(PetBiteSpell, ActionType.Enabled).Packed))));
            Assert.Equal(PetBiteSpell, charm.GetButton(3).Action);

            // move the spell to slot 5 with autocast on: the spell list follows
            kit.Controller.HandleSetAction(owner, PetPackets.ReadSetAction(SetAction((5, ActionButton.Make(PetBiteSpell, ActionType.Enabled).Packed))));
            Assert.Equal((byte)ActionType.Enabled, charm.GetButton(5).Type);
            Assert.True(charm.PetSpells[PetBiteSpell]);

            // a spell the pet does not know is dropped
            kit.Controller.HandleSetAction(owner, PetPackets.ReadSetAction(SetAction((6, ActionButton.Make(PetShieldSpell, ActionType.Enabled).Packed))));
            Assert.Equal(0u, charm.GetButton(6).Action);

            // 0 empties a spell slot
            kit.Controller.HandleSetAction(owner, PetPackets.ReadSetAction(SetAction((5, ActionButton.Make(0, ActionType.Disabled).Packed))));
            Assert.Equal(0u, charm.GetButton(5).Action);
        }
    }

    [Fact]
    public void Autocast_TogglesAKnownCastableSpell_AndIgnoresUnknownAndPassiveOnes()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            CharmInfo charm = pet.Summon!.Charm!;
            charm.LearnSpell(PetBiteSpell);
            charm.LearnSpell(PetPassiveSpell);

            kit.Controller.HandleAutocast(owner, new PetAutocastRequest(pet.Guid, PetBiteSpell, true));
            Assert.True(charm.PetSpells[PetBiteSpell]);
            Assert.Equal((byte)ActionType.Enabled, charm.GetButton(3).Type);

            kit.Controller.HandleAutocast(owner, new PetAutocastRequest(pet.Guid, PetBiteSpell, false));
            Assert.False(charm.PetSpells[PetBiteSpell]);
            Assert.Equal((byte)ActionType.Disabled, charm.GetButton(3).Type);

            kit.Controller.HandleAutocast(owner, new PetAutocastRequest(pet.Guid, PetPassiveSpell, true)); // passive
            Assert.False(charm.PetSpells[PetPassiveSpell]);
            kit.Controller.HandleAutocast(owner, new PetAutocastRequest(pet.Guid, PetShieldSpell, true)); // not learned
            Assert.False(charm.HasSpell(PetShieldSpell));
        }
    }

    [Fact]
    public void SpellButton_CastsAtTheTarget_OrAnswersWithTheCastFailure()
    {
        (PetTestKit kit, Player owner, FakeSession session, Creature pet) = Summoned();
        using (kit)
        {
            CharmInfo charm = pet.Summon!.Charm!;
            Creature enemy = Enemy(kit, 8, 5);

            // unknown to the pet: SPELL_FAILED_NOT_KNOWN (PetHandler.cpp:110-114)
            session.Clear();
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, PetBiteSpell, ActionType.Disabled, enemy.Guid)));
            AssertCastFailed(session, PetBiteSpell, SpellCastResult.NotKnown);

            charm.LearnSpell(PetBiteSpell);

            // an enemy-targeted spell without a target: SPELL_FAILED_BAD_IMPLICIT_TARGETS
            session.Clear();
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, PetBiteSpell, ActionType.Disabled)));
            AssertCastFailed(session, PetBiteSpell, SpellCastResult.BadImplicitTargets);

            // a negative spell on the pet itself: SPELL_FAILED_BAD_TARGETS
            session.Clear();
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, PetBiteSpell, ActionType.Disabled, pet.Guid)));
            AssertCastFailed(session, PetBiteSpell, SpellCastResult.BadTargets);

            // the real cast lands
            session.Clear();
            uint health = enemy.Health;
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, PetBiteSpell, ActionType.Disabled, enemy.Guid)));
            Assert.Empty(Packets(session, WorldOpcode.SmsgPetCastFailed).Select(p => $"{p[4]}/{p[5]:X}"));
            Assert.Equal(health - 8, enemy.Health);

            // and is now on cooldown (the global cooldown): SPELL_FAILED_NOT_READY
            session.Clear();
            kit.Controller.HandleAction(owner, PetPackets.ReadAction(Action(pet.Guid, PetBiteSpell, ActionType.Disabled, enemy.Guid)));
            AssertCastFailed(session, PetBiteSpell, SpellCastResult.NotReady);
        }
    }

    [Fact]
    public void PetCastSpellOpcode_RequiresTheCurrentPet_AndAKnownSpell()
    {
        (PetTestKit kit, Player owner, FakeSession session, Creature pet) = Summoned();
        using (kit)
        {
            Creature enemy = Enemy(kit, 8, 5);
            byte[] Cast(ObjectGuid unit, uint spell)
            {
                var w = new PacketWriter(24);
                w.WriteUInt64(unit.Value);
                w.WriteUInt32(spell);
                w.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
                w.WriteBytes(enemy.Guid.ToPacked());
                return w.ToArray();
            }

            // not learned: silently ignored
            uint health = enemy.Health;
            kit.Controller.HandleCast(owner, PetPackets.ReadCast(Cast(pet.Guid, PetBiteSpell)));
            Assert.Equal(health, enemy.Health);

            pet.Summon!.Charm!.LearnSpell(PetBiteSpell);
            kit.Controller.HandleCast(owner, PetPackets.ReadCast(Cast(pet.Guid, PetBiteSpell)));
            Assert.Equal(health - 8, enemy.Health);

            // a guardian is not the "current pet" (UNIT_FIELD_SUMMON): refused
            kit.Cast(owner, GuardianSpell);
            Creature guardian = kit.Map.Pets!.GuardiansOf(owner).Single();
            guardian.Summon!.Charm!.LearnSpell(PetBiteSpell);
            kit.Spells.Advance(2_000);
            kit.Controller.HandleCast(owner, PetPackets.ReadCast(Cast(guardian.Guid, PetBiteSpell)));
            Assert.Equal(health - 8, enemy.Health);
        }
    }

    [Fact]
    public void CancelAura_RemovesTheAuraFromThePet_AndAnswersForADeadPet()
    {
        (PetTestKit kit, Player owner, FakeSession session, Creature pet) = Summoned();
        using (kit)
        {
            kit.Spells.System.CastSpell(pet, SpellTestKit.HotSpell, SpellCastTargets.ForSelf(), triggered: true);
            Assert.True(kit.Spells.System.HasAura(pet, SpellTestKit.HotSpell));

            kit.Controller.HandleCancelAura(owner, pet.Guid, SpellTestKit.HotSpell);
            Assert.False(kit.Spells.System.HasAura(pet, SpellTestKit.HotSpell));

            session.Clear();
            pet.Health = 0;
            kit.Controller.HandleCancelAura(owner, pet.Guid, SpellTestKit.HotSpell);
            Assert.Equal([(byte)PetFeedback.PetDead], Assert.Single(Packets(session, WorldOpcode.SmsgPetActionFeedback)));
        }
    }

    [Fact]
    public void SetEnabled_GreysTheBarOut_AndSendsThePetMode()
    {
        (PetTestKit kit, Player owner, FakeSession session, Creature pet) = Summoned();
        using (kit)
        {
            session.Clear();
            kit.Controller.SetEnabled(pet, false);

            Assert.False(pet.Summon!.Charm!.Enabled);
            // wow_messages smsg_pet_mode.wowm: guid, react, command, 0, enabled byte (0x8 = disabled, vmangos Pet.cpp:2374)
            byte[] expected = [.. BitConverter.GetBytes(pet.Guid.Value), (byte)ReactState.Defensive, (byte)CommandState.Follow, 0, 0x8];
            Assert.Equal(expected, Assert.Single(Packets(session, WorldOpcode.SmsgPetMode)));

            kit.Controller.SendPetSpells(owner, pet);
            Assert.Equal(0x8, Packets(session, WorldOpcode.SmsgPetSpells)[0][15]);

            kit.Controller.SetEnabled(pet, true);
            Assert.True(pet.Summon.Charm.Enabled);
        }
    }

    private static void AssertCastFailed(FakeSession session, uint spell, SpellCastResult result)
    {
        byte[] packet = Assert.Single(Packets(session, WorldOpcode.SmsgPetCastFailed));
        Assert.Equal(spell, BitConverter.ToUInt32(packet));
        Assert.Equal(2, packet[4]);
        Assert.Equal((byte)result, packet[5]);
    }
}
