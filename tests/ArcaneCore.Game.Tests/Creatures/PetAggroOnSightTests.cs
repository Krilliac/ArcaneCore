using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// mangos PetAI::MoveInLineOfSight (Object/PetAI.cpp:79-108): an aggressive pet attacks a hostile creature that comes into its
/// aggro radius through the creature relocation notify; defensive, passive and disabled pets do not, and a pet that fights keeps
/// its victim. The pet is assembled by hand (summon record, charm info, owner field, PetAI) so the test needs no spell system.
/// </summary>
public sealed class PetAggroOnSightTests
{
    private const uint PetEntry = 400;
    private const uint MobEntry = 401;

    /// <summary>Creatures are enemies of creatures; nobody is an enemy of a player (the owner stays out of every fight).</summary>
    private sealed class CreaturesOnly : ICreatureHostility
    {
        public bool IsHostile(Creature creature, Unit target) => target is Creature;

        public bool CanAssist(Creature helper, Creature caller) => false;
    }

    private sealed record Yard(WorldRuntime World, Map Map, CreatureMapSystem System, Player Owner, FakeSession Session, Creature Pet) : IDisposable
    {
        public void Dispose() => World.Dispose();

        /// <summary>A mob that never aggroes on its own (NO_AGGRO: defensive), so only the pet's reaction is measured.</summary>
        public Creature SpawnMob(float x, float y) => System.SpawnTemporary(Template(MobEntry) with { ExtraFlags = 0x02 }, x, y, 83.5f, 0);
    }

    /// <summary>
    /// The first <paramref name="swings"/> table rolls in the fight are misses (a roll of 0 is inside the miss band), every later roll comes from
    /// a fixed seed. The dice is shared by both fighters: the defensive mob swings back at the pet, and whoever swings first takes the first
    /// roll, so forcing only the first roll can land on the mob's swing and leave the pet's first swing free to hit; three misses cover the pet's first swing and its next one (an earlier version of
    /// this case did exactly that and could not fail). A hit always deals at least 1 damage (MeleeHitTable.ApplyArmor), so a miss (like a
    /// dodge, a parry or a full block) is the only way a swing leaves the mob's health alone; unseeded, those outcomes decided whether
    /// the pet had struck by the end of the window.
    /// </summary>
    private sealed class FirstSwingsMiss(int seed, int swings) : ICombatRandom
    {
        private readonly SeededCombatDice _rest = new(seed);
        private int _rolled;

        /// <summary>True once the scripted swings have all rolled the hit table.</summary>
        public bool Spent => _rolled >= swings;

        public int Next(int minInclusive, int maxInclusive)
        {
            if (Spent)
            {
                return _rest.Next(minInclusive, maxInclusive);
            }

            _rolled++;
            return minInclusive;
        }

        public float NextFloat(float min, float max) => _rest.NextFloat(min, max);
    }

