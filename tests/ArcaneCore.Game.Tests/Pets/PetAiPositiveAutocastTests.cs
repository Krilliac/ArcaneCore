using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>vmangos PetAI positive and ally autocast (AI/PetAI.cpp UpdateAI positive branch, UpdateAllies, Spell::CanAutoCast).</summary>
public sealed class PetAiPositiveAutocastTests
{
    private const uint DashSpell = 910100;
    private const uint HowlSpell = 910101;
    private const uint FireShieldSpell = 910102;
    private const uint SacrificeSpell = 910103;
    private const uint TaintedBloodSpell = 910104;
    private const uint FakeShieldSpell = 910105;

    private static IEnumerable<SpellInfo> Spells() =>
    [
        // Dash: self aura, cooldown 30 s > duration 15 s: combat only.
        Spell(DashSpell, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.ModIncreaseSpeed)) with
        {
            Duration = new SpellDuration(15_000, 0, 15_000),
            RecoveryTime = 30_000,
        },
        // Furious Howl: MOD_DAMAGE_DONE on a friend: combat only.
        Spell(HowlSpell, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitFriend, aura: AuraType.ModDamageDone)) with
        {
            Duration = new SpellDuration(20_000, 0, 20_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        },
        // Fire Shield: friend aura, warlock imp-buff family + visual 289 override the combat-only rules.
        Spell(FireShieldSpell, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitFriend, aura: AuraType.ModResistance)) with
        {
            Duration = new SpellDuration(120_000, 0, 120_000),
            RecoveryTime = 130_000,
            SpellVisual = 289,
            SpellFamilyName = 5,
            SpellFamilyFlags = 1UL << 23,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        },
        // Sacrifice-like: kills the caster.
        Spell(SacrificeSpell, Effect(SpellEffectName.Instakill, 0)),
        // Tainted Blood-shaped: icon 153 forces combat-only although cooldown and duration alone would not.
        Spell(TaintedBloodSpell, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance)) with
        {
            Duration = new SpellDuration(15_000, 0, 15_000),
            SpellIconId = 153,
        },
        // Visual 289 without the imp-buff family: no exemption, so cooldown > duration keeps it combat-only.
        Spell(FakeShieldSpell, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitFriend, aura: AuraType.ModResistance)) with
        {
            Duration = new SpellDuration(120_000, 0, 120_000),
            RecoveryTime = 130_000,
            SpellVisual = 289,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        },
    ];

    private static (PetTestKit Kit, Player Owner, Creature Pet) Summoned()
    {
        var kit = new PetTestKit(Spells());
        (Player owner, _) = kit.AddPlayer(1, 5, 5);
        kit.Cast(owner, PetTestKit.PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        return (kit, owner, pet);
    }

    private static void Resolve(PetTestKit kit)
    {
        if (kit.Spells.System.SpellBatchWindowMs != 0)
        {
            kit.Spells.Advance(kit.Spells.System.SpellBatchWindowMs);
        }
    }

    private static Creature Enemy(PetTestKit kit, float x = 7)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(PetTestKit.NpcCasterEntry)!, x, 5, 83.5f, 0);

    private static void StartFight(PetTestKit kit, Creature pet, float enemyX = 7)
    {
        kit.Controller.HandleCommand(pet, CommandState.Attack, Enemy(kit, enemyX));
        Assert.NotNull(pet.Combat.Victim);
    }

    private static bool Has(PetTestKit kit, Unit unit, uint spell) => kit.Spells.System.HasAura(unit, spell);

    private sealed class FakeGroups : ISpellGroupResolver
    {
        public HashSet<ObjectGuid> Members { get; } = [];

        public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid) => Members.Count == 0 ? [unit.Guid] : [.. Members];
    }

    [Fact]
    public void Dash_IsCastOnItselfInCombat_NotWithoutAVictim()
    {
        (PetTestKit kit, _, Creature pet) = Summoned();
        using (kit)
        {
            pet.Summon!.Charm!.LearnSpell(DashSpell, autocast: true);
            kit.Run(500);
            Resolve(kit);
            Assert.False(Has(kit, pet, DashSpell));

            StartFight(kit, pet, enemyX: 40);
            kit.Run(300);
            Resolve(kit);
            Assert.True(Has(kit, pet, DashSpell));
        }
    }

    [Fact]
    public void Dash_IsHeldWhileTheVictimIsAlreadyInMeleeReach()
    {
        (PetTestKit kit, _, Creature pet) = Summoned();
        using (kit)
        {
            pet.Summon!.Charm!.LearnSpell(DashSpell, autocast: true);
            StartFight(kit, pet, enemyX: pet.X + 0.5f);
            Assert.True(MapCombat.CanReachWithMeleeAutoAttack(pet, pet.Combat.Victim!));
            kit.Run(300);
            Resolve(kit);
            Assert.False(Has(kit, pet, DashSpell));
        }
    }

    [Fact]
    public void FuriousHowl_IsCastOnlyWhileFighting_OnItselfOrAnAlly()
    {
        (PetTestKit kit, Player owner, Creature pet) = Summoned();
        using (kit)
        {
            pet.Summon!.Charm!.LearnSpell(HowlSpell, autocast: true);
            kit.Run(500);
            Resolve(kit);
            Assert.False(Has(kit, pet, HowlSpell));
            Assert.False(Has(kit, owner, HowlSpell));

            StartFight(kit, pet);
            kit.Run(300);
            Resolve(kit);
            Assert.True(Has(kit, pet, HowlSpell) || Has(kit, owner, HowlSpell));
        }
    }

    private static (Player Member, FakeGroups Groups) PartyWithShieldedPetAndOwner(PetTestKit kit, Player owner, Creature pet, uint spell)
    {
        (Player member, _) = kit.AddPlayer(2, 6, 5);
        var groups = new FakeGroups();
        groups.Members.Add(owner.Guid);
        groups.Members.Add(member.Guid);
        kit.Spells.System.Groups = groups;

        // the pet and the owner already wear it: the party member is the only valid target
        kit.Cast(pet, spell, SpellCastTargets.ForUnit(pet.Guid));
        kit.Cast(pet, spell, SpellCastTargets.ForUnit(owner.Guid));
        kit.Spells.System.ClearCooldown(pet, spell);
        return (member, groups);
    }

    [Fact]
    public void FireShield_GoesOnlyOnAnAttackedPartyMember_AndSendsTheOwnerAGrowlOrTalk()
    {
        (PetTestKit kit, Player owner, Creature pet) = Summoned();
        using (kit)
        {
            (Player member, _) = PartyWithShieldedPetAndOwner(kit, owner, pet, FireShieldSpell);
            Assert.False(Has(kit, member, FireShieldSpell));

            pet.Summon!.Charm!.LearnSpell(FireShieldSpell, autocast: true);
            Assert.Null(pet.Combat.Victim);

            // nobody attacks the member: vmangos CanAutoCast refuses
            kit.Run(500);
            Resolve(kit);
            Assert.False(Has(kit, member, FireShieldSpell));

            var session = Assert.IsType<FakeSession>(owner.Session);
            session.Clear();
            Assert.True(kit.Map.Combat.Attack(Enemy(kit), member, melee: false));
            kit.Run(500);
            Resolve(kit);
            Assert.True(Has(kit, member, FireShieldSpell));

            // PetAI.cpp:331-337: growl or special-spell talk, one of them, for a target that is not the pet
            Assert.Equal(1, Packets(session, WorldOpcode.SmsgAiReaction).Count + Packets(session, WorldOpcode.SmsgPetActionSound).Count);
        }
    }

    [Fact]
    public void FireShield_IsNotRecastOverALiveAura()
    {
        (PetTestKit kit, Player owner, Creature pet) = Summoned();
        using (kit)
        {
            (Player member, _) = PartyWithShieldedPetAndOwner(kit, owner, pet, FireShieldSpell);
            pet.Summon!.Charm!.LearnSpell(FireShieldSpell, autocast: true);
            Assert.True(kit.Map.Combat.Attack(Enemy(kit), member, melee: false));
            kit.Run(500);
            Resolve(kit);
            Assert.True(Has(kit, member, FireShieldSpell));

            // the cast put the spell on its cooldown: clear it so only the aura check can stop a second cast
            SpellInfo shield = kit.Spells.Store.Get(FireShieldSpell)!;
            Assert.False(kit.Spells.System.IsSpellReadyIgnoringGlobalCooldown(pet, shield));
            kit.Spells.System.ClearCooldown(pet, FireShieldSpell);
            Assert.True(kit.Spells.System.IsSpellReadyIgnoringGlobalCooldown(pet, shield));
            kit.Run(2_000);
            Resolve(kit);
            Assert.True(kit.Spells.System.IsSpellReadyIgnoringGlobalCooldown(pet, shield)); // no cast happened, so no new cooldown
        }
    }

    [Fact]
    public void AMemberWhoLeftTheGroup_IsDroppedFromTheAllySetOnTheNextRefresh()
    {
        (PetTestKit kit, Player owner, Creature pet) = Summoned();
        using (kit)
        {
            (Player member, FakeGroups groups) = PartyWithShieldedPetAndOwner(kit, owner, pet, FireShieldSpell);
            pet.Summon!.Charm!.LearnSpell(FireShieldSpell, autocast: true);
            kit.Run(500);
            Resolve(kit);

            var ai = Assert.IsType<PetAI>(pet.AI);
            Assert.Contains(owner.Guid, ai.Allies);
            Assert.Contains(member.Guid, ai.Allies);

            // the member leaves; the spell stays ready (nobody attacks) so the autocast keeps asking for allies
            groups.Members.Remove(member.Guid);
            kit.Run(15_000);
            Resolve(kit);
            Assert.Contains(owner.Guid, ai.Allies);
            Assert.DoesNotContain(member.Guid, ai.Allies);
        }
    }

    [Fact]
    public void ATaintedBloodShapedSpell_IsNotCastWithoutAVictim()
    {
        (PetTestKit kit, _, Creature pet) = Summoned();
        using (kit)
        {
            pet.Summon!.Charm!.LearnSpell(TaintedBloodSpell, autocast: true);
            kit.Run(500);
            Resolve(kit);
            Assert.False(Has(kit, pet, TaintedBloodSpell));

            StartFight(kit, pet);
            kit.Run(300);
            Resolve(kit);
            Assert.True(Has(kit, pet, TaintedBloodSpell));
        }
    }

    [Fact]
    public void Visual289WithoutTheImpBuffFamily_GetsNoFireShieldExemption()
    {
        (PetTestKit kit, Player owner, Creature pet) = Summoned();
        using (kit)
        {
            (Player member, _) = PartyWithShieldedPetAndOwner(kit, owner, pet, FakeShieldSpell);
            pet.Summon!.Charm!.LearnSpell(FakeShieldSpell, autocast: true);
            Assert.True(kit.Map.Combat.Attack(Enemy(kit), member, melee: false));
            kit.Run(500);
            Resolve(kit);
            Assert.False(Has(kit, member, FakeShieldSpell));
        }
    }

    [Fact]
    public void ASacrificeLikeSpell_IsNeverAutocastWithoutAVictim()
    {
        (PetTestKit kit, _, Creature pet) = Summoned();
        using (kit)
        {
            Assert.True(kit.Spells.Store.Get(SacrificeSpell)!.IsPositive);
            pet.Summon!.Charm!.LearnSpell(SacrificeSpell, autocast: true);
            kit.Run(1_000);
            Resolve(kit);
            Assert.True(pet.IsAlive);

            StartFight(kit, pet);
            kit.Run(300);
            Resolve(kit);
            Assert.False(pet.IsAlive);
        }
    }

    [Fact]
    public void APossessedPet_AutocastsNothing()
    {
        (PetTestKit kit, _, Creature pet) = Summoned();
        using (kit)
        {
            pet.Summon!.Charm!.LearnSpell(DashSpell, autocast: true);
            StartFight(kit, pet);
            pet.UnitFlags |= UnitFlags.Possessed;
            kit.Run(500);
            Resolve(kit);
            Assert.False(Has(kit, pet, DashSpell));
        }
    }
}
