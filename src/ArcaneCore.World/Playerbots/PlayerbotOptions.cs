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

    /// <summary>The highest <see cref="MaxBots"/> accepted (docs/integration/perf-limits-20261008.md has the measurement behind it).</summary>
    public const int MaxBotsCeiling = 1000;

    /// <summary>The highest <see cref="MaxRegisteredBots"/> accepted.</summary>
    public const int MaxRegisteredBotsCeiling = 10_000;

    /// <summary>
    /// The most managed bots running at once (0..1000, <see cref="MaxBotsCeiling"/>). Only running bots take a slot: a stopped,
    /// faulted or quarantined bot does not, and a start beyond the cap is refused with "playerbot-capacity" (a quarantine retry
    /// or startup restore that finds no slot waits for one). Live through <c>.reload config</c>: raising it lets more bots start
    /// at once; lowering it below the running count stops nobody, it only refuses new starts until enough bots are stopped.
    /// </summary>
    public int MaxBots { get; set; } = 8;

    /// <summary>
    /// The most managed bots registered (running or not; 0..10000, <see cref="MaxRegisteredBotsCeiling"/>): <c>.playerbot create</c> is
    /// refused with "playerbot-registry-full" beyond it. Each one is a real account and character. Live through <c>.reload config</c>.
    /// </summary>
    public int MaxRegisteredBots { get; set; } = 1000;

    /// <summary>
    /// The most bots one bulk command (<c>.playerbot summon|send|start|stop ... all</c> or a prefix) moves or starts without
    /// <c>force</c> (1..<see cref="MaxBotsCeiling"/>). A selection larger than this, with no smaller count, is refused. Live through
    /// <c>.reload config</c>.
    /// </summary>
    public int MaxBulkCommand { get; set; } = 50;

    /// <summary>Milliseconds between two decisions of a bot (50..60000).</summary>
    public int ThinkIntervalMs { get; set; } = 500;

    /// <summary>The most managed client actions all bots may send in one world tick (1..64).</summary>
    public int MaxActionsPerTick { get; set; } = 4;

    /// <summary>
    /// A single bot think longer than this many milliseconds is logged with its bot, goal, phases and path searches
    /// (<see cref="PlayerbotSlowThinkLog"/>; 0 = off, 0..60000). Live: <c>.reload config</c> applies a change at once.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 60000 ('.' is the decimal point); 0 turns the log off")]
    public double SlowThinkLogMs { get; set; } = 50;

    /// <summary>
    /// The most navigation-mesh search nodes the fallback path queries of one bot think may create together (the steep escape, the
    /// starts beside the bot, the hazard detours; <see cref="PlayerbotThinkProbe.NodeBudget"/>): past it a fallback runs with what is
    /// left, or not at all, and finds nothing (its plan may then fail, as when no such way exists). A plan's own query is neither held
    /// to it nor counted. 0 = no bound, else 2048..10000000; three of
    /// the largest single searches (<see cref="PlayerbotNavigation.MaxSearchNodes"/>) by default. Live: <c>.reload config</c>.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("0 (no bound), or a whole number from 2048 to 10000000")]
    public int MaxThinkSearchNodes { get; set; } = 3 * PlayerbotNavigation.MaxSearchNodes;

    /// <summary>The most points of one bot route (1..4096).</summary>
    public int MaxPathPoints { get; set; } = 128;

    /// <summary>The longest bot route in yards (above 0, at most 100000).</summary>
    public float MaxRouteYards { get; set; } = 2000f;

    /// <summary>Maximum same-map straight-line trip from a quest giver to its nearest present ender for an unhomed bot (0 disables the distance limit; 0..100000).</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 100000 ('.' is the decimal point); 0 disables the distance limit")]
    public float MaxDeliveryYards { get; set; } = 1200f;

    /// <summary>The bots' movement speed in yards per second, never above the player's run speed (above 0, at most 100).</summary>
    public float MoveSpeed { get; set; } = 7f;

    /// <summary>
    /// Seconds of world time a living bot may go without any progress (it moved less than 10 yards and its level, experience,
    /// money, bags, quest log and spells did not change) before it reports a stall in <c>.playerbot list</c> and gives up its
    /// goal (<see cref="PlayerbotStallWatch"/>; 10..86400).
    /// </summary>
    public int StallSeconds { get; set; } = 120;

    /// <summary>
    /// How a bot's movement reaches the world (on by default). On: the bot reports its motion with the MSG_MOVE_* packets a 1.12
    /// client sends, dispatched through the ordinary movement handlers like any client's. Off: the server relocates the bot itself
    /// (the same locomotion observers) and sends its observers the same MSG_MOVE_* relay, skipping the opcode dispatch, the
    /// packet encode/decode and the handler, which is cheaper with many bots. Live: <c>.reload config</c> applies a change at once.
    /// </summary>
    public bool MovementPackets { get; set; } = true;

    /// <summary>
    /// How much real-client packet work a bot's session does (<see cref="PlayerbotClientFidelity.Lean"/> by default). Lean: a bot has no
    /// client, so the map builds no SMSG_UPDATE_OBJECT blocks for it (its visible set is still maintained) and its session keeps only
    /// the received packets a bot reads back (<see cref="PlayerbotPacketIntake"/>). Full: bots are treated like real clients, every
    /// packet is built, compressed and copied into the bot's receive ring, which is the stress-test mode for load testing. A session
    /// with an observer attached (scenario, packet capture) always receives everything. Read at start; a restart applies a change.
    /// </summary>
    public PlayerbotClientFidelity ClientFidelity { get; set; } = PlayerbotClientFidelity.Lean;

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

    /// <summary>Risk against reward before a pull, and retreat from a lost fight (<c>World:Playerbots:Risk</c>; live).</summary>
    public PlayerbotRiskOptions Risk { get; set; } = new();

    /// <summary>
    /// Bots answer players who talk to them (<c>World:Playerbots:Chat</c>; live): built-in template replies by default, with optional
    /// model providers (Anthropic, OpenAI-compatible, local servers) in front of them (docs/areas/playbots.md, Bot chat).
    /// </summary>
    public Chat.PlayerbotChatOptions Chat { get; set; } = new();

    /// <summary>Bots group up for content they cannot do alone (<c>World:Playerbots:Groups</c>; live).</summary>
    public PlayerbotGroupOptions Groups { get; set; } = new();

    /// <summary>How a following bot keeps up with its leader and catches up when it cannot walk (<c>World:Playerbots:Follow</c>; live).</summary>
    public PlayerbotFollowOptions Follow { get; set; } = new();

    /// <summary>How a homed bot returns to its home area: walk, teleport, or teleport when the walk is long or unsafe (<c>World:Playerbots:Home</c>; live).</summary>
    public PlayerbotHomeOptions Home { get; set; } = new();

    /// <summary>A target number of bots kept online by level band and the world's tick time (<c>World:Playerbots:Population</c>; live; off by default).</summary>
    public Population.PlayerbotPopulationOptions Population { get; set; } = new();

    /// <summary>Bot guilds, guild chat and greetings (<c>World:Playerbots:Social</c>; live; docs/areas/playbots-social.md).</summary>
    public BotSocial.PlayerbotSocialOptions Social { get; set; } = new();

    /// <summary>Bots use the faction auction house and mailbox (<c>World:Playerbots:Economy</c>; live; docs/areas/playbots-economy.md).</summary>
    public PlayerbotEconomyOptions Economy { get; set; } = new();
    /// <summary>Bots queue for, play and leave battlegrounds (<c>World:Playerbots:Battlegrounds</c>; live).</summary>
    public Battlegrounds.PlayerbotBattlegroundOptions Battlegrounds { get; set; } = new();

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
        ArcaneCore.World.Playerbots.Population.PlayerbotPopulationOptions.PrepareBinding(options.Population, section);
        section.Bind(options);
    }

    public void Validate()
    {
        if (MaxBots is < 0 or > MaxBotsCeiling) throw new InvalidOperationException($"{SectionName}: MaxBots must be 0..{MaxBotsCeiling}.");
        if (MaxRegisteredBots is < 0 or > MaxRegisteredBotsCeiling)
            throw new InvalidOperationException($"{SectionName}: MaxRegisteredBots must be 0..{MaxRegisteredBotsCeiling}.");
        if (MaxBulkCommand is < 1 or > MaxBotsCeiling) throw new InvalidOperationException($"{SectionName}: MaxBulkCommand must be 1..{MaxBotsCeiling}.");
        if (ThinkIntervalMs is < 50 or > 60_000) throw new InvalidOperationException($"{SectionName}: ThinkIntervalMs must be 50..60000.");
        if (MaxActionsPerTick is < 1 or > 64) throw new InvalidOperationException($"{SectionName}: MaxActionsPerTick must be 1..64.");
        if (!double.IsFinite(SlowThinkLogMs) || SlowThinkLogMs is < 0 or > 60_000) throw new InvalidOperationException($"{SectionName}: SlowThinkLogMs must be 0..60000.");
        if (MaxThinkSearchNodes != 0 && MaxThinkSearchNodes is < ArcaneCore.Game.Maps.Collision.PathOptions.DefaultMaxSearchNodes or > 10_000_000)
            throw new InvalidOperationException($"{SectionName}: MaxThinkSearchNodes must be 0 or {ArcaneCore.Game.Maps.Collision.PathOptions.DefaultMaxSearchNodes}..10000000.");
        if (MaxPathPoints is < 1 or > 4096) throw new InvalidOperationException($"{SectionName}: MaxPathPoints must be 1..4096.");
        if (!float.IsFinite(MaxRouteYards) || MaxRouteYards <= 0 || MaxRouteYards > 100_000)
            throw new InvalidOperationException($"{SectionName}: MaxRouteYards must be finite and > 0..100000.");
        if (!float.IsFinite(MaxDeliveryYards) || MaxDeliveryYards < 0 || MaxDeliveryYards > 100_000)
            throw new InvalidOperationException($"{SectionName}: MaxDeliveryYards must be finite and 0..100000.");
        if (!float.IsFinite(MoveSpeed) || MoveSpeed <= 0 || MoveSpeed > 100)
            throw new InvalidOperationException($"{SectionName}: MoveSpeed must be finite and > 0..100.");
        if (StallSeconds is < 10 or > 86_400) throw new InvalidOperationException($"{SectionName}: StallSeconds must be 10..86400.");
        if (!Enum.IsDefined(ClientFidelity)) throw new InvalidOperationException($"{SectionName}: ClientFidelity must be Lean or Full.");
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
        if (Risk is null) throw new InvalidOperationException($"{SectionName}:Risk is missing.");
        Risk.Validate();
        if (Chat is null) throw new InvalidOperationException($"{SectionName}:Chat is missing.");
        Chat.Validate();
        if (Groups is null) throw new InvalidOperationException($"{SectionName}:Groups is missing.");
        Groups.Validate();
        if (Follow is null) throw new InvalidOperationException($"{SectionName}:Follow is missing.");
        Follow.Validate();
        if (Home is null) throw new InvalidOperationException($"{SectionName}:Home is missing.");
        Home.Validate();
        if (Population is null) throw new InvalidOperationException($"{SectionName}:Population is missing.");
        Population.Validate();
        if (Social is null) throw new InvalidOperationException($"{SectionName}:Social is missing.");
        Social.Validate();
        if (Economy is null) throw new InvalidOperationException($"{SectionName}:Economy is missing.");
        Economy.Validate();
        if (Battlegrounds is null) throw new InvalidOperationException($"{SectionName}:Battlegrounds is missing.");
        Battlegrounds.Validate();
    }
}

/// <summary>How much real-client packet work a bot session does (<see cref="PlayerbotOptions.ClientFidelity"/>).</summary>
public enum PlayerbotClientFidelity
{
    /// <summary>No update-object blocks are built for bots and only the packets bots read back are kept.</summary>
    Lean,

    /// <summary>Bots are treated like real clients: every packet is built, compressed and copied into the bot's receive ring (load-test mode).</summary>
    Full,
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

    /// <summary>
    /// Teleport to the master when the follower cannot keep up by walking (vmangos PartyBotAI .goname; on by default). Off: the
    /// bot never teleports to its master, whatever <see cref="PlayerbotFollowOptions.CatchUpMode"/> says.
    /// </summary>
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
