using ArcaneCore.Game.Battlegrounds;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>A battleground type and the level bracket a bot queues in.</summary>
internal readonly record struct PlayerbotBattlegroundChoice(BattlegroundType Type, int Bracket);

/// <summary>Why a battleground type is not offered to a bot (the coordinator logs a type's first refusal once).</summary>
internal enum PlayerbotBattlegroundRefusal : byte
{
    None,
    NoTemplate,
    LevelOutOfRange,
    MissingContent,
    NoBattlemaster,

    /// <summary>A type the bots do not play (<see cref="PlayerbotBattlegroundBrackets.Supported"/>; every vanilla type is played now).</summary>
    Unsupported,

    /// <summary>The configured cap is below 2 x the template's <c>MinPlayersPerTeam</c> and <c>RaiseCapToFillTemplate</c> is off: bots alone could never start a match.</summary>
    CapBelowMinimum,
}

/// <summary>
/// Which battlegrounds a bot of a level may queue for, free of world state (unit tested). The bracket is the manager's own:
/// <see cref="BattlegroundConstants.BracketOfLevel"/> over the template's minimum level, as <see cref="BattlegroundManager.JoinQueue"/>
/// computes it (vmangos <c>Player::GetBattleGroundBracketIdFromLevel</c>, Player.cpp:19455-19468). Fails closed: a type the bots do not
/// play (<see cref="Supported"/>), a type with no template, no objective content or no friendly battlemaster is skipped,
/// never guessed.
/// </summary>
internal static class PlayerbotBattlegroundBrackets
{
    /// <summary>
    /// The three vanilla battleground types in the order bots consider them.
    /// </summary>
    internal static readonly BattlegroundType[] Types = [BattlegroundType.WarsongGulch, BattlegroundType.ArathiBasin, BattlegroundType.AlteracValley];

    /// <summary>
    /// The types bots queue for and play: Warsong Gulch, Arathi Basin and Alterac Valley.
    /// </summary>
    internal static bool Supported(BattlegroundType type)
        => type is BattlegroundType.WarsongGulch or BattlegroundType.ArathiBasin or BattlegroundType.AlteracValley;

    /// <summary>
    /// The type and bracket of one level, or the refusal. A type that is not <see cref="Supported"/> is
    /// <see cref="PlayerbotBattlegroundRefusal.Unsupported"/> whatever its template; <paramref name="template"/> null is <see cref="PlayerbotBattlegroundRefusal.NoTemplate"/>;
    /// a level below the template's minimum (bracket -1) or above its maximum is <see cref="PlayerbotBattlegroundRefusal.LevelOutOfRange"/>
    /// (the handler ignores a join below the minimum, and the port refuses a player past <c>bg.MaxLevel</c>).
    /// </summary>
    internal static PlayerbotBattlegroundRefusal Check(BattlegroundType type, uint level, BattlegroundTemplate? template, out int bracket)
    {
        bracket = -1;
        if (!Supported(type)) return PlayerbotBattlegroundRefusal.Unsupported;
        if (template is null || template.Type != type) return PlayerbotBattlegroundRefusal.NoTemplate;
        if (level < template.MinLevel || level > template.MaxLevel) return PlayerbotBattlegroundRefusal.LevelOutOfRange;
        bracket = BattlegroundConstants.BracketOfLevel(level, template.MinLevel);
        return bracket < 0 ? PlayerbotBattlegroundRefusal.LevelOutOfRange : PlayerbotBattlegroundRefusal.None;
    }

    /// <summary>
    /// Every (type, bracket) a bot of <paramref name="level"/> is eligible for. <paramref name="templates"/> answers the manager's
    /// template, <paramref name="hasObjectiveContent"/> whether the type's flags, banners and triggers exist, <paramref name="hasBattlemaster"/>
    /// whether a friendly battlemaster of the type is within reach; <paramref name="refused"/> hears each skipped type and why.
    /// <paramref name="capCoversMinimum"/> (null: always) answers whether the bot cap covers both teams' template minimum; a type it
    /// rejects is <see cref="PlayerbotBattlegroundRefusal.CapBelowMinimum"/>, checked right after the template and level.
    /// </summary>
    internal static List<PlayerbotBattlegroundChoice> Eligible(uint level, Func<BattlegroundType, BattlegroundTemplate?> templates,
        Func<BattlegroundType, bool> hasObjectiveContent, Func<BattlegroundType, bool> hasBattlemaster,
        Action<BattlegroundType, PlayerbotBattlegroundRefusal>? refused = null, Func<BattlegroundType, bool>? capCoversMinimum = null)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(hasObjectiveContent);
        ArgumentNullException.ThrowIfNull(hasBattlemaster);
        var choices = new List<PlayerbotBattlegroundChoice>(Types.Length);
        foreach (BattlegroundType type in Types)
        {
            PlayerbotBattlegroundRefusal why = Check(type, level, templates(type), out int bracket);
            if (why == PlayerbotBattlegroundRefusal.None && capCoversMinimum is not null && !capCoversMinimum(type)) why = PlayerbotBattlegroundRefusal.CapBelowMinimum;
            if (why == PlayerbotBattlegroundRefusal.None && !hasObjectiveContent(type)) why = PlayerbotBattlegroundRefusal.MissingContent;
            if (why == PlayerbotBattlegroundRefusal.None && !hasBattlemaster(type)) why = PlayerbotBattlegroundRefusal.NoBattlemaster;
            if (why == PlayerbotBattlegroundRefusal.None) choices.Add(new(type, bracket));
            else refused?.Invoke(type, why);
        }

        return choices;
    }
}
