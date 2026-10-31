using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
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
        // Fire Shield: friend aura, visual 289 exempts it from the cooldown rule.
        Spell(FireShieldSpell, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitFriend, aura: AuraType.ModResistance)) with
        {
            Duration = new SpellDuration(120_000, 0, 120_000),
            RecoveryTime = 130_000,
            SpellVisual = 289,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        },
        // Sacrifice-like: kills the caster.
        Spell(SacrificeSpell, Effect(SpellEffectName.Instakill, 0)),
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

    private static Creature Enemy(PetTestKit kit)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(PetTestKit.NpcCasterEntry)!, 7, 5, 83.5f, 0);

    private static void StartFight(PetTestKit kit, Creature pet)
    {
        kit.Controller.HandleCommand(pet, CommandState.Attack, Enemy(kit));
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

            StartFight(kit, pet);
            kit.Run(300);
            Resolve(kit);
            Assert.True(Has(kit, pet, DashSpell));
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

    [Fact]
    public void FireShield_GoesOnAPartyMemberOutOfCombat_AndIsNotRecastWhileTheAuraIsUp()
    {
        (PetTestKit kit, Player owner, Creature pet) = Summoned();
        using (kit)
        {
            (Player member, _) = kit.AddPlayer(2, 6, 5);
            var groups = new FakeGroups();
            groups.Members.Add(owner.Guid);
            groups.Members.Add(member.Guid);
            kit.Spells.System.Groups = groups;

            // the pet and the owner already wear it: the party member is the only valid target
            kit.Cast(pet, FireShieldSpell, SpellCastTargets.ForUnit(pet.Guid));
            kit.Cast(pet, FireShieldSpell, SpellCastTargets.ForUnit(owner.Guid));
            kit.Spells.System.ClearCooldown(pet, FireShieldSpell);
            Assert.True(Has(kit, pet, FireShieldSpell));
            Assert.True(Has(kit, owner, FireShieldSpell));
            Assert.False(Has(kit, member, FireShieldSpell));

            pet.Summon!.Charm!.LearnSpell(FireShieldSpell, autocast: true);
            Assert.Null(pet.Combat.Victim);
            kit.Run(500);
            Resolve(kit);
            Assert.True(Has(kit, member, FireShieldSpell));

            // not recast while the aura is up (CanAutoCast)
            SpellAuraHolder holder = kit.Spells.System.GetAuras(member).Single(h => h.Spell.Id == FireShieldSpell);
            kit.Run(2_000);
            Resolve(kit);
            Assert.Same(holder, kit.Spells.System.GetAuras(member).Single(h => h.Spell.Id == FireShieldSpell));
        }
    }

    [Fact]
    public void AMemberWhoLeftTheGroup_IsDroppedFromTheAllySetOnTheNextRefresh()
    {
        (PetTestKit kit, Player owner, Creature pet) = Summoned();
        using (kit)
        {
            (Player member, _) = kit.AddPlayer(2, 6, 5);
            var groups = new FakeGroups();
            groups.Members.Add(owner.Guid);
            groups.Members.Add(member.Guid);
            kit.Spells.System.Groups = groups;
            kit.Cast(pet, FireShieldSpell, SpellCastTargets.ForUnit(pet.Guid));
            kit.Cast(pet, FireShieldSpell, SpellCastTargets.ForUnit(owner.Guid));
            kit.Spells.System.ClearCooldown(pet, FireShieldSpell);

            pet.Summon!.Charm!.LearnSpell(FireShieldSpell, autocast: true);
            kit.Run(500);
            Resolve(kit);
            Assert.True(Has(kit, member, FireShieldSpell));

            // the aura goes and the member leaves; after the refresh timer the pet no longer shields him
            groups.Members.Remove(member.Guid);
            kit.Spells.System.RemoveAuras(member, FireShieldSpell);
            Assert.False(Has(kit, member, FireShieldSpell));
            kit.Run(15_000);
            Resolve(kit);
            Assert.False(Has(kit, member, FireShieldSpell));
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
