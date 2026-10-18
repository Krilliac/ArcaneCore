using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Arei (entry 9598, Felwood), "Ancient Spirit" (quest 4261): mangos-classic ScriptDev2 <c>npc_arei</c> (kalimdor/felwood.cpp:308-491, registered
/// :509-516) on <see cref="EscortAI"/>. Taking the quest makes her say her start line, take the neutral passive escort faction (113) until respawn
/// and run her script path. The first Irontree Wanderer or Stomper she fights draws one line, the first Toxic Horror another. At the last point (37)
/// she stops: an Irontree Stomper (7139, with her call to destroy it) and two Wanderers (7138) come at her (one minute out of combat); when the last
/// of them dies the epilogue plays (aEpilogDialogue): her sad line, 4 s, Ashenvale lies ahead, 5 s, something strange, 3 s, she transforms (14888)
/// and the curse lifts, 7 s, then she thanks the player, the group gets the quest event and she goes ten seconds later.
/// </summary>
public sealed class AreiAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 9598, QuestAncientSpirit = 4261, FactionEscortNeutralPassive = 113;
    public const uint NpcToxicHorror = 7132, NpcIrontreeWanderer = 7138, NpcIrontreeStomper = 7139;
    public const uint SpellWitherStrike = 5337, SpellAreiTransform = 14888, SummonLifetimeMs = 60_000, DespawnAfterMs = 10_000;
    public const int SayEscortStart = -1001159, SayAttackIrontree = -1001160, SayAttackToxicHorror = -1001161, SayExitWoods = -1001162;
    public const int SayClearPath = -1001163, SayAshenvale = -1001164, SayTransform = -1001165, SayLiftCurse = -1001166, SayEscortComplete = -1001167;

    /// <summary>The ambush of point 37: the Stomper first, then the two Wanderers (:423-425).</summary>
    public static readonly (uint Entry, float X, float Y, float Z)[] Ambush =
    [
        (NpcIrontreeStomper, 6573.321f, -1195.213f, 442.489f), (NpcIrontreeWanderer, 6573.240f, -1213.475f, 443.643f),
        (NpcIrontreeWanderer, 6583.354f, -1209.811f, 444.769f),
    ];

    /// <summary>aEpilogDialogue (:334-342): the step and the time to the next one.</summary>
    private static readonly (EpilogStep Step, uint DelayMs)[] Epilog =
    [
        (EpilogStep.ClearPath, 4000), (EpilogStep.Ashenvale, 5000), (EpilogStep.Transform, 3000), (EpilogStep.LiftCurse, 7000), (EpilogStep.Complete, 0),
    ];

    private enum EpilogStep
    {
        ClearPath,
        Ashenvale,
        Transform,
        LiftCurse,
        Complete,
    }

    private readonly List<ObjectGuid> _summons = [];
    private bool _aggroIrontree, _aggroHorror;
    private uint _witherStrikeMs;
    private int _epilogIndex = -1;
    private uint _epilogMs;

    /// <summary>Her script path ends at the ambush; she stays there for the epilogue (cmangos holds a paused escort at its last point).</summary>
    protected override bool HoldAtEndWhilePaused => true;

    /// <summary>The epilogue step running, or -1.</summary>
    public int EpilogIndex => _epilogIndex;

    /// <summary>The living ambushers she still waits for.</summary>
    public int SummonsLeft => _summons.Count;

    public uint WitherStrikeMs => _witherStrikeMs;

    /// <summary>Reset (:361-364).</summary>
    protected override void Reset() => _witherStrikeMs = (uint)(System?.RandomInt(1000, 4000) ?? 1000);

    /// <summary>QuestAccept_npc_arei and ReceiveAIEvent (:406-415, :485-491).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestAncientSpirit)
        {
            return;
        }

        System?.SayText(Me, SayEscortStart, player);
        Me.SetFactionTemporary(FactionEscortNeutralPassive, TemporaryFactionFlags.RestoreRespawn);
        Start(run: true, player: player, questId: questId);
    }

    /// <summary>Aggro (:366-379): one line for the first Irontree, one for the first Toxic Horror (to the escorting player).</summary>
    protected override void Aggro(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        uint entry = target is Creature c ? c.Entry : 0;
        if (entry is NpcIrontreeWanderer or NpcIrontreeStomper && !_aggroIrontree)
        {
            System?.SayText(Me, SayAttackIrontree);
            _aggroIrontree = true;
        }
        else if (entry == NpcToxicHorror && !_aggroHorror)
        {
            if (GetPlayerForEscort() is { } player)
            {
                System?.SayText(Me, SayAttackToxicHorror, player);
            }

            _aggroHorror = true;
        }
    }

    /// <summary>WaypointReached (:417-427): the ambush at point 37.</summary>
    protected override void WaypointReached(uint pointId)
    {
        if (pointId != 37)
        {
            return;
        }

        SetEscortPaused(true);
        if (System is not { } system)
        {
            return;
        }

        foreach ((uint entry, float x, float y, float z) in Ambush)
        {
            system.SummonAt(Me, entry, x, y, z, 0f, target: null, SummonLifetimeMs); // TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN, 60000
        }
    }

    /// <summary>JustSummoned (:381-393): the Stomper draws her call; each Irontree attacks her and is waited for.</summary>
    public override void OnJustSummoned(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        switch (summoned.Entry)
        {
            case NpcIrontreeStomper:
                System?.SayText(Me, SayExitWoods, summoned);
                goto case NpcIrontreeWanderer;
            case NpcIrontreeWanderer:
                summoned.AI?.AttackStart(Me);
                _summons.Add(summoned.Guid);
                break;
        }
    }

    /// <summary>SummonedCreatureJustDied (:395-404): the last Irontree down starts the epilogue.</summary>
    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        if (summoned.Entry is not (NpcIrontreeStomper or NpcIrontreeWanderer))
        {
            return;
        }

        _summons.Remove(summoned.Guid);
        if (_summons.Count == 0)
        {
            StartEpilogStep(0);
        }
    }

    /// <summary>DialogueHelper::StartNextDialogueText / JustDidDialogueStep (:437-459): the step now, the next after its delay.</summary>
    private void StartEpilogStep(int index)
    {
        _epilogIndex = index;
        (EpilogStep step, uint delay) = Epilog[index];
        _epilogMs = delay;
        if (System is not { } system)
        {
            return;
        }

        Player? player = GetPlayerForEscort();
        switch (step)
        {
            case EpilogStep.ClearPath:
                system.SayText(Me, SayClearPath);
                break;
            case EpilogStep.Ashenvale:
                if (player is not null)
                {
                    system.SayText(Me, SayAshenvale, player);
                }

                break;
            case EpilogStep.Transform:
                system.SayText(Me, SayTransform);
                break;
            case EpilogStep.LiftCurse:
                DoCast(Me, SpellAreiTransform);
                if (player is not null)
                {
                    system.SayText(Me, SayLiftCurse, player);
                }

                break;
            case EpilogStep.Complete:
                if (player is not null)
                {
                    system.SayText(Me, SayEscortComplete, player);
                    system.RewardGroupEventExplored(player, QuestAncientSpirit, Me);
                    system.ForcedDespawn(Me, DespawnAfterMs);
                }

                break;
        }

        if (delay == 0)
        {
            _epilogIndex = -1; // the last entry ends the dialogue
        }
    }

    /// <summary>UpdateEscortAI (:461-477): the epilogue, then Wither Strike every 3-6 s in a fight.</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_epilogIndex >= 0)
        {
            if (_epilogMs <= diffMs)
            {
                StartEpilogStep(_epilogIndex + 1);
            }
            else
            {
                _epilogMs -= diffMs;
            }
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (_witherStrikeMs < diffMs)
        {
            if (DoCast(victim, SpellWitherStrike) == CreatureCastResult.Ok)
            {
                _witherStrikeMs = (uint)(System?.RandomInt(3000, 6000) ?? 3000);
            }
        }
        else
        {
            _witherStrikeMs -= diffMs;
        }
    }
}
