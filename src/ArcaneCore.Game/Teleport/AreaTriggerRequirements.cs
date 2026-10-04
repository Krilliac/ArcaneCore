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

/// <summary>
/// A system that can veto a teleport through an area trigger on grounds this assembly has no state for (the quest log, a later
/// instance-lockout or faction system). Called on the world thread, once per teleport attempt, after the built-in requirements
/// of <see cref="AreaTriggerRequirements"/> passed. Every registered world feature that implements it is consulted
/// (<c>TeleportHandlers</c> filters the discovered <c>IWorldFeature</c>s, no shared registration edit); the first refusal wins.
/// </summary>
public interface IAreaTriggerGate
{
    /// <summary>Whether <paramref name="player"/> may use <paramref name="teleport"/>. Must not allocate per call beyond the verdict (a struct).</summary>
    AreaTriggerVerdict Check(Player player, AreaTriggerTeleport teleport);
}

/// <summary>
/// Entry requirements of an <c>areatrigger_teleport</c> row, evaluated when a player steps on the trigger (the lock-status part of
/// the reference cores' <c>HandleAreaTriggerOpcode</c>: mangos-zero <c>Player::GetAreaTriggerLockStatus</c> and
/// <c>SendTransferAbortedByLockStatus</c>, PlayerAreaTrigger.cpp:94-262). Order: a game master always passes, then the required level,
/// the required items, the quest and further vetoes of the <see cref="IAreaTriggerGate"/>s, then the conditions-table reference.
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

    /// <summary>Evaluate every requirement of <paramref name="teleport"/> for <paramref name="player"/>.</summary>
    /// <param name="player">The player stepping on the trigger (world thread).</param>
    /// <param name="teleport">The destination row with its requirements.</param>
    /// <param name="conditions">The conditions-table evaluator; null when none is registered, which refuses any row that names a condition (fail closed).</param>
    /// <param name="gates">The additional vetoes, in the order they are consulted; may be empty.</param>
    public static AreaTriggerVerdict Evaluate(Player player, AreaTriggerTeleport teleport, IConditionEvaluator? conditions, IEnumerable<IAreaTriggerGate> gates)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(teleport);
        ArgumentNullException.ThrowIfNull(gates);

        // mangos-zero GetAreaTriggerLockStatus: "Gamemaster can always enter".
        if (player.IsGameMaster)
        {
            return AreaTriggerVerdict.Allow;
        }

        if (player.Level < teleport.RequiredLevel)
        {
            return Refuse(teleport, string.Format(CultureInfo.InvariantCulture, LevelRequiredText, teleport.RequiredLevel));
        }

        // The keyring and the bags count, the bank does not (Player::HasItemCount with its default inBankAlso = false).
        if (MissingItem(player, teleport.RequiredItem) is { } first)
        {
            return Refuse(teleport, ItemText(player, first));
        }

        if (MissingItem(player, teleport.RequiredItem2) is { } second)
        {
            return Refuse(teleport, ItemText(player, second));
        }

        foreach (IAreaTriggerGate gate in gates)
        {
            AreaTriggerVerdict verdict = gate.Check(player, teleport);
            if (!verdict.Allowed)
            {
                return Refuse(teleport, verdict.Message);
            }
        }

        if (teleport.RequiredCondition != 0 && conditions?.IsSatisfied(teleport.RequiredCondition, player, null) != true)
        {
            return Refuse(teleport, null);
        }

        return AreaTriggerVerdict.Allow;
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
