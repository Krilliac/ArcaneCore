using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.AntiCheat;

/// <summary>
/// <c>AntiCheat:SpellHoneypot</c>: a CMSG_CAST_SPELL for a spell no legitimate client can send (a spell that is deliberately inert
/// because nothing ever casts it, a test spell, a deprecated row, or an operator-listed GM-tool id) is refused as before and, when
/// enabled, also recorded as a strong anticheat signal. Off by default: retail simply refuses the cast, so the signal is a deviation.
/// </summary>
public sealed class SpellHoneypotOptions
{
    /// <summary>Record the signal. Bound from AntiCheat:SpellHoneypot:Enabled; default false (an ordinary refusal only, as retail).</summary>
    public bool Enabled { get; set; }

    /// <summary>Score weight of one trapped cast (0 to <see cref="AntiCheatScores.MaxWeight"/>). Default 60: one trapped cast reaches the rubberband score.</summary>
    public float Weight { get; set; } = 60f;

    /// <summary>
    /// Extra spell ids that count as traps, for example the ids of a GM tool spell that no player has. Bound from
    /// AntiCheat:SpellHoneypot:ToolSpellIds; default empty.
    /// </summary>
    public List<uint> ToolSpellIds { get; set; } = [];

    public SpellHoneypotOptions Clone()
    {
        var copy = (SpellHoneypotOptions)MemberwiseClone();
        copy.ToolSpellIds = [.. ToolSpellIds];
        return copy;
    }

    public IEnumerable<string> Validate()
    {
        if (!(Weight >= 0) || !float.IsFinite(Weight) || Weight > AntiCheatScores.MaxWeight)
        {
            yield return $"AntiCheat:SpellHoneypot:Weight ({Weight}) must be 0 to {AntiCheatScores.MaxWeight}.";
        }
    }
}

/// <summary>
/// The honeypot decision (pure, any thread): is this cast request a trap, and what is the finding. The refusal itself is the spell
/// system's ordinary one; this only decides whether a signal is recorded. A GM or any exempt session is never flagged (a GM is trapped only if
/// an operator raises <c>ExemptSecurity</c> above GameMaster, the same trade-off as <see cref="SpellHoneypotOptions.ToolSpellIds"/>).
/// Trap sources: the inert registry, <see cref="Spells.Gm.CustomSpellRegistry"/>, the configured tool ids and the World layer's built-in catalog.
/// </summary>
public static class CastSpellHoneypot
{
    /// <summary>The fixed finding text (never client input).</summary>
    public const string Detail = "cast of a never-castable spell (honeypot)";

    /// <summary>
    /// Whether <paramref name="spellId"/> is a trap: NeverCast, TestSpell or Deprecated in <see cref="DeliberatelyInertSpells"/>, in
    /// <see cref="Spells.Gm.CustomSpellRegistry"/>, listed in <see cref="SpellHoneypotOptions.ToolSpellIds"/>, or one of <paramref name="builtInToolSpellIds"/> (the World layer passes the GM first-login
    /// tool catalog, which lives above Game).
    /// </summary>
    public static bool IsTrap(SpellHoneypotOptions options, uint spellId, IReadOnlySet<uint>? builtInToolSpellIds = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Spells.Gm.CustomSpellRegistry.Contains(spellId))
        {
            return true; // a staff-only repurposed spell: no player's client sends it (GmSpellbook refuses the cast)
        }

        if (DeliberatelyInertSpells.TryGet(spellId, out InertSpell? entry)
            && entry.Reason is InertSpellReason.NeverCast or InertSpellReason.TestSpell or InertSpellReason.Deprecated)
        {
            return true;
        }

        return (builtInToolSpellIds?.Contains(spellId) ?? false) || options.ToolSpellIds.Contains(spellId);
    }

    /// <summary>The finding to record for a cast request, or null (honeypot off, caller exempt, or not a trap).</summary>
    public static AntiCheatFinding? Evaluate(SpellHoneypotOptions options, uint spellId, bool callerExempt, IReadOnlySet<uint>? builtInToolSpellIds = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || callerExempt || !IsTrap(options, spellId, builtInToolSpellIds))
        {
            return null;
        }

        return new AntiCheatFinding(AntiCheatViolation.Spell, options.Weight, Detail);
    }
}
