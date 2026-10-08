using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// The ScriptDev unit calls the Alterac Valley scripts make, over the creature map system: mount, walk, fly, model, speech, emotes, the
/// creatures of an entry in reach and the nearest object of an entry. World thread.
/// </summary>
internal static class AvScript
{
    /// <summary>EMOTE_ONESHOT_BOW and EMOTE_ONESHOT_SHOUT (Emotes.dbc).</summary>
    public const uint EmoteBow = 2;
    public const uint EmoteShout = 22;

    /// <summary>vmangos Unit::Mount(display) / Unmount: UNIT_FIELD_MOUNTDISPLAYID.</summary>
    public static void Mount(Creature creature, uint displayId) => creature.SetUInt32(UpdateFields.UnitFieldMountdisplayid, displayId);

    public static void Unmount(Creature creature) => Mount(creature, 0);

    public static bool IsMounted(Creature creature) => creature.GetUInt32(UpdateFields.UnitFieldMountdisplayid) != 0;

    /// <summary>vmangos Unit::SetWalk(walk): the creature's scripted moves walk or run.</summary>
    public static void SetWalk(Creature creature, bool walk) => creature.System?.SetScriptRun(creature, !walk);

    /// <summary>vmangos Unit::SetFly (Unit.cpp:7304-7310): MOVEFLAG_FLYING on the unit's movement.</summary>
    public static void SetFly(Creature creature, bool fly)
    {
        if (fly)
        {
            creature.AddMovementFlags(MovementFlags.Flying);
        }
        else
        {
            creature.RemoveMovementFlags(MovementFlags.Flying);
        }
    }

    /// <summary>ScriptDev DoScriptText(textId, source, target).</summary>
    public static void Say(Creature creature, int textId, Unit? target = null) => creature.System?.SayText(creature, textId, target);

    /// <summary>vmangos Unit::HandleEmote for a one-shot emote: SMSG_EMOTE to the creature's observers.</summary>
    public static void Emote(Creature creature, uint emote)
        => creature.Map?.BroadcastToObservers(creature, WorldOpcode.SmsgEmote, CreatureChatPackets.BuildEmote(emote, creature.Guid));

    /// <summary>ScriptDev GetCreatureListWithEntryInGrid.</summary>
    public static IReadOnlyList<Creature> Near(Creature creature, uint entry, float range)
        => creature.System?.CreaturesOfEntryInRange(creature, entry, range) ?? [];

    /// <summary>The object system of the creature's map.</summary>
    public static GameObjectMapSystem? Objects(WorldObject source) => source.Map?.FindUpdater<GameObjectMapSystem>();

    /// <summary>vmangos FindNearestGameObject / GetGameObjectListWithEntryInGrid: the spawned objects of an entry within range, nearest first.</summary>
    public static IReadOnlyList<GameObject> NearObjects(WorldObject source, uint entry, float range)
    {
        if (Objects(source) is not { } objects)
        {
            return [];
        }

        float rangeSq = range * range;
        return [.. objects.GameObjects
            .Where(g => g.Entry == entry && g.IsSpawned && DistanceSq(source, g) <= rangeSq)
            .OrderBy(g => DistanceSq(source, g))];
    }

    /// <summary>The 3D distance from a creature to a point (vmangos WorldObject::GetDistance, without the bounding radius).</summary>
    public static float Distance(WorldObject source, float x, float y, float z)
    {
        float dx = source.X - x;
        float dy = source.Y - y;
        float dz = source.Z - z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>vmangos urand(min, max), inclusive.</summary>
    public static uint URand(Random random, uint min, uint max) => min >= max ? min : (uint)random.Next((int)min, (int)max + 1);

    /// <summary>vmangos WorldObject::GetAngle(target): the absolute angle from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static float Angle(WorldObject from, WorldObject to)
    {
        float angle = MathF.Atan2(to.Y - from.Y, to.X - from.X);
        return angle >= 0 ? angle : angle + (2 * MathF.PI);
    }

    private static float DistanceSq(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}
