using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Tooga (entry 5955, Tanaris), "Tooga's Quest" (quest 1560): mangos-classic ScriptDev2 <c>npc_tooga</c> (kalimdor/tanaris.cpp:251-405,
/// registered :421-426) on <see cref="FollowerAI"/>. Accepting the quest makes him follow the player (faction 290). While following and out of
/// combat he rolls every 5 s for one of his two worried lines (1 in 51 each). Torta (6015) within interaction distance completes the quest for
/// the player's group when it is incomplete and starts the reunion: after a second, every 5 s Tooga and Torta trade six lines, and with the last
/// one he walks into the water, where he is done (despawned by the follow check). A dead or missing Torta ends it at once.
/// </summary>
public sealed class ToogaAI(Creature creature) : FollowerAI(creature), IQuestScriptAI
{
    public const uint Entry = 5955, QuestTooga = 1560, NpcTorta = 6015, FactionEscortNeutralFriendPassive = 290, PointToWater = 1;
    public const int SayThirst = -1000391, SayWorried = -1000392, SayPost1 = -1000393, SayTortaPost2 = -1000394, SayPost3 = -1000395;
    public const int SayTortaPost4 = -1000396, SayPost5 = -1000397, SayTortaPost6 = -1000398;
    public const uint SpeechCheckFirstMs = 2500, SpeechCheckMs = 5000, PostEventFirstMs = 1000, PostEventMs = 5000;

    /// <summary>INTERACTION_DISTANCE.</summary>
    public const float InteractionDistance = 5f;

    /// <summary>m_afToWaterLoc (:270).</summary>
    public static readonly (float X, float Y, float Z) ToWater = (-7032.664551f, -4906.199219f, -1.606446f);

    private uint _speechMs, _postEventMs, _postEventPhase;
    private ObjectGuid _torta;

    public uint PostEventPhase => _postEventPhase;

    /// <summary>Reset (:281-288).</summary>
    protected override void Reset()
    {
        _speechMs = SpeechCheckFirstMs;
        _postEventMs = PostEventFirstMs;
        _postEventPhase = 0;
        _torta = default;
    }

    /// <summary>QuestAccept_npc_tooga (:398-407).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestTooga)
        {
            StartFollow(player, FactionEscortNeutralFriendPassive, questId);
        }
    }

    /// <summary>MoveInLineOfSight (:290-311): Torta within INTERACTION_DISTANCE (3D, bounding radii added).</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        ArgumentNullException.ThrowIfNull(who);
        base.MoveInLineOfSight(who);
        if (Me.Combat.Victim is not null || HasFollowState(FollowState.Complete | FollowState.PostEvent) || who is not Creature { Entry: NpcTorta } torta)
        {
            return;
        }

        float dx = Me.X - who.X, dy = Me.Y - who.Y, dz = Me.Z - who.Z;
        float reach = InteractionDistance + Me.BoundingRadius + who.BoundingRadius;
        if ((dx * dx) + (dy * dy) + (dz * dz) > reach * reach)
        {
            return;
        }

        if (GetLeaderForFollower() is { } player && System is { } system && system.QuestIncomplete(player, QuestTooga))
        {
            system.RewardGroupEventExplored(player, QuestTooga, Me);
        }

        _torta = torta.Guid;
        SetFollowComplete(withEndEvent: true);
    }

    /// <summary>MovementInform (:313-322): reaching the water ends the follow.</summary>
    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        base.OnMovementInform(type, pointId);
        if (type == MovementGeneratorType.Point && pointId == PointToWater)
        {
            SetFollowComplete();
        }
    }

    /// <summary>UpdateFollowerAI (:324-390).</summary>
    protected override void UpdateFollowerAI(uint diffMs)
    {
        if (UpdateVictim() && Victim is not null)
        {
            return;
        }

        if (HasFollowState(FollowState.PostEvent))
        {
            if (_postEventMs < diffMs)
            {
                _postEventMs = PostEventMs;
                UpdatePostEvent();
            }
            else
            {
                _postEventMs -= diffMs;
            }
        }
        else if (HasFollowState(FollowState.InProgress))
        {
            if (_speechMs < diffMs)
            {
                _speechMs = SpeechCheckMs;
                if (System is { } system)
                {
                    switch (system.RandomInt(0, 50))
                    {
                        case 10:
                            system.SayText(Me, SayThirst);
                            break;
                        case 25:
                            system.SayText(Me, SayWorried);
                            break;
                    }
                }
            }
            else
            {
                _speechMs -= diffMs;
            }
        }
    }

    /// <summary>The reunion (:333-370). Phase 0 says nothing; phase 6 also sends him into the water.</summary>
    private void UpdatePostEvent()
    {
        if (System is not { } system || system.FindCreature(_torta) is not { IsAlive: true } torta)
        {
            SetFollowComplete();
            return;
        }

        switch (_postEventPhase)
        {
            case 1:
                system.SayText(Me, SayPost1);
                break;
            case 2:
                system.SayText(torta, SayTortaPost2);
                break;
            case 3:
                system.SayText(Me, SayPost3);
                break;
            case 4:
                system.SayText(torta, SayTortaPost4);
                break;
            case 5:
                system.SayText(Me, SayPost5);
                break;
            case 6:
                system.SayText(torta, SayTortaPost6);
                Me.Motion.MovePoint(PointToWater, ToWater.X, ToWater.Y, ToWater.Z, run: false);
                break;
        }

        ++_postEventPhase;
    }
}