    private static Yard Start(ReactState react, int seed = 1, ICombatRandom? dice = null)
    {
        CreatureContent content = Content([Template(PetEntry), Template(MobEntry)], []);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new CreaturesOnly() },
            combatRandom: dice, combatSeed: seed);
        (Player owner, FakeSession session) = AddPlayer(runtime, 1, 0, 0);

        Creature pet = system.SpawnTemporary(Template(PetEntry), 2, 0, 83.5f, 0);
        pet.Summon = new SummonLinks(SummonKind.Pet, owner.Guid, spellId: 1, TotemSlots.None, durationMs: -1) { Charm = new CharmInfo(react) };
        pet.SetOwnerGuid(owner.Guid);
        pet.FactionTemplate = owner.FactionTemplate;
        pet.AI = new PetAI(pet);
        Run(runtime, 1100); // the pet's own relocation notify is over and it follows its owner
        Assert.True(pet.IsPet);
        Assert.Null(pet.Combat.Victim);
        session.Clear();
        return new Yard(runtime, map, system, owner, session, pet);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AnAggressivePet_AttacksAHostileCreatureThatComesIntoRange_AndTheOwnerHearsIt(int seed)
    {
        using Yard y = Start(ReactState.Aggressive, seed);
        Creature mob = y.SpawnMob(12, 0);

        Run(y.World, 1100); // the mob's relocation notify reaches the pet

        Assert.Same(mob, y.Pet.Combat.Victim);
        Assert.Equal(MovementGeneratorType.Chase, y.Pet.Motion.CurrentType);
        Assert.NotEmpty(Packets(y.Session, WorldOpcode.SmsgAiReaction)); // PetAI::DoAttack: the pet picked the target itself
        // The attack start's combat flag is not kept until the pet strikes: a player's pet uses the PvP combat timer, and the 1.2 s check
        // clears it while no creature holds the pet on a threat list (vmangos Unit::Update, Unit.cpp:256-287; 90716802). Once it hits, the
        // mob holds it and it stays in combat.
        Run(y.World, 3000);
        Assert.Same(mob, y.Pet.Combat.Victim);
        Assert.True(y.Pet.Combat.IsInCombat);
        Assert.True(mob.Health < mob.MaxHealth, "the pet never struck");
        Assert.Null(y.Owner.Combat.Victim);
    }

    [Fact]
    public void AnAggressivePetWhoseFirstSwingMisses_StillStrikesWithinOneAttackSpeed()
    {
        var dice = new FirstSwingsMiss(1, swings: 3);
        using Yard y = Start(ReactState.Aggressive, dice: dice);
        Creature mob = y.SpawnMob(12, 0);
        Run(y.World, 1100);
        Assert.Same(mob, y.Pet.Combat.Victim);

        // Chase until the first three swings have rolled the table (the scripted misses); allow the same 3 s the seeded theory uses.
        uint waited = 0;
        while (!dice.Spent && waited < 3000)
        {
            Run(y.World, 100);
            waited += 100;
        }

        Assert.True(dice.Spent, "the first three swings never all happened");
        Assert.True(mob.Health == mob.MaxHealth, $"the scripted first swings missed, health {mob.Health}/{mob.MaxHealth} after {waited} ms");

        // The next swing is one attack speed later (the pet template's base attack time is 2 s).
        uint untilStrike = 0;
        while (mob.Health >= mob.MaxHealth && untilStrike < 2000)
        {
            Run(y.World, 100);
            untilStrike += 100;
        }

        Assert.True(mob.Health < mob.MaxHealth, $"the pet never struck within one attack speed after its first swing missed (health {mob.Health}/{mob.MaxHealth}, {untilStrike} ms)");
    }

    [Theory]
    [InlineData(ReactState.Defensive)]
    [InlineData(ReactState.Passive)]
    public void ADefensiveOrPassivePet_LetsTheCreaturePass(ReactState react)
    {
        using Yard y = Start(react);
        Creature mob = y.SpawnMob(12, 0);

        Run(y.World, 1100);

        Assert.Null(y.Pet.Combat.Victim);
        Assert.Null(mob.Combat.Victim);
    }

    [Fact]
    public void ADisabledPet_DoesNotReact()
    {
        using Yard y = Start(ReactState.Aggressive);
        y.Pet.Summon!.Charm!.Enabled = false; // the owner mounted
        y.SpawnMob(12, 0);

        Run(y.World, 1100);

        Assert.Null(y.Pet.Combat.Victim);
    }

    [Fact]
    public void APetWithAVictim_DoesNotSwitchOnSight()
    {
        using Yard y = Start(ReactState.Aggressive);
        Creature first = y.SpawnMob(12, 0);
        Run(y.World, 1100);
        Assert.Same(first, y.Pet.Combat.Victim);

        Creature second = y.SpawnMob(0, 6);
        Run(y.World, 1100);

        Assert.Same(first, y.Pet.Combat.Victim);
        Assert.Null(second.Combat.Victim);
    }

    [Fact]
    public void ACreatureOutOfTheAggroRadius_IsNotSeen()
    {
        using Yard y = Start(ReactState.Aggressive);
        y.SpawnMob(30, 0);

        Run(y.World, 1100);

        Assert.Null(y.Pet.Combat.Victim);
    }
}
