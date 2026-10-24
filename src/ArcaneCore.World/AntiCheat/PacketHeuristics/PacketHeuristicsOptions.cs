namespace ArcaneCore.World.AntiCheat.PacketHeuristics;

/// <summary>
/// The <c>World:PacketHeuristics</c> section (docs/areas/packet-heuristics.md): report-only analysis of the packets sessions send to
/// the server. Every finding goes to the anomaly ledger (subsystem <c>packet</c>); a packet is never dropped, changed, delayed or
/// answered differently because of a finding, and no finding feeds the AntiCheat score. Off by default; <c>.packetcheck on</c> and
/// <c>.packetcheck off</c> switch it at runtime until the next restart.
/// </summary>
public sealed class PacketHeuristicsOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "World:PacketHeuristics";

    /// <summary>Largest opcode value the per-opcode tables cover (the 1.12.1 opcode space ends at 827).</summary>
    internal const int OpcodeSpace = 1024;

    /// <summary>Run the checks at all. Default false: with it off a packet costs one volatile read and no per-session state exists.</summary>
    public bool Enabled { get; set; }

    /// <summary>The server's own managed playerbot sessions are not checked. Default true; with false their traffic is checked and counted as bot traffic.</summary>
    public bool ExemptBots { get; set; } = true;

    /// <summary>Staff (Moderator and above, or a player in GM mode) are not checked. Default true; with false their traffic is checked and counted as staff traffic.</summary>
    public bool ExemptStaff { get; set; } = true;

    /// <summary>At most one ledger report per session, check and opcode per this many seconds; every occurrence is still counted. 1-3600, default 10.</summary>
    public int ReportIntervalSeconds { get; set; } = 10;

    /// <summary>Report an opcode dropped because the session state does not allow it (<c>wrong-state</c>). Default true.</summary>
    public bool WrongState { get; set; } = true;

    /// <summary>After the player left the world (logout, back at the character screen) world opcodes are expected for this many milliseconds (packets in flight) and are not reported as <c>wrong-state</c>. Only this post-logout window is timed: a map-less player in a far teleport is excused by the teleport stage, however long the loading screen takes. 0-60000, default 5000.</summary>
    public int WrongStateGraceMs { get; set; } = 5000;

    /// <summary>Report an opcode with no handler or outside the opcode table (<c>unknown-opcode</c>). Default true.</summary>
    public bool UnknownOpcode { get; set; } = true;

    /// <summary>Report a stripped developer, GM, cheat or debug opcode that the release client never sends (<c>dev-opcode</c>); the packet is dropped unread either way. Default true.</summary>
    public bool DevOpcode { get; set; } = true;

    /// <summary>Report a payload a handler rejected as malformed, or a bad frame header (<c>malformed</c>). Default true.</summary>
    public bool Malformed { get; set; } = true;

    /// <summary>Report a payload whose length differs from the fixed length of its message (<c>shape</c>). Default true.</summary>
    public bool Shape { get; set; } = true;

    /// <summary>Report an opcode class sent faster than its ceiling (<c>cadence</c>). Default true.</summary>
    public bool Cadence { get; set; } = true;

    /// <summary>The ceilings of the cadence check.</summary>
    public PacketCadenceOptions CadenceLimits { get; set; } = new();

    /// <summary>Report the same opcode with the same payload repeated (<c>replay</c>). Default true.</summary>
    public bool Replay { get; set; } = true;

    /// <summary>The same opcode and payload this many times within <see cref="ReplayWindowMs"/> is a replay. 2-1000, default 5.</summary>
    public int ReplayCount { get; set; } = 5;

    /// <summary>The window of the replay check, in milliseconds. 100-60000, default 1000.</summary>
    public int ReplayWindowMs { get; set; } = 1000;

    /// <summary>
    /// Keep the built-in list of opcodes that legitimately repeat an identical payload out of the replay check: movement and its acknowledgements,
    /// ping, every query, selection and attack toggles, cancels, stand state, loot release, area triggers, character enumeration and the polling a
    /// client or bot does (docs/areas/packet-heuristics.md has the list and the reason for each entry). Default true; set false to take full
    /// control with <see cref="ReplayExclude"/>.
    /// </summary>
    public bool ReplayBuiltInExclusions { get; set; } = true;

    /// <summary>
    /// More opcodes never checked for replay, on top of the built-in list: exact opcode names (CMSG_PING) or names with a leading and/or trailing *
    /// (MSG_MOVE_*, *QUERY*). A configured list is added to the default (empty) one, so entries never remove anything; use
    /// <see cref="ReplayBuiltInExclusions"/> to drop the built-in list. Default empty.
    /// </summary>
    public string[] ReplayExclude { get; set; } = [];

    /// <summary>Report an unsolicited or mismatching movement acknowledgement (<c>orphan-ack</c>). Default true.</summary>
    public bool OrphanAck { get; set; } = true;

    /// <summary>Report an action opcode received while the player is dead or a ghost, on a taxi flight or in a far transfer (<c>incapacitated-action</c>; a near teleport such as Blink is not one). Default true.</summary>
    public bool IncapacitatedAction { get; set; } = true;

    /// <summary>A copy (the live options are replaced whole, never edited in place).</summary>
    public PacketHeuristicsOptions Clone()
    {
        var copy = (PacketHeuristicsOptions)MemberwiseClone();
        copy.CadenceLimits = CadenceLimits.Clone();
        copy.ReplayExclude = [.. ReplayExclude];
        return copy;
    }

    /// <summary>Every problem of these values (empty when they are usable); shared by the startup check and the settings builder.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        Range(problems, nameof(ReportIntervalSeconds), ReportIntervalSeconds, 1, 3600);
        Range(problems, nameof(WrongStateGraceMs), WrongStateGraceMs, 0, 60_000);
        Range(problems, nameof(ReplayCount), ReplayCount, 2, 1000);
        Range(problems, nameof(ReplayWindowMs), ReplayWindowMs, 100, 60_000);
        problems.AddRange(CadenceLimits.Validate());
        if (ReplayExclude is null)
        {
            problems.Add($"{SectionName}:{nameof(ReplayExclude)} must not be null.");
        }
        else
        {
            foreach (string pattern in ReplayExclude)
            {
                if (!OpcodePatterns.TryParse(pattern, out _))
                {
                    problems.Add($"{SectionName}:{nameof(ReplayExclude)} entry '{pattern}' is not an opcode name (CMSG_PING) or a name with a leading and/or trailing *.");
                }
                else if (!OpcodePatterns.MatchesAny(pattern))
                {
                    problems.Add($"{SectionName}:{nameof(ReplayExclude)} entry '{pattern}' matches no opcode.");
                }
            }
        }

        return problems;
    }

    internal static void Range(List<string> problems, string key, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            problems.Add($"{SectionName}:{key} ({value}) must be {min}-{max}.");
        }
    }
}

