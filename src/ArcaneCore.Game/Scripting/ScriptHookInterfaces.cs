using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Scripting;

// The global script hooks (AzerothCore src/server/game/Scripting/ScriptDefines/*: PlayerScript, WorldScript, UnitScript, ItemScript,
// AllSpellScript, GroupScript, GuildScript, BGScript). A hook class implements one or more of these interfaces and overrides only the
// methods it needs (every method has an empty default). ScriptHookRegistry.Register reads which methods a class really overrides and adds it
// to those hooks only, the way AzerothCore's PLAYERHOOK_* enabled-hook lists do, so a hook nobody overrides costs one empty-array check.
// All hooks run on the world thread. A hook that throws is logged and the others still run (docs/integration/script-hooks.md).

/// <summary>Marker for a class of global script hooks (<see cref="ScriptHookRegistry.Register"/>).</summary>
public interface IScriptHooks;

/// <summary>A chat line as the player hooks see it: the client's chat type and language, the text, and the whisper target or channel name.</summary>
public readonly record struct ScriptChatMessage(uint ChatType, uint Language, string Text, string? Target);

/// <summary>
/// An addon line (CMSG_MESSAGECHAT language 0xFFFFFFFF). Prefix and Text come from the client's "%s\t%s" join (Wow.exe 5875
/// SendAddonMessage 0x49F9A2, format string 0x844B5C; HermesProxy@841a26f5 ChatHandler.cs:212). Prefix is null when the line has no TAB.
/// </summary>
public readonly record struct ScriptAddonMessage(uint ChatType, string? Prefix, string Text, string? Target)
{
    /// <summary>
    /// Split <paramref name="raw"/> at the FIRST TAB, because the client does not stop a prefix from containing one (Wow.exe 0x49F97F-0x49F9A0).
    /// Without a TAB the prefix is null and the text is the whole line, so a sender cannot skip a filter by leaving the TAB out.
    /// </summary>
    public static ScriptAddonMessage Parse(uint chatType, string raw, string? target)
    {
        ArgumentNullException.ThrowIfNull(raw);
        int tab = raw.IndexOf('\t');
        return tab < 0 ? new(chatType, null, raw, target) : new(chatType, raw[..tab], raw[(tab + 1)..], target);
    }
}

/// <summary>AzerothCore <c>PlayerScript</c> (ScriptDefines/PlayerScript.h).</summary>
public interface IPlayerHooks : IScriptHooks
{
    /// <summary>The player finished entering the world (<c>OnPlayerLogin</c>; after <see cref="Maps.WorldRuntime.PlayerLoggedIn"/>).</summary>
    void OnLogin(Player player) { }

    /// <summary>The player is leaving the world, still in its map and before the save (<c>OnPlayerLogout</c>).</summary>
    void OnLogout(Player player) { }

    /// <summary>The player's level changed through <see cref="Progression.PlayerProgression.GiveLevel"/> (<c>OnPlayerLevelChanged</c>).</summary>
    void OnLevelChanged(Player player, byte oldLevel) { }

    /// <summary>The player killed a unit, player or creature (<c>OnPlayerPVPKill</c> / <c>OnPlayerCreatureKill</c>).</summary>
    void OnKill(Player killer, Unit victim) { }

    /// <summary>The player was killed; <paramref name="killer"/> is null for environmental deaths (<c>OnPlayerKilledByCreature</c>).</summary>
    void OnKilled(Player victim, Unit? killer) { }

    /// <summary>A chat line before delivery (<c>OnPlayerCanUseChat</c>). Return false to drop it.</summary>
    bool OnChat(Player player, ScriptChatMessage message) => true;

    /// <summary>An addon line before it is offered to the chat features (AzerothCore OnPlayerCanUseChat with LANG_ADDON). Return false to drop it. Raised only while World:Chat:AddonChannel is on, after the optional addon mute/flood check.</summary>
    bool OnAddonMessage(Player player, ScriptAddonMessage message) => true;

