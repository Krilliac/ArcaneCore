namespace ArcaneCore.Kernel.Rules.Creatures;

/// <summary>
/// The creature host's vocabulary ids (<c>rule.event_type</c>, <c>rule_action.action_type</c>, <c>rule_action.target_type</c> of host
/// <c>creature</c>), shared by the primitives in the Game assembly and by the EventAI converter in the Data assembly (Data cannot reference
/// Game). The ids are stable: rows in a database name them. Events start at 1; actions and targets start at 100 and 10, clear of the
/// shared primitives (actions 1-4, targets 1-2), so shared ids may grow without colliding. docs/reference/rules-conversion.md maps every
/// EventAI type onto these.
/// </summary>
public static class CreatureRuleIds
{
    /// <summary><c>rule.host</c> value of the creature host (<c>RuleHostKind.Creature</c>).</summary>
    public const byte Host = 2;

    /// <summary><c>rule.owner_kind</c> of a creature entry (<c>RuleOwnerKind.Entry</c>).</summary>
    public const byte OwnerEntry = 1;

    /// <summary><c>rule.owner_kind</c> of a creature spawn guid (<c>RuleOwnerKind.Guid</c>).</summary>
    public const byte OwnerGuid = 2;

    /// <summary><c>rule.flags</c> bit <c>RuleFlags.Repeatable</c>.</summary>
    public const int FlagRepeatable = 0x01;

    // --- events ----------------------------------------------------------------------------------------------------------------

    public const short EventTimerInCombat = 1;
    public const short EventTimerOutOfCombat = 2;
    public const short EventTimer = 3;
    public const short EventHealthPct = 4;
    public const short EventManaPct = 5;
    public const short EventEnergyPct = 6;
    public const short EventTargetHealthPct = 7;
    public const short EventTargetManaPct = 8;
    public const short EventTargetInRange = 9;
    public const short EventTargetCasting = 10;
    public const short EventFacingTarget = 11;
    public const short EventTargetUnreachable = 12;
    public const short EventHasAura = 13;
    public const short EventTargetHasAura = 14;
    public const short EventMissingAura = 15;
    public const short EventTargetMissingAura = 16;
    public const short EventFriendlyHealth = 17;
    public const short EventFriendlyCrowdControlled = 18;
    public const short EventFriendlyMissingBuff = 19;
    public const short EventSelectThreatTarget = 20;
    public const short EventAggro = 21;
    public const short EventKill = 22;
    public const short EventDeath = 23;
    public const short EventEvade = 24;
    public const short EventSpellHit = 25;
    public const short EventSpawned = 26;
    public const short EventReachedHome = 27;
    public const short EventReceiveEmote = 28;
    public const short EventOutOfCombatLineOfSight = 29;
    public const short EventSummoned = 30;
    public const short EventSummonedDied = 31;
    public const short EventSummonedDespawned = 32;
    public const short EventReceiveAiEvent = 33;
    public const short EventSpellHitTarget = 34;

    // --- targets ---------------------------------------------------------------------------------------------------------------

    public const short TargetSelf = 10;
    public const short TargetVictim = 11;
    public const short TargetSecondOnThreat = 12;
    public const short TargetLastOnThreat = 13;
    public const short TargetRandomOnThreat = 14;
    public const short TargetRandomNotTop = 15;
    public const short TargetInvoker = 16;
    public const short TargetInvokerOwner = 17;
    public const short TargetRandomPlayer = 18;
    public const short TargetRandomNotTopPlayer = 19;
    public const short TargetEventSender = 20;
    public const short TargetSpawner = 21;
    public const short TargetEventTarget = 22;
    public const short TargetDefault = 23;
    public const short TargetTextAudience = 24;

    // --- actions ---------------------------------------------------------------------------------------------------------------

