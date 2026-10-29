using ArcaneCore.Kernel.Rules;
using ArcaneCore.Kernel.Rules.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Data.World.Rules;

/// <summary>How one source row ended up in a conversion.</summary>
public enum EventAiConversionOutcome
{
    /// <summary>The row was written as an enabled rule.</summary>
    Converted,

    /// <summary>The row was written as a disabled rule (the old executor never runs it) and the reason says why.</summary>
    Disabled,

    /// <summary>The row could not be expressed as a rule and was not written.</summary>
    Unconvertible,
}

/// <summary>One finding of the converter: the <c>creature_ai_scripts.id</c> it concerns, what happened and why.</summary>
public sealed record EventAiConversionIssue(uint SourceId, EventAiConversionOutcome Outcome, string Reason)
{
    public override string ToString() => $"creature_ai_scripts {SourceId}: {Outcome}: {Reason}";
}

/// <summary>The result of <see cref="EventAiRuleConverter.Convert"/>: the rule rows, and every row that was not a plain conversion.</summary>
public sealed record EventAiConversion(RuleContent Content, IReadOnlyList<EventAiConversionIssue> Issues, int SourceRows)
{
    public int Converted { get; init; }

    public int Disabled => Issues.Count(i => i.Outcome == EventAiConversionOutcome.Disabled);

    public int Unconvertible => Issues.Count(i => i.Outcome == EventAiConversionOutcome.Unconvertible);
}

/// <summary>
/// Converts EventAI rows (<c>creature_ai_scripts</c> with <c>creature_ai_texts</c> and <c>broadcast_text</c>) to rows of the creature rule host
/// (<c>rule</c>, <c>rule_action</c>, <c>rule_text</c>). It is a pure function of its input: the same content gives the same rows and the same ids.
/// The id scheme and the mapping of every EventAI type are in <c>docs/reference/rules-conversion.md</c>; the vocabulary ids are
/// <see cref="CreatureRuleIds"/>.
/// <para>
/// A row that cannot be expressed is reported by id and reason and is not written (it is never silently dropped or clamped). Converted rows are
/// content of the operator's database: they are written by the importer command and never committed to the repository.
/// </para>
/// </summary>
public static class EventAiRuleConverter
{
    /// <summary>The longest timer the rule engine accepts, in milliseconds (the 24 h cap of <c>cooldown_*_ms</c> and <c>delay_ms</c>).</summary>
    public const int MaxTimerMs = 86_400_000;

    private const int MaxPhase = 31;

    // EventAiFlags values (cmangos EventFlags).
    private const uint FlagRepeatable = 0x01;
    private const uint FlagRandomAction = 0x20;
    private const uint FlagDebugOnly = 0x80;
    private const uint FlagRangedModeOnly = 0x100;
    private const uint FlagMeleeModeOnly = 0x200;
    private const uint FlagCombatAction = 0x400;

    private const byte EventSpawned = 11;
    private const byte ActionText = 1;
    private const byte ActionTextNew = 54;
    private const byte ActionSetPhase = 22;
    private const byte ActionSummonId = 32;

    /// <summary>One EventAI event type: the rule event id, which source parameters (0-based) fill the rule's event parameters, and where the repeat range is.</summary>
    /// <param name="RuleId">The creature event id.</param>
    /// <param name="Params">Source parameter indices that become <c>event_param1..</c> in order.</param>
    /// <param name="RepeatMin">Source index of the repeat minimum, or -1 when the event never repeats.</param>
    /// <param name="RepeatMax">Source index of the repeat maximum, or -1.</param>
    /// <param name="TimerParams">Source indices that are initial timers in ms (checked against the cap).</param>
    public sealed record EventMap(short RuleId, int[] Params, int RepeatMin, int RepeatMax, int[] TimerParams, int BoolMask, int NonNegativeMask);

    /// <summary>One EventAI action type: the rule action id and how its three parameters and its target move over.</summary>
    /// <param name="RuleId">The creature action id (or the shared one).</param>
    /// <param name="Params">For rule parameter 1..n, the 1-based source parameter that supplies it.</param>
    /// <param name="TargetParam">The 1-based source parameter that holds the EventAI target type, or 0 for an action without one.</param>
    /// <param name="Optional">The old handler ignored a target that did not resolve, so the rule's target is optional (<c>target_param1 = 1</c>).</param>
    public sealed record ActionMap(short RuleId, int[] Params, int TargetParam, bool Optional, int BoolMask, int NonNegativeMask, int PositiveMask);

