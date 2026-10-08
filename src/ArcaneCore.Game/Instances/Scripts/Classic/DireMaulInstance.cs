using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Dire Maul (map 429): the Alzzin part of ScriptDev2's <c>instance_dire_maul</c> (mangos-classic
/// AI/ScriptDevAI/scripts/kalimdor/dire_maul/instance_dire_maul.cpp, SetData's TYPE_ALZZIN branch, GetData, Load and the matching
/// OnObjectCreate cases; dire_maul.h:8-44). classic-db z2815 EventAI sets TYPE_ALZZIN (0) to SPECIAL when Alzzin the Wildshaper drops to 40%
/// health and to DONE when he dies ("This Encounter is expected to be handled within Acid").
/// <para>
/// Ported: the nineteen states and their save string; SPECIAL breaks the crumbling wall (177220) once; DONE breaks it if that has not happened
/// and opens the corrupted vine (179502); a wall or vine created while Alzzin is done (or the wall already broken) is created open; a loaded
/// state of DONE or more counts as the wall broken. The Gordok Tribute run continues in DireMaul/DireMaulTribute.cs.
/// Not ported (logged at debug level): the Felvine shards' respawn on DONE, Zevrim, Ironbark, Warpwood, Immol'thar, the prince, the pylons
/// and the Dreadsteed ritual.
/// </para>
/// </summary>
[InstanceScript(MapId)]
public sealed partial class DireMaulInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 429;
    public const int MaxEncounter = 19;

    public const uint TypeAlzzin = 0;

    public const uint GoCrumbleWall = 177220;
    public const uint GoCorruptVine = 179502;

    private bool _wallDestroyed;

    /// <summary><c>m_bWallDestroyed</c>.</summary>
    public bool WallDestroyed => _wallDestroyed;

    public override void Initialize()
    {
        base.Initialize();
        _wallDestroyed = false;
    }

    public override void OnObjectCreate(GameObject go)
    {
        switch (go.Entry)
        {
            case GoNorthLibraryDoor:
            case GoGordokTribute:
                break;
            case GoCrumbleWall:
                OpenIf(go, _wallDestroyed || Encounters[TypeAlzzin] == EncounterState.Done);
                break;
            case GoCorruptVine:
                OpenIf(go, Encounters[TypeAlzzin] == EncounterState.Done);
                break;
            default:
                return;
        }

        StoreGameObject(go);
    }

    public override void SetData(uint type, uint data)
    {
        if (type != TypeAlzzin)
        {
            if (!SetAdditionalData(type, data))
            {
                NotPorted(type, data, "(a Dire Maul event other than Alzzin's and the Tribute run)");
            }
            return;
        }

        if (data == EncounterState.Done)
        {
            if (!_wallDestroyed)
            {
                DoUseDoorOrButton(GoCrumbleWall);
                _wallDestroyed = true;
            }

            DoUseDoorOrButton(GoCorruptVine);
            NotPorted(type, data, "(the Felvine shards' respawn)");
        }
        else if (data == EncounterState.Special && !_wallDestroyed)
        {
            DoUseDoorOrButton(GoCrumbleWall);
            _wallDestroyed = true;
        }

        Encounters[TypeAlzzin] = data;
        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type < MaxEncounter ? Encounters[type] : 0;

    public override void Load(string data)
    {
        // The original reads every state, sets the wall flag from TYPE_ALZZIN >= DONE, and only then turns IN_PROGRESS into NOT_STARTED.
        base.Load(data);
        if (Encounters[TypeAlzzin] >= EncounterState.Done)
        {
            _wallDestroyed = true;
        }
    }
}
