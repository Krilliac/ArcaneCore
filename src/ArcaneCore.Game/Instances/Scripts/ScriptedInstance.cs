using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>
/// ScriptDev2's <c>ScriptedInstance</c> (vmangos AI/ScriptedInstance.h:38-80, mangos-classic AI/ScriptDevAI/include/sc_instance.h/.cpp): the
/// encounter array every instance script keeps (<c>m_auiEncounter[MAX_ENCOUNTER]</c>), its save string (the states separated by spaces, the
/// <c>saveStream &lt;&lt; m_auiEncounter[0] &lt;&lt; " " &lt;&lt; ...</c> each script writes when an encounter is DONE) and its load (a state saved
/// IN_PROGRESS comes back NOT_STARTED), and the stores of creatures and game objects by entry with the door helper
/// (<c>DoUseDoorOrButton</c>).
/// </summary>
public abstract class ScriptedInstance : InstanceData
{
    private readonly Dictionary<uint, ObjectGuid> _gameObjects = [];
    private readonly Dictionary<uint, ObjectGuid> _creatures = [];

    protected ScriptedInstance(Map instance, int maxEncounter)
        : base(instance)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEncounter);
        Encounters = new uint[maxEncounter];
    }

    /// <summary>The encounter states (<c>m_auiEncounter</c>).</summary>
    protected uint[] Encounters { get; }

    /// <summary>A copy of the encounter states, for diagnostics and tests.</summary>
    public IReadOnlyList<uint> EncounterStates => [.. Encounters];

    /// <summary>SD2 <c>Initialize</c>: <c>memset(&amp;m_auiEncounter, 0, ...)</c>.</summary>
    public override void Initialize() => Array.Clear(Encounters);

    /// <summary>The encounter states separated by single spaces (each script's <c>saveStream</c>).</summary>
    public override string? GetSaveData() => string.Join(' ', Encounters.Select(e => e.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// SD2 <c>Load</c>: the states in order (<c>loadStream &gt;&gt; m_auiEncounter[0] &gt;&gt; ...</c>; a value the string lacks, or one that is not a
    /// number, stops the read and leaves the rest as they are), then <see cref="AfterLoad"/> per state (IN_PROGRESS becomes NOT_STARTED).
    /// </summary>
    public override void Load(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string[] values = data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Encounters.Length && i < values.Length; i++)
        {
            if (!uint.TryParse(values[i], NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
            {
                break; // istream failure: the remaining extractions do nothing
            }

            Encounters[i] = value;
        }

        for (int i = 0; i < Encounters.Length; i++)
        {
            Encounters[i] = AfterLoad(i, Encounters[i]);
        }
    }

    /// <summary>What a loaded state becomes (SD2: <c>if (i == IN_PROGRESS) i = NOT_STARTED</c>); a script with another rule overrides it.</summary>
    protected virtual uint AfterLoad(int index, uint state) => state == EncounterState.InProgress ? EncounterState.NotStarted : state;

    /// <summary>The common tail of every SD2 <c>SetData</c>: <c>if (data == DONE) { ...; SaveToDB(); }</c>.</summary>
    protected void SaveIfDone(uint data)
    {
        if (data == EncounterState.Done)
        {
            SaveToDB();
        }
    }

    /// <summary>A type this port does not handle (logged at debug level; SD2 scripts ignore unknown types the same way).</summary>
    protected void NotPorted(uint type, uint data, string what)
        => Logger.LogDebug("instance script of map {Map} instance {Instance}: SetData({Type}, {Data}) {What} is not ported", Instance.MapId,
            Instance.InstanceId, type, data, what);

    // --- stores (m_goEntryGuidStore, m_npcEntryGuidStore) --------------------------------------

    /// <summary>Remember a game object by its entry (<c>m_goEntryGuidStore[entry] = guid</c>).</summary>
    protected void StoreGameObject(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        _gameObjects[go.Template.Entry] = go.Guid;
    }

    /// <summary>Remember a creature by its entry (<c>m_npcEntryGuidStore[entry] = guid</c>).</summary>
    protected void StoreCreature(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        _creatures[creature.Template.Entry] = creature.Guid;
    }

    /// <summary>SD2 <c>GetSingleGameObjectFromStorage</c>: the stored game object of an entry, if it is still in the map.</summary>
    protected GameObject? GetSingleGameObjectFromStorage(uint entry)
        => _gameObjects.TryGetValue(entry, out ObjectGuid guid) ? Instance.FindUpdater<GameObjectMapSystem>()?.Find(guid) : null;

    /// <summary>SD2 <c>GetSingleCreatureFromStorage</c>: the stored creature of an entry, if it is still in the map.</summary>
    protected Creature? GetSingleCreatureFromStorage(uint entry)
        => _creatures.TryGetValue(entry, out ObjectGuid guid) ? Instance.FindObject(guid) as Creature : null;

    /// <summary>
    /// SD2 <c>DoUseDoorOrButton(entry, withRestoreTime)</c> (sc_instance.cpp:15-43): the stored door or button of the entry is used when it is
    /// ready and reset when it is active (<see cref="GameObjectMapSystem.ToggleDoorOrButton"/>); nothing happens when no such object was
    /// created yet.
    /// </summary>
    protected void DoUseDoorOrButton(uint entry, uint withRestoreTimeSeconds = 0)
    {
        if (GetSingleGameObjectFromStorage(entry) is not { } go || Instance.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            Logger.LogDebug("instance script of map {Map}: DoUseDoorOrButton({Entry}) but no such game object was created yet", Instance.MapId, entry);
            return;
        }

        objects.ToggleDoorOrButton(go, withRestoreTimeSeconds);
    }

    /// <summary>A stored door that was created while its encounter is done starts open (each script's <c>OnObjectCreate</c>: <c>SetGoState(GO_STATE_ACTIVE)</c>).</summary>
    protected static void OpenIf(GameObject go, bool open)
    {
        if (open)
        {
            go.State = GameObjectState.Active;
        }
    }
}
