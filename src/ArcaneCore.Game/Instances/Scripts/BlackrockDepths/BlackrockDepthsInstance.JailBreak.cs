using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts.BlackrockDepths;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// The "Jail Break!" (quest 4322) side of the Blackrock Depths instance script, from the behaviour spec 230-npc_marshal_windsor.md section 8:
/// the creature and object lookups Marshal Windsor needs (<see cref="MarshalWindsorAI"/>), and the prisoner deaths that release his pause.
/// World thread (map thread of the instance).
/// </summary>
public sealed partial class BlackrockDepthsInstance
{
    public const uint NpcDughalStormwing = 9022;
    public const uint NpcOgrabisi = 9677;
    public const uint NpcShillDinger = 9678;
    public const uint NpcTobiasSeecher = 9679;
    public const uint NpcCrestKiller = 9680;
    public const uint NpcJaz = 9681;
    public const uint NpcMarshalWindsor = 9023;
    public const uint NpcIronhandGuardian = 8982;
    public const uint GoSupplyRoomDoor = 170561;
    public const uint GoSupplyCrate = 166872;

    /// <summary>OnCreatureCreate: store the prisoners for single-creature lookup (spec section 8, item 1).</summary>
    private void RecordJailBreakCreature(Creature creature)
    {
        if (creature.Template.Entry is NpcDughalStormwing or NpcOgrabisi or NpcShillDinger or NpcTobiasSeecher or NpcCrestKiller or NpcJaz)
        {
            StoreCreature(creature);
        }
    }

    /// <summary>OnObjectCreate: store the supply room door and crate (spec section 8, item 1).</summary>
    private void RecordJailBreakObject(GameObject go)
    {
        if (go.Entry is GoSupplyRoomDoor or GoSupplyCrate)
        {
            StoreGameObject(go);
        }
    }

    /// <summary>The AIs of the Iron Hall guardians and of Marshal Windsor, registered with the other entry AIs of the instance.</summary>
    private void RegisterJailBreakAis(CreatureMapSystem creatures)
    {
        creatures.RegisterEntryAi(NpcIronhandGuardian, c => new IronhandGuardianAI(c, this));
        creatures.RegisterEntryAi(NpcMarshalWindsor, c => new MarshalWindsorAI(c, this));
    }

    /// <summary>
    /// OnCreatureDeath (spec section 8, item 2): the death of Ograbisi, Shill Dinger, Crest Killer or Jaz while the Jail Break is IN_PROGRESS
    /// sets TYPE_QUEST_JAIL_BREAK to SPECIAL, which Windsor's next update consumes. A death while it is anything else does nothing.
    /// </summary>
    private void OnJailBreakPrisonerDeath(Creature creature)
    {
        if (creature.Template.Entry is NpcOgrabisi or NpcShillDinger or NpcCrestKiller or NpcJaz
            && GetData(TypeQuestJailBreak) == EncounterState.InProgress)
        {
            SetData(TypeQuestJailBreak, EncounterState.Special);
        }
    }

    /// <summary>
    /// SetData(TYPE_QUEST_JAIL_BREAK) (spec section 8, item 3): the value is stored, but the case returns before the "save when DONE" step, so
    /// DONE is not persisted.
    /// </summary>
    private void SetJailBreakData(uint data) => Encounters[TypeQuestJailBreak - 1] = data;

    /// <summary>
    /// Whether the player's Jail Break (4322) is INCOMPLETE, the condition under which Dughal Stormwing's and Tobias Seecher's gossip offers the
    /// release. False when the map has no quest seam (<see cref="IInstanceQuestRewards"/>) or the player's journal is not loaded.
    /// </summary>
    internal static bool JailBreakIsIncomplete(Player player)
        => player.Map?.FindUpdater<IInstanceQuestRewards>()?.IsIncomplete(player, MarshalWindsorAI.QuestJailBreak) == true;

    /// <summary>
    /// The text of a ScriptDev2 gossip_texts option (-3230000 ..), read from the imported text store (the importer carries those rows into
    /// creature_ai_texts). Null when the world database lacks the row: the option is then not offered (nothing is invented in its place).
    /// </summary>
    internal static string? GossipOptionText(Player player, int textId)
        => player.Map?.FindUpdater<CreatureMapSystem>()?.Content.Ai.FindText(textId)?.Content is { Length: > 0 } text ? text : null;

    /// <summary>
    /// The release of a Jail Break prisoner by gossip (spec 230-npc_dughal_stormwing.md section 3 and 230-npc_tobias_seecher.md section 3): the
    /// Jail Break reads SPECIAL (which resumes Windsor's pause at his next update). The caller then speaks, and calls
    /// <see cref="SendPrisonerOut"/>.
    /// </summary>
    internal void ReleasePrisoner() => SetData(TypeQuestJailBreak, EncounterState.Special);

    /// <summary>
    /// The rest of the release: the prisoner loses its gossip flag, runs, and starts its waypoint movement. The fixture has no
    /// creature_movement row for Dughal (guid 2301001) nor Tobias (guid 2301093), nor a template path for either entry, so the waypoint movement
    /// leaves the prisoner standing where it is (spec section 9, ambiguity 1, option (i): faithful to the reference and the data).
    /// </summary>
    internal static void SendPrisonerOut(Creature prisoner)
    {
        prisoner.NpcFlags &= ~(uint)NpcFlags.Gossip;
        prisoner.System?.SetScriptRun(prisoner, true);
        prisoner.System?.ChangeMovement(prisoner, 2, 0, 0);
    }

    /// <summary>A stored Jail Break creature (prisoner), or null when it was not created or is gone.</summary>
    internal Creature? JailBreakCreature(uint entry) => GetSingleCreatureFromStorage(entry);

    /// <summary>A stored Jail Break object (the supply room door or crate), or null.</summary>
    internal GameObject? JailBreakObject(uint entry) => GetSingleGameObjectFromStorage(entry);

    /// <summary>SD2 DoUseDoorOrButton for the supply room door.</summary>
    internal void UseJailBreakObject(uint entry) => DoUseDoorOrButton(entry);

    /// <summary>
    /// "Use the Supply Crate" (spec section 5, point 20). The crate is a goober (type 10), which the door/button toggle refuses, so the crate is
    /// used the way GameObject::Use(Unit) uses any object: by Windsor himself.
    /// </summary>
    internal void UseSupplyCrate(Creature user)
    {
        if (GetSingleGameObjectFromStorage(GoSupplyCrate) is { } crate)
        {
            Instance.FindUpdater<GameObjectMapSystem>()?.UseByUnit(user, crate);
        }
    }
}
