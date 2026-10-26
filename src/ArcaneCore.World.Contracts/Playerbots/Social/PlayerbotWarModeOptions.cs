namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:WarMode</c>: the memory side of the War Mode server mode for massive world PvP. Off by default; it is switched on
/// deliberately. War Mode never changes what a bot normally remembers: each relation keeps a second, war layer (affinity and Rival flag) that is fed the
/// same events under the war rules only while this is on, and every decision reads the war layer only while it is on, so switching it off leaves
/// normal behaviour exactly as if it had never been on. It does not change the rules of the game (it never force-flags anyone for PvP and a
/// player's opt-out still applies; the first rivalry that acts on this memory is the emote lane, <c>Social:Rivalry</c>, which reads the war layer toward real players only). Every key is live.
/// </summary>
public sealed class PlayerbotWarModeOptions
{
    /// <summary>Hold grudges harder and longer (off by default).</summary>
    public bool Enabled { get; set; }

    /// <summary>While enabled, every negative affinity change is multiplied by this (1..10); positive changes are untouched.</summary>
    public float NegativeMultiplier { get; set; } = 3.0f;

    /// <summary>In the war layer negative affinity never drifts back toward 0 and the war Rival flag is never cleared by drift, only by positive events that lift the war affinity back over the threshold.</summary>
    public bool NeverForget { get; set; } = true;

    /// <summary>In the war layer the daily affinity gain cap and the one-apology-a-day limit are lifted, and a relation a full memory drops from the normal layer stays for the war layer when the war layer holds a grudge against it (a war-only entry: the normal layer, and War Mode while it is off, never see it).</summary>
    public bool RemoveCaps { get; set; } = true;

    /// <summary>Wipe the war layer of every bot, running or not, at the first checkpoint after War Mode is switched off, and write every bot it changed once (off by default: the layer stays dormant and comes back when War Mode is switched on again).</summary>
    public bool ClearOnDisable { get; set; }

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:WarMode";
        if (!float.IsFinite(NegativeMultiplier) || NegativeMultiplier < 1f || NegativeMultiplier > 10f)
            throw new InvalidOperationException($"{section}: NegativeMultiplier must be 1..10.");
    }
}
