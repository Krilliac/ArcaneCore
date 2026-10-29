namespace ArcaneCore.Kernel.Rules;

/// <summary>
/// The rungs of the bot goal ladder, in today's order, high to low (docs/superpowers/specs/2026-10-28-rule-engine-design.md, section 11,
/// "Bot ladder semantics"). A rung is a C# driver of the bot brain; the value is stored in <c>playerbot_goal.driver</c>, so the numbers
/// are data and never change. <see cref="None"/> (0) marks a catalogue row nothing runs.
/// </summary>
public enum PlayerbotLadderRung : short
{
    None = 0,

    /// <summary>Lending a hand to a stranger (the social pass's request).</summary>
    HelpErrand = 1,

    /// <summary>Equipping better gear.</summary>
    Equipment = 2,

    /// <summary>Class upkeep between fights (forms, buffs, pet, heals, stealth before a pull).</summary>
    ClassUpkeep = 3,

    /// <summary>Walking back into the bot's home area.</summary>
    ReturnHome = 4,

    /// <summary>The urgent town errand: bag space for a quest turn-in, or broken worn gear to repair or sell.</summary>
    UrgentTown = 5,

    /// <summary>The goal policy's promoted goal (<c>RuleBasedPolicy</c> / <c>PlayerbotModelPolicy</c> as one more proposal source).</summary>
    Policy = 6,

    /// <summary>A quest exchange with a giver or ender in sight.</summary>
    Quests = 7,

    /// <summary>A town errand: vendor, repair, trainer list.</summary>
    Town = 8,

    /// <summary>Resurrection sickness: nothing below this rung starts a new fight.</summary>
    SicknessStandDown = 9,

    /// <summary>Crafting and gathering.</summary>
    Crafting = 10,

    /// <summary>The auction house errands.</summary>
    Economy = 11,

    /// <summary>Banking at the capital home.</summary>
    Bank = 12,

    /// <summary>A chest or rare worth its guards.</summary>
    Opportunities = 13,

    /// <summary>A trip to a class trainer.</summary>
    Trainers = 14,

    /// <summary>A trip for riding training or a mount.</summary>
    Riding = 15,

    /// <summary>A trip to a quest giver or ender out of sight.</summary>
    QuestDestinations = 16,
}

/// <summary>The ids of the bot host's vocabulary (<c>Game/Rules/Bot</c> declares them), shared with the shipped content.</summary>
public static class BotRuleIds
{
    public const short GoalEvaluate = 1;
    public const short GoldBelow = 2;
    public const short FreeBagSlotsBelow = 3;
    public const short ProposeGoal = 100;
    public const short SuppressGoal = 101;
    public const short SelfTarget = 1;
}

/// <summary>
/// The shipped bot content: the goal catalogue (<c>playerbot_goal</c>) and the ladder as rules on <c>bot.goal_evaluate</c> (design 5.3 and
/// section 11). One definition: the world schema step seeds the rows from it (<c>PlayerbotLadderDataModule</c>), and a brain on a database
/// without them (no rule store) runs from it.
/// <para>
/// Catalogue ids: 0-17 are <c>PlayerbotGoalKind</c> values (a custom rule that proposes one runs the kind's default rung, when it has
/// one); 101-116 are the ladder rungs (100 + <see cref="PlayerbotLadderRung"/>), including those with no goal kind of their own; 1000 and up
/// are server-defined goals. Rung weights are 100 apart in today's order; the policy rung's weight sits between the urgent town errand and
/// the quests. <c>base_priority</c> equals the weight and breaks a tie between equal weights (higher wins).
/// </para>
/// </summary>
public static class PlayerbotLadderContent
{
    /// <summary>Catalogue id of a rung: this plus the rung's number.</summary>
    public const short RungGoalIdBase = 100;

    /// <summary>The weight step between two shipped rungs.</summary>
    public const int WeightStep = 100;

    /// <summary>The id of the first shipped ladder rule; the rule of rung n is this plus n - 1 (ids 10001-10016 are reserved for the bot ladder).</summary>
    public const int FirstRuleId = 10001;

    public static short GoalIdOf(PlayerbotLadderRung rung) => (short)(RungGoalIdBase + (short)rung);

    /// <summary>The weight of a shipped rung: 1600 for the first rung down to 200 for the last, 100 apart, except that the policy rung (1150) sits between the urgent town errand (1200) and the quests (1100).</summary>
    public static int WeightOf(PlayerbotLadderRung rung) => rung switch
    {
        PlayerbotLadderRung.HelpErrand => 1600,
        PlayerbotLadderRung.Equipment => 1500,
        PlayerbotLadderRung.ClassUpkeep => 1400,
        PlayerbotLadderRung.ReturnHome => 1300,
        PlayerbotLadderRung.UrgentTown => 1200,
        PlayerbotLadderRung.Policy => 1150,
        PlayerbotLadderRung.Quests => 1100,
        PlayerbotLadderRung.Town => 1000,
        PlayerbotLadderRung.SicknessStandDown => 900,
        PlayerbotLadderRung.Crafting => 800,
        PlayerbotLadderRung.Economy => 700,
        PlayerbotLadderRung.Bank => 600,
        PlayerbotLadderRung.Opportunities => 500,
        PlayerbotLadderRung.Trainers => 400,
        PlayerbotLadderRung.Riding => 300,
        PlayerbotLadderRung.QuestDestinations => 200,
        _ => throw new ArgumentOutOfRangeException(nameof(rung), rung, "not a ladder rung"),
    };

