using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// The <c>AllowDangerousDebug</c> gate (plan I8) in front of the debug commands that can hurt a running world: time offset, inject,
/// opcode disable, stall, combat RNG override, forced acks, map/grid unload, watchdog dumps. The option is Restart-only (read once) and
/// off by default. On the live server (<c>ARCANECORE_LIVE_SERVER=1</c>, the same probe as the map feed) the commands are refused
/// whatever the option says: nothing overrides that.
/// <para>Thread affinity: immutable after construction, any thread.</para>
/// </summary>
public sealed class DangerousDebugGate
{
    /// <summary>The configuration key.</summary>
    public const string OptionPath = GmOptions.SectionName + ":" + nameof(GmOptions.AllowDangerousDebug);

    /// <summary>The reply when a gated command is refused because the option is off.</summary>
    public const string RefusedText = "Refused: set " + OptionPath + "=true and restart.";

    /// <summary>The reply when a gated command is refused on the live server.</summary>
    public const string RefusedLiveText = "Refused: dangerous debug commands are disabled on the live server.";

    /// <summary>The <see cref="CommandContext.Fail"/> reason of a refusal.</summary>
    public const string FailureReason = "dangerous-debug-disabled";

    /// <summary>Whether this process is the live server, asked on every <see cref="Allow"/>. Tests swap it (never in production code).</summary>
    internal static Func<bool> LiveServerProbe { get; set; } = WorldPacketToolsOptions.IsLiveServer;

    private DangerousDebugGate(bool allowed, bool liveServer, bool configured)
    {
        Allowed = allowed;
        LiveServer = liveServer;
        Configured = configured;
    }

    /// <summary>Whether gated commands may run: the option is on and this is not the live server.</summary>
    public bool Allowed { get; }

    /// <summary>Whether this process was the live server when the gate was built.</summary>
    public bool LiveServer { get; }

    /// <summary>The option's bound value.</summary>
    public bool Configured { get; }

    /// <summary>The decision from explicit facts (the testable form). Pure.</summary>
    public static DangerousDebugGate Create(bool configured, bool liveServer)
        => new(configured && !liveServer, liveServer, configured);

    /// <summary>The decision for a host; <paramref name="configuration"/> may be null (no host configuration: off).</summary>
    public static DangerousDebugGate Create(IConfiguration? configuration, bool liveServer)
    {
        if (configuration is null)
        {
            return Create(configured: false, liveServer);
        }

        // The raw text, not GmOptions.Bind: here a value that is not a boolean is off. (A host's GmAuditFeature binds GmOptions first,
        // which rejects such a value at startup; only this pure path turns it off quietly.)
        string? raw = configuration.GetSection(GmOptions.SectionName)[nameof(GmOptions.AllowDangerousDebug)];
        return Create(bool.TryParse(raw, out bool configured) && configured, liveServer);
    }

    /// <summary>One line for a status reply.</summary>
    public string Describe()
        => Allowed ? "AllowDangerousDebug: on"
            : LiveServer ? "AllowDangerousDebug: off (refused on the live server)"
            : "AllowDangerousDebug: off";

    /// <summary>
    /// The gate for a command handler: true when allowed; otherwise replies with the refusal (naming <paramref name="feature"/>), marks
    /// the command failed (<see cref="FailureReason"/>) and returns false, so the handler returns true ("handled") without doing
    /// anything. The live server is refused unconditionally. A host without <see cref="GmAuditFeature"/> counts as off. The gate does
    /// not log: the caller records the write (<see cref="DebugWrite.Record"/>) once it goes ahead.
    /// </summary>
    public static bool Allow(CommandContext context, string feature)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        DangerousDebugGate gate = context.Session.Services.GetService<GmAuditFeature>()?.DangerousDebug ?? Create(configured: false, liveServer: false);
        bool live = gate.LiveServer || LiveServerProbe();
        if (gate.Configured && !live)
        {
            return true;
        }

        context.Fail(FailureReason);
        context.Reply((live ? RefusedLiveText : RefusedText) + " (" + feature + ")");
        return false;
    }
}
