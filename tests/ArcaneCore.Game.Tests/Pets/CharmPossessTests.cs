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

/// <summary>
/// Charm and possession (docs/areas/unit-control.md): SPELL_AURA_MOD_POSSESS, SPELL_AURA_MOD_CHARM, SPELL_AURA_MOD_POSSESS_PET and
/// SPELL_AURA_AOE_CHARM against vmangos Aura::HandleModPossess / Unit::ModPossess, HandleModCharm, Player::ModPossessPet and
/// HandleAuraAoeCharm (SpellAuras.cpp:2954-3442, 5740-5748), the pet commands on a charm (Unit::HandlePetCommand) and the charm cast
/// checks (Spell.cpp:6292-6363). Only fields, packets and the pet opcodes are used, so these run against a build without the feature.
/// </summary>
public sealed class CharmPossessTests
{
    public const uint PossessSpell = 920_001;
    public const uint CharmSpell = 920_002;
    public const uint EyesOfTheBeast = 920_003;
    public const uint OtherAoeCharm = 920_004;
    public const uint DismissFirstCharm = 920_005;
    public const uint ChainsOfKelThuzad = 28_410;
    public const uint LowCharm = 920_006;

    private const uint EnemyFaction = 14;

    private static SpellInfo Ranged(SpellInfo spell, int durationMs) => spell with
    {
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        Duration = new SpellDuration(durationMs, 0, durationMs),
    };

