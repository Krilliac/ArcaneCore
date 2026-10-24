using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// The <c>AllowDangerousDebug</c> gate (plan I8) in front of the debug commands that can hurt a running world: time offset, inject,
/// opcode disable, stall, combat RNG override, forced acks, map/grid unload, watchdog dumps. The option is Restart-only (read once) and
/// off by default. On the live server (<c>ARCANECORE_LIVE_SERVER=1</c>) it counts only when the key is also present in the configuration,
/// so a changed default can never switch it on there. A value that is not a boolean is off.
/// <para>Thread affinity: immutable after construction, any thread.</para>
/// </summary>
public sealed class DangerousDebugGate
{
    /// <summary>The configuration key.</summary>
    public const string OptionPath = GmOptions.SectionName + ":" + nameof(GmOptions.AllowDangerousDebug);

    /// <summary>The reply when a gated command is refused.</summary>
    public const string RefusedText = "Dangerous debug commands are disabled. Set " + OptionPath + " to true and restart to allow them.";

    /// <summary>The reply when a gated command is refused on the live server.</summary>
    public const string RefusedLiveText = "Dangerous debug commands are refused on the live server unless " + OptionPath + " is set to true explicitly in the configuration (then restart).";

    /// <summary>The <see cref="CommandContext.Fail"/> reason of a refusal.</summary>
    public const string FailureReason = "dangerous-debug-disabled";

    private DangerousDebugGate(bool allowed, bool liveServer, bool explicitlySet, bool configured)
    {
        Allowed = allowed;
        LiveServer = liveServer;
        ExplicitlySet = explicitlySet;
        Configured = configured;
    }

    /// <summary>Whether gated commands may run.</summary>
    public bool Allowed { get; }

    /// <summary>Whether this process is the live server.</summary>
    public bool LiveServer { get; }

    /// <summary>Whether the key is present in the configuration at all.</summary>
    public bool ExplicitlySet { get; }

    /// <summary>The option's bound value.</summary>
    public bool Configured { get; }

    /// <summary>The decision from explicit facts (the testable form). Pure.</summary>
    public static DangerousDebugGate Create(bool configured, bool explicitlySet, bool liveServer)
        => new(configured && (!liveServer || explicitlySet), liveServer, explicitlySet, configured);

    /// <summary>The decision for a host; <paramref name="configuration"/> may be null (no host configuration: off).</summary>
    public static DangerousDebugGate Create(IConfiguration? configuration, bool liveServer)
    {
        if (configuration is null)
        {
            return Create(configured: false, explicitlySet: false, liveServer);
        }

        // The raw text, not GmOptions.Bind: a value that is not a boolean is off here instead of an exception.
        string? raw = configuration.GetSection(GmOptions.SectionName)[nameof(GmOptions.AllowDangerousDebug)];
        return Create(bool.TryParse(raw, out bool configured) && configured, raw is not null, liveServer);
    }

    /// <summary>One line for a status reply.</summary>
    public string Describe()
        => Allowed ? "AllowDangerousDebug: on" + (LiveServer ? " (live server, set explicitly)" : string.Empty)
            : Configured && LiveServer ? "AllowDangerousDebug: off (live server needs the key set explicitly)"
            : "AllowDangerousDebug: off";

    /// <summary>
    /// The gate for a command handler: true when allowed; otherwise replies with the refusal, marks the command failed
    /// (<see cref="FailureReason"/>) and returns false, so the handler returns true ("handled") without doing anything. A host
    /// without <see cref="GmAuditFeature"/> counts as off.
    /// </summary>
    public static bool TryAllow(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        DangerousDebugGate gate = context.Session.Services.GetService<GmAuditFeature>()?.DangerousDebug
            ?? Create(configured: false, explicitlySet: false, WorldPacketToolsOptions.IsLiveServer());
        if (gate.Allowed)
        {
            return true;
        }

        context.Fail(FailureReason);
        context.Reply(gate.LiveServer ? RefusedLiveText : RefusedText);
        return false;
    }
}
