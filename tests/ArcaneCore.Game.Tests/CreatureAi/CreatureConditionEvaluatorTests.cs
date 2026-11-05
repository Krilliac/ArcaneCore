using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// The <c>combat_condition</c> and <c>unit_condition</c> rows of a spell list (cmangos CombatConditionMgr::Meets, UnitConditionMgr::Meets), on a real
/// creature in a map. Rows are hand-written in the classic-db layout; no GPL rows are copied.
/// </summary>
public sealed class CreatureConditionEvaluatorTests
{
    private const uint ListId = 1000;
    private const uint Bolt = 7001;
    private const uint FriendEntry = 9102;

    // cmangos UnitCondition variables.
    private const uint Level = 3;
    private const uint IsSelf = 4;
    private const uint HealthPercent = 12;
    private const uint HasPet = 10;
    private const uint DamagePhysical = 24;
    private const uint InCombat = 31;
    private const uint IsCasting = 33;
    private const uint IsChanneling = 35;
    private const uint IsAttackingMe = 38;
    private const uint Range = 39;
    private const uint IsPlayer = 63;
    private const uint CreatureId = 74;
    private const uint HasAura = 76;

    private const CreatureConditionOperation Equal = CreatureConditionOperation.EqualTo;
    private const CreatureConditionOperation Greater = CreatureConditionOperation.GreaterThan;
    private const CreatureConditionOperation Less = CreatureConditionOperation.LessThan;

    private sealed class CastingUnits : IUnitSpellQueries
    {
        public HashSet<Unit> Casting { get; } = [];

        public int GetAuraStacks(Unit unit, uint spellId) => 0;

        public bool IsCasting(Unit unit) => Casting.Contains(unit);
    }

    private sealed class Rig : IDisposable
    {
        public Rig(CreatureConditionCatalog conditions, params CreatureSpellListEntry[] entries)
        {
            var catalog = new CreatureSpellListCatalog([new CreatureSpellList(ListId, "test", 0, 0, entries)]) { Conditions = conditions };
            CreatureContent content = new(
                [
                    Template(configure: t => t.AIName = CreatureAiFactory.EventAIName) with { SpellList = entries.Length == 0 ? 0 : ListId },
                    Template(FriendEntry),
                ],
                [Spawn(1, WolfEntry, 5, 0), Spawn(2, FriendEntry, 7, 0)], [], [], [],
                new CreatureAiContent([], []) { SpellLists = catalog });
            (World, Map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = Caster, UnitSpells = Casters });
            (Player, _) = AddPlayer(World, 1, 50, 0);
            Wolf = Assert.Single(system.Creatures, c => c.Template.Entry == WolfEntry);
            Friend = Assert.Single(system.Creatures, c => c.Template.Entry == FriendEntry);
            Evaluator = new CreatureConditionEvaluator(system, Wolf);
        }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public Player Player { get; }

        public Creature Wolf { get; }

        public Creature Friend { get; }

        public CreatureSpellListWiringTests.ListCaster Caster { get; } = new();

        public CastingUnits Casters { get; } = new();

        public CreatureConditionEvaluator Evaluator { get; }

        public void Pull() => Map.Combat.DealDamage(Player, Wolf, 1, direct: false);