    /// <summary>A duel started, after the 3 s countdown (<c>OnPlayerDuelStart</c>).</summary>
    void OnDuelStart(Player first, Player second) { }

    /// <summary>A duel ended (<c>OnPlayerDuelEnd</c>). For Interrupted nobody won: <paramref name="winner"/> is just the other player.</summary>
    void OnDuelEnd(Player winner, Player loser, DuelCompleteType type) { }
}

/// <summary>AzerothCore <c>WorldScript</c> (ScriptDefines/WorldScript.h).</summary>
public interface IWorldHooks : IScriptHooks
{
    /// <summary>The world thread started, before its first tick (<c>OnStartup</c>).</summary>
    void OnStartup() { }

    /// <summary>Once per world tick, before the maps update (<c>OnUpdate</c>).</summary>
    void OnUpdate(uint diffMs) { }

    /// <summary><c>.reload config</c> committed (<c>OnAfterConfigLoad(reload: true)</c>).</summary>
    void OnConfigReload() { }
}

/// <summary>AzerothCore <c>UnitScript</c> (ScriptDefines/UnitScript.h).</summary>
public interface IUnitHooks : IScriptHooks
{
    /// <summary>Damage about to be dealt by <see cref="MapCombat.DealDamage"/>; change <paramref name="damage"/> to modify it (<c>OnDamage</c>).</summary>
    void OnDamage(Unit attacker, Unit victim, ref uint damage) { }

    /// <summary>A unit died through <see cref="MapCombat.Kill"/> (<c>OnUnitDeath</c>).</summary>
    void OnDeath(Unit victim, Unit? killer) { }
}

/// <summary>AzerothCore <c>ItemScript</c> / <c>AllItemScript</c> (ScriptDefines/ItemScript.h, AllItemScript.h).</summary>
public interface IItemHooks : IScriptHooks
{
    /// <summary>CMSG_USE_ITEM passed the use checks. Return true to handle it here (the item's spell is not cast) (<c>OnItemUse</c>).</summary>
    bool OnUse(Player player, Item item, SpellCastTargets targets) => false;

    /// <summary>The item went into equipment slot <paramref name="slot"/> (0-18) (<c>OnPlayerEquip</c>).</summary>
    void OnEquip(Player player, Item item, byte slot) { }

    /// <summary>The item left equipment slot <paramref name="slot"/> (<c>OnPlayerUnequip</c>).</summary>
    void OnUnequip(Player player, Item item, byte slot) { }
}

/// <summary>AzerothCore <c>AllSpellScript</c> / <c>PlayerScript::OnPlayerSpellCast</c>: every finished cast, from any caster.</summary>
public interface ISpellHooks : IScriptHooks
{
    /// <summary>A cast finished; <paramref name="completed"/> is false when it was cancelled or failed.</summary>
    void OnCastFinished(SpellCast cast, bool completed) { }
}

/// <summary>AzerothCore <c>GroupScript</c> (ScriptDefines/GroupScript.h).</summary>
public interface IGroupHooks : IScriptHooks
{
    void OnMemberAdded(Group group, ObjectGuid member) { }

    void OnMemberRemoved(Group group, ObjectGuid member) { }

    void OnDisband(Group group) { }
}

/// <summary>AzerothCore <c>GuildScript</c> (ScriptDefines/GuildScript.h).</summary>
public interface IGuildHooks : IScriptHooks
{
    void OnMemberAdded(Guild guild, uint characterId) { }

    void OnMemberRemoved(Guild guild, uint characterId) { }
}

/// <summary>AzerothCore <c>BGScript</c> / <c>AllBattlegroundScript</c> (ScriptDefines/AllBattlegroundScript.h).</summary>
public interface IBattlegroundHooks : IScriptHooks
{
    /// <summary>The match began (<c>OnBattlegroundStart</c>).</summary>
    void OnStart(Battleground battleground) { }

    /// <summary>The match ended; <paramref name="winner"/> null is a draw (<c>OnBattlegroundEnd</c>).</summary>
    void OnEnd(Battleground battleground, Team? winner) { }
}
