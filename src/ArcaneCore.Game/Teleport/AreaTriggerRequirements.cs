using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Teleport;

/// <summary>The verdict of one entry check: let the player through, or refuse (with a text, or silently).</summary>
/// <param name="Allowed">True when the player may be teleported.</param>
/// <param name="Message">The refusal text for SMSG_AREA_TRIGGER_MESSAGE; null for a silent refusal (and for every allowed verdict).</param>
public readonly record struct AreaTriggerVerdict(bool Allowed, string? Message)
{
    /// <summary>The player may enter.</summary>
    public static AreaTriggerVerdict Allow { get; } = new(true, null);

    /// <summary>Refuse with <paramref name="message"/>; null refuses without telling the client anything.</summary>
    public static AreaTriggerVerdict Refuse(string? message) => new(false, message);
}

/// <summary>Which requirement of an area trigger refused (<see cref="AreaTriggerCheck.Failed"/>).</summary>
public enum AreaTriggerRequirement : byte
{
    /// <summary>Nothing refused.</summary>
    None = 0,

    /// <summary>The required level.</summary>
    Level,

    /// <summary>The required key item.</summary>
    Item,

    /// <summary>The required quest (an <see cref="IAreaTriggerGate"/> whose <see cref="IAreaTriggerGate.Requirement"/> is this).</summary>
    Quest,

    /// <summary>Any other gate (an instance script's block).</summary>
    Gate,

    /// <summary>The conditions-table reference.</summary>
    Condition,
}

/// <summary>
/// Requirements an <see cref="IAreaTriggerWaiver"/> may skip. The level is never waivable, nor a gate that is neither a quest nor
/// the conditions table (an instance script's block).
/// </summary>
[Flags]
public enum AreaTriggerWaivers : byte
{
    None = 0,
    Item = 1,
    Quest = 2,
    Condition = 4,
}

/// <summary>A world feature that waives some entry requirements for a player on one teleport (consulted before <see cref="AreaTriggerRequirements.Check"/>).</summary>
public interface IAreaTriggerWaiver
{
    /// <summary>The requirements to skip for <paramref name="player"/> entering through <paramref name="teleport"/> (world thread).</summary>
    AreaTriggerWaivers Waives(Player player, AreaTriggerTeleport teleport);
}

/// <summary>The outcome of <see cref="AreaTriggerRequirements.Check"/>: the verdict and which requirement refused.</summary>
public readonly record struct AreaTriggerCheck(AreaTriggerVerdict Verdict, AreaTriggerRequirement Failed);

/// <summary>
/// A system that can veto a teleport through an area trigger on grounds this assembly has no state for (the quest log, a later
/// instance-lockout or faction system). Called on the world thread, once per teleport attempt, after the built-in requirements
/// of <see cref="AreaTriggerRequirements"/> passed. Every registered world feature that implements it is consulted
/// (<c>TeleportHandlers</c> filters the discovered <c>IWorldFeature</c>s, no shared registration edit); the first refusal wins.
/// </summary>
public interface IAreaTriggerGate
{
    /// <summary>The kind of requirement this gate enforces (what <see cref="AreaTriggerRequirements.Check"/> reports and what a waiver may skip).</summary>
    AreaTriggerRequirement Requirement => AreaTriggerRequirement.Gate;

    /// <summary>Whether <paramref name="player"/> may use <paramref name="teleport"/>. Must not allocate per call beyond the verdict (a struct).</summary>
    AreaTriggerVerdict Check(Player player, AreaTriggerTeleport teleport);
}

/// <summary>
/// Entry requirements of an <c>areatrigger_teleport</c> row, evaluated when a player steps on the trigger (the lock-status part of
/// the reference cores' <c>HandleAreaTriggerOpcode</c>: mangos-classic
/// <c>Player::GetAreaTriggerLockStatus</c>, Entities/Player.cpp:19985-20057). Order: a game master always passes, then the required level,
/// the required item (either one when both columns are set), the quest and further vetoes of the
/// <see cref="IAreaTriggerGate"/>s, then the conditions-table reference.
/// <para>
/// A row whose <see cref="AreaTriggerTeleport.Message"/> is not empty shows that text for every refusal (the cores' failed-text
/// column replaces the generated message). Otherwise the level and item refusals use the stock texts (mangos_string 49 and 50) and a
/// refusal by a gate or the conditions table is silent unless the gate supplies a text, which is what the reference does for an
/// unfinished quest and an unknown condition (it leaves "ToDo: SendAreaTriggerMessage" there).
/// </para>
/// Stateless: every input is a parameter; the evaluation allocates only the refusal text, never on the passing path.
/// </summary>
public static class AreaTriggerRequirements
{
    /// <summary>mangos_string 49 (LANG_LEVEL_MINREQUIRED): "You must be at least level %u to enter." (reference Language.h:77).</summary>
    public const string LevelRequiredText = "You must be at least level {0} to enter.";

