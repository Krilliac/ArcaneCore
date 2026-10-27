using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots.Policy;

/// <summary>
/// The goals a policy ranks: the rungs of <see cref="PlayerbotBrain"/>'s goal ladder that a cheap probe can say are open, in ladder order
/// (the first open one is the ladder's choice). Crafting and the auction errands are rungs too but have no probe, so a policy cannot move
/// them; they keep their place in the ladder.
/// </summary>
public enum PlayerbotPolicyGoal : byte
{
    /// <summary>A quest exchange with a giver or ender in sight (<c>PlayerbotQuestGoals</c>).</summary>
    Quest,
    /// <summary>A town errand: vendor, trainer list, repair (<c>PlayerbotTownGoals</c>).</summary>
    Town,
    /// <summary>A chest, locked chest or rare worth its guards (<c>PlayerbotOpportunities</c>). The ladder offers it after the town errands, crafting and the auction errands, before the trainer trip.</summary>
    Opportunity,
    /// <summary>A trip to a class trainer (<c>PlayerbotTrainerDestinations</c>).</summary>
    Train,
    /// <summary>A trip for riding training or a mount (<c>PlayerbotRidingTrips</c>).</summary>
    Mount,
    /// <summary>A trip to a quest giver or ender out of sight (<c>PlayerbotWorldDestinations</c>).</summary>
    QuestTravel,
    /// <summary>A fight with a creature in sight. The ladder's floor: choosing it leaves the ladder in charge.</summary>
    Grind,
    /// <summary>Exploring for somewhere new (always open).</summary>
    Explore,
}

/// <summary>A set of <see cref="PlayerbotPolicyGoal"/>s (bit <c>1 &lt;&lt; goal</c>).</summary>
public readonly record struct PlayerbotPolicyGoalMask(byte Bits)
{
    public bool Contains(PlayerbotPolicyGoal goal) => (Bits & (1 << (int)goal)) != 0;

    public PlayerbotPolicyGoalMask With(PlayerbotPolicyGoal goal) => new((byte)(Bits | (1 << (int)goal)));

    public PlayerbotPolicyGoalMask Without(PlayerbotPolicyGoal goal) => new((byte)(Bits & ~(1 << (int)goal)));

    public int Count => System.Numerics.BitOperations.PopCount(Bits);

    /// <summary>The ladder's choice: the first open goal in ladder order, or null when none is.</summary>
    public PlayerbotPolicyGoal? LadderPick
        => Bits == 0 ? null : (PlayerbotPolicyGoal)System.Numerics.BitOperations.TrailingZeroCount(Bits);
}

/// <summary>The goal vocabulary shared by the brain, the policies, the weights file and the telemetry.</summary>
public static class PlayerbotPolicySchema
{
    /// <summary>The number of goals (<see cref="PlayerbotPolicyGoal"/>).</summary>
    public const int GoalCount = 8;

    /// <summary>
    /// The per-bot feature vector, in order. A weights file names these and must name exactly these, in this order: a model trained on
    /// another layout would read the wrong numbers. Each is scaled to about 0..1 (<see cref="PlayerbotBrain"/> builds them).
    /// </summary>
    public static readonly IReadOnlyList<string> FeatureNames =
    [
        "level",             // level / 60
        "money",             // log10(1 + copper) / 7
        "free_bag_slots",    // free bag slots / 32
        "quest_log",         // quests in the log / 20
        "quests_complete",   // quests in the log ready to hand in / 20
        "set_asides",        // goals the stall watch set aside (deferred or suspended) / 16
        "stalls",            // stalls reported since login / 10, capped at 1
        "zone",              // zone id / 4096
        "map",               // map id / 1024
        "nearby_targets",    // attackable creatures in sight (up to 8) / 8
        "health",            // health / max health
        "in_home",           // 1 inside the bot's home area, 0 outside or without one
    ];

    /// <summary>The number of features (<see cref="FeatureNames"/>).</summary>
    public const int FeatureCount = 12;

