using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.AntiCheat.PacketHeuristics;

/// <summary>The checks of the packet heuristics; the ledger code of a finding is <see cref="PacketChecks.Code"/>.</summary>
public enum PacketCheck
{
    /// <summary>An opcode dropped because the session state does not allow it.</summary>
    WrongState = 0,

    /// <summary>An opcode with no handler or outside the opcode table.</summary>
    UnknownOpcode = 1,

    /// <summary>A payload a handler rejected as malformed, or a bad frame header.</summary>
    Malformed = 2,

    /// <summary>A payload whose length differs from the fixed length of its message.</summary>
    Shape = 3,

    /// <summary>An opcode class sent faster than its ceiling.</summary>
    Cadence = 4,

    /// <summary>The same opcode and payload repeated.</summary>
    Replay = 5,

    /// <summary>A movement acknowledgement that matches nothing pending.</summary>
    OrphanAck = 6,

    /// <summary>An action opcode received while the player cannot act.</summary>
    IncapacitatedAction = 7,
}

/// <summary>Whose traffic a finding is counted under.</summary>
public enum PacketTraffic
{
    /// <summary>A real player session.</summary>
    Player = 0,

    /// <summary>A managed playerbot session.</summary>
    Bot = 1,

    /// <summary>A staff account (Moderator and above) or a player in GM mode.</summary>
    Staff = 2,

    /// <summary>A packet an operator's packet tool injected into the session (the subject is <c>tool N</c>, not the target's account).</summary>
    Tool = 3,
}

/// <summary>Names, ledger codes and default severities of the checks.</summary>
public static class PacketChecks
{
    /// <summary>The ledger subsystem of every finding.</summary>
    public const string Subsystem = "packet";

    /// <summary>Number of checks.</summary>
    public const int Count = 8;

    /// <summary>Number of traffic kinds.</summary>
    public const int TrafficKinds = 4;

    /// <summary>The ledger code of <paramref name="check"/>: the check name, lower case with dashes.</summary>
    public static string Code(PacketCheck check) => check switch
    {
        PacketCheck.WrongState => "wrong-state",
        PacketCheck.UnknownOpcode => "unknown-opcode",
        PacketCheck.Malformed => "malformed",
        PacketCheck.Shape => "shape",
        PacketCheck.Cadence => "cadence",
        PacketCheck.Replay => "replay",
        PacketCheck.OrphanAck => "orphan-ack",
        PacketCheck.IncapacitatedAction => "incapacitated-action",
        _ => throw new ArgumentOutOfRangeException(nameof(check)),
    };

    /// <summary>The severity a finding of <paramref name="check"/> is reported with (unknown-opcode and incapacitated-action vary with the finding).</summary>
    public static AnomalySeverity Severity(PacketCheck check) => check == PacketCheck.Malformed ? AnomalySeverity.Error : AnomalySeverity.Warning;
}

/// <summary>
/// The immutable, validated view of <see cref="PacketHeuristicsOptions"/> plus the tables derived from it (replay mask, cadence ceilings). A
/// runtime change builds a new one and swaps the reference, so a check never sees half a change.
/// </summary>
internal sealed class PacketHeuristicsSettings
{
    private readonly bool[] _check;
    private readonly int[] _ceilings = new int[PacketOpcodeClasses.ClassCount];

    internal PacketHeuristicsSettings(PacketHeuristicsOptions options)
    {
        Options = options.Clone();
        _check = new bool[PacketChecks.Count];
        _check[(int)PacketCheck.WrongState] = options.WrongState;
        _check[(int)PacketCheck.UnknownOpcode] = options.UnknownOpcode;
        _check[(int)PacketCheck.Malformed] = options.Malformed;
        _check[(int)PacketCheck.Shape] = options.Shape;
        _check[(int)PacketCheck.Cadence] = options.Cadence;
        _check[(int)PacketCheck.Replay] = options.Replay;
        _check[(int)PacketCheck.OrphanAck] = options.OrphanAck;
        _check[(int)PacketCheck.IncapacitatedAction] = options.IncapacitatedAction;
        _ceilings[(int)CadenceClass.Cast] = options.CadenceLimits.CastPerSecond;
        _ceilings[(int)CadenceClass.Use] = options.CadenceLimits.UsePerSecond;
        _ceilings[(int)CadenceClass.Query] = options.CadenceLimits.QueryPerSecond;
        _ceilings[(int)CadenceClass.Emote] = options.CadenceLimits.EmotePerSecond;
        PingMinIntervalMs = options.CadenceLimits.PingMinIntervalMs;
        ReplayChecked = InvertedMask(OpcodePatterns.Mask(options.ReplayBuiltInExclusions ? PacketOpcodeClasses.BuiltInReplayExclusions.Concat(options.ReplayExclude) : options.ReplayExclude));
        ReplayCount = options.ReplayCount;
        ReplayWindowMs = options.ReplayWindowMs;
        ReportIntervalMs = options.ReportIntervalSeconds * 1000L;
        WrongStateGraceMs = options.WrongStateGraceMs;
        Enabled = options.Enabled;
        ExemptBots = options.ExemptBots;
        ExemptStaff = options.ExemptStaff;
    }

    /// <summary>The options this view was built from (a private copy).</summary>
    internal PacketHeuristicsOptions Options { get; }

    internal bool Enabled { get; }

    internal bool ExemptBots { get; }

    internal bool ExemptStaff { get; }

    internal long ReportIntervalMs { get; }

    internal int WrongStateGraceMs { get; }

    internal int PingMinIntervalMs { get; }

    internal int ReplayCount { get; }

    internal long ReplayWindowMs { get; }

    /// <summary>Indexed by opcode: true when the replay check looks at the opcode (not excluded).</summary>
    internal bool[] ReplayChecked { get; }

    internal bool CheckOn(PacketCheck check) => _check[(int)check];

    /// <summary>The ceiling per sliding second of a cadence class; 0 when the class is off.</summary>
    internal int CeilingOf(CadenceClass kind) => _ceilings[(int)kind];

    private static bool[] InvertedMask(bool[] excluded)
    {
        var result = new bool[excluded.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = !excluded[i];
        }

        return result;
    }
}
