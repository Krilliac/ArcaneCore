using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Razorfen Kraul (map 47): ScriptDev2's <c>instance_razorfen_kraul</c> (mangos-classic
/// AI/ScriptDevAI/scripts/kalimdor/razorfen_kraul/instance_razorfen_kraul.cpp:29-133, razorfen_kraul.h:8-16). Every Death's Head Ward Keeper
/// (4625) added to the map is counted; classic-db z2815 EventAI sets TYPE_AGATHELOS (1) to DONE when one dies, and when the last counted keeper
/// is gone the encounter takes that value and Agathelos' ward (21099) opens. As in the original the counter is unsigned: a death with nothing
/// counted wraps it and nothing opens.
/// </summary>
[InstanceScript(MapId)]
public sealed class RazorfenKraulInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 47;
    public const int MaxEncounter = 1;

    public const uint TypeAgathelos = 1;
    public const uint GoAgathelosWard = 21099;
    public const uint NpcWardKeeper = 4625;

    private uint _wardKeepersRemaining;
    private bool _willixRegistered;
    private bool _gopherRegistered;

    /// <summary>The ward keepers counted and not yet reported dead (<c>m_uiWardKeepersRemaining</c>).</summary>
    public uint WardKeepersRemaining => _wardKeepersRemaining;

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry == GoAgathelosWard)
        {
            StoreGameObject(go);
            OpenIf(go, Encounters[0] == EncounterState.Done);
        }
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Template.Entry == NpcWardKeeper)
        {
            _wardKeepersRemaining++;
        }

        // ScriptDev2 razorfen_kraul.cpp: the escort and Blueleaf gopher are entry scripts, not instance states.
        if (creature.System is { } system)
        {
            if (creature.Template.Entry == 4508 && !_willixRegistered)
            {
                _willixRegistered = true;
                system.RegisterEntryAi(4508, c => new RazorfenKraul.WillixAi(c), rebuildExisting: creature.AI is not null);
            }
            else if (creature.Template.Entry == 4781 && !_gopherRegistered)
            {
                _gopherRegistered = true;
                system.RegisterEntryAi(4781, c => new RazorfenKraul.SnufflenoseGopherAi(c), rebuildExisting: creature.AI is not null);
            }
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type == TypeAgathelos)
        {
            _wardKeepersRemaining = unchecked(_wardKeepersRemaining - 1);
            if (_wardKeepersRemaining == 0)
            {
                Encounters[0] = data;
                DoUseDoorOrButton(GoAgathelosWard);
            }
        }

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type == TypeAgathelos ? Encounters[0] : 0;
}