    private static readonly int[] None = [];

    private static EventMap Event(short id, int[] parameters, int repeatMin = 2, int repeatMax = 3, int[]? timers = null, int bools = 0, int nonNegative = 0)
        => new(id, parameters, repeatMin, repeatMax, timers ?? None, bools, nonNegative);

    /// <summary>EventAI event type to creature rule event.</summary>
    public static IReadOnlyDictionary<byte, EventMap> Events { get; } = new Dictionary<byte, EventMap>
    {
        [0] = Event(CreatureRuleIds.EventTimerInCombat, [0, 1], timers: [0, 1]),
        [1] = Event(CreatureRuleIds.EventTimerOutOfCombat, [0, 1], timers: [0, 1]),
        [2] = Event(CreatureRuleIds.EventHealthPct, [0, 1, 4], bools: 4),
        [3] = Event(CreatureRuleIds.EventManaPct, [0, 1]),
        [4] = Event(CreatureRuleIds.EventAggro, [], -1, -1),
        [5] = Event(CreatureRuleIds.EventKill, [2], 0, 1),
        [6] = Event(CreatureRuleIds.EventDeath, [0], -1, -1, nonNegative: 1),
        [7] = Event(CreatureRuleIds.EventEvade, [], -1, -1),
        [8] = Event(CreatureRuleIds.EventSpellHit, [0, 1]),
        [9] = Event(CreatureRuleIds.EventTargetInRange, [0, 1]),
        [10] = Event(CreatureRuleIds.EventOutOfCombatLineOfSight, [0, 1, 4, 5], nonNegative: 8),
        [11] = Event(CreatureRuleIds.EventSpawned, [0, 1], -1, -1),
        [12] = Event(CreatureRuleIds.EventTargetHealthPct, [0, 1]),
        [13] = Event(CreatureRuleIds.EventTargetCasting, [], 0, 1),
        [14] = Event(CreatureRuleIds.EventFriendlyHealth, [0, 1, 4], bools: 4),
        [15] = Event(CreatureRuleIds.EventFriendlyCrowdControlled, [1]),
        [16] = Event(CreatureRuleIds.EventFriendlyMissingBuff, [0, 1, 4]),
        [17] = Event(CreatureRuleIds.EventSummoned, [0], 1, 2),
        [18] = Event(CreatureRuleIds.EventTargetManaPct, [0, 1]),
        [21] = Event(CreatureRuleIds.EventReachedHome, [], -1, -1),
        [22] = Event(CreatureRuleIds.EventReceiveEmote, [0, 1], nonNegative: 2),
        [23] = Event(CreatureRuleIds.EventHasAura, [0, 1]),
        [24] = Event(CreatureRuleIds.EventTargetHasAura, [0, 1]),
        [25] = Event(CreatureRuleIds.EventSummonedDied, [0], 1, 2),
        [26] = Event(CreatureRuleIds.EventSummonedDespawned, [0], 1, 2),
        [27] = Event(CreatureRuleIds.EventMissingAura, [0, 1]),
        [28] = Event(CreatureRuleIds.EventTargetMissingAura, [0, 1]),
        [29] = Event(CreatureRuleIds.EventTimer, [0, 1], timers: [0, 1]),
        [30] = Event(CreatureRuleIds.EventReceiveAiEvent, [0, 1]),
        [31] = Event(CreatureRuleIds.EventEnergyPct, [0, 1]),
        [32] = Event(CreatureRuleIds.EventSelectThreatTarget, [0, 1]),
        [33] = Event(CreatureRuleIds.EventFacingTarget, [0]),
        [34] = Event(CreatureRuleIds.EventSpellHitTarget, [0, 1]),
        [36] = Event(CreatureRuleIds.EventTargetUnreachable, []),
    };

    private static ActionMap Action(short id, int[] parameters, int targetParam = 0, bool optional = false, int bools = 0, int nonNegative = 0, int positive = 0)
        => new(id, parameters, targetParam, optional, bools, nonNegative, positive);

