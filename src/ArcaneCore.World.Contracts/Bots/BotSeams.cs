using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Bots;

/// <summary>
/// How the core treats a managed bot's session (docs/design/hot-reload-tiers.md, section 4.1, "managed-bot session seam"). The
/// playerbot pack registers one on the core's <c>ManagedBotRegistry</c> while it is attached; without one (the pack detached) a
/// bot session does the full client packet work, which is always correct, only dearer.
/// </summary>
public interface IManagedBotSessionPolicy
{
    /// <summary>Whether bot sessions skip real-client packet work (<c>World:Playerbots:ClientFidelity</c> = Lean). Read on every packet, so live.</summary>
    bool Lean { get; }

    /// <summary>Whether a lean bot's session keeps an outbound packet of <paramref name="opcode"/> for the bot to read back.</summary>
    bool KeepsPacket(WorldOpcode opcode);

    /// <summary>
    /// World thread, just before a managed action dispatches <paramref name="opcode"/> for the bot (e.g. dismount for an action a
    /// mounted player cannot take).
    /// </summary>
    void BeforeAction(IPlayerSession session, Player player, WorldOpcode opcode);
}

/// <summary>Hears a spell cast a managed bot asked for and the server refused (bot QA's spell watch). World thread.</summary>
public interface IBotCastRefusalObserver
{
    void OnCastRefused(Player player, uint spellId, SpellCastResult result);
}

/// <summary>Where the tick watchdog reports a hang that was the code's doing (bot QA). Called on the watchdog thread; must not throw.</summary>
public interface IBotTickHangSink
{
    /// <summary>How long after start a hang is not reported (the start-up warm-up).</summary>
    TimeSpan Warmup { get; }

    /// <summary>Report a hang: the tick phase, the kind of work in it, the alert text and how many hangs it stands for.</summary>
    void ReportHang(string phase, string work, string message, long occurrences);

    /// <summary>
    /// A hang stack capture finished: how long the tick had run (ms) and the innermost <c>ArcaneCore</c> frames of the world thread (<c>Type.Method</c>, innermost first).
    /// Called on a thread-pool thread, not the watchdog thread; must not throw. The default does nothing.
    /// </summary>
    void ReportHangStack(long tickMs, IReadOnlyList<string> worldFrames)
    {
    }
}
