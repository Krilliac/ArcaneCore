using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// What event and action handlers see of the creature and the world: the creature, its map system, the phase,
/// combat state, an injectable random source and the clock, and the target resolver (cmangos
/// <c>GetTargetByType</c>, CreatureEventAI.cpp:1715-1825). Randomness and time come only from the map system,
/// so a test with a seeded system is deterministic.
/// </summary>
public sealed class EventAiContext
{
    private readonly CreatureEventAI _ai;

    internal EventAiContext(CreatureEventAI ai, EventAiOptions options)
    {
        _ai = ai;
        Options = options;
    }

    public EventAiOptions Options { get; }

    public Creature Me => _ai.Me;

    /// <summary>The EventAI content (rows, texts, broadcast texts, summon locations) this creature runs on.</summary>
    public Kernel.WorldData.Creatures.CreatureAiContent Content => _ai.AiContent;

    internal CreatureEventAI Ai => _ai;

    /// <summary>The creature's map system, or null while it is outside a map.</summary>
    public CreatureMapSystem? System => _ai.Host;

    /// <summary>The current phase (0..31, cmangos m_Phase).</summary>
    public int Phase { get; set; }

    public bool InCombat => Me.Combat.IsInCombat;

    public bool IsEvading => Me.IsInEvadeMode;

    public Unit? Victim => Me.Combat.Victim;

    /// <summary>The map system clock in milliseconds (0 outside a map).</summary>
    public long ClockMs => System?.ClockMs ?? 0;

    /// <summary>Whether the creature is currently in ranged (caster) mode.</summary>
    public bool RangedMode => _ai.CurrentRangedMode;

    /// <summary>The threat entries, highest first (empty without a threat list).</summary>
    public IReadOnlyList<ThreatEntry> Threat => Me.Combat.HasThreatList ? Me.Combat.Threat.Entries : [];

    /// <summary>Whether the creature is casting a non-melee spell (cmangos IsNonMeleeSpellCasted).</summary>
    public bool IsCasting => System?.AiServices.Spells?.IsCasting(Me) ?? false;

    /// <summary>
    /// cmangos UnitAI::CanExecuteCombatAction (UnitAI.cpp:778): alive, not silenced and pacified together, and not
    /// in the middle of a non-melee cast. (The combat-script flag and propelled/retreating states do not exist yet.)
    /// </summary>
    public bool CanExecuteCombatAction
        => Me.IsAlive && !((Me.UnitFlags & UnitFlags.Silenced) != 0 && (Me.UnitFlags & UnitFlags.Pacified) != 0) && !IsCasting;

    /// <summary>The stack amount of <paramref name="spellId"/> on <paramref name="unit"/> (0 when absent or without a spell system).</summary>
    public int AuraStacks(Unit unit, uint spellId) => System?.AiServices.UnitSpells?.GetAuraStacks(unit, spellId) ?? 0;

    /// <summary>Whether <paramref name="unit"/> is casting a non-melee spell (cmangos IsNonMeleeSpellCasted).</summary>
    public bool IsCastingNonMelee(Unit unit) => System?.AiServices.UnitSpells?.IsCasting(unit) ?? false;

    /// <summary>A uniform integer in [<paramref name="min"/>, <paramref name="max"/>] (cmangos urand).</summary>
    public int Random(int min, int max) => System?.RandomInt(min, max) ?? min;

    /// <summary>A full-width random value (cmangos <c>urand()</c> without bounds); 0 outside a map, so chances pass deterministically.</summary>
    public uint NextRandom() => System is { } system ? (uint)system.RandomInt(0, int.MaxValue) : 0;

    /// <summary>
    /// cmangos GetTargetByType for the target types with a resolver. <paramref name="error"/> is set when the type
    /// needs a unit that is not there or has no resolver yet (the action then fails).
    /// </summary>
    public Unit? SelectTarget(int target, EventAiInvocation invocation, out bool error)
    {
        error = false;
        IReadOnlyList<ThreatEntry> threat = Threat;
        Unit? result;
        switch (target)
        {
            case (int)EventAiTarget.Self:
                return Me;
            case (int)EventAiTarget.Victim:
                result = Victim;
                break;
            case (int)EventAiTarget.SecondOnThreat:
                result = threat.Count > 1 ? threat[1].Target : null;
                break;
            case (int)EventAiTarget.LastOnThreat:
                result = threat.Count > 0 ? threat[^1].Target : null;
                break;
            case (int)EventAiTarget.RandomOnThreat:
                result = threat.Count > 0 ? threat[Random(0, threat.Count - 1)].Target : null;
                break;
            case (int)EventAiTarget.RandomNotTop:
                result = threat.Count > 1 ? threat[Random(1, threat.Count - 1)].Target : null;
                break;
            case (int)EventAiTarget.Invoker:
                result = invocation.Invoker;
                break;
            case 7: // TARGET_T_ACTION_INVOKER_OWNER: the beneficiary; without pets or owners that is the invoker
                result = invocation.Invoker;
                break;
            case 10: // TARGET_T_EVENT_SENDER
                result = invocation.Sender;
                break;
            case 12: // TARGET_T_EVENT_SPECIFIC
                result = invocation.EventTarget;
                break;
            case (int)EventAiTarget.None:
                return null;
            default:
                error = true;
                return null;
        }

        if (result is null)
        {
            error = true;
        }

        return result;
    }
}

/// <summary>
/// EventAI tuning, bound from <c>Creatures:EventAi</c>. Defaults are cmangos-classic's:
/// <c>EVENT_UPDATE_TIME</c> 500 ms (CreatureEventAI.h:32) and debug-only rows excluded.
/// </summary>
public sealed class EventAiOptions
{
    /// <summary>How often the timer-driven events are evaluated (cmangos EVENT_UPDATE_TIME). 500 is retail behaviour.</summary>
    public uint UpdateIntervalMs { get; set; } = 500;

    /// <summary>Run rows flagged EFLAG_DEBUG_ONLY (0x80); cmangos only does in a debug build. Off by default.</summary>
    public bool DebugOnlyEvents { get; set; }

    /// <summary>Report rows with unsupported events, actions or conditions once per creature entry.</summary>
    public bool ReportUnsupported { get; set; } = true;
}