    /// <summary>EventAI action type to creature rule action (types 1, 22 and 54 are converted by hand and are listed here for the mapping table).</summary>
    public static IReadOnlyDictionary<byte, ActionMap> Actions { get; } = new Dictionary<byte, ActionMap>
    {
        [1] = Action(CreatureRuleIds.ActionSay, [1, 2, 3]),
        [2] = Action(CreatureRuleIds.ActionSetFaction, [1], nonNegative: 1),
        [3] = Action(CreatureRuleIds.ActionMorph, [1, 2], nonNegative: 3),
        [4] = Action(CreatureRuleIds.ActionPlaySound, [1], nonNegative: 1),
        [5] = Action(CreatureRuleIds.ActionEmote, [1], nonNegative: 1),
        [9] = Action(CreatureRuleIds.ActionRandomSound, [1, 2, 3]),
        [10] = Action(CreatureRuleIds.ActionRandomEmote, [1, 2, 3]),
        [11] = Action(CreatureRuleIds.ActionCast, [1, 3], targetParam: 2, nonNegative: 2, positive: 1),
        [12] = Action(CreatureRuleIds.ActionSummon, [1, 3], targetParam: 2, optional: true, nonNegative: 2, positive: 1),
        [13] = Action(CreatureRuleIds.ActionThreatSingle, [1, 3], targetParam: 2, bools: 2),
        [14] = Action(CreatureRuleIds.ActionThreatAllPct, [1]),
        [15] = Action(CreatureRuleIds.ActionQuestEvent, [1, 3], targetParam: 2, optional: true, bools: 2, nonNegative: 1),
        [17] = Action(CreatureRuleIds.ActionSetUnitField, [1, 2], targetParam: 3, optional: true),
        [18] = Action(CreatureRuleIds.ActionAddUnitFlags, [1], targetParam: 2, optional: true),
        [19] = Action(CreatureRuleIds.ActionRemoveUnitFlags, [1], targetParam: 2, optional: true),
        [20] = Action(CreatureRuleIds.ActionAutoAttack, [1], bools: 1),
        [21] = Action(CreatureRuleIds.ActionCombatMovement, [1], bools: 1),
        [22] = Action(CreatureRuleIds.SharedActionSetPhase, [1]),
        [23] = Action(CreatureRuleIds.ActionIncPhase, [1]),
        [24] = Action(CreatureRuleIds.ActionEvade, [1], bools: 1),
        [25] = Action(CreatureRuleIds.ActionFleeForAssist, []),
        [28] = Action(CreatureRuleIds.ActionRemoveAuras, [2], targetParam: 1, optional: true, nonNegative: 1),
        [29] = Action(CreatureRuleIds.ActionRangedMovement, [1, 2]),
        [30] = Action(CreatureRuleIds.ActionRandomPhase, [1, 2, 3]),
        [31] = Action(CreatureRuleIds.ActionRandomPhaseRange, [1, 2]),
        [32] = Action(CreatureRuleIds.ActionSummonAt, [1], targetParam: 2, optional: true, positive: 1),
        [33] = Action(CreatureRuleIds.ActionKillCredit, [1], targetParam: 2, optional: true, nonNegative: 1),
        [34] = Action(CreatureRuleIds.ActionSetInstanceData, [1, 2]),
        [35] = Action(CreatureRuleIds.ActionSetInstanceData64, [1], targetParam: 2),
        [36] = Action(CreatureRuleIds.ActionUpdateTemplate, [1], nonNegative: 1),
        [37] = Action(CreatureRuleIds.ActionDie, []),
        [38] = Action(CreatureRuleIds.ActionZoneCombatPulse, []),
        [39] = Action(CreatureRuleIds.ActionCallForHelp, [1]),
        [40] = Action(CreatureRuleIds.ActionSetSheath, [1]),
        [41] = Action(CreatureRuleIds.ActionDespawn, [1], nonNegative: 1),
        [42] = Action(CreatureRuleIds.ActionSetDeathPrevention, [1], bools: 1),
        [43] = Action(CreatureRuleIds.ActionMount, [1, 2], nonNegative: 3),
        [45] = Action(CreatureRuleIds.ActionSendAiEvent, [1, 2], targetParam: 3, nonNegative: 1),
        [47] = Action(CreatureRuleIds.ActionSetStandState, [1]),
        [48] = Action(CreatureRuleIds.ActionChangeMovement, [1, 2, 3]),
        [50] = Action(CreatureRuleIds.ActionSetReactState, [1]),
        [51] = Action(CreatureRuleIds.ActionPauseWaypoints, [1], bools: 1),
        [53] = Action(CreatureRuleIds.ActionStartRelayScript, [1], targetParam: 2),
        [54] = Action(CreatureRuleIds.ActionSay, [1], targetParam: 2),
        [55] = Action(CreatureRuleIds.ActionAttackStart, [], targetParam: 1),
        [56] = Action(CreatureRuleIds.ActionDespawnGuardians, [1], nonNegative: 1),
        [57] = Action(CreatureRuleIds.ActionSetRangedMode, [1, 2]),
        [58] = Action(CreatureRuleIds.ActionSetWalk, [1]),
        [59] = Action(CreatureRuleIds.ActionSetFacing, [2], targetParam: 1, bools: 1),
        [61] = Action(CreatureRuleIds.ActionSetImmobilized, [1, 2], bools: 3),
        [63] = Action(CreatureRuleIds.ActionSetImmunitySet, [1], nonNegative: 1),
        [64] = Action(CreatureRuleIds.ActionSetFollowMovement, [1]),
    };

