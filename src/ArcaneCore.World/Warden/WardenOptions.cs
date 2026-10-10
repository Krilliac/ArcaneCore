using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Warden;

/// <summary>What Warden does to a session that fails (vmangos Warden::ApplyPenalty, WARDEN_ACTION_LOG / KICK / BAN).</summary>
public enum WardenAction
{
    /// <summary>Write a warning and keep the session (MaNGOS Zero WardenEnforcementMode::Observe).</summary>
    Log = 0,

    /// <summary>Disconnect the session.</summary>
    Kick = 1,

    /// <summary>Ban the account for <see cref="WardenOptions.BanSeconds"/> (0 = permanent), then disconnect.</summary>
    Ban = 2,
}

/// <summary>The kind of a configured scan (vmangos WindowsScanType; MaNGOS Zero WardenCheckType).</summary>
public enum WardenCheckKind
{
    /// <summary>READ_MEMORY: read <c>Length</c> bytes at <c>Address</c> (in <c>Module</c> when set, else the main image) and compare.</summary>
    Memory,

    /// <summary>FIND_MEM_IMAGE_CODE_BY_HASH (page check A): search the executable images for <c>Pattern</c> from <c>Address</c>.</summary>
    PageA,

    /// <summary>FIND_CODE_BY_HASH (page check B): search every committed page for <c>Pattern</c> from <c>Address</c>.</summary>
    PageB,

    /// <summary>FIND_DRIVER_BY_NAME: look for a loaded driver by <c>DriverName</c> and <c>DriverPath</c>.</summary>
    Driver,

    /// <summary>CHECK_TIMING_VALUES: the client clock is consistent.</summary>
    Timing,
}

/// <summary>One scan from <c>Warden:Checks</c>. Byte values are hex strings.</summary>
public sealed class WardenCheckOptions
{
    public uint Id { get; set; }

    public WardenCheckKind Kind { get; set; }

    /// <summary>Memory: the module name (empty for the main executable).</summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>Memory: the absolute address (or module-relative offset); page checks: the start offset.</summary>
    public uint Address { get; set; }

    /// <summary>Memory: the expected bytes (1-255).</summary>
    public string Expected { get; set; } = string.Empty;

    /// <summary>Page checks: the code pattern (1-255 bytes) whose HMAC-SHA1 the client searches for.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Driver: the driver name sent in the string table.</summary>
    public string DriverName { get; set; } = string.Empty;

    /// <summary>Driver: the target path whose HMAC-SHA1 the client compares.</summary>
    public string DriverPath { get; set; } = string.Empty;

    /// <summary>Page and driver checks: true when the pattern or driver must be present (a legitimate client), false when finding it is the failure (a cheat signature).</summary>
    public bool Wanted { get; set; }

    /// <summary>A per-check action; null uses <see cref="WardenOptions.Action"/> (vmangos warden_scans.penalty).</summary>
    public WardenAction? Action { get; set; }

    public string Comment { get; set; } = string.Empty;
}

/// <summary>
/// Warden for build 5875 (<c>Warden</c> section). Off by default. Ported from MaNGOS Zero src/game/Warden (module delivery, handshake,
/// RC4 keys) and vmangos src/game/Anticheat/WardenAnticheat (the memory, page and driver scans and the penalty).
/// </summary>
public sealed class WardenOptions
{
    public const string SectionName = "Warden";

    /// <summary>Warden:Enabled; default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Warden:Action, applied when a scan fails (default Log).</summary>
    public WardenAction Action { get; set; } = WardenAction.Log;

    /// <summary>
    /// Warden:ProtocolAction, applied when the handshake or a reply breaks the protocol (a wrong hash, a malformed or late reply, a
    /// failed module load). Never above Kick: a broken handshake is not evidence of a cheat (vmangos kicks, Warden.cpp:166-173). Default Log.
    /// </summary>
    public WardenAction ProtocolAction { get; set; } = WardenAction.Log;

    /// <summary>Warden:BanSeconds for <see cref="WardenAction.Ban"/>; 0 is permanent. Default 86400 (vmangos Warden.ClientBanDuration).</summary>
    public long BanSeconds { get; set; } = 86400;

    /// <summary>Warden:ExemptSecurity: accounts at or above it are never scanned. Default Moderator.</summary>
    public AccountSecurity ExemptSecurity { get; set; } = AccountSecurity.Moderator;

    /// <summary>Warden:ResponseTimeoutSeconds: every awaited reply must arrive within this (MaNGOS Zero WardenLimits.deadlineMs 30000).</summary>
    public uint ResponseTimeoutSeconds { get; set; } = 30;

    /// <summary>Warden:ScanIntervalMinSeconds: the shortest random gap between scan requests (MaNGOS Zero normal interval 30-60 s).</summary>
    public uint ScanIntervalMinSeconds { get; set; } = 30;

    /// <summary>Warden:ScanIntervalMaxSeconds: the longest gap between scan requests. Default 60.</summary>
    public uint ScanIntervalMaxSeconds { get; set; } = 60;

    /// <summary>Warden:ScansPerRequest (vmangos Warden.NumScans). Default 3.</summary>
    public int ScansPerRequest { get; set; } = 3;

    /// <summary>Warden:ChunkSize: module bytes per MODULE_CACHE frame (MaNGOS Zero 500).</summary>
    public int ChunkSize { get; set; } = 500;

    /// <summary>Warden:Checks: the scans to run. Empty runs the timing scan only.</summary>
    public List<WardenCheckOptions> Checks { get; set; } = [];

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Enum.IsDefined(Action) || !Enum.IsDefined(ProtocolAction))
        {
            problems.Add("Warden:Action and Warden:ProtocolAction must be Log, Kick or Ban.");
        }

        if (ResponseTimeoutSeconds == 0)
        {
            problems.Add("Warden:ResponseTimeoutSeconds must be above 0.");
        }

        if (ScanIntervalMinSeconds == 0 || ScanIntervalMaxSeconds < ScanIntervalMinSeconds)
        {
            problems.Add("Warden:ScanIntervalMinSeconds must be above 0 and not above ScanIntervalMaxSeconds.");
        }

        if (ScansPerRequest is < 1 or > 32)
        {
            problems.Add("Warden:ScansPerRequest must be 1-32.");
        }

        if (ChunkSize is < 1 or > 500)
        {
            problems.Add("Warden:ChunkSize must be 1-500.");
        }

        var ids = new HashSet<uint>();
        foreach (WardenCheckOptions check in Checks)
        {
            if (!ids.Add(check.Id))
            {
                problems.Add($"Warden:Checks id {check.Id} is used twice.");
            }

            if (WardenCheck.TryCreate(check, out _) is { } problem)
            {
                problems.Add($"Warden:Checks id {check.Id}: {problem}");
            }
        }

        return problems;
    }
}
