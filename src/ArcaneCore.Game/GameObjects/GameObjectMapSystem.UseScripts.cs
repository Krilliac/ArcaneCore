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
    /// before the type switch (GameObject.cpp:1489-1490). Every object of the template runs it, every type. False when there was no script, or the
    /// use was refused first (<see cref="GooberRefusesUse"/>). A click on an object in use never gets here (<see cref="UseCore"/>, as
    /// SpellHandler.cpp:295-296). A use that does get here, a click once the object is usable again or a use through the spell or script path,
    /// starts another run even while a delayed step of the first is pending (ScriptsStart's default SCRIPT_EXEC_PARAM_NONE, Maps/Map.h:233-240).
    /// </summary>
    private bool StartTemplateUseScript(Unit user, GameObject go)
        => !GooberRefusesUse(user, go)
            && Map.FindUpdater<CreatureMapSystem>() is { } scripts
            && scripts.HasDbScript(DbScriptKind.GameObjectTemplate, go.Template.Entry)
            && scripts.StartDbScript(DbScriptKind.GameObjectTemplate, go.Template.Entry, user, go);

    /// <summary>
    /// A goober on cooldown refuses the use before anything fires (mangos-classic GameObject.cpp:1482 returns on the cooldown above the template
    /// script at :1490). A trap is gated by its cooldown alone: GetCooldown covers GAMEOBJECT_TYPE_TRAP and GOOBER (GameObject.h:458-466), so a
    /// trap on cooldown also returns before the template script. The not-ready half is not GameObject::Use: it keeps the script in step with
    /// <see cref="UseGoober"/> (a player: an object with an auto-close time that is not ready) and <see cref="UseGooberByUnit"/> (any other
    /// unit: not ready), which refuse such a use on the spell and script paths, so the script does not start for a use they turn down. A click
    /// on an object in use, of any type, is refused earlier and does not depend on this gate (<see cref="UseCore"/>). Other types have no gate here.
    /// </summary>
    private bool GooberRefusesUse(Unit user, GameObject go)
    {
        if (go.Type is not (GameObjectType.Goober or GameObjectType.Trap))
        {
            return false;
        }

        if (go.CooldownUntilMs > _clockMs)
        {
            return true;
        }

        return go.Type == GameObjectType.Goober && go.LootState != GameObjectLootState.Ready && (user is not Player || go.Template.AutoCloseSeconds() > 0);
    }

    /// <summary>
    /// <c>dbscripts_on_go_use</c> script <c>go.Spawn.Guid</c>, which only a door, a button and a goober start (GameObject.cpp:1506, :1529,
    /// :1703). An object without a spawn row (a summon) has no database guid (GetDbGuid 0) and no script. False when there was none. Repeat
    /// uses behave as for the template script (<see cref="StartTemplateUseScript"/>): a click on an object in use does not get here, and any
    /// use that does starts another run.
    /// </summary>
    private bool StartGuidUseScript(Unit user, GameObject go)
        => go.Spawn is { Guid: var guid and not 0 }
            && Map.FindUpdater<CreatureMapSystem>() is { } scripts
            && scripts.HasDbScript(DbScriptKind.GameObject, guid)
            && scripts.StartDbScript(DbScriptKind.GameObject, guid, user, go);
}