    /// <summary>The first action of a converted row: carries the row's EventAI options (<see cref="RowFlags"/>).</summary>
    public const short ActionRow = 100;
    public const short ActionSay = 101;
    public const short ActionSetFaction = 102;
    public const short ActionMorph = 103;
    public const short ActionPlaySound = 104;
    public const short ActionEmote = 105;
    public const short ActionRandomSound = 106;
    public const short ActionRandomEmote = 107;
    public const short ActionCast = 108;
    public const short ActionSummon = 109;
    public const short ActionThreatSingle = 110;
    public const short ActionThreatAllPct = 111;
    public const short ActionQuestEvent = 112;
    public const short ActionSetUnitField = 113;
    public const short ActionAddUnitFlags = 114;
    public const short ActionRemoveUnitFlags = 115;
    public const short ActionAutoAttack = 116;
    public const short ActionCombatMovement = 117;
    public const short ActionIncPhase = 118;
    public const short ActionEvade = 119;
    public const short ActionFleeForAssist = 120;
    public const short ActionRemoveAuras = 121;
    public const short ActionRangedMovement = 122;
    public const short ActionRandomPhase = 123;
    public const short ActionRandomPhaseRange = 124;
    public const short ActionSummonAt = 125;
    public const short ActionKillCredit = 126;
    public const short ActionSetInstanceData = 127;
    public const short ActionSetInstanceData64 = 128;
    public const short ActionUpdateTemplate = 129;
    public const short ActionDie = 130;
    public const short ActionZoneCombatPulse = 131;
    public const short ActionCallForHelp = 132;
    public const short ActionSetSheath = 133;
    public const short ActionDespawn = 134;
    public const short ActionSetDeathPrevention = 135;
    public const short ActionMount = 136;
    public const short ActionSendAiEvent = 137;
    public const short ActionSetStandState = 138;
    public const short ActionChangeMovement = 139;
    public const short ActionSetReactState = 140;
    public const short ActionPauseWaypoints = 141;
    public const short ActionStartRelayScript = 142;
    public const short ActionAttackStart = 143;
    public const short ActionDespawnGuardians = 144;
    public const short ActionSetRangedMode = 145;
    public const short ActionSetWalk = 146;
    public const short ActionSetFacing = 147;
    public const short ActionSetImmobilized = 148;
    public const short ActionSetImmunitySet = 149;
    public const short ActionSetFollowMovement = 150;

    /// <summary>The shared <c>set_phase</c> action (RuleHostKind.Any id 1), which EventAI SET_PHASE converts to.</summary>
    public const short SharedActionSetPhase = 1;

    // --- id schemes of converted content ---------------------------------------------------------------------------------------

    /// <summary>
    /// Converted <c>creature_ai_scripts</c> rows get <c>rule.id = ConvertedRuleIdBase + creature_ai_scripts.id</c> (2^30 plus the source id).
    /// Source ids reach into the millions (the classic EventAI ids are creature entry times 100 plus an index), so they cannot all fit
    /// below the 1 000 000 that the design reserves for shipped content; the band starts above the custom range (1 000 000 up to
    /// 2^30 - 1), so a converted id never collides with a rule authored on a server.
    /// </summary>
    public const int ConvertedRuleIdBase = 1 << 30;

    /// <summary>The largest <c>creature_ai_scripts.id</c> the id scheme can carry.</summary>
    public const uint MaxConvertedSourceId = (1u << 30) - 1;

    /// <summary>A positive EventAI text id (a <c>broadcast_text</c> id) is <c>rule_text.id</c> as it is, below this bound.</summary>
    public const int BroadcastTextIdLimit = 500_000;

    /// <summary>A negative EventAI text id (<c>creature_ai_texts</c>, -N) becomes <c>rule_text.id = AiTextIdBase + N</c>, for N below <see cref="BroadcastTextIdLimit"/>.</summary>
    public const int AiTextIdBase = 500_000;

    // --- the row marker --------------------------------------------------------------------------------------------------------

    /// <summary><c>creature.row</c> param1: the EventAI row options.</summary>
    [Flags]
    public enum RowFlags
    {
        None = 0,

        /// <summary>EFLAG_REPEATABLE: the event is not disabled after it triggers.</summary>
        Repeatable = 0x01,

        /// <summary>EFLAG_RANDOM_ACTION: one of the row's actions runs instead of all of them.</summary>
        RandomAction = 0x02,

        /// <summary>EFLAG_COMBAT_ACTION: skipped while the creature cannot act, retried when the first action fails.</summary>
        CombatAction = 0x04,

        /// <summary>EFLAG_RANGED_MODE_ONLY.</summary>
        RangedModeOnly = 0x08,

        /// <summary>EFLAG_MELEE_MODE_ONLY.</summary>
        MeleeModeOnly = 0x10,

        /// <summary>The repeat range is empty (maximum below minimum): the event disables itself after it triggers, whatever the repeatable flag says.</summary>
        OneShot = 0x20,
    }
}
