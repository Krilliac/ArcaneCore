using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// go_rune_of_the_defiler (mangos-classic eastern_kingdoms/blasted_lands.cpp:32-44, registered :58-60): the Rune of the Defiler trap (153204)
/// takes only a player holding a Ward of the Defiler (item 10757; HasItemCount does not look in the bank). The trap's own spell (12885) is cast
/// by the engine on the player it picks.
/// </summary>
internal sealed class RuneOfTheDefilerAi : IGameObjectAi
{
    public const uint ItemWardOfTheDefiler = 10757;

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
    }

    public bool AcceptsTrapTarget(GameObjectMapSystem objects, GameObject go, Unit candidate)
        => candidate is Player player && player.Inventory.GetItemCount(ItemWardOfTheDefiler) >= 1;
}

/// <summary>
/// go_mausoleum_door (mangos-classic eastern_kingdoms/tirisfal_glades.cpp:47-60, registered :183-187): while quest 1819 is incomplete, using the
/// Doors (176594) when a Mausoleum Trigger (104593) stands within 30 yd of the player resets that trigger to ready and summons Ulag the Cleaver
/// (6390) at the mausoleum for 300 s (TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN). It never handles the use: the door opens as usual.
/// </summary>
internal sealed class MausoleumDoorAi(Func<Player, uint, bool> questIncomplete) : IGameObjectAi
{
    public const uint QuestUlag = 1819, NpcUlag = 6390, TriggerObject = 104593, DoorObject = 176594;
    public const float SearchRange = 30f;
    public const uint UlagLifetimeMs = 300_000;

    /// <summary>Where Ulag appears (tirisfal_glades.cpp:56).</summary>
    public static readonly (float X, float Y, float Z, float O) UlagSpawn = (2390.26f, 336.47f, 40.01f, 2.26f);

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
    }

    public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
    {
        if (user is not Player player || !questIncomplete(player, QuestUlag))
        {
            return false;
        }

        if (objects.ClosestOfEntry(player, TriggerObject, SearchRange) is { } trigger)
        {
            trigger.State = GameObjectState.Ready;
            objects.Map.FindUpdater<CreatureMapSystem>()?.SummonInstanceCreatureTimedOocOrDead(NpcUlag, UlagSpawn.X, UlagSpawn.Y, UlagSpawn.Z, UlagSpawn.O,
                UlagLifetimeMs);
        }

        return false;
    }
}

/// <summary>
/// go_mausoleum_trigger (mangos-classic eastern_kingdoms/tirisfal_glades.cpp:62-77, registered :189-193): while quest 1819 is incomplete, using
/// the Mausoleum Trigger (104593) when the Doors (176594) stand within 30 yd of the player presses the trigger (Active) and clears the door's
/// interact-condition flag so it can be opened; the use is then handled. Without the quest or the door the use is left to the object.
/// </summary>
internal sealed class MausoleumTriggerAi(Func<Player, uint, bool> questIncomplete) : IGameObjectAi
{
    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
    }

    public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
    {
        if (user is not Player player || !questIncomplete(player, MausoleumDoorAi.QuestUlag)
            || objects.ClosestOfEntry(player, MausoleumDoorAi.DoorObject, MausoleumDoorAi.SearchRange) is not { } door)
        {
            return false;
        }

        go.State = GameObjectState.Active;
        door.Flags &= ~GameObjectFlags.InteractCond;
        return true;
    }
}
