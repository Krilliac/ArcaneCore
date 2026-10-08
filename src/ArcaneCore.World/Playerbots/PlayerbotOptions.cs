using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Playerbots;

/// <summary>Configuration policy for server-managed autonomous players.</summary>
public sealed class PlayerbotOptions
{
    public const string SectionName = "World:Playerbots";

    /// <summary>Run server-managed playerbots (off by default).</summary>
    public bool Enabled { get; set; }

    /// <summary>Log the saved managed bots back in when the world starts.</summary>
    public bool RestoreOnStartup { get; set; }

    /// <summary>The most managed bots online at once (0..64).</summary>
    public int MaxBots { get; set; } = 8;

    /// <summary>Milliseconds between two decisions of a bot (50..60000).</summary>
    public int ThinkIntervalMs { get; set; } = 500;

    /// <summary>The most managed client actions all bots may send in one world tick (1..64).</summary>
    public int MaxActionsPerTick { get; set; } = 4;

    /// <summary>The most points of one bot route (1..4096).</summary>
    public int MaxPathPoints { get; set; } = 128;

    /// <summary>The longest bot route in yards (above 0, at most 100000).</summary>
    public float MaxRouteYards { get; set; } = 2000f;

    /// <summary>The bots' movement speed in yards per second, never above the player's run speed (above 0, at most 100).</summary>
    public float MoveSpeed { get; set; } = 7f;

    /// <summary>
    /// How a bot's movement reaches the world (on by default). On: the bot reports its motion with the MSG_MOVE_* packets a 1.12
    /// client sends, dispatched through the ordinary movement handlers like any client's. Off: the server relocates the bot itself
    /// (the same locomotion observers) and sends its observers the same MSG_MOVE_* relay, skipping the opcode dispatch, the
    /// packet encode/decode and the handler, which is cheaper with many bots. Live: <c>.reload config</c> applies a change at once.
    /// </summary>
    public bool MovementPackets { get; set; } = true;

    /// <summary>The maps bots may act and travel on (non-empty, no duplicates; a configured list replaces the default continents 0 and 1).</summary>
    public uint[] AllowedMaps { get; set; } = [0, 1];

    /// <summary>
    /// Seconds a bot waits in quarantine after an action fault before it is logged in again (1..3600); each further fault in
    /// <see cref="FaultWindowSeconds"/> doubles the wait, up to an hour. The bot stays desired meanwhile.
    /// </summary>
    public int FaultBackoffSeconds { get; set; } = 30;

    /// <summary>The faults within <see cref="FaultWindowSeconds"/> that disable a bot for good (DesiredEnabled off; 1..100).</summary>
    public int MaxFaults { get; set; } = 3;

    /// <summary>How far back faults count towards <see cref="MaxFaults"/>, in seconds of world time (1..604800).</summary>
    public int FaultWindowSeconds { get; set; } = 3600;

    /// <summary>Let a local language model choose among the bot's candidate goals (off by default; the rules choose otherwise).</summary>
    public bool AllowLocalLlm { get; set; }

    /// <summary>The local model name sent to the loopback inference endpoint (at most 128 characters).</summary>
    public string LocalLlmModel { get; set; } = "R4C3R/qwen3-0.6b-heretic:q4_k_m";

    /// <summary>The local model's context size in tokens (512..2048).</summary>
    public int LocalLlmContextSize { get; set; } = 1024;

    /// <summary>Milliseconds a local model choice may take before the rules decide (100..10000).</summary>
    public int LocalLlmTimeoutMs { get; set; } = 5000;

    /// <summary>The scenario harness on a live world (<c>World:Playerbots:Scenarios</c>; off by default).</summary>
    public PlayerbotScenarioOptions Scenarios { get; set; } = new();

    /// <summary>How a bot behaves in a real player's group (<c>World:Playerbots:Party</c>; <see cref="Party.PlayerbotPartyAI"/>).</summary>
    public PlayerbotPartyOptions Party { get; set; } = new();

    public static PlayerbotOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new PlayerbotOptions();
        ApplyConfiguration(options, configuration);
        return options;
    }

    internal static void ApplyConfiguration(PlayerbotOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        // ConfigurationBinder appends array elements to a non-empty initialized array.
        // Explicit maps replace the defaults; missing maps retain the default continents.
        if (section.GetChildren().Any(child => string.Equals(child.Key, nameof(AllowedMaps), StringComparison.OrdinalIgnoreCase))) options.AllowedMaps = [];
        section.Bind(options);
    }

    public void Validate()
    {
        if (MaxBots is < 0 or > 64) throw new InvalidOperationException($"{SectionName}: MaxBots must be 0..64.");
        if (ThinkIntervalMs is < 50 or > 60_000) throw new InvalidOperationException($"{SectionName}: ThinkIntervalMs must be 50..60000.");
        if (MaxActionsPerTick is < 1 or > 64) throw new InvalidOperationException($"{SectionName}: MaxActionsPerTick must be 1..64.");
        if (MaxPathPoints is < 1 or > 4096) throw new InvalidOperationException($"{SectionName}: MaxPathPoints must be 1..4096.");
        if (!float.IsFinite(MaxRouteYards) || MaxRouteYards <= 0 || MaxRouteYards > 100_000)
            throw new InvalidOperationException($"{SectionName}: MaxRouteYards must be finite and > 0..100000.");
        if (!float.IsFinite(MoveSpeed) || MoveSpeed <= 0 || MoveSpeed > 100)
            throw new InvalidOperationException($"{SectionName}: MoveSpeed must be finite and > 0..100.");
        if (AllowedMaps is null || AllowedMaps.Length == 0 || AllowedMaps.Distinct().Count() != AllowedMaps.Length)
            throw new InvalidOperationException($"{SectionName}: AllowedMaps must be non-empty and contain no duplicates.");
        if (FaultBackoffSeconds is < 1 or > 3600 || MaxFaults is < 1 or > 100 || FaultWindowSeconds is < 1 or > 604_800)
            throw new InvalidOperationException($"{SectionName}: FaultBackoffSeconds must be 1..3600, MaxFaults 1..100 and FaultWindowSeconds 1..604800.");
        if (string.IsNullOrWhiteSpace(LocalLlmModel) || LocalLlmModel.Length > 128 || LocalLlmModel.Any(char.IsControl)
            || LocalLlmContextSize is < 512 or > 2048 || LocalLlmTimeoutMs is < 100 or > 10_000)
            throw new InvalidOperationException($"{SectionName}: invalid bounded local model settings.");
        if (Scenarios is null || Scenarios.MaxDurationSeconds is < 5 or > 600 || Scenarios.StepTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException($"{SectionName}:Scenarios: MaxDurationSeconds must be 5..600 and StepTimeoutSeconds 1..300.");
        if (Party is null) throw new InvalidOperationException($"{SectionName}:Party is missing.");
        Party.Validate();
    }
}