    /// <summary>EventAI target type (<c>EventAiTarget</c> numbering) to creature rule target.</summary>
    public static IReadOnlyDictionary<int, short> Targets { get; } = new Dictionary<int, short>
    {
        [0] = CreatureRuleIds.TargetSelf,
        [1] = CreatureRuleIds.TargetVictim,
        [2] = CreatureRuleIds.TargetSecondOnThreat,
        [3] = CreatureRuleIds.TargetLastOnThreat,
        [4] = CreatureRuleIds.TargetRandomOnThreat,
        [5] = CreatureRuleIds.TargetRandomNotTop,
        [6] = CreatureRuleIds.TargetInvoker,
        [7] = CreatureRuleIds.TargetInvokerOwner,
        [8] = CreatureRuleIds.TargetRandomPlayer,
        [9] = CreatureRuleIds.TargetRandomNotTopPlayer,
        [10] = CreatureRuleIds.TargetEventSender,
        [11] = CreatureRuleIds.TargetSpawner,
        [12] = CreatureRuleIds.TargetEventTarget,
        [15] = CreatureRuleIds.TargetDefault,
    };

    /// <summary>The <c>rule.id</c> a source row converts to.</summary>
    public static int RuleIdOf(uint sourceId) => checked(CreatureRuleIds.ConvertedRuleIdBase + (int)sourceId);

    /// <summary>The <c>rule_text.id</c> an EventAI text reference converts to: the converted band plus the broadcast text id for a positive id, plus <c>AiTextIdBase + N</c> for a negative id <c>-N</c>. Null when the id cannot be represented.</summary>
    public static int? TextIdOf(int eventAiTextId)
    {
        if (eventAiTextId > 0 && eventAiTextId < CreatureRuleIds.BroadcastTextIdLimit)
        {
            return CreatureRuleIds.ConvertedTextIdBase + eventAiTextId;
        }

        if (eventAiTextId < 0 && eventAiTextId > -CreatureRuleIds.AiTextIdBase)
        {
            return CreatureRuleIds.ConvertedTextIdBase + CreatureRuleIds.AiTextIdBase - eventAiTextId;
        }

        return null;
    }

    /// <summary>The rule phase mask of an EventAI inverse phase mask, or null when the mask hides the row in every phase (the row is disabled).</summary>
    public static int? PhaseMaskOf(uint inversePhaseMask)
        => inversePhaseMask == uint.MaxValue ? null : inversePhaseMask == 0 ? 0 : unchecked((int)~inversePhaseMask);

    /// <summary>Converts every row of <paramref name="content"/>; texts and summon positions are looked up in it.</summary>
    public static EventAiConversion Convert(CreatureAiContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var rules = new List<RuleRecord>();
        var actions = new List<RuleActionRecord>();
        var texts = new SortedDictionary<int, RuleTextRecord>();
        var issues = new List<EventAiConversionIssue>();
        int converted = 0;
        foreach (CreatureAiEvent row in content.AllEvents.OrderBy(r => r.Id))
        {
            RuleRecord? rule = ConvertRow(content, row, actions, texts, out string? failure, out string? disabledReason);
            if (rule is null)
            {
                issues.Add(new EventAiConversionIssue(row.Id, EventAiConversionOutcome.Unconvertible, failure ?? "unknown"));
                continue;
            }

            rules.Add(rule);
            if (disabledReason is not null)
            {
                issues.Add(new EventAiConversionIssue(row.Id, EventAiConversionOutcome.Disabled, disabledReason));
            }
            else
            {
                converted++;
            }
        }

        var result = new RuleContent
        {
            Rules = rules,
            Actions = actions,
            Texts = [.. texts.Values],
        };
        return new EventAiConversion(result, issues, content.AllEvents.Count) { Converted = converted };
    }