    /// <summary>
    /// mangos_string 50 (LANG_REQUIRED_ITEM): "You must have item %s to enter." (reference Language.h:78). UNVERIFIED against a retail 1.12.1
    /// client capture: it is the stock mangos text; a world DB that wants the retail wording sets the row's message.
    /// </summary>
    public const string ItemRequiredText = "You must have item {0} to enter.";

    /// <summary>Evaluate every requirement of <paramref name="teleport"/> for <paramref name="player"/> (no waivers).</summary>
    /// <param name="player">The player stepping on the trigger (world thread).</param>
    /// <param name="teleport">The destination row with its requirements.</param>
    /// <param name="conditions">The conditions-table evaluator; null when none is registered, which refuses any row that names a condition (fail closed).</param>
    /// <param name="gates">The additional vetoes, in the order they are consulted; may be empty.</param>
    public static AreaTriggerVerdict Evaluate(Player player, AreaTriggerTeleport teleport, IConditionEvaluator? conditions, IEnumerable<IAreaTriggerGate> gates)
        => Check(player, teleport, conditions, gates, AreaTriggerWaivers.None).Verdict;

    /// <summary>
    /// <see cref="Evaluate"/> with the failing requirement reported and some requirements waived (<paramref name="waivers"/>: the key item,
    /// the quest gates, the conditions table). The order is the same: game master, level, items, gates, condition. The level and any
    /// gate that is not a quest gate are never waived.
    /// </summary>
    public static AreaTriggerCheck Check(Player player, AreaTriggerTeleport teleport, IConditionEvaluator? conditions,
        IEnumerable<IAreaTriggerGate> gates, AreaTriggerWaivers waivers)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(teleport);
        ArgumentNullException.ThrowIfNull(gates);

        // mangos-zero GetAreaTriggerLockStatus: "Gamemaster can always enter".
        if (player.IsGameMaster)
        {
            return new(AreaTriggerVerdict.Allow, AreaTriggerRequirement.None);
        }

        if (player.Level < teleport.RequiredLevel)
        {
            return new(Refuse(teleport, string.Format(CultureInfo.InvariantCulture, LevelRequiredText, teleport.RequiredLevel)), AreaTriggerRequirement.Level);
        }

        // mangos-classic Player::GetAreaTriggerLockStatus (Entities/Player.cpp:20010-20027):
        // when both columns name keys, either one suffices; a refusal names the first.
        // HasItemCount's default includes bags/keyring, not the bank.
        if ((waivers & AreaTriggerWaivers.Item) == 0)
        {
            if (MissingItem(player, teleport.RequiredItem) is { } first
                && (teleport.RequiredItem2 == 0 || MissingItem(player, teleport.RequiredItem2) is not null))
            {
                return new(Refuse(teleport, ItemText(player, first)), AreaTriggerRequirement.Item);
            }

            if (teleport.RequiredItem == 0 && MissingItem(player, teleport.RequiredItem2) is { } second)
            {
                return new(Refuse(teleport, ItemText(player, second)), AreaTriggerRequirement.Item);
            }
        }

        foreach (IAreaTriggerGate gate in gates)
        {
            AreaTriggerRequirement kind = gate.Requirement;
            if (kind == AreaTriggerRequirement.Quest && (waivers & AreaTriggerWaivers.Quest) != 0)
            {
                continue;
            }

            AreaTriggerVerdict verdict = gate.Check(player, teleport);
            if (!verdict.Allowed)
            {
                return new(Refuse(teleport, verdict.Message), kind);
            }
        }

        if ((waivers & AreaTriggerWaivers.Condition) == 0
            && teleport.RequiredCondition != 0 && conditions?.IsSatisfied(teleport.RequiredCondition, player, null) != true)
        {
            return new(Refuse(teleport, null), AreaTriggerRequirement.Condition);
        }

        return new(AreaTriggerVerdict.Allow, AreaTriggerRequirement.None);
    }

    // The row's own text replaces whatever the failing check would have said (reference: failed_text_mangos_string_id wins).
    private static AreaTriggerVerdict Refuse(AreaTriggerTeleport teleport, string? generated)
        => AreaTriggerVerdict.Refuse(teleport.Message.Length > 0 ? teleport.Message : generated);

    private static uint? MissingItem(Player player, uint entry)
        => entry != 0 && player.Inventory.GetItemCount(entry) == 0 ? entry : null;

    // The reference dereferences the template and would crash on an unknown item; here the entry id stands in for the name.
    private static string ItemText(Player player, uint entry)
        => string.Format(CultureInfo.InvariantCulture, ItemRequiredText,
            player.Inventory.Templates.Find(entry)?.Name is { Length: > 0 } name ? name : entry.ToString(CultureInfo.InvariantCulture));
}
