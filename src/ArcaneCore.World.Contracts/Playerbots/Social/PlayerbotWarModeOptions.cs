using ArcaneCore.Game.WorldState;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:WarMode</c>: the memory side of the War Mode server mode for massive world PvP. The switch itself is
/// <c>World:WarMode:Enabled</c> (<see cref="WarModeOptions"/>, one switch for the rules and the bots); this section only tunes how bots remember
/// and hunt while it is on. War Mode never changes what a bot normally remembers: each relation keeps a second, war layer (affinity and Rival flag) that is fed the
/// same events under the war rules only while War Mode is on, and every decision reads the war layer only while it is on, so switching it off leaves
/// normal behaviour exactly as if it had never been on. The war layer decides the Rival grudges bots act on: the rivalry emote lane (<c>Social:Rivalry</c>) and the
/// hunt (<c>HuntRadiusYards</c>). A rivalry opt-out silences emotes; it does not exempt anyone from War Mode. Every key is live.
/// </summary>
public sealed class PlayerbotWarModeOptions
{
    /// <summary>Lowest hunt radius.</summary>
    public const int MinHuntRadiusYards = 10;

    /// <summary>Highest hunt radius.</summary>
    public const int MaxHuntRadiusYards = 150;

    private WarModeOptions _shared = new();

    /// <summary>
    /// Whether War Mode is on: <c>World:WarMode:Enabled</c>, read live from the shared <see cref="WarModeOptions"/> the daemon links with
    /// <see cref="Link"/>. Not a key of this section. The setter exists for tests and writes the shared switch.
    /// </summary>
    public bool Enabled
    {
        get => _shared.Enabled;
        internal set => _shared.Enabled = value;
    }

    /// <summary>The shared World:WarMode options this section reads its switch from (a private default, off, until <see cref="Link"/>). A method, not a property: it is no key.</summary>
    public WarModeOptions SharedSwitch() => _shared;

    /// <summary>Read the War Mode switch from <paramref name="shared"/> from now on (the playerbot pack does this when it attaches to a world).</summary>
    public void Link(WarModeOptions shared) => _shared = shared ?? throw new ArgumentNullException(nameof(shared));

    /// <summary>While enabled, every negative affinity change is multiplied by this (1..10); positive changes are untouched.</summary>
    public float NegativeMultiplier { get; set; } = 3.0f;

    /// <summary>In the war layer negative affinity never drifts back toward 0 and the war Rival flag is never cleared by drift, only by positive events that lift the war affinity back over the threshold.</summary>
    public bool NeverForget { get; set; } = true;

    /// <summary>In the war layer the daily affinity gain cap and the one-apology-a-day limit are lifted, and a relation a full memory drops from the normal layer stays for the war layer when the war layer holds a grudge against it (a war-only entry: the normal layer, and War Mode while it is off, never see it).</summary>
    public bool RemoveCaps { get; set; } = true;

    /// <summary>Wipe the war layer of every bot, running or not, at the first checkpoint after War Mode is switched off, and write every bot it changed once (off by default: the layer stays dormant and comes back when War Mode is switched on again).</summary>
    public bool ClearOnDisable { get; set; }

    /// <summary>
    /// While War Mode is on, an idle bot hunts the nearest flagged enemy-faction player or bot within this many yards that its war layer marks
    /// Rival, outside the exempt areas (10 to 150).
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("10 to 150")]
    public int HuntRadiusYards { get; set; } = 60;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:WarMode";
        if (!float.IsFinite(NegativeMultiplier) || NegativeMultiplier < 1f || NegativeMultiplier > 10f)
            throw new InvalidOperationException($"{section}: NegativeMultiplier must be 1..10.");
        if (HuntRadiusYards < MinHuntRadiusYards || HuntRadiusYards > MaxHuntRadiusYards)
            throw new InvalidOperationException($"{section}: HuntRadiusYards must be {MinHuntRadiusYards}..{MaxHuntRadiusYards}.");
    }
}