        public void Dispose() => World.Dispose();
    }

    private static UnitConditionDefinition Unit(int id, bool anyOf, params UnitConditionTerm[] terms) => new(id, anyOf, terms);

    private static UnitConditionTerm Term(uint variable, CreatureConditionOperation operation, int value) => new(variable, operation, value);

    private static CombatConditionClause Clause(int condition, CreatureConditionOperation operation, uint count) => new(condition, operation, count);

    private static CombatConditionDefinition Combat(
        int id,
        uint worldState = 0,
        int self = 0,
        int target = 0,
        CreatureConditionLogic friendLogic = CreatureConditionLogic.None,
        CombatConditionClause friend0 = default,
        CombatConditionClause friend1 = default,
        CombatConditionClause enemy0 = default)
        => new(id, worldState, self, target, friendLogic, CreatureConditionLogic.None, friend0, friend1, enemy0, default);

    private static CreatureConditionCatalog Conditions(IEnumerable<CombatConditionDefinition> combat, params UnitConditionDefinition[] unit) => new(combat, unit);

    [Fact]
    public void TheEnemyIsCastingRows_HoldAListEntryUntilAnEnemyCasts()
    {
        // The shape of classic-db combat_condition 1221 and unit_condition 855: more than 0 enemies casting or channeling.
        CreatureConditionCatalog conditions = Conditions(
            [Combat(1221, enemy0: Clause(855, Greater, 0))],
            Unit(855, true, Term(IsCasting, Equal, 1), Term(IsChanneling, Equal, 1)));
        CreatureSpellListEntry entry = CreatureSpellListWiringTests.Entry(1, Bolt) with { CombatCondition = 1221 };
        using var rig = new Rig(conditions, entry);

        rig.Pull();
        Run(rig.World, 3000);
        Assert.Empty(rig.Caster.Casts);

        rig.Casters.Casting.Add(rig.Player);
        Run(rig.World, 1500);
        Assert.Equal([(Bolt, (Unit?)rig.Player)], rig.Caster.Casts);
    }

    [Fact]
    public void AnEnemyClause_CountsOnlyTheEnemiesOfTheCreature()
    {
        CreatureConditionCatalog conditions = Conditions(
            [Combat(1, enemy0: Clause(2, Greater, 0))],
            Unit(2, false, Term(IsPlayer, Equal, 1)));
        using var rig = new Rig(conditions);

        Assert.False(rig.Evaluator.MeetsCombat(1, 100f)); // no enemy yet

        rig.Pull();
        Assert.True(rig.Evaluator.MeetsCombat(1, 100f));
    }

    [Theory]
    [InlineData(CreatureConditionLogic.None, true)]
    [InlineData(CreatureConditionLogic.And, false)]
    [InlineData(CreatureConditionLogic.Or, true)]
    [InlineData(CreatureConditionLogic.Xor, true)]
    public void FriendClauses_CombineByTheRowsLogic(CreatureConditionLogic logic, bool expected)
    {
        // The first clause holds (one friend of that entry), the second does not (nobody has entry 1).
        CreatureConditionCatalog conditions = Conditions(
            [Combat(1, friendLogic: logic, friend0: Clause(2, Greater, 0), friend1: Clause(3, Greater, 0))],
            Unit(2, false, Term(CreatureId, Equal, (int)FriendEntry)),
            Unit(3, false, Term(CreatureId, Equal, 1)));
        using var rig = new Rig(conditions);

        Assert.Equal(expected, rig.Evaluator.MeetsCombat(1, 100f));
    }

    [Fact]
    public void AFriendClause_SeesOnlyFriendsInTheSpellsRange()
    {
        CreatureConditionCatalog conditions = Conditions(
            [Combat(1, friend0: Clause(2, Greater, 0))],
            Unit(2, false, Term(CreatureId, Equal, (int)FriendEntry)));
        using var rig = new Rig(conditions);

        Assert.True(rig.Evaluator.MeetsCombat(1, 30f));

        rig.Friend.Relocate(90, 0, 83.5f, 0);
        Assert.False(rig.Evaluator.MeetsCombat(1, 30f));
    }

    [Fact]
    public void SelfAndTargetConditions_ReadTheCreatureAndItsVictim()
    {
        CreatureConditionCatalog conditions = Conditions(
            [Combat(1, self: 2), Combat(3, target: 4)],
            Unit(2, false, Term(HealthPercent, Greater, 50)),
            Unit(4, false, Term(IsPlayer, Equal, 1)));
        using var rig = new Rig(conditions);

        Assert.True(rig.Evaluator.MeetsCombat(1, 100f));
        Assert.False(rig.Evaluator.MeetsCombat(3, 100f)); // no victim

        rig.Pull();
        Assert.True(rig.Evaluator.MeetsCombat(3, 100f));

        rig.Wolf.Health = rig.Wolf.MaxHealth / 4;
        Assert.False(rig.Evaluator.MeetsCombat(1, 100f));
    }

    [Fact]
    public void AWorldStateExpression_FailsClosed_AnUnknownRowDoesNotHold_AndAMissingUnitRowNamesNothing()
    {
        CreatureConditionCatalog conditions = Conditions([Combat(1, worldState: 7), Combat(2, self: 12345)]);
        using var rig = new Rig(conditions);

        Assert.False(rig.Evaluator.MeetsCombat(1, 100f));
        Assert.False(rig.Evaluator.MeetsCombat(99, 100f));
        Assert.True(rig.Evaluator.MeetsCombat(2, 100f));
        Assert.False(rig.Evaluator.MeetsUnit(rig.Wolf, rig.Wolf, 99));
    }

    [Fact]
    public void AUnitRow_NeedsEveryTerm_OrWithItsOrFlagOne()
    {
        CreatureConditionCatalog conditions = Conditions(
            [],
            Unit(1, false, Term(IsPlayer, Equal, 1), Term(Level, Greater, 1000)),
            Unit(2, true, Term(IsPlayer, Equal, 1), Term(Level, Greater, 1000)),
            Unit(3, true, Term(Level, Greater, 1000), Term(IsPlayer, Equal, 0)));
        using var rig = new Rig(conditions);

        Assert.False(rig.Evaluator.MeetsUnit(rig.Player, rig.Wolf, 1));
        Assert.True(rig.Evaluator.MeetsUnit(rig.Player, rig.Wolf, 2));
        Assert.False(rig.Evaluator.MeetsUnit(rig.Player, rig.Wolf, 3));
    }

    [Fact]
    public void AnUnmodelledVariable_MakesItsTermFalse_AndAVariableTheReferenceLeavesOutReadsZero()
    {
        CreatureConditionCatalog conditions = Conditions(
            [],
            Unit(1, false, Term(HasPet, Equal, 0)),
            Unit(2, true, Term(HasPet, Equal, 0), Term(IsPlayer, Equal, 1)),
            Unit(3, false, Term(DamagePhysical, Equal, 0)));
        using var rig = new Rig(conditions);

        Assert.False(rig.Evaluator.MeetsUnit(rig.Player, rig.Wolf, 1));
        Assert.True(rig.Evaluator.MeetsUnit(rig.Player, rig.Wolf, 2));
        Assert.True(rig.Evaluator.MeetsUnit(rig.Player, rig.Wolf, 3));
    }

    [Fact]
    public void TheUnitVariables_ReadTheUnits()
    {
        const int AuraId = 555;
        CreatureConditionCatalog conditions = Conditions(
            [],
            Unit(1, false, Term(IsSelf, Equal, 1)),
            Unit(2, false, Term(CreatureId, Equal, (int)WolfEntry)),
            Unit(3, false, Term(HealthPercent, Less, 60)),
            Unit(4, false, Term(InCombat, Equal, 1)),
            Unit(5, false, Term(IsAttackingMe, Equal, 1)),
            Unit(6, false, Term(Range, Greater, 40)),
            Unit(7, false, Term(HasAura, Equal, AuraId)),
            Unit(8, false, Term(Level, Equal, 2)));
        using var rig = new Rig(conditions);
        CreatureConditionEvaluator e = rig.Evaluator;

        Assert.True(e.MeetsUnit(rig.Wolf, rig.Wolf, 1));
        Assert.False(e.MeetsUnit(rig.Wolf, rig.Friend, 1));
        Assert.True(e.MeetsUnit(rig.Wolf, rig.Wolf, 2));
        Assert.False(e.MeetsUnit(rig.Friend, rig.Wolf, 2));
        Assert.False(e.MeetsUnit(rig.Player, rig.Wolf, 2));
        Assert.True(e.MeetsUnit(rig.Wolf, rig.Wolf, 8));

        Assert.False(e.MeetsUnit(rig.Wolf, rig.Wolf, 3));
        rig.Wolf.Health = rig.Wolf.MaxHealth / 2;
        Assert.True(e.MeetsUnit(rig.Wolf, rig.Wolf, 3));

        Assert.False(e.MeetsUnit(rig.Wolf, rig.Wolf, 4));
        Assert.False(e.MeetsUnit(rig.Wolf, rig.Player, 5));
        rig.Pull();
        Assert.True(e.MeetsUnit(rig.Wolf, rig.Wolf, 4));
        Assert.True(e.MeetsUnit(rig.Wolf, rig.Player, 5));

        Assert.True(e.MeetsUnit(rig.Wolf, rig.Player, 6)); // 45 yards apart
        Assert.False(e.MeetsUnit(rig.Wolf, rig.Friend, 6));

        Assert.False(e.MeetsUnit(rig.Wolf, rig.Wolf, 7));
        rig.Caster.Auras.Add((rig.Wolf, (uint)AuraId));
        Assert.True(e.MeetsUnit(rig.Wolf, rig.Wolf, 7));
    }
}
