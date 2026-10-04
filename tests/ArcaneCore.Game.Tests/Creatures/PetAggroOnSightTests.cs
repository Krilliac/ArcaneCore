using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

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

    private static Yard Start(ReactState react)
    {
        CreatureContent content = Content([Template(PetEntry), Template(MobEntry)], []);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new CreaturesOnly() });
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

    [Fact]
    public void AnAggressivePet_AttacksAHostileCreatureThatComesIntoRange_AndTheOwnerHearsIt()
    {
        using Yard y = Start(ReactState.Aggressive);
        Creature mob = y.SpawnMob(12, 0);

        Run(y.World, 1100); // the mob's relocation notify reaches the pet

        Assert.Same(mob, y.Pet.Combat.Victim);
        Assert.Equal(MovementGeneratorType.Chase, y.Pet.Motion.CurrentType);
        Assert.True(y.Pet.Combat.IsInCombat);
        Assert.NotEmpty(Packets(y.Session, WorldOpcode.SmsgAiReaction)); // PetAI::DoAttack: the pet picked the target itself
        Assert.Null(y.Owner.Combat.Victim);
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
