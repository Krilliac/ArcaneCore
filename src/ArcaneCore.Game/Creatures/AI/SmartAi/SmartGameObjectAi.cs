using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The smart-script AI of game objects (AzerothCore SmartGameObjectAI, src/server/game/AI/SmartScripts/SmartAI.cpp:1459-1490, re-implemented; no
/// code copied), wired as the <see cref="IGameObjectFallbackAi"/> of every <see cref="GameObjectMapSystem"/>. AzerothCore opts an object in through
/// <c>gameobject_template.AIName 'SmartGameObjectAI'</c>; ArcaneCore's template has no such column, so the opt-in is the rows themselves: an object
/// runs its <c>smart_scripts</c> source-type-1 rows (its spawn's, else its entry's) unless a C# AI is registered for its entry. One instance serves
/// every object of a map; each object has its own <see cref="SmartScript"/>, kept until the object is collected. World thread only.
/// </summary>
/// <param name="catalog">The loaded catalog, read on every use: the creature feature loads it after the maps are created.</param>
public sealed class SmartGameObjectAi(Func<SmartScriptCatalog> catalog) : IGameObjectFallbackAi
{
    private readonly ConditionalWeakTable<GameObject, SmartScript> _scripts = new();

    public bool HandlesAny => catalog().HasGameObjectRows;

    public bool Handles(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        return catalog().ForGameObject(go.Entry, go.Spawn?.Guid ?? 0).Count > 0;
    }

    /// <summary>
    /// The object's script, created on first use: the timers start, then SMART_EVENT_AI_INIT and SMART_EVENT_JUST_CREATED fire once
    /// (SmartScript.cpp:5473-5480: OnInitialize inits the timers, then raises AI_INIT, then JUST_CREATED).
    /// </summary>
    public SmartScript ScriptFor(GameObjectMapSystem objects, GameObject go)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(go);
        if (_scripts.TryGetValue(go, out SmartScript? existing))
        {
            return existing;
        }

        SmartScriptCatalog current = catalog();
        var script = new SmartScript(SmartScriptOwner.ForGameObject(objects, go, current), current.ForGameObject(go.Entry, go.Spawn?.Guid ?? 0));
        _scripts.Add(go, script);
        script.ProcessEventsFor(SmartEvent.AiInit);
        script.ProcessEventsFor(SmartEvent.JustCreated);
        return script;
    }

    /// <summary>
    /// SmartGameObjectAI::Reset (SmartAI.cpp:1476-1481) runs OnReset, and GameObject::Update calls it on respawn (GameObject.cpp:663-665): phase 0,
    /// timers and one-time events back. ArcaneCore respawns the same instance, so its script outlives the respawn. An object that never ran has no
    /// script yet; the one it gets later starts fresh.
    /// </summary>
    public void OnRespawn(GameObjectMapSystem objects, GameObject go)
    {
        if (_scripts.TryGetValue(go, out SmartScript? script))
        {
            script.OnReset();
        }
    }

    /// <summary>SmartGameObjectAI::UpdateAI: the timers every update. An object that is not spawned is not in the world and does not tick.</summary>
    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        if (!go.IsSpawned)
        {
            return;
        }

        ScriptFor(objects, go).OnUpdate(diffMs);
    }

    /// <summary>
    /// SmartGameObjectAI::GossipHello (SmartAI.cpp:1459-1490) raises SMART_EVENT_GOSSIP_HELLO with the player and var0 = report use, and returns
    /// false, so the use always goes on. GameObject::Use passes false for every player use (GameObject.cpp:1490-1496); the report-use opcode does not
    /// exist in 1.12, so the var0 is always 0.
    /// </summary>
    public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
    {
        if (user is Player player)
        {
            ScriptFor(objects, go).ProcessEventsFor(SmartEvent.GossipHello, player, 0);
        }

        return false;
    }

    /// <summary>A trap's target is not a smart event; the trap casts as it would without a script.</summary>
    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
}