    public static IEnumerable<SpellInfo> ControlSpells() =>
    [
        Ranged(Spell(PossessSpell, Effect(SpellEffectName.ApplyAura, 60, SpellImplicitTarget.UnitEnemy, aura: AuraType.ModPossess)), 10_000),
        Ranged(Spell(CharmSpell, Effect(SpellEffectName.ApplyAura, 60, SpellImplicitTarget.UnitEnemy, aura: AuraType.ModCharm)), 300_000),
        Ranged(Spell(LowCharm, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitEnemy, aura: AuraType.ModCharm)), 300_000),
        Ranged(Spell(DismissFirstCharm, Effect(SpellEffectName.ApplyAura, 60, SpellImplicitTarget.UnitEnemy, aura: AuraType.ModCharm)), 300_000)
            with { AttributesEx = (SpellAttributesEx)0x00000001 },
        Ranged(Spell(ChainsOfKelThuzad, Effect(SpellEffectName.ApplyAura, 60, SpellImplicitTarget.UnitEnemy, aura: AuraType.AoeCharm)), 20_000),
        Ranged(Spell(OtherAoeCharm, Effect(SpellEffectName.ApplyAura, 60, SpellImplicitTarget.UnitEnemy, aura: AuraType.AoeCharm)), 20_000),
        Spell(EyesOfTheBeast, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCasterPet, aura: AuraType.ModPossessPet)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        },
    ];

    private static PetTestKit Kit() => new(ControlSpells());

    private static Creature Mob(PetTestKit kit, float x = 8, float y = 5)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, x, y, 83.5f, 0);

    private static SpellCastResult CastAt(PetTestKit kit, Unit caster, uint spell, Unit target)
        => kit.Spells.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    private static List<(ulong Guid, byte Allow)> ControlUpdates(FakeSession session)
        => [.. Packets(session, WorldOpcode.SmsgClientControlUpdate).Select(p =>
        {
            var r = new PacketReader(p);
            ulong guid = r.ReadPackedGuid();
            return (guid, r.ReadByte());
        })];

    private static uint[] Bar(ref PacketReader r)
    {
        uint[] words = new uint[10];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = r.ReadUInt32();
        }

        return words;
    }

    // --- possession ---------------------------------------------------------------------------------------------

    [Fact]
    public void Possess_GivesThePlayerTheCreature_ItsCameraMoverAndAnEmptyPossessBar()
    {
        using PetTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, CastAt(kit, player, PossessSpell, mob));

        // the target: possessed by the player, with the player's faction (Unit::ModPossess apply)
        Assert.Equal(player.Guid, mob.CharmerGuid);
        Assert.NotEqual(0u, (uint)(mob.UnitFlags & UnitFlags.Possessed));
        Assert.Equal(player.FactionTemplate, mob.FactionTemplate);
        Assert.IsType<PetAI>(mob.AI);

        // the caster: charm field and camera on the target
        Assert.Equal(mob.Guid, player.CharmGuid);
        Assert.Equal(mob.Guid.Value, player.GetUInt64(UpdateFields.PlayerFarsight));

        // SMSG_PET_SPELLS (Player::PossessSpellInitialize): guid, the aura's duration, u32 0, attack + nine empty passive slots, no spells
        var r = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells)));
        Assert.Equal(mob.Guid.Value, r.ReadUInt64());
        Assert.InRange(r.ReadInt32(), 9_000, 10_000);
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal([0x07000002u, .. Enumerable.Repeat(0x01000000u, 9)], Bar(ref r));
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.ReadByte());

        // Unit::UpdateControl: the possessing client may move the creature
        Assert.Contains((mob.Guid.Value, (byte)1), ControlUpdates(session));
    }

    [Fact]
    public void PossessionEnds_ThePlayerGetsItsControlBack_AndTheCreatureTurnsOnIt()
    {
        using PetTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        CastAt(kit, player, PossessSpell, mob);
        session.Clear();

        kit.Spells.Advance(10_000);

        Assert.True(player.CharmGuid.IsEmpty);
        Assert.Equal(0ul, player.GetUInt64(UpdateFields.PlayerFarsight));
        Assert.Equal([(player.Guid.Value, (byte)1), (mob.Guid.Value, (byte)0)], ControlUpdates(session));
        Assert.Equal(new byte[8], Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells))); // Player::RemovePetActionBar

        Assert.True(mob.CharmerGuid.IsEmpty);
        Assert.Equal(0u, (uint)(mob.UnitFlags & UnitFlags.Possessed));
        Assert.Equal(EnemyFaction, mob.FactionTemplate);
        Assert.IsNotType<PetAI>(mob.AI);

        // AttackedBy + threat equal to its maximum health (SpellAuras.cpp:3105-3114)
        Assert.Same(player, mob.Combat.Victim);
        Assert.Equal(mob.MaxHealth, (uint)mob.Combat.Threat.GetThreat(player));
    }

    [Fact]
    public void APossessedCreature_ThatDies_DoesNotTurnOnItsFormerMaster()
    {
        using PetTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        CastAt(kit, player, PossessSpell, mob);
        session.Clear();

        kit.Map.Combat.Kill(player, mob);
        kit.Spells.System.OnUnitDied(mob);

        Assert.True(player.CharmGuid.IsEmpty);
        Assert.Equal(0ul, player.GetUInt64(UpdateFields.PlayerFarsight));
        Assert.Equal(0f, mob.Combat.Threat.GetThreat(player));
    }

    [Fact]
    public void APossessedPlayer_LosesControlOfItself_UntilThePossessionEnds()
    {
        using PetTestKit kit = Kit();
        (Player priest, FakeSession priestSession) = kit.AddPlayer(1, 5, 5);
        (Player victim, FakeSession victimSession) = kit.AddPlayer(2, 8, 5);
        victim.UnitFlags |= UnitFlags.Pvp; // a valid PvP target
        uint raceFaction = victim.FactionTemplate;
        priest.FactionTemplate = 2; // the possessed player takes the possessor's faction
        priestSession.Clear();
        victimSession.Clear();

        Assert.Equal(SpellCastResult.CastOk, CastAt(kit, priest, PossessSpell, victim));
        Assert.Equal(priest.Guid, victim.CharmerGuid);
        Assert.Equal(2u, victim.FactionTemplate);
        Assert.Contains((victim.Guid.Value, (byte)0), ControlUpdates(victimSession));
        Assert.Contains((victim.Guid.Value, (byte)1), ControlUpdates(priestSession));

        victimSession.Clear();
        kit.Spells.System.RemoveAuras(victim, PossessSpell);

        Assert.True(victim.CharmerGuid.IsEmpty);
        Assert.Equal(raceFaction, victim.FactionTemplate);
        Assert.Contains((victim.Guid.Value, (byte)1), ControlUpdates(victimSession));
    }

    // --- charm --------------------------------------------------------------------------------------------------

    [Fact]
    public void Charm_MakesTheCreatureAPetOfTheCaster_ThatFollowsAndObeys()
    {
        using PetTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, CastAt(kit, player, CharmSpell, mob));

        Assert.Equal(player.Guid, mob.CharmerGuid);
        Assert.Equal(mob.Guid, player.CharmGuid);
        Assert.Equal(player.FactionTemplate, mob.FactionTemplate);
        Assert.NotEqual(0u, (uint)(mob.UnitFlags & UnitFlags.PlayerControlled));
        Assert.Equal(0u, (uint)(mob.UnitFlags & UnitFlags.Possessed));
        Assert.Equal(0ul, player.GetUInt64(UpdateFields.PlayerFarsight)); // a charm moves no camera
        Assert.IsType<PetAI>(mob.AI);
        Assert.Equal(MovementGeneratorType.Follow, mob.Motion.CurrentType);
        Assert.Same(player, mob.Motion.TargetedUnit);

        // SMSG_PET_SPELLS (Player::CharmSpellInitialize): defensive, follow, the pet bar, no spells (not a warlock's demon)
        var r = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells)));
        Assert.Equal(mob.Guid.Value, r.ReadUInt64());
        Assert.InRange(r.ReadInt32(), 299_000, 300_000);
        Assert.Equal((byte)ReactState.Defensive, r.ReadByte());
        Assert.Equal((byte)CommandState.Follow, r.ReadByte());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(
            [0x07000002u, 0x07000001u, 0x07000000u, 0x81000000u, 0x81000000u, 0x81000000u, 0x81000000u, 0x06000002u, 0x06000001u, 0x06000000u],
            Bar(ref r));
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.Remaining);

        // the pet opcodes work on the charm: attack an enemy
        Creature enemy = Mob(kit, 6, 6);
        kit.Controller.HandleAction(player, PetPackets.ReadAction(PetAction(mob.Guid, (uint)CommandState.Attack, ActionType.Command, enemy.Guid)));
        Assert.Same(enemy, mob.Combat.Victim);
    }

    [Fact]
    public void DismissingACharm_ReleasesIt_AndItTurnsOnItsFormerCharmer()
    {
        using PetTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        CastAt(kit, player, CharmSpell, mob);
        session.Clear();

        // review finding 32: COMMAND_DISMISS on a charmed unit is pCharmer->Uncharm()
        kit.Controller.HandleAction(player, PetPackets.ReadAction(PetAction(mob.Guid, (uint)CommandState.Dismiss, ActionType.Command)));

        Assert.True(mob.IsAlive);
        Assert.Same(mob, kit.Creatures.FindCreature(mob.Guid)); // released, not despawned
        Assert.True(mob.CharmerGuid.IsEmpty);
        Assert.True(player.CharmGuid.IsEmpty);
        Assert.Equal(EnemyFaction, mob.FactionTemplate);
        Assert.Equal(0u, (uint)(mob.UnitFlags & UnitFlags.PlayerControlled));
        Assert.False(kit.Spells.System.HasAura(mob, CharmSpell));
        Assert.Equal(new byte[8], Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells)));
        Assert.Same(player, mob.Combat.Victim);
    }

    [Fact]
    public void TheCharmersDeath_EndsTheCharm()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        CastAt(kit, player, CharmSpell, mob);

        kit.Map.Combat.Kill(null, player);
        kit.Spells.System.OnUnitDied(player);

        Assert.True(mob.CharmerGuid.IsEmpty);
        Assert.Equal(EnemyFaction, mob.FactionTemplate);
        Assert.False(kit.Spells.System.HasAura(mob, CharmSpell));
    }

    [Fact]
    public void TheCharmerLeavingTheMap_EndsTheCharm()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        CastAt(kit, player, CharmSpell, mob);

        kit.Map.RemovePlayer(player);
        kit.Run(100);

        Assert.True(mob.CharmerGuid.IsEmpty);
        Assert.Equal(EnemyFaction, mob.FactionTemplate);
        Assert.False(kit.Spells.System.HasAura(mob, CharmSpell));
    }

    [Fact]
    public void ADespawnedCharm_GivesThePlayerItsCharmFieldAndBarBack()
    {
        using PetTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        CastAt(kit, player, PossessSpell, mob);
        session.Clear();

        kit.Creatures.Despawn(mob);
        kit.Run(100);

        Assert.True(player.CharmGuid.IsEmpty);
        Assert.Equal(0ul, player.GetUInt64(UpdateFields.PlayerFarsight));
        Assert.Contains((player.Guid.Value, (byte)1), ControlUpdates(session));
        Assert.Equal(new byte[8], Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells)));
    }

    [Fact]
    public void AWarlocksCharmedDemon_GetsAPetNumberAndClass_AndLosesThemAtTheEnd()
    {
        var kit = new PetTestKit(ControlSpells());
        using (kit)
        {
            (Player warlock, _) = kit.AddPlayer(1, 5, 5);
            warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
            Creature demon = kit.Creatures.SpawnTemporary(
                kit.Content.FindTemplate(NpcCasterEntry)! with { CreatureType = 3, UnitClass = 0 }, 8, 5, 83.5f, 0);
            demon.SetByte(UpdateFields.UnitFieldBytes0, 1, 0);

            CastAt(kit, warlock, CharmSpell, demon);
            Assert.Equal((byte)Class.Mage, demon.GetByte(UpdateFields.UnitFieldBytes0, 1));
            Assert.NotEqual(0u, demon.GetUInt32(UpdateFields.UnitFieldPetnumber));
            Assert.NotEqual(0u, demon.GetUInt32(UpdateFields.UnitFieldPetNameTimestamp));

            kit.Spells.System.RemoveAuras(demon, CharmSpell);
            Assert.Equal(0u, demon.GetUInt32(UpdateFields.UnitFieldPetnumber));
        }
    }

    // --- AoE charm ----------------------------------------------------------------------------------------------

    [Fact]
    public void AoeCharm_ChargesOnlyChainsOfKelThuzad()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        Creature first = Mob(kit);
        Creature second = Mob(kit, 9, 6);

        CastAt(kit, player, OtherAoeCharm, first);
        Assert.True(kit.Spells.System.HasAura(first, OtherAoeCharm));
        Assert.True(first.CharmerGuid.IsEmpty); // vmangos HandleAuraAoeCharm: any other spell does nothing

        CastAt(kit, player, ChainsOfKelThuzad, second);
        Assert.Equal(player.Guid, second.CharmerGuid);
        Assert.Equal(second.Guid, player.CharmGuid);
    }

    // --- possess pet --------------------------------------------------------------------------------------------

    [Fact]
    public void EyesOfTheBeast_MovesTheCameraAndMoverToThePet_AndTheLeashDoesNotHoldIt()
    {
        using PetTestKit kit = Kit();
        ArcaneCore.Game.Spells.Utility.Targets.PetTargets.Install(kit.Spells.System); // TARGET_UNIT_CASTER_PET (the world's PetTargetFeature)
        (Player hunter, FakeSession session) = kit.AddPlayer(1, 5, 5);
        kit.Cast(hunter, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(hunter, EyesOfTheBeast));
        Assert.Equal(hunter.Guid, pet.CharmerGuid);
        Assert.Equal(pet.Guid, hunter.CharmGuid);
        Assert.Equal(pet.Guid.Value, hunter.GetUInt64(UpdateFields.PlayerFarsight));
        Assert.NotEqual(0u, (uint)(pet.UnitFlags & UnitFlags.Possessed));
        Assert.Equal(hunter.FactionTemplate, pet.FactionTemplate);
        Assert.Contains((pet.Guid.Value, (byte)1), ControlUpdates(session));

        // review finding 32: the 120 yd leash does not unsummon a pet its owner is possessing (Pet.cpp:670)
        pet.Relocate(5 + 150, 5, 83.5f, 0, kit.Spells.Now);
        kit.Run(500);
        Assert.Same(pet, kit.Creatures.FindCreature(pet.Guid));

        kit.Spells.System.RemoveAuras(pet, EyesOfTheBeast);
        Assert.True(hunter.CharmGuid.IsEmpty);
        Assert.True(pet.CharmerGuid.IsEmpty);
        Assert.Equal(0ul, hunter.GetUInt64(UpdateFields.PlayerFarsight));
    }

    // --- cast checks --------------------------------------------------------------------------------------------

    [Fact]
    public void CharmChecks_RefuseACharmedTarget_AHighLevelTarget_AndASecondCharm()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        (Player other, _) = kit.AddPlayer(2, 5, 6);
        Creature mob = Mob(kit);
        Creature second = Mob(kit, 9, 6);

        Assert.Equal(SpellCastResult.Highlevel, CastAt(kit, player, LowCharm, mob)); // level 10 above the value 5
        Assert.Equal(SpellCastResult.CastOk, CastAt(kit, player, CharmSpell, mob));
        Assert.Equal(SpellCastResult.Charmed, CastAt(kit, other, CharmSpell, mob));
        Assert.Equal(SpellCastResult.AlreadyHaveCharm, CastAt(kit, player, CharmSpell, second));

        // SPELL_ATTR_EX_DISMISS_PET_FIRST uncharms first
        Assert.Equal(SpellCastResult.CastOk, CastAt(kit, player, DismissFirstCharm, second));
        Assert.True(mob.CharmerGuid.IsEmpty);
        Assert.Equal(player.Guid, second.CharmerGuid);
    }

    [Fact]
    public void CharmChecks_APetMustBeDismissedFirst_AndOnlyAPlayerPossesses()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        kit.Cast(player, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Creature mob = Mob(kit);

        Assert.Equal(SpellCastResult.AlreadyHaveSummon, CastAt(kit, player, CharmSpell, mob));
        Assert.Equal(SpellCastResult.CastOk, CastAt(kit, player, DismissFirstCharm, mob));
        Assert.Null(kit.Creatures.FindCreature(pet.Guid));

        Creature caster = Mob(kit, 20, 20);
        Creature victim = Mob(kit, 21, 20);
        Assert.Equal(SpellCastResult.BadTargets, CastAt(kit, caster, PossessSpell, victim));
    }

    [Fact]
    public void EyesOfTheBeast_NeedsAPet()
    {
        using PetTestKit kit = Kit();
        ArcaneCore.Game.Spells.Utility.Targets.PetTargets.Install(kit.Spells.System);
        (Player hunter, _) = kit.AddPlayer(1, 5, 5);
        kit.Spells.Spellbook.Teach(hunter, EyesOfTheBeast);
        Assert.Equal(SpellCastResult.NoPet, kit.Spells.System.CastSpell(hunter, EyesOfTheBeast, SpellCastTargets.ForSelf(), triggered: false));
    }

    private static byte[] PetAction(ObjectGuid pet, uint action, ActionType type, ObjectGuid target = default)
    {
        var w = new PacketWriter(20);
        w.WriteUInt64(pet.Value);
        w.WriteUInt32(ActionButton.Make(action, type).Packed);
        w.WriteUInt64(target.Value);
        return w.ToArray();
    }
}