    private static RuleRecord? ConvertRow(
        CreatureAiContent content,
        CreatureAiEvent row,
        List<RuleActionRecord> actionSink,
        SortedDictionary<int, RuleTextRecord> textSink,
        out string? failure,
        out string? disabledReason)
    {
        failure = null;
        disabledReason = null;
        if (row.Id > CreatureRuleIds.MaxConvertedSourceId)
        {
            failure = $"source id above {CreatureRuleIds.MaxConvertedSourceId} does not fit the converted id band";
            return null;
        }

        bool guidRow = row.CreatureGuid != 0;
        uint owner = guidRow ? row.CreatureGuid : row.CreatureId;
        if (owner == 0 || owner > int.MaxValue)
        {
            failure = owner == 0 ? "row has neither a creature entry nor a spawn guid" : "owner id does not fit rule.owner_id";
            return null;
        }

        if (!Events.TryGetValue(row.EventType, out EventMap? map))
        {
            failure = $"event type {row.EventType} is not supported by the EventAI executor";
            return null;
        }

        int[] source = [row.Param1, row.Param2, row.Param3, row.Param4, row.Param5, row.Param6];
        if (row.EventType == EventSpawned && source[0] is < 0 or > 2)
        {
            failure = $"spawned condition {source[0]} is not supported";
            return null;
        }

        foreach (int index in map.TimerParams)
        {
            if (source[index] > MaxTimerMs)
            {
                failure = $"initial timer {source[index]} ms is over the 24 h cap ({MaxTimerMs} ms)";
                return null;
            }
        }

        int repeatMin = 0;
        int repeatMax = 0;
        bool oneShot = false;
        if (map.RepeatMin >= 0)
        {
            repeatMin = source[map.RepeatMin];
            repeatMax = source[map.RepeatMax];
            if (repeatMin < 0 || repeatMax < 0)
            {
                failure = $"repeat timer {repeatMin}..{repeatMax} ms is negative";
                return null;
            }

            if (repeatMin > MaxTimerMs || repeatMax > MaxTimerMs)
            {
                failure = $"repeat timer {repeatMin}..{repeatMax} ms is over the 24 h cap ({MaxTimerMs} ms)";
                return null;
            }

            if (repeatMax < repeatMin)
            {
                repeatMin = 0;
                repeatMax = 0;
                oneShot = true;
            }
        }

        uint flags = row.Flags;
        CreatureAiAction[] sourceActions = [.. row.Actions.Where(a => !a.IsEmpty)];
        if ((flags & FlagCombatAction) != 0 && row.Action1.IsEmpty)
        {
            failure = "combat-action row with an empty first action (the old executor never completes it)";
            return null;
        }

        int ruleId = RuleIdOf(row.Id);
        var converted = new List<RuleActionRecord>(sourceActions.Length + 1);
        var rowTexts = new List<RuleTextRecord>();
        byte seq = 1;
        foreach (CreatureAiAction action in sourceActions)
        {
            RuleActionRecord? record = ConvertAction(content, ruleId, seq, action, rowTexts, out string? actionFailure);
            if (record is null)
            {
                failure = actionFailure;
                return null;
            }

            converted.Add(record);
            seq++;
        }

        var rowFlags = CreatureRuleIds.RowFlags.None;
        if ((flags & FlagRepeatable) != 0)
        {
            rowFlags |= CreatureRuleIds.RowFlags.Repeatable;
        }

        if ((flags & FlagRandomAction) != 0)
        {
            rowFlags |= CreatureRuleIds.RowFlags.RandomAction;
        }

        if ((flags & FlagCombatAction) != 0)
        {
            rowFlags |= CreatureRuleIds.RowFlags.CombatAction;
        }

        if ((flags & FlagRangedModeOnly) != 0)
        {
            rowFlags |= CreatureRuleIds.RowFlags.RangedModeOnly;
        }

        if ((flags & FlagMeleeModeOnly) != 0)
        {
            rowFlags |= CreatureRuleIds.RowFlags.MeleeModeOnly;
        }

        if (oneShot)
        {
            rowFlags |= CreatureRuleIds.RowFlags.OneShot;
        }

        actionSink.Add(new RuleActionRecord
        {
            OwnerId = ruleId,
            Seq = 0,
            ActionType = CreatureRuleIds.ActionRow,
            TargetType = CreatureRuleIds.TargetSelf,
            Param1 = (int)rowFlags,
            Param2 = converted.Count,
        });
        actionSink.AddRange(converted);
        foreach (RuleTextRecord text in rowTexts)
        {
            textSink[text.Id] = text;
        }

        int? phaseMask = PhaseMaskOf(row.InversePhaseMask);
        bool enabled = true;
        if (phaseMask is null)
        {
            enabled = false;
            disabledReason = "inverse phase mask hides the row in all 32 phases, so it never runs";
        }
        else if (row.Chance == 0)
        {
            enabled = false;
            disabledReason = "chance 0: the old executor re-arms the event without running any action";
        }
        else if ((flags & FlagDebugOnly) != 0)
        {
            enabled = false;
            disabledReason = "debug-only row: the old executor skips it unless debug events are on";
        }

        int[] eventParams = new int[4];
        for (int i = 0; i < map.Params.Length; i++)
        {
            int value = source[map.Params[i]];
            if ((map.BoolMask >> i & 1) != 0)
            {
                value = value != 0 ? 1 : 0;
            }
            else if ((map.NonNegativeMask >> i & 1) != 0 && value < 0)
            {
                failure = $"event parameter {map.Params[i] + 1} = {value} must not be negative";
                return null;
            }

            eventParams[i] = value;
        }

        return new RuleRecord
        {
            Id = ruleId,
            Host = CreatureRuleIds.Host,
            OwnerKind = guidRow ? CreatureRuleIds.OwnerGuid : CreatureRuleIds.OwnerEntry,
            OwnerId = (int)owner,
            EventType = map.RuleId,
            EventParam1 = eventParams[0],
            EventParam2 = eventParams[1],
            EventParam3 = eventParams[2],
            EventParam4 = eventParams[3],
            PhaseMask = phaseMask ?? 0,
            ChancePct = (byte)Math.Clamp((int)row.Chance, 1, 100),
            CooldownMinMs = repeatMin,
            CooldownMaxMs = repeatMax,
            Priority = (short)(guidRow ? 1 : 0),
            Flags = CreatureRuleIds.FlagRepeatable,
            Enabled = enabled,
            Comment = $"eventai {row.Id}",
        };
    }

