using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Ringo (entry 9999, Un'Goro Crater), "A Little Help From My Friends" (quest 4491): mangos-classic ScriptDev2 <c>npc_ringo</c>
/// (kalimdor/ungoro_crater.cpp:156-320, QuestAccept :327-339, registered :816-820) on <see cref="FollowerAI"/>. He lies dead until the quest is
/// taken, then follows the player (faction 290). Out of combat he faints after 30-60 s and every 60-120 s after that, and holds until the
/// player revives him with Spraggle's water (Revive Ringo, 15591). Within interaction distance of Spraggle Frock (9997) the quest is done for
/// the player's group and a short scene with Spraggle plays before he goes.
/// </summary>
public sealed class RingoAI : FollowerAI, IQuestScriptAI
{
    public const uint Entry = 9999, QuestALittleHelp = 4491, NpcSpraggle = 9997, SpellReviveRingo = 15591, FactionEscortNeutralFriendPassive = 290;
    public const int SayStart1 = -1000416, SayStart2 = -1000417;
    public const int SayEnd1 = -1000426, SaySpraggleEnd2 = -1000427, SayEnd3 = -1000428, EmoteEnd4 = -1000429, EmoteEnd5 = -1000430;
    public const int SayEnd6 = -1000431, SaySpraggleEnd7 = -1000432, EmoteEnd8 = -1000433;
    public const uint FirstFaintMinMs = 30_000, FirstFaintMaxMs = 60_000, FaintMinMs = 60_000, FaintMaxMs = 120_000;

    /// <summary>SAY_FAINT_1..4 (:132-135) and SAY_WAKE_1..4 (:137-140).</summary>
    public static readonly int[] FaintSays = [-1000418, -1000419, -1000420, -1000421], WakeSays = [-1000422, -1000423, -1000424, -1000425];

    /// <summary>INTERACTION_DISTANCE.</summary>
    public const float InteractionDistance = 5f;

    private uint _faintMs, _endEventProgress, _endEventMs;
    private ObjectGuid _spraggle;

    /// <summary>The constructor lays him down (:158-162).</summary>
    public RingoAI(Creature creature) : base(creature) => Me.StandState = StandState.Dead;

    public uint FaintMs => _faintMs;

    public uint EndEventProgress => _endEventProgress;

    /// <summary>Whether he has fainted and waits for the water (follow paused).</summary>
    public bool Fainted => HasFollowState(FollowState.Paused);

    /// <summary>Reset (:170-176).</summary>
    protected override void Reset()
    {
        _faintMs = (uint)(System?.RandomInt((int)FirstFaintMinMs, (int)FirstFaintMaxMs) ?? (int)FirstFaintMinMs);
        _endEventProgress = 0;
        _endEventMs = 1000;
        _spraggle = default;
    }

    /// <summary>QuestAccept_npc_ringo (:327-339).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestALittleHelp)
        {
            return;
        }

        Me.StandState = StandState.Stand;
        StartFollow(player, FactionEscortNeutralFriendPassive, questId);
    }

    /// <summary>MoveInLineOfSight (:178-196): Spraggle within INTERACTION_DISTANCE (3D, bounding radii added) ends the follow with the scene.</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        ArgumentNullException.ThrowIfNull(who);
        base.MoveInLineOfSight(who);
        if (Me.Combat.Victim is not null || HasFollowState(FollowState.Complete) || who is not Creature { Entry: NpcSpraggle } spraggle)
        {
            return;
        }

        float dx = Me.X - who.X, dy = Me.Y - who.Y, dz = Me.Z - who.Z;
        float reach = InteractionDistance + Me.BoundingRadius + who.BoundingRadius;
        if ((dx * dx) + (dy * dy) + (dz * dz) > reach * reach)
        {
            return;
        }

        if (GetLeaderForFollower() is { } player && System is { } system && system.QuestIncomplete(player, QuestALittleHelp))
        {
            system.RewardGroupEventExplored(player, QuestALittleHelp, Me);
        }

        _spraggle = spraggle.Guid;
        SetFollowComplete(withEndEvent: true);
    }

    /// <summary>SpellHit (:198-202): the water revives him while he follows.</summary>
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (HasFollowState(FollowState.InProgress | FollowState.Paused) && spell.Id == SpellReviveRingo)
        {
            ClearFaint();
        }
    }

    /// <summary>SetFaint (:204-221).</summary>
    private void SetFaint()
    {
        if (!HasFollowState(FollowState.PostEvent))
        {
            SetFollowPaused(true);
            if (System is { } system)
            {
                system.SayText(Me, FaintSays[system.RandomInt(0, FaintSays.Length - 1)]);
            }
        }

        Me.StandState = StandState.Sleep;
    }

    /// <summary>ClearFaint (:223-239).</summary>
    private void ClearFaint()
    {
        Me.StandState = StandState.Stand;
        if (HasFollowState(FollowState.PostEvent))
        {
            return;
        }

        if (System is { } system)
        {
            system.SayText(Me, WakeSays[system.RandomInt(0, WakeSays.Length - 1)]);
        }

        SetFollowPaused(false);
    }

    /// <summary>UpdateFollowerAI (:241-319).</summary>
    protected override void UpdateFollowerAI(uint diffMs)
    {
        if (UpdateVictim() && Victim is not null)
        {
            return;
        }

        if (HasFollowState(FollowState.PostEvent))
        {
            if (_endEventMs < diffMs)
            {
                UpdateEndEvent();
            }
            else
            {
                _endEventMs -= diffMs;
            }
        }
        else if (HasFollowState(FollowState.InProgress) && !HasFollowState(FollowState.Paused))
        {
            if (_faintMs < diffMs)
            {
                SetFaint();
                _faintMs = (uint)(System?.RandomInt((int)FaintMinMs, (int)FaintMaxMs) ?? (int)FaintMinMs);
            }
            else
            {
                _faintMs -= diffMs;
            }
        }
    }

    /// <summary>The end scene (:247-296). Step 0 does nothing and leaves the timer run out, so step 1 follows on the next update.</summary>
    private void UpdateEndEvent()
    {
        if (System is not { } system || system.FindCreature(_spraggle) is not { IsAlive: true } spraggle)
        {
            SetFollowComplete();
            return;
        }

        switch (_endEventProgress)
        {
            case 1:
                system.SayText(Me, SayEnd1);
                _endEventMs = 3000;
                break;
            case 2:
                system.SayText(spraggle, SaySpraggleEnd2);
                _endEventMs = 5000;
                break;
            case 3:
                system.SayText(Me, SayEnd3);
                _endEventMs = 1000;
                break;
            case 4:
                system.SayText(Me, EmoteEnd4);
                SetFaint();
                _endEventMs = 9000;
                break;
            case 5:
                system.SayText(Me, EmoteEnd5);
                ClearFaint();
                _endEventMs = 1000;
                break;
            case 6:
                system.SayText(Me, SayEnd6);
                _endEventMs = 3000;
                break;
            case 7:
                system.SayText(spraggle, SaySpraggleEnd7);
                _endEventMs = 10000;
                break;
            case 8:
                system.SayText(Me, EmoteEnd8);
                _endEventMs = 5000;
                break;
            case 9:
                SetFollowComplete();
                break;
        }

        ++_endEventProgress;
    }
}
