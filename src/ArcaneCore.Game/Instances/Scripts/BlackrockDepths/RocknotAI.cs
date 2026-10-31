using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// mangos-classic blackrock_depths/blackrock_depths.cpp npc_rocknotAI (Reset, WaypointReached, UpdateEscortAI) and
/// QuestRewarded_npc_rocknot. Each Rocknot's Ale (quest 4295) is counted in the instance script (TYPE_ROCKNOT SPECIAL); at the third ale he
/// walks his script_waypoint path to the bar keg, breaks the back door and sets TYPE_ROCKNOT DONE. When Nagmara's Potion of Love scene sets
/// TYPE_NAGMARA SPECIAL he walks the second part of the same path instead. Quest 4201 is Nagmara's, see <see cref="NagmaraAI"/>.
/// World thread; the instance script is the only shared state.
/// </summary>
public sealed class RocknotAI(Creature creature, BlackrockDepthsInstance instance) : EscortAI(creature)
{
    public const uint QuestAle = 4295;
    public const uint SpellDrunkenRage = 14872;
    public const int SayGotBeer = -1230000;
    public const int SayMoreBeer = -1230036;
    public const int SayBarrel1 = -1230044;
    public const int SayBarrel2 = -1230045;
    public const int SayBarrel3 = -1230046;
    public const float InitialOrientation = 3.21141f;
    private const uint EmoteOneShotCheer = 4;

    private uint _breakKegMs;
    private uint _breakDoorMs;
    private uint _emoteMs;
    private uint _barReactMs;

    /// <summary>Reset: nothing is reset while the escort runs (the original returns early).</summary>
    protected override void Reset()
    {
        if (HasEscortState(EscortState.Escorting))
        {
            return;
        }

        _breakKegMs = _breakDoorMs = _emoteMs = _barReactMs = 0;
    }

    /// <summary>
    /// QuestRewarded_npc_rocknot: ignored once the third ale is in (SPECIAL) or the event is DONE; quest 4295 moves the event to IN_PROGRESS,
    /// he turns to the player, says thanks, cheers (or starts the walk, see <see cref="UpdateEscortAI"/>) 1.5 s later, and the ale is counted.
    /// </summary>
    public override void OnQuestRewarded(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        base.OnQuestRewarded(player, questId);
        uint state = instance.GetData(BlackrockDepthsInstance.TypeRocknot);
        if (state is EncounterState.Done or EncounterState.Special || questId != QuestAle)
        {
            return;
        }

        if (state != EncounterState.InProgress)
        {
            instance.SetData(BlackrockDepthsInstance.TypeRocknot, EncounterState.InProgress);
        }

        FaceTo(System, Me, player);
        System?.SayText(Me, SayGotBeer);
        _emoteMs = 1500;
        instance.SetData(BlackrockDepthsInstance.TypeRocknot, EncounterState.Special);
    }

    protected override void WaypointReached(uint pointId)
    {
        Creature? nagmara = instance.StoredCreature(BlackrockDepthsInstance.NpcNagmara);
        switch (pointId)
        {
            case 1: // with the Potion of Love scene in progress, the second part of the path
                SetEscortPaused(true);
                if (instance.GetData(BlackrockDepthsInstance.TypeNagmara) == EncounterState.InProgress)
                {
                    SetNextWaypointByPointId(10);
                }

                SetEscortPaused(false);
                break;
            case 3:
            case 6:
                System?.SayText(Me, SayBarrel1);
                break;
            case 4:
            case 5:
                System?.SayText(Me, SayBarrel2);
                break;
            case 7:
                DoCast(Me, SpellDrunkenRage);
                _breakKegMs = 2000;
                break;
            case 9: // back home
                SetEscortPaused(true);
                System?.SetFacingTo(Me, InitialOrientation);
                break;
            case 10: // start of the alternate path used with Nagmara: she follows him
                if (nagmara is null)
                {
                    SetEscortPaused(true);
                    SetNextWaypointByPointId(9);
                }
                else
                {
                    nagmara.Motion.MoveChase(Me, 2f);
                }

                break;
            case 17: // the bar back door, unless it is open
                instance.OpenBarDoorOnce();
                nagmara?.Motion.MoveChase(Me, 2f);
                break;
            case 34: // under the stair: Nagmara walks to her place and takes the scene back
                if (nagmara is null)
                {
                    break;
                }

                System?.MoveIdle(nagmara);
                nagmara.Motion.MovePoint(0, NagmaraAI.RocknotSpotX, NagmaraAI.RocknotSpotY, NagmaraAI.RocknotSpotZ, run: false);
                if (nagmara.AI is NagmaraAI nagmaraAi)
                {
                    nagmaraAi.BeginKissingPhase();
                }

                SetEscortPaused(true);
                break;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        // Nagmara reached him with the potion: the second part of the escort.
        if (instance.GetData(BlackrockDepthsInstance.TypeNagmara) == EncounterState.Special)
        {
            instance.SetData(BlackrockDepthsInstance.TypeNagmara, EncounterState.InProgress);
            Start(run: false);
            return;
        }

        if (Elapsed(ref _breakKegMs, diffMs))
        {
            instance.SetDepthsObjectState(BlackrockDepthsInstance.GoBarKegShot, GameObjectState.Active);
            _breakDoorMs = 1000;
            _barReactMs = 5000;
        }

        if (Elapsed(ref _breakDoorMs, diffMs))
        {
            if (!instance.BarDoorOpen)
            {
                instance.SetDepthsObjectState(BlackrockDepthsInstance.GoBarDoor, GameObjectState.ActiveAlternative);
            }

            System?.SayText(Me, SayBarrel3);
            instance.SetDepthsObjectState(BlackrockDepthsInstance.GoBarKegTrap, GameObjectState.Active);
        }

        if (Elapsed(ref _barReactMs, diffMs))
        {
            instance.StartPhalanx();
            instance.SetData(BlackrockDepthsInstance.TypeRocknot, EncounterState.Done);
        }

        if (Elapsed(ref _emoteMs, diffMs))
        {
            // SPECIAL (the third ale): a line and the walk to the barrel; before that he cheers the player.
            if (instance.GetData(BlackrockDepthsInstance.TypeRocknot) == EncounterState.Special)
            {
                System?.SayText(Me, SayMoreBeer);
                Start(run: false);
            }
            else
            {
                System?.PlayEmote(Me, EmoteOneShotCheer);
            }
        }
    }

    /// <summary>A running timer: false while it counts down (or is 0), true once, when it runs out.</summary>
    internal static bool Elapsed(ref uint remainingMs, uint diffMs)
    {
        if (remainingMs == 0)
        {
            return false;
        }

        if (remainingMs <= diffMs)
        {
            remainingMs = 0;
            return true;
        }

        remainingMs -= diffMs;
        return false;
    }

    /// <summary>Unit::SetFacingToObject: turn towards <paramref name="target"/> (not while it is moving, which <c>SetFacingTo</c> guards).</summary>
    internal static void FaceTo(CreatureMapSystem? system, Creature who, WorldObject target)
        => system?.SetFacingTo(who, MathF.Atan2(target.Y - who.Y, target.X - who.X));
}