    private static RuleActionRecord? ConvertAction(
        CreatureAiContent content,
        int ruleId,
        byte seq,
        CreatureAiAction action,
        List<RuleTextRecord> rowTexts,
        out string? failure)
    {
        failure = null;
        if (!Actions.TryGetValue(action.Type, out ActionMap? map))
        {
            failure = $"action type {action.Type} is not supported by the EventAI executor";
            return null;
        }

        int[] source = [action.Param1, action.Param2, action.Param3];
        int[] parameters = new int[6];
        short targetType = CreatureRuleIds.TargetSelf; // every rule action names a target; the creature target also applies the row gate (random action, combat action)
        int targetParam1 = 0;

        switch (action.Type)
        {
            case ActionText:
                if (!ConvertTexts(content, [source[0], source[1], source[2]], rowTexts, out int[]? ids, out failure))
                {
                    return null;
                }

                parameters[0] = ids![0];
                parameters[1] = ids[1];
                parameters[2] = ids[2];
                targetType = CreatureRuleIds.TargetTextAudience;
                break;

            case ActionTextNew:
                if (source[2] != 0)
                {
                    failure = source[2] < 0
                        ? $"text action {action.Type} has a negative template id {source[2]}"
                        : $"text action {action.Type} uses text template {source[2]} (random template text is converted with the DB scripts lane)";
                    return null;
                }

                if (!ConvertTexts(content, [source[0], 0, 0], rowTexts, out int[]? newIds, out failure))
                {
                    return null;
                }

                parameters[0] = newIds![0];
                break;

            case ActionSetPhase:
                parameters[0] = Math.Clamp(source[0], 0, MaxPhase);
                break;

            case ActionSummonId:
                if (content.FindSummon((uint)source[2]) is not { } at)
                {
                    failure = $"summon position {source[2]} (creature_ai_summons) does not exist";
                    return null;
                }

                if (at.LifetimeMs > MaxTimerMs)
                {
                    failure = $"summon {source[2]} lifetime {at.LifetimeMs} ms is over the 24 h cap ({MaxTimerMs} ms)";
                    return null;
                }

                parameters[0] = source[0];
                parameters[1] = (int)at.LifetimeMs;
                parameters[2] = BitConverter.SingleToInt32Bits(at.X);
                parameters[3] = BitConverter.SingleToInt32Bits(at.Y);
                parameters[4] = BitConverter.SingleToInt32Bits(at.Z);
                parameters[5] = BitConverter.SingleToInt32Bits(at.Orientation);
                break;

            default:
                for (int i = 0; i < map.Params.Length; i++)
                {
                    parameters[i] = source[map.Params[i] - 1];
                }

                break;
        }

        for (int i = 0; i < map.Params.Length; i++)
        {
            if ((map.BoolMask >> i & 1) != 0)
            {
                parameters[i] = parameters[i] != 0 ? 1 : 0;
            }
            else if ((map.PositiveMask >> i & 1) != 0 && parameters[i] <= 0)
            {
                failure = $"action {action.Type} parameter {map.Params[i]} = {parameters[i]} must be positive";
                return null;
            }
            else if ((map.NonNegativeMask >> i & 1) != 0 && parameters[i] < 0)
            {
                failure = $"action {action.Type} parameter {map.Params[i]} = {parameters[i]} must not be negative";
                return null;
            }
        }

        if (action.Type == 12 && parameters[1] > MaxTimerMs)
        {
            failure = $"summon lifetime {parameters[1]} ms is over the 24 h cap ({MaxTimerMs} ms)";
            return null;
        }

        int targetSource = map.TargetParam;
        if (action.Type == 59 && source[1] != 0)
        {
            targetSource = 0; // SET_FACING with the reset flag never looks at its target
        }

        if (targetSource != 0)
        {
            int eventAiTarget = source[targetSource - 1];
            if (!Targets.TryGetValue(eventAiTarget, out short mapped))
            {
                failure = $"target type {eventAiTarget} of action {action.Type} is not supported by the EventAI executor";
                return null;
            }

            targetType = mapped;
            targetParam1 = map.Optional ? 1 : 0;
        }

        return new RuleActionRecord
        {
            OwnerId = ruleId,
            Seq = seq,
            ActionType = map.RuleId,
            Param1 = parameters[0],
            Param2 = parameters[1],
            Param3 = parameters[2],
            Param4 = parameters[3],
            Param5 = parameters[4],
            Param6 = parameters[5],
            TargetType = targetType,
            TargetParam1 = targetParam1,
        };
    }