/// <summary>Who may invite a managed bot into a group (<see cref="PlayerbotPartyOptions.InvitePolicy"/>).</summary>
public enum PlayerbotInvitePolicy
{
    /// <summary>Only the names in <see cref="PlayerbotPartyOptions.Allowlist"/>.</summary>
    None,

    /// <summary>The allowlist, the bot's guild mates and players on the bot's own friend list (a player's own list does not count).</summary>
    GuildOrFriends,

    /// <summary>Every player.</summary>
    Anyone,
}

/// <summary>What a grouped bot answers to a group loot roll (<see cref="PlayerbotPartyOptions.LootRoll"/>).</summary>
public enum PlayerbotLootRoll
{
    Pass,
    Greed,
}

/// <summary>
/// <c>World:Playerbots:Party</c>: a bot in a real player's group (vmangos PartyBotAI; mangoszero playerbot actions). The bot
/// accepts or declines invitations by <see cref="InvitePolicy"/>, follows and assists its master, takes the master's whispered or
/// party-chat commands, answers loot rolls at once and goes where the master goes (docs/areas/playbots.md, Party bots).
/// </summary>
public sealed class PlayerbotPartyOptions
{
    /// <summary>The most names <see cref="Allowlist"/> may hold.</summary>
    public const int MaxAllowlist = 64;

    /// <summary>Who may invite a bot (<see cref="PlayerbotInvitePolicy.GuildOrFriends"/> by default).</summary>
    public PlayerbotInvitePolicy InvitePolicy { get; set; } = PlayerbotInvitePolicy.GuildOrFriends;

    /// <summary>Character names whose invitations a bot always accepts, whatever <see cref="InvitePolicy"/> says (case-insensitive).</summary>
    public string[] Allowlist { get; set; } = [];

    /// <summary>Teleport to the master when it is more than 100 yards away or on another map (vmangos PartyBotAI .goname; on by default).</summary>
    public bool TeleportToLeader { get; set; } = true;

    /// <summary>Revive a dead bot in place when vmangos <c>PartyBotAI::ShouldAutoRevive</c> says so (on by default); otherwise it runs back.</summary>
    public bool AutoRevive { get; set; } = true;

    /// <summary>The vote on every group loot roll (<see cref="PlayerbotLootRoll.Pass"/> by default: the items go to the players).</summary>
    public PlayerbotLootRoll LootRoll { get; set; } = PlayerbotLootRoll.Pass;

    /// <summary>Seconds a bot waits for an offline or departed master before it leaves the group and goes back to its own goals (1..3600).</summary>
    public int MasterTimeoutSeconds { get; set; } = 60;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Party";
        if (!Enum.IsDefined(InvitePolicy)) throw new InvalidOperationException($"{section}: InvitePolicy must be None, GuildOrFriends or Anyone.");
        if (!Enum.IsDefined(LootRoll)) throw new InvalidOperationException($"{section}: LootRoll must be Pass or Greed.");
        if (MasterTimeoutSeconds is < 1 or > 3600) throw new InvalidOperationException($"{section}: MasterTimeoutSeconds must be 1..3600.");
        if (Allowlist is null || Allowlist.Length > MaxAllowlist
            || Allowlist.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 12 || !name.All(char.IsLetter))
            || Allowlist.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Allowlist.Length)
            throw new InvalidOperationException($"{section}: Allowlist must hold at most {MaxAllowlist} distinct character names (letters only, at most 12).");
    }
}

/// <summary>
/// <c>World:Playerbots:Scenarios</c>: whether an Administrator may run registered bot scenarios against the live world
/// (<c>.playerbot scenario run</c>). Scenarios create/start their own managed bots in scripted mode and use world-thread
/// setup helpers (placement, items, money, spells) that are never reachable by ordinary players.
/// </summary>
public sealed class PlayerbotScenarioOptions
{
    /// <summary>Let an Administrator run registered bot scenarios against the live world (off by default).</summary>
    public bool Enabled { get; set; }

    /// <summary>Wall-clock bound of one scenario run in seconds (5..600).</summary>
    public int MaxDurationSeconds { get; set; } = 120;

    /// <summary>Default bound of one WaitUntil step in seconds (1..300).</summary>
    public int StepTimeoutSeconds { get; set; } = 20;
}
