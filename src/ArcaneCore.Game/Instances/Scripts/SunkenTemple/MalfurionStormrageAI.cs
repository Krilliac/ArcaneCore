using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.SunkenTemple;

/// <summary>
/// Malfurion Stormrage (15362), summoned by the Shade of Eranikus area trigger (<see cref="Classic.SunkenTempleInstance.OnAreaTrigger"/>):
/// mangos-classic ScriptDev2 <c>npc_malfurion_stormrage</c> (eastern_kingdoms/sunken_temple/sunken_templeScripts.cpp:64-149). He comes in
/// with Spirit Spawn-in (17321) and without his quest giver flag. In a dungeon he first emotes that the chamber trembles, then bows,
/// says his four lines (2, 12, 12, 11 and 4 s apart, talking meanwhile), and gives "Eranikus, Tyrant of the Dream" (8733) once he is done.
/// He does not fight (the source's UpdateAI has no combat).
/// </summary>
public sealed class MalfurionStormrageAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = Classic.SunkenTempleInstance.NpcMalfurion, SpellSpiritSpawnIn = 17321;
    public const int EmoteMalfurion1 = -1109000, SayMalfurion1 = -1109001, SayMalfurion2 = -1109002, SayMalfurion3 = -1109003,
        SayMalfurion4 = -1109004;
    public const uint EmoteOneshotBow = 2, EmoteStateTalk = 378, EmoteStateNone = 30;
    public const uint MaxTempleSpeeches = 6;

    private uint _speech;
    private uint _sayMs = 3000;
    private bool _spawned;

    /// <summary>m_uiSpeech: the next step of the temple speech (6 once done).</summary>
    public uint Speech => _speech;

    /// <summary>m_creature->GetMap()->IsDungeon(): the map runs an instance script.</summary>
    private bool InDungeon => Me.Map?.FindUpdater<InstanceData>() is not null;

    protected override void Reset()
    {
    }

    /// <summary>The constructor (:80-93), on the first placement.</summary>
    public override void OnRespawn()
    {
        if (!_spawned)
        {
            _spawned = true;
            if (InDungeon)
            {
                System?.SayText(Me, EmoteMalfurion1);
            }

            DoCast(Me, SpellSpiritSpawnIn, triggered: true);
            Me.NpcFlags &= ~(uint)ArcaneCore.Game.Npc.NpcFlags.QuestGiver;
        }

        Reset();
    }

    /// <summary>UpdateAI (:96-143).</summary>
    public override void OnUpdate(uint diffMs)
    {
        if (!InDungeon || _speech >= MaxTempleSpeeches || System is not { } system)
        {
            return;
        }

        if (_sayMs > diffMs)
        {
            _sayMs -= diffMs;
            return;
        }

        switch (_speech)
        {
            case 0:
                system.PlayEmote(Me, EmoteOneshotBow);
                _sayMs = 2000;
                break;
            case 1:
                system.SayText(Me, SayMalfurion1);
                system.PlayEmote(Me, EmoteStateTalk);
                _sayMs = 12000;
                break;
            case 2:
                system.SayText(Me, SayMalfurion2);
                _sayMs = 12000;
                break;
            case 3:
                system.SayText(Me, SayMalfurion3);
                _sayMs = 11000;
                break;
            case 4:
                system.SayText(Me, SayMalfurion4);
                _sayMs = 4000;
                break;
            case 5:
                Me.NpcFlags |= (uint)ArcaneCore.Game.Npc.NpcFlags.QuestGiver;
                system.PlayEmote(Me, EmoteStateNone);
                break;
        }

        ++_speech;
    }
}