    // The TEXT action picks among up to three texts: all three when two and three are set, two when only the second is, else the first.
    private static bool ConvertTexts(CreatureAiContent content, int[] sourceIds, List<RuleTextRecord> rowTexts, out int[]? ids, out string? failure)
    {
        failure = null;
        ids = new int[3];
        int count = sourceIds[1] != 0 && sourceIds[2] != 0 ? 3 : sourceIds[1] != 0 ? 2 : 1;
        if (sourceIds[0] == 0)
        {
            failure = "text action without a text id";
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            int id = sourceIds[i];
            if (TextIdOf(id) is not { } ruleTextId)
            {
                failure = $"text id {id} does not fit the rule_text id bands";
                return false;
            }

            if (content.FindText(id) is not { } text)
            {
                failure = $"text {id} does not exist";
                return false;
            }

            if (text.Language > byte.MaxValue)
            {
                failure = $"text {id} has language {text.Language} outside the rule_text range";
                return false;
            }

            if (string.IsNullOrWhiteSpace(text.Content))
            {
                failure = $"text {id} is empty";
                return false;
            }

            rowTexts.Add(new RuleTextRecord
            {
                Id = ruleTextId,
                ContentDefault = text.Content,
                Sound = (int)text.Sound,
                Type = text.Type,
                Language = (byte)text.Language,
                Emote = (int)text.Emote,
                BroadcastTextId = (int)text.BroadcastTextId,
                Comment = id < 0 ? $"creature_ai_texts {id}" : $"broadcast_text {id}",
            });
            ids[i] = ruleTextId;
        }

        return true;
    }
}
