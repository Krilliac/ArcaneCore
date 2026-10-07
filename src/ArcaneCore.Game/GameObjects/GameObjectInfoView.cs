using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Typed accessors over the raw <c>data0..data23</c> words of a <see cref="GameObjectTemplate"/>,
/// mirroring the helper methods of vmangos <c>GameObjectInfo</c> (GameObjectDefines.h:536-668) so
/// every behaviour reads the same per-type column the retail server reads. Behaviour re-implemented,
/// no code copied.
/// </summary>
public static class GameObjectInfoView
{
    /// <summary>
    /// GetAutoCloseTime (GameObjectDefines.h:654-668): door data2, button data2, trap data6, goober data3,
    /// transport data2, area damage data5 hold <c>seconds * 0x10000</c>; the helper divides by 0x10000
    /// (an unsigned division, so the signed trap column behaves exactly as in vmangos).
    /// </summary>
    public static uint AutoCloseSeconds(this GameObjectTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        uint raw = (GameObjectType)t.Type switch
        {
            GameObjectType.Door => t.GetData(2),
            GameObjectType.Button => t.GetData(2),
            GameObjectType.Trap => t.GetData(6),
            GameObjectType.Goober => t.GetData(3),
            GameObjectType.Transport => t.GetData(2),
            GameObjectType.AreaDamage => t.GetData(5),
            _ => 0,
        };
        return raw / 0x10000;
    }

    /// <summary>GetCooldown (GameObjectDefines.h:640-648): trap data5 and goober data6, in seconds.</summary>
    public static uint CooldownSeconds(this GameObjectTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return (GameObjectType)t.Type switch
        {
            GameObjectType.Trap => t.GetData(5),
            GameObjectType.Goober => t.GetData(6),
            _ => 0,
        };
    }

    /// <summary>GetCharges (GameObjectDefines.h:630-638): trap data4, guard post data1, spell caster data1.</summary>
    public static uint Charges(this GameObjectTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return (GameObjectType)t.Type switch
        {
            GameObjectType.Trap => t.GetData(4),
            GameObjectType.GuardPost => t.GetData(1),
            GameObjectType.SpellCaster => t.GetData(1),
            _ => 0,
        };
    }

    /// <summary>GetLinkedGameObjectEntry (GameObjectDefines.h:650-659): button data3, chest data7, spell focus data2, goober data12.</summary>
    public static uint LinkedTrapEntry(this GameObjectTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return (GameObjectType)t.Type switch
        {
            GameObjectType.Button => t.GetData(3),
            GameObjectType.Chest => t.GetData(7),
            GameObjectType.SpellFocus => t.GetData(2),
            GameObjectType.Goober => t.GetData(12),
            _ => 0,
        };
    }

    /// <summary>IsDespawnAtAction (GameObjectDefines.h:536-544): chest data3 and goober data5 are the "consumable" flag.</summary>
    public static bool IsDespawnAtAction(this GameObjectTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return (GameObjectType)t.Type switch
        {
            GameObjectType.Chest => t.GetData(3) != 0,
            GameObjectType.Goober => t.GetData(5) != 0,
            _ => false,
        };
    }

    /// <summary>
    /// GetDespawnPossibility (GameObjectDefines.h:584-600): the per-type noDamageImmune column of door (data3),
    /// button (data4), quest giver (data5), goober (data11) and flag stand (data5); every other type answers true.
    /// </summary>
    public static bool DespawnPossibility(this GameObjectTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return (GameObjectType)t.Type switch
        {
            GameObjectType.Door => t.GetData(3) != 0,
            GameObjectType.Button => t.GetData(4) != 0,
            GameObjectType.QuestGiver => t.GetData(5) != 0,
            GameObjectType.Goober => t.GetData(11) != 0,
            GameObjectType.FlagStand => t.GetData(5) != 0,
            _ => true,
        };
    }

    /// <summary>CannotBeUsedUnderImmunity (GameObjectDefines.h:602-619): the same noDamageImmune columns; every chest is refused.</summary>
    public static bool CannotBeUsedUnderImmunity(this GameObjectTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return (GameObjectType)t.Type switch
        {
            GameObjectType.Chest => true,
            GameObjectType.Door or GameObjectType.Button or GameObjectType.QuestGiver
                or GameObjectType.Goober or GameObjectType.FlagStand => t.DespawnPossibility(), // the noDamageImmune column itself
            _ => false,
        };
    }

    /// <summary>IsUsableMounted (GameObjectDefines.h:546-557): mailbox always; quest giver data8, text data3, goober data17, spell caster data3.</summary>
    public static bool IsUsableMounted(this GameObjectTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return (GameObjectType)t.Type switch
        {
            GameObjectType.Mailbox => true,
            GameObjectType.QuestGiver => t.GetData(8) != 0,
            GameObjectType.Text => t.GetData(3) != 0,
            GameObjectType.Goober => t.GetData(17) != 0,
            GameObjectType.SpellCaster => t.GetData(3) != 0,
            _ => false,
        };
    }

    /// <summary>
    /// GameObject::HasCustomAnim (GameObject.cpp:2431-2450): the displays that play a custom animation instead of changing state
    /// (eternal flame, the hunter traps, lava and plague fissures, the Dun Morogh mortar, Sapphiron's birth, Silithyst).
    /// </summary>
    public static bool HasCustomAnim(uint displayId)
        => displayId is 2570 or 3071 or 3072 or 3073 or 3074 or 4392 or 4472 or 4491 or 6785 or 6747 or 6871;

    /// <summary>
    /// GameObject::LoadFromDB (GameObject.cpp:985-991): a database spawn whose type neither despawns when
    /// targeted (noDamageImmune clear) nor at use (not consumable) and whose spawntimesecsmin is non-negative never despawns:
    /// it carries GO_FLAG_NODESPAWN and has no respawn delay.
    /// </summary>
    public static bool NeverDespawns(this GameObjectTemplate t, int spawnTimeSecondsMin)
    {
        ArgumentNullException.ThrowIfNull(t);
        return !t.DespawnPossibility() && !t.IsDespawnAtAction() && spawnTimeSecondsMin >= 0;
    }
}

