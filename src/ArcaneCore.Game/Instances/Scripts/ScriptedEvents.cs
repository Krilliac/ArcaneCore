using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>What <see cref="ScriptedEvents.Start"/> did with an event.</summary>
public enum ScriptedEventResult
{
    /// <summary>A script handler took the event, and its dbscripts_on_event script must not run (the handler returned true).</summary>
    Handled,

    /// <summary>The event's DB script started, or was already running for the same object and was skipped as cmangos does.</summary>
    Started,

    /// <summary>No handler took it and the event has no dbscripts_on_event rows.</summary>
    NoScript,
}

/// <summary>
/// cmangos <c>StartEvents_Event</c> (DBScripts/ScriptMgr.cpp:3445-3482): the ScriptDev2 handler bound to the event id in
/// <c>scripted_event_id</c> answers first, and only when it returns false does <c>Map::ScriptsStart</c> run the event's
/// dbscripts_on_event rows. ArcaneCore's handlers are the map's <see cref="InstanceData.OnSpellEvent"/> overrides and, for handlers with no
/// instance-script port, <see cref="GatedOff"/>. Every event start (the SEND_EVENT spell effect, chest data6 and goober data2) goes
/// through here, so a handler that suppresses the DB script suppresses it on every path. World thread (the map's).
/// </summary>
public static class ScriptedEvents
{
    /// <summary>event_purify_food (darkshore.cpp:826-860, quest 4763 bonfire).</summary>
    public const uint PurifyFood = 3938;

    /// <summary>event_razorgore_possess (boss_razorgore.cpp:216-225).</summary>
    public const uint RazorgorePossess = 8302;

    /// <summary>event_spells_warlock_dreadsteed (dire_maul.cpp:37-80): J'eevee's summon and the Xorothian Dreadsteed's summon.</summary>
    public const uint SummonJeevee = 8420, SummonDreadsteed = 8428;

    /// <summary>event_naxxramas EVENT_ID_DECIMATE (naxxramas.h:198, sent by Gluth's Decimate).</summary>
    public const uint GluthDecimate = 10495;

    /// <summary>Gluth (naxxramas.h NPC_GLUTH).</summary>
    public const uint Gluth = 15932;

    /// <summary>
    /// Start event <paramref name="eventId"/> with <paramref name="source"/> and <paramref name="target"/> on <paramref name="map"/>: the
    /// instance script first, then the unported-handler gates, then the DB script.
    /// </summary>
    public static ScriptedEventResult Start(Map map, uint eventId, Unit source, WorldObject? target)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(source);
        if (map.FindUpdater<InstanceData>()?.OnSpellEvent(source, eventId) == true || GatedOff(map, eventId, source, target))
        {
            return ScriptedEventResult.Handled;
        }

        CreatureMapSystem? scripts = map.FindUpdater<CreatureMapSystem>();
        if (scripts?.HasDbScript(DbScriptKind.Event, eventId) != true)
        {
            return ScriptedEventResult.NoScript;
        }

        scripts.StartDbScript(DbScriptKind.Event, eventId, source, target);
        return ScriptedEventResult.Started;
    }

    /// <summary>
    /// The ClassicDB z2815 <c>scripted_event_id</c> handlers that also have dbscripts_on_event rows and no instance-script port: what each
    /// returns to StartEvents_Event, so the DB rows run only where cmangos runs them. Of the 50 bound event ids, 14 have rows: 2488 and
    /// 2609 (ZulFarrakInstance), 4884 (BlackrockSpireInstance) and 5618-5623 (ScholomanceInstance) are ported as instance scripts; the
    /// five here are not. Their own side effects (the furbolg purification, the possess visual, J'eevee's ritual, the zombie chow
    /// movement) are not ported; only the gate is.
    /// </summary>
    public static bool GatedOff(Map map, uint eventId, Unit source, WorldObject? target) => eventId switch
    {
        // Runs its furbolg sequence and returns false (the DB script spawns Xabraxxis) only for a player at a game object.
        PurifyFood => !(source is Player && target is GameObject),
        // Always returns true: the DB rows (possess visual, Mind Exhaustion) never run in cmangos.
        RazorgorePossess => true,
        // Returns false only for a player while TYPE_DREADSTEED allows the step (NOT_STARTED or FAIL for J'eevee, SPECIAL for the
        // Dreadsteed). The Dire Maul port has no Dreadsteed ritual state (DireMaulInstance), so the gate never opens.
        SummonJeevee or SummonDreadsteed => true,
        // instance_naxxramas::DoHandleEvent (naxxramas.cpp:916-951): false (the DB script runs) while Gluth is in the instance's
        // storage, true without him. ArcaneCore has no Naxxramas instance script; Gluth on the map stands for the storage.
        GluthDecimate => map.FindUpdater<CreatureMapSystem>()?.Creatures.Any(creature => creature.Template.Entry == Gluth) != true,
        _ => false,
    };
}
