using ArcaneCore.Kernel.Items;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Resolves the items of <c>creature_equip_template</c> rows for the creatures of this map.</summary>
public sealed partial class CreatureMapSystem
{
    private readonly HashSet<uint> _warnedEquipmentItems = [];
    private Func<uint, ItemTemplate?>? _equipmentItemResolver;

    /// <summary>The delegate every creature of this system is built with (one instance, so creating a creature allocates nothing for it).</summary>
    private Func<uint, ItemTemplate?> EquipmentItemResolver => _equipmentItemResolver ??= ResolveEquipmentItem;

    /// <summary>
    /// The world item store's template of an equipment item (the lookup <see cref="CreatureAiServices.ItemTemplateOf"/> gives scripts). An
    /// item the store does not know is warned about once per entry and its slot stays empty. Without an item store nothing resolves and
    /// nothing is warned about: the host has no items at all.
    /// </summary>
    private ItemTemplate? ResolveEquipmentItem(uint item)
    {
        if (_ai.ItemTemplateOf is not { } lookup)
        {
            return null;
        }

        ItemTemplate? template = lookup(item);
        if (template is null && _warnedEquipmentItems.Add(item))
        {
            _logger.LogWarning("creature_equip_template names item {Item}, which item_template does not have; that slot stays empty", item);
        }

        return template;
    }
}