    /// <summary>The ladder rungs in ladder order (highest weight first).</summary>
    public static IReadOnlyList<PlayerbotLadderRung> Rungs { get; } =
        [.. Enum.GetValues<PlayerbotLadderRung>().Where(r => r != PlayerbotLadderRung.None).OrderByDescending(WeightOf)];

    private static readonly Dictionary<PlayerbotLadderRung, string> RungNames = new()
    {
        [PlayerbotLadderRung.HelpErrand] = "ladder: help errand",
        [PlayerbotLadderRung.Equipment] = "ladder: equipment",
        [PlayerbotLadderRung.ClassUpkeep] = "ladder: class upkeep",
        [PlayerbotLadderRung.ReturnHome] = "ladder: return home",
        [PlayerbotLadderRung.UrgentTown] = "ladder: urgent town errand",
        [PlayerbotLadderRung.Policy] = "ladder: goal policy",
        [PlayerbotLadderRung.Quests] = "ladder: quests",
        [PlayerbotLadderRung.Town] = "ladder: town errands",
        [PlayerbotLadderRung.SicknessStandDown] = "ladder: sickness stand-down",
        [PlayerbotLadderRung.Crafting] = "ladder: crafting",
        [PlayerbotLadderRung.Economy] = "ladder: auction errands",
        [PlayerbotLadderRung.Bank] = "ladder: bank",
        [PlayerbotLadderRung.Opportunities] = "ladder: opportunities",
        [PlayerbotLadderRung.Trainers] = "ladder: trainer trip",
        [PlayerbotLadderRung.Riding] = "ladder: riding trip",
        [PlayerbotLadderRung.QuestDestinations] = "ladder: quest travel",
    };

    /// <summary>The rung a goal kind runs when a rule proposes the kind itself (kinds not listed have no default rung and cannot be proposed).</summary>
    private static PlayerbotLadderRung DefaultRungOf(Characters.PlayerbotGoalKind kind) => kind switch
    {
        Characters.PlayerbotGoalKind.Quest => PlayerbotLadderRung.Quests,
        Characters.PlayerbotGoalKind.Vendor => PlayerbotLadderRung.Town,
        Characters.PlayerbotGoalKind.Train => PlayerbotLadderRung.Trainers,
        Characters.PlayerbotGoalKind.Mount => PlayerbotLadderRung.Riding,
        Characters.PlayerbotGoalKind.Gather => PlayerbotLadderRung.Crafting,
        _ => PlayerbotLadderRung.None,
    };

    /// <summary>The shipped <c>playerbot_goal</c> rows: the 18 goal kinds, then the 16 ladder rungs.</summary>
    public static IReadOnlyList<PlayerbotGoalRecord> Goals { get; } =
    [
        .. Enum.GetValues<Characters.PlayerbotGoalKind>().Select(kind => new PlayerbotGoalRecord
        {
            GoalId = (short)kind,
            Name = kind.ToString().ToLowerInvariant(),
            BasePriority = 0,
            Enabled = true,
            Driver = (short)DefaultRungOf(kind),
            Comment = DefaultRungOf(kind) == PlayerbotLadderRung.None ? "goal kind; the brain sets it itself, a rule cannot propose it" : "goal kind; a rule that proposes it runs its default ladder rung",
        }),
        .. Rungs.Select(rung => new PlayerbotGoalRecord
        {
            GoalId = GoalIdOf(rung),
            Name = RungNames[rung],
            BasePriority = (short)WeightOf(rung),
            Enabled = true,
            Driver = (short)rung,
            Comment = "shipped ladder rung",
        }),
    ];

    /// <summary>The shipped ladder rules: one repeatable global rule per rung on <c>bot.goal_evaluate</c> whose one action proposes the rung's goal at the rung's weight.</summary>
    public static IReadOnlyList<RuleRecord> Rules { get; } =
    [
        .. Rungs.Select(rung => new RuleRecord
        {
            Id = RuleIdOf(rung),
            Host = (byte)1,
            OwnerKind = (byte)0,
            EventType = BotRuleIds.GoalEvaluate,
            Flags = 0x01,
            Priority = (short)((short)rung - 1),
            Comment = RungNames[rung],
        }),
    ];

    /// <summary>The shipped ladder rules' actions (<c>bot.propose_goal</c>).</summary>
    public static IReadOnlyList<RuleActionRecord> Actions { get; } =
    [
        .. Rungs.Select(rung => new RuleActionRecord
        {
            OwnerId = RuleIdOf(rung),
            Seq = 0,
            ActionType = BotRuleIds.ProposeGoal,
            Param1 = GoalIdOf(rung),
            Param2 = WeightOf(rung),
            TargetType = BotRuleIds.SelfTarget,
        }),
    ];

    public static int RuleIdOf(PlayerbotLadderRung rung) => FirstRuleId + (int)rung - 1;

    /// <summary>The shipped content as a rule store would return it.</summary>
    public static RuleContent Content { get; } = new() { Rules = Rules, Actions = Actions, Goals = Goals };
}
