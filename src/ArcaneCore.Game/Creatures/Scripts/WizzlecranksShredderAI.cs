using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Wizzlecrank's Shredder (entry 3439, the Barrens), "Ignition" / "The Escape" (quest 863): mangos-classic ScriptDev2
/// <c>npc_wizzlecranks_shredder</c> (kalimdor/the_barrens.cpp:509-660, registered :1131-1135) on <see cref="EscortAI"/>. Taking the quest makes
/// it say its start line, take the Ratchet faction (637) until respawn and run its script path. Point 1 starts it up, it walks from point 10
/// (with its second start-up line as it leaves) and runs again leaving 19; at 18 two Southsea Mercenaries (3282) ambush it (one calls out),
/// and at point 25 it stops for the end: three lines five seconds apart, then the group gets the quest event and Pilot Wizzlecrank (3451)
/// climbs out for three minutes while the shredder lies down.
/// </summary>
public sealed class WizzlecranksShredderAI : EscortAI, IQuestScriptAI
{
    public const uint Entry = 3439, QuestEscape = 863, FactionRatchet = 637, NpcPilotWizz = 3451, NpcMercenary = 3282;
    public const int SayStart = -1000298, SayStartup1 = -1000299, SayStartup2 = -1000300, SayMercenary = -1000301;
    public const int SayProgress1 = -1000302, SayProgress2 = -1000303, SayProgress3 = -1000304, SayEnd = -1000305;
    public const uint MercenaryLifetimeMs = 120_000, PilotLifetimeMs = 180_000, PostEventFirstMs = 1000, PostEventMs = 5000;

    /// <summary>The two mercenary ambush spots of point 18.</summary>
    public static readonly (float X, float Y, float Z, float O)[] MercenarySpots =
    [
        (1128.489f, -3037.611f, 92.701f, 1.472f), (1160.172f, -2980.168f, 97.313f, 3.690f),
    ];

    private bool _postEvent;
    private uint _postEventMs, _postEventCount;

    /// <summary>The constructor sets the post event fields once (:533-540).</summary>
    public WizzlecranksShredderAI(Creature creature)
        : base(creature)
    {
        _postEventMs = PostEventFirstMs;
    }

    public bool IsPostEvent => _postEvent;

    public uint PostEventCount => _postEventCount;

    /// <summary>Reset (:546-557): only outside an escort.</summary>
    protected override void Reset()
    {
        if (HasEscortState(EscortState.Escorting))
        {
            return;
        }

        if (Me.StandState == StandState.Dead)
        {
            Me.StandState = StandState.Stand;
        }

        _postEvent = false;
        _postEventMs = PostEventFirstMs;
        _postEventCount = 0;
    }

    /// <summary>QuestAccept_npc_wizzlecranks_shredder (:633-645).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestEscape)
        {
            return;
        }

        System?.SayText(Me, SayStart);
        Me.SetFactionTemporary(FactionRatchet, TemporaryFactionFlags.RestoreRespawn);
        Start(run: true, player: player, questId: questId);
    }

    /// <summary>WaypointReached (:559-583).</summary>
    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.SayText(Me, SayStartup1, player);
                }

                break;
            case 10:
                SetRun(false);
                break;
            case 18:
                if (System is { } system && SummonMercenary(system, MercenarySpots[0]) is { } first)
                {
                    system.SayText(first, SayMercenary);
                    SummonMercenary(system, MercenarySpots[1]);
                }

                break;
            case 25:
                _postEvent = true;
                break;
        }
    }

    /// <summary>WaypointStart (:585-600).</summary>
    protected override void WaypointStart(uint pointId)
    {
        switch (pointId)
        {
            case 10:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.SayText(Me, SayStartup2, player);
                }

                break;
            case 19:
                if (GetPlayerForEscort() is { } runner)
                {
                    System?.SayText(Me, SayProgress1, runner);
                }

                SetRun(true);
                break;
        }
    }

    /// <summary>SummonCreature(NPC_MERCENARY, ..., TEMPSPAWN_TIMED_OOC_DESPAWN, 120000) and its JustSummoned attack (:602-609).</summary>
    private Creature? SummonMercenary(CreatureMapSystem system, (float X, float Y, float Z, float O) spot)
    {
        if (system.SummonDeadDespawn(Me, NpcMercenary, spot.X, spot.Y, spot.Z, spot.O) is not { } mercenary)
        {
            return null;
        }

        system.MarkTimedOutOfCombatDespawn(mercenary, MercenaryLifetimeMs);
        mercenary.AI?.AttackStart(Me);
        return mercenary;
    }

    /// <summary>UpdateEscortAI (:611-631).</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        if (UpdateVictim() && Victim is not null)
        {
            return;
        }

        if (!_postEvent)
        {
            return;
        }

        if (_postEventMs >= diffMs)
        {
            _postEventMs -= diffMs;
            return;
        }

        switch (_postEventCount)
        {
            case 0:
                System?.SayText(Me, SayProgress2);
                break;
            case 1:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.SayText(Me, SayProgress3, player);
                }

                break;
            case 2:
                System?.SayText(Me, SayEnd);
                break;
            case 3:
                if (GetPlayerForEscort() is { } escort && System is { } system)
                {
                    system.RewardGroupEventExplored(escort, QuestEscape, Me);
                    SummonPilot(system);
                }

                break;
        }

        ++_postEventCount;
        _postEventMs = PostEventMs;
    }

    /// <summary>
    /// SummonCreature(NPC_PILOT_WIZZ, 0, 0, 0, 0, TEMPSPAWN_TIMED_DESPAWN, 180000) and its JustSummoned (:604-605): the shredder lies down. The
    /// all-zero position is cmangos' close point beside the summoner; here it is a yard in front of it.
    /// </summary>
    private void SummonPilot(CreatureMapSystem system)
    {
        float x = Me.X + MathF.Cos(Me.Orientation), y = Me.Y + MathF.Sin(Me.Orientation);
        if (system.SummonDeadDespawn(Me, NpcPilotWizz, x, y, Me.Z, Me.Orientation) is { } pilot)
        {
            system.MarkTimedDespawn(pilot, PilotLifetimeMs);
            Me.StandState = StandState.Dead;
        }
    }
}
