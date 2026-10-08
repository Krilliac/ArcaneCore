using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>Ashcrombe and Ada's courtyard escort, from mangos-classic
/// shadowfang_keep/shadowfang_keep.cpp:61-158 (npc_shadowfang_prisonerAI::WaypointReached/HandleSpeech).</summary>
public sealed class ShadowfangPrisonerAi(Creature creature, ShadowfangKeepInstance instance) : EscortAI(creature)
{
    private uint _speechTimer;
    private int _speechStep = 1;
    private bool IsAsh => Me.Entry == ShadowfangKeepInstance.NpcAsh;

    protected override void Reset()
    {
        _speechTimer = 0;
    }

    protected override void WaypointReached(uint pointId)
    {
        if (pointId == 11 && IsAsh)
        {
            System?.SayText(Me, -1033001);
        }
        else if (pointId == 12)
        {
            SetEscortPaused(true);
            if (IsAsh)
            {
                DoCast(Me, 6421); // unlock
                _speechTimer = 5_000;
            }
            else
            {
                System?.SayText(Me, -1033004);
                System?.SayText(Me, -1033015);
                _speechTimer = 6_000;
            }
        }
        else if (pointId == 31 && !IsAsh)
        {
            System?.ForcedDespawn(Me, 0);
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_speechTimer == 0)
        {
            UpdateVictim();
            return;
        }

        if (_speechTimer > diffMs)
        {
            _speechTimer -= diffMs;
            return;
        }

        switch (_speechStep++)
        {
            case 1:
                System?.SayText(Me, IsAsh ? -1033002 : -1033005);
                instance.SetData(ShadowfangKeepInstance.TypeFreeNpc, EncounterState.Done);
                _speechTimer = 2_000;
                break;
            case 2:
                if (IsAsh)
                {
                    DoCast(Me, 6422); // fire after opening the door
                    _speechTimer = 2_500;
                }
                else
                {
                    SetRun(true);
                    SetEscortPaused(false);
                    System?.SayText(Me, -1033006);
                    _speechTimer = 0;
                }

                break;
            case 3:
                if (IsAsh)
                {
                    System?.SayText(Me, -1033014);
                    System?.ForcedDespawn(Me, 0);
                }

                _speechTimer = 0;
                break;
        }
    }
}