    /// <summary>The goal names a weights file lists, in <see cref="PlayerbotPolicyGoal"/> order.</summary>
    public static readonly IReadOnlyList<string> GoalNames = Enum.GetNames<PlayerbotPolicyGoal>();

    /// <summary>
    /// The policy goal a brain goal belongs to, for telemetry labels, or null for a goal no policy chooses (combat, loot, rest, recovery,
    /// the group drivers...). Errand goals map to the rung that set them.
    /// </summary>
    public static PlayerbotPolicyGoal? GoalOf(PlayerbotGoalKind kind) => kind switch
    {
        PlayerbotGoalKind.Quest => PlayerbotPolicyGoal.Quest,
        PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Gather => PlayerbotPolicyGoal.Town,
        PlayerbotGoalKind.Train => PlayerbotPolicyGoal.Train,
        PlayerbotGoalKind.Mount => PlayerbotPolicyGoal.Mount,
        PlayerbotGoalKind.Grind => PlayerbotPolicyGoal.Grind,
        PlayerbotGoalKind.Explore => PlayerbotPolicyGoal.Explore,
        _ => null,
    };

    /// <summary>
    /// The label of a think that ended on <paramref name="kind"/>. The opportunity goal has no goal kind of its own: its chest goal is
    /// <see cref="PlayerbotGoalKind.Loot"/>, which also covers looting a corpse, so a think the opportunity controller drove
    /// (<paramref name="opportunityDriven"/>) is labelled <see cref="PlayerbotPolicyGoal.Opportunity"/>. Quest travel has none either: its
    /// trip is a <see cref="PlayerbotGoalKind.Quest"/> goal, so a think whose trip the quest destinations planned
    /// (<paramref name="questTravelDriven"/>) is labelled <see cref="PlayerbotPolicyGoal.QuestTravel"/>. The kind decides otherwise.
    /// </summary>
    public static PlayerbotPolicyGoal? GoalOf(PlayerbotGoalKind kind, bool opportunityDriven, bool questTravelDriven = false)
        => opportunityDriven ? PlayerbotPolicyGoal.Opportunity : questTravelDriven ? PlayerbotPolicyGoal.QuestTravel : GoalOf(kind);
}

/// <summary>
/// A goal policy: scores every goal for a batch of bots from their feature vectors. Higher is better; only the order of one bot's scores
/// matters. Called on a worker thread, one batch at a time per policy instance per map, and never on the world thread: it must not touch
/// game state. A score that is not finite makes that bot's think fall back to the ladder.
/// </summary>
public interface IPlayerbotPolicy
{
    /// <summary>A short name for logs and inspection (<c>rules</c>, <c>model:file.json</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Score <paramref name="count"/> bots: row <c>i</c> of <paramref name="features"/> (<see cref="PlayerbotPolicySchema.FeatureCount"/>
    /// wide) into row <c>i</c> of <paramref name="scores"/> (<see cref="PlayerbotPolicySchema.GoalCount"/> wide).
    /// </summary>
    void Score(ReadOnlySpan<float> features, int count, Span<float> scores);
}

/// <summary>
/// The goal ladder as a policy: each goal scores by its ladder rank and the features are ignored, so the best open goal is always the
/// ladder's own choice and the brain does what it does with no policy. The default, and the fallback whenever a model is missing or fails.
/// </summary>
public sealed class RuleBasedPolicy : IPlayerbotPolicy
{
    public static RuleBasedPolicy Instance { get; } = new();

    public string Name => "rules";

    public void Score(ReadOnlySpan<float> features, int count, Span<float> scores)
    {
        for (int row = 0; row < count; row++)
        {
            Span<float> line = scores.Slice(row * PlayerbotPolicySchema.GoalCount, PlayerbotPolicySchema.GoalCount);
            for (int goal = 0; goal < line.Length; goal++) line[goal] = PlayerbotPolicySchema.GoalCount - goal;
        }
    }
}
