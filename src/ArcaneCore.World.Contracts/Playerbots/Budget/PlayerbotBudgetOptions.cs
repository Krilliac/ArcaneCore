namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Budget</c>: how a bot plans its money (docs/areas/playbots-economy.md, Budget). Every spend site asks the bot's
/// <c>PlayerbotBudget</c> what it may spend; the budget holds the costs the bot saves for, in order of priority: repairs,
/// a small stock of food, drink and ammunition, the class spells due at the next levels, riding training and a mount, then the discretionary
/// spending (auction purchases). Every key is live: <c>.reload config</c> changes this object in place.
/// </summary>
public sealed class PlayerbotBudgetOptions
{
    /// <summary>The most levels ahead of <c>PlayerbotBudget</c>'s class-training reserve that can be configured.</summary>
    public const int MaxClassLevelsAhead = 10;

    /// <summary>Let bots save for what they will need (on by default). Off: nothing is reserved and every spend site may spend all the bot's money.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many levels ahead of its own the bot saves for class spells (0..10): the spells its class trainer teaches at levels up to this
    /// many above the bot's, plus the ones it can learn now, are reserved at the trainer's prices, so lower-priority spending (riding, the
    /// auction house) cannot eat the money they cost. 0 reserves only what can be learned now.
    /// </summary>
    public int ClassLevelsAhead { get; set; } = 2;

    /// <summary>
    /// Copper a bot may always spend on food, drink and ammunition (0..100000000), whatever it saves for: the part of a purchase that
    /// the reserves would otherwise forbid is allowed up to this price, so a saving bot never starves.
    /// </summary>
    public uint EssentialsAllowanceCopper { get; set; } = 10_000;

    /// <summary>Seconds between two recomputations of a bot's reserves (1..3600); a level, a learned spell or a new map recompute them at once.</summary>
    public int RefreshSeconds { get; set; } = 30;

    /// <summary>
    /// The share of its current money (0..1) a bot may always spend on discretionary purchases (auction upgrades), even when the reserves
    /// leave nothing over: a reserve the bot may never reach (a tier far above its means) does not stop its optional buying for good.
    /// 0 lets the reserves take everything. Auction listing deposits are never limited by the reserves.
    /// </summary>
    public float DiscretionaryFloorFraction { get; set; } = 0.10f;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Budget";
        if (ClassLevelsAhead is < 0 or > MaxClassLevelsAhead) throw new InvalidOperationException($"{section}: ClassLevelsAhead must be 0..{MaxClassLevelsAhead}.");
        if (EssentialsAllowanceCopper > 100_000_000) throw new InvalidOperationException($"{section}: EssentialsAllowanceCopper must be 0..100000000.");
        if (RefreshSeconds is < 1 or > 3600) throw new InvalidOperationException($"{section}: RefreshSeconds must be 1..3600.");
        if (!float.IsFinite(DiscretionaryFloorFraction) || DiscretionaryFloorFraction is < 0f or > 1f)
            throw new InvalidOperationException($"{section}: DiscretionaryFloorFraction must be 0..1.");
    }
}

/// <summary>
/// <c>World:Playerbots:Mounts</c>: bots save for riding training and a mount, train, buy and ride it (docs/areas/playbots-economy.md,
/// Mounts). Every key is live: <c>.reload config</c> changes this object in place.
/// </summary>
public sealed class PlayerbotMountOptions
{
    /// <summary>The most levels <see cref="SaveLevelsAhead"/> can be set to.</summary>
    public const int MaxSaveLevelsAhead = 20;

    /// <summary>Let bots save for, train, buy and ride mounts (on by default). Off: no riding reserve, no trips, and a mounted bot is left as it is.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Within this many levels of the level a riding tier needs (the trainer's required level, read from the world data), a bot starts
    /// to reserve that tier's riding training and the cheapest mount it can use (0..20).
    /// </summary>
    public int SaveLevelsAhead { get; set; } = 4;

    /// <summary>A bot rides on a walk of at least this many yards (1..100000); shorter legs are walked.</summary>
    public float MountMinTravelYards { get; set; } = 80f;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Mounts";
        if (SaveLevelsAhead is < 0 or > MaxSaveLevelsAhead) throw new InvalidOperationException($"{section}: SaveLevelsAhead must be 0..{MaxSaveLevelsAhead}.");
        if (!float.IsFinite(MountMinTravelYards) || MountMinTravelYards is < 1f or > 100000f)
            throw new InvalidOperationException($"{section}: MountMinTravelYards must be 1..100000.");
    }
}
