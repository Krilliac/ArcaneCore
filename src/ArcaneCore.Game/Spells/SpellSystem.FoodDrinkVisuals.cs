using ArcaneCore.Game.Entities;
using System.Runtime.CompilerServices;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private readonly ConditionalWeakTable<Unit, object> _foodHeartbeatSubscriptions = new();

    internal void ObserveFoodDrinkHeartbeat(SpellAuraHolder holder)
    {
        if (!IsFoodDrinkHolder(holder)) return;
        _foodHeartbeatSubscriptions.GetValue(holder.Target, unit =>
        {
            unit.Heartbeat += TriggerFoodDrinkHeartbeat;
            return new object();
        });
    }

    private void ForgetFoodDrinkHeartbeat(Unit unit)
    {
        if (_foodHeartbeatSubscriptions.Remove(unit))
            unit.Heartbeat -= TriggerFoodDrinkHeartbeat;
    }

    public void TriggerFoodDrinkHeartbeat(Unit unit)
    {
        foreach (SpellAuraHolder holder in GetAuras(unit))
        {
            if (!IsFoodDrinkHolder(holder)) continue;
            foreach (SpellAura? aura in holder.Auras.Where(a => a is not null))
            {
                if (holder.HasAura(AuraType.ModRegen))
                    SendToSet(unit, WorldOpcode.SmsgPlaySpellVisual,
                        FoodDrinkVisualPackets.PlaySpellVisual(unit.Guid, FoodDrinkVisualPackets.FoodVisualKit), includeSelf: true);
                if (holder.HasAura(AuraType.ModPowerRegen))
                    SendToSet(unit, WorldOpcode.SmsgPlaySpellVisual,
                        FoodDrinkVisualPackets.PlaySpellVisual(unit.Guid, FoodDrinkVisualPackets.DrinkVisualKit), includeSelf: true);
            }
        }
    }

    internal bool IsFoodDrinkHolder(SpellAuraHolder holder)
        => !holder.IsRemoved && holder.Spell.SpellFamilyName == 0
            && (holder.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) != 0
            && (holder.HasAura(AuraType.ModRegen) || holder.HasAura(AuraType.ModPowerRegen));

    internal void ApplyFoodDrinkVisual(SpellAuraHolder holder)
    {
        if (!IsFoodDrinkHolder(holder)) return;
        SendToSet(holder.Target, WorldOpcode.SmsgEmote,
            FoodDrinkVisualPackets.Emote(holder.Target.Guid, FoodDrinkVisualPackets.EatEmote), includeSelf: true);
    }
}