/// <summary>
/// <c>World:PacketHeuristics:CadenceLimits</c>: how fast a session may send each class of opcodes. A ceiling is the most packets of the
/// class in any sliding second; 0 switches that class off.
/// </summary>
public sealed class PacketCadenceOptions
{
    /// <summary>CMSG_CAST_SPELL and CMSG_PET_CAST_SPELL per second. 0-1000, default 10.</summary>
    public int CastPerSecond { get; set; } = 10;

    /// <summary>CMSG_USE_ITEM, CMSG_GAMEOBJ_USE and CMSG_LOOT per second. 0-1000, default 10.</summary>
    public int UsePerSecond { get; set; } = 10;

    /// <summary>Query opcodes (names containing QUERY) per second. A client entering a crowded city can burst; raise it if that is all you see. 0-1000, default 50.</summary>
    public int QueryPerSecond { get; set; } = 50;

    /// <summary>CMSG_EMOTE and CMSG_TEXT_EMOTE per second. 0-1000, default 5.</summary>
    public int EmotePerSecond { get; set; } = 5;

    /// <summary>A CMSG_PING that follows the previous one by less than this many milliseconds is reported (a 1.12.1 client pings every 30 seconds). 0 switches it off. 0-60000, default 5000.</summary>
    public int PingMinIntervalMs { get; set; } = 5000;

    /// <summary>A copy.</summary>
    public PacketCadenceOptions Clone() => (PacketCadenceOptions)MemberwiseClone();

    internal IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        PacketHeuristicsOptions.Range(problems, "CadenceLimits:" + nameof(CastPerSecond), CastPerSecond, 0, 1000);
        PacketHeuristicsOptions.Range(problems, "CadenceLimits:" + nameof(UsePerSecond), UsePerSecond, 0, 1000);
        PacketHeuristicsOptions.Range(problems, "CadenceLimits:" + nameof(QueryPerSecond), QueryPerSecond, 0, 1000);
        PacketHeuristicsOptions.Range(problems, "CadenceLimits:" + nameof(EmotePerSecond), EmotePerSecond, 0, 1000);
        PacketHeuristicsOptions.Range(problems, "CadenceLimits:" + nameof(PingMinIntervalMs), PingMinIntervalMs, 0, 60_000);
        return problems;
    }
}
