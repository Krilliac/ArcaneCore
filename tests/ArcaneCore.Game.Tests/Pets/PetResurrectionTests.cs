using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

public sealed class PetResurrectionTests
{
    private const uint Revive = 963113;
    private const uint Sacrifice = 963114;
    private const uint OtherScript = 963115;
    private const uint Persistent = 963116;

    [Theory]
    [InlineData(37, 37u)]
    [InlineData(150, 100u)]
    [InlineData(0, 0u)]
    [InlineData(-1, 100u)]
    public void FlatPetRevival_RestoresSameCorpseWithFreshAiAndPreservesPowers(int health, uint expected)
    {
        using PetTestKit kit = Kit(health);
        (Player owner, FakeSession session) = kit.AddPlayer(1, 5, 6);
        Creature pet = DeadPet(kit, owner);
        kit.Run(100); // the owner now sees the corpse at its original position
        CreatureAI? oldAi = pet.AI;
        pet.SetUInt32(UpdateFields.UnitFieldPower1, 7);
        pet.UnitFlags |= UnitFlags.Skinnable;
        pet.SetUInt32(UpdateFields.UnitDynamicFlags, 0xFF);
        owner.Relocate(20, 21, 22, 1.3f, 0);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, Revive, SpellCastTargets.ForUnit(pet.Guid)));

        Assert.Equal(CreatureDeathState.Alive, pet.DeathState);
        Assert.Equal(DeathState.Alive, pet.Combat.DeathState);
        Assert.Equal(expected, pet.Health);
        Assert.Equal(7u, pet.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(0u, pet.GetUInt32(UpdateFields.UnitDynamicFlags));
        Assert.Equal(UnitFlags.None, pet.UnitFlags & UnitFlags.Skinnable);
        Assert.Equal((owner.X, owner.Y, owner.Z, owner.Orientation), (pet.X, pet.Y, pet.Z, pet.Orientation));
        Assert.IsType<PetAI>(pet.AI);
        Assert.NotSame(oldAi, pet.AI);
        Assert.Same(pet, kit.Creatures.FindCreature(owner.PetGuid));
        Assert.Equal(0u, pet.CorpseDecayMs);
        Assert.Equal(0L, pet.RespawnAtMs);
        List<byte[]> teleports = Packets(session, WorldOpcode.MsgMoveTeleport);
        Assert.Equal(2, teleports.Count); // source broadcasts before and after relocation
        foreach (byte[] packet in teleports)
        {
            var reader = new PacketReader(packet);
            Assert.Equal(pet.Guid.Value, reader.ReadPackedGuid());
            MovementInfo movement = MovementInfo.Read(ref reader);
            Assert.Equal((owner.X, owner.Y, owner.Z, owner.Orientation), (movement.X, movement.Y, movement.Z, movement.Orientation));
            Assert.Equal(0, reader.Remaining);
        }

        // ALIVE with source-permitted zero health is still an already revived pet.
        CreatureAI? revivedAi = pet.AI;
        owner.Relocate(30, 31, 32, 2, 0);
        kit.Cast(owner, Revive, SpellCastTargets.ForUnit(pet.Guid));
        Assert.Same(revivedAi, pet.AI);
        Assert.Equal(20f, pet.X);
    }

    [Fact]
    public void RevivedPet_SurvivesOriginalCorpseDeadlineAndFollowsItsOwner()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = DeadPet(kit, owner);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, Revive, SpellCastTargets.ForUnit(pet.Guid)));
        kit.Run(60_000);
        Assert.Same(pet, kit.Creatures.FindCreature(pet.Guid));
        Assert.Equal(pet.Guid, owner.PetGuid);
        Assert.True(pet.IsAlive);
        Assert.IsType<PetAI>(pet.AI);
    }

    [Fact]
    public void Revive_PreservesExactDeathPersistentAuraHolderAndItsContribution()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(pet, Persistent));
        SpellAuraHolder holder = Assert.Single(kit.Spells.System.GetAuras(pet));
        int strength = pet.GetInt32(UpdateFields.UnitFieldStat0);
        kit.Creatures.KillCreature(pet);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, Revive, SpellCastTargets.ForUnit(pet.Guid)));
        Assert.Equal(CreatureDeathState.Alive, pet.DeathState);
        Assert.Same(holder, Assert.Single(kit.Spells.System.GetAuras(pet)));
        Assert.False(holder.IsRemoved);
        Assert.Equal(strength, pet.GetInt32(UpdateFields.UnitFieldStat0));
        Assert.True(SpellSystem.HasLiveCasterOwnership(holder));
        Assert.Same(pet, kit.Spells.System.ResolveAuraActor(holder));
    }

    [Fact]
    public void Revive_RemovesOnlyOwnersDemonicSacrificeOverrideAuras()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        (Player caster, _) = kit.AddPlayer(2);
        Creature pet = DeadPet(kit, owner);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, Sacrifice));
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, OtherScript));
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, Sacrifice));
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, Revive, SpellCastTargets.ForUnit(pet.Guid)));
        Assert.True(pet.IsAlive);
        Assert.False(kit.Spells.System.HasAura(owner, Sacrifice));
        Assert.True(kit.Spells.System.HasAura(owner, OtherScript));
        Assert.True(kit.Spells.System.HasAura(caster, Sacrifice));
    }

    [Fact]
    public void HeldOwner_BlocksForeignCastersRevivalUntilRetried()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        (Player caster, _) = kit.AddPlayer(2);
        Creature pet = DeadPet(kit, owner);
        kit.Cast(owner, Sacrifice);
        Guid operation = Guid.NewGuid();
        Assert.True(owner.BeginQuestSettlement(operation));
        kit.Cast(caster, Revive, SpellCastTargets.ForUnit(pet.Guid));
        Assert.Equal(CreatureDeathState.Corpse, pet.DeathState);
        Assert.True(kit.Spells.System.HasAura(owner, Sacrifice));
        Assert.True(owner.EndQuestSettlement(operation));
        kit.Cast(caster, Revive, SpellCastTargets.ForUnit(pet.Guid));
        Assert.True(pet.IsAlive);
        Assert.False(kit.Spells.System.HasAura(owner, Sacrifice));
    }

    [Fact]
    public void StaleOwnerLink_DoesNotRevivePet()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = DeadPet(kit, owner);
        owner.SetPetGuid(ObjectGuid.Empty);
        kit.Cast(owner, Revive, SpellCastTargets.ForUnit(pet.Guid));
        Assert.Equal(CreatureDeathState.Corpse, pet.DeathState);
        Assert.Equal(0u, pet.Health);
    }

    [Theory]
    [InlineData(PetTestKit.GuardianSpell)]
    [InlineData(PetTestKit.WildSpell)]
    [InlineData(PetTestKit.CritterSpell)]
    public void OtherSummonKinds_AreNotRevived(uint summon)
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, summon);
        Creature creature = Assert.Single(kit.Creatures.Creatures);
        kit.Creatures.KillCreature(creature);
        kit.Cast(owner, Revive, SpellCastTargets.ForUnit(creature.Guid));
        Assert.Equal(CreatureDeathState.Corpse, creature.DeathState);
    }

    [Fact]
    public void PercentagePlayerResurrection_DoesNotRevivePet()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = DeadPet(kit, owner);
        kit.Spells.System.Store = new SpellStore([.. kit.Spells.System.Store.All,
            Revival(963118, 50) with { Effects = [Effect(SpellEffectName.Resurrect, 50, SpellImplicitTarget.Unit)] }], [], []);
        kit.Cast(owner, 963118, SpellCastTargets.ForUnit(pet.Guid));
        Assert.Equal(CreatureDeathState.Corpse, pet.DeathState);
    }

    private static Creature DeadPet(PetTestKit kit, Player owner)
    {
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        kit.Creatures.KillCreature(pet);
        Assert.Equal(CreatureDeathState.Corpse, pet.DeathState);
        return pet;
    }

    private static PetTestKit Kit(int health = 37) => new([
        Revival(Revive, health),
        Spell(Sacrifice, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.OverrideClassScripts, misc: 2228)) with { Duration = new SpellDuration(-1, 0, -1) },
        Spell(OtherScript, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.OverrideClassScripts, misc: 2229)) with { Duration = new SpellDuration(-1, 0, -1) },
        Spell(Persistent, Effect(SpellEffectName.ApplyAura, 7, aura: AuraType.ModStat, misc: 0)) with { AttributesEx3 = 0x00100000, Duration = new SpellDuration(-1, 0, -1) },
    ]);

    private static SpellInfo Revival(uint id, int health) => Spell(id, Effect(SpellEffectName.ResurrectNew, health, SpellImplicitTarget.Unit)) with
    {
        AttributesEx2 = SpellAttributesEx2.AllowDeadTarget, RangeIndex = 4, Range = new SpellRange(0, 100),
        StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };
}
