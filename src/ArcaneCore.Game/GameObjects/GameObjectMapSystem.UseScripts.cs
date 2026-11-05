using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// The game object DB scripts, <c>dbscripts_on_go_template_use</c> and <c>dbscripts_on_go_use</c> (mangos-classic GameObject::Use,
/// Entities/GameObject.cpp:1490 template, :1506 door, :1529 button, :1703 goober; both <c>Map::ScriptsStart(type, id, spellCaster, this)</c>
/// with the user as source and the object as target). They run on the map's DB script executor (<see cref="CreatureMapSystem.StartDbScript"/>),
/// which is the one the quest, gossip and event scripts use, so a map without creature spawns has no executor and starts nothing.
/// </summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>
    /// <c>dbscripts_on_go_template_use</c> script <c>go.Template.Entry</c> for a use by <paramref name="user"/>, at the top of GameObject::Use
    /// before the type switch (GameObject.cpp:1489-1490). Every object of the template runs it, every type. False when there was no script, or
    /// it already runs for this user and object.
    /// </summary>
    private bool StartTemplateUseScript(Unit user, GameObject go)
        => !GooberRefusesUse(user, go)
            && Map.FindUpdater<CreatureMapSystem>() is { } scripts
            && scripts.HasDbScript(DbScriptKind.GameObjectTemplate, go.Template.Entry)
            && scripts.StartDbScript(DbScriptKind.GameObjectTemplate, go.Template.Entry, user, go);

    /// <summary>
    /// A goober on cooldown, or one still in use, refuses the click before anything fires (mangos-classic GameObject.cpp:1482 returns on the
    /// cooldown above the template script at :1490), so a repeated or crafted use must not start the template script again. The same gate as
    /// <see cref="UseGoober"/> (a player: the cooldown, and an object with an auto-close time that is not ready) and
    /// <see cref="UseGooberByUnit"/> (any other unit: the cooldown, or not ready). Other types have no such gate.
    /// </summary>
    private bool GooberRefusesUse(Unit user, GameObject go)
    {
        if (go.Type != GameObjectType.Goober)
        {
            return false;
        }

        if (go.CooldownUntilMs > _clockMs)
        {
            return true;
        }

        return go.LootState != GameObjectLootState.Ready && (user is not Player || go.Template.AutoCloseSeconds() > 0);
    }

    /// <summary>
    /// <c>dbscripts_on_go_use</c> script <c>go.Spawn.Guid</c>, which only a door, a button and a goober start (GameObject.cpp:1506, :1529,
    /// :1703). An object without a spawn row (a summon) has no database guid (GetDbGuid 0) and no script. False when there was none, or it
    /// already runs for this user and object.
    /// </summary>
    private bool StartGuidUseScript(Unit user, GameObject go)
        => go.Spawn is { Guid: var guid and not 0 }
            && Map.FindUpdater<CreatureMapSystem>() is { } scripts
            && scripts.HasDbScript(DbScriptKind.GameObject, guid)
            && scripts.StartDbScript(DbScriptKind.GameObject, guid, user, go);
}
