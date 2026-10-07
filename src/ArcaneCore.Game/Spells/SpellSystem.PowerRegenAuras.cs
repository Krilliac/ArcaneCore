using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed class PowerRegenAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
        => system.RegisterAura(AuraType.ModPowerRegen, new AuraHandler(static (spells, holder, aura, apply) =>
        {
            if (!apply) return;
            spells.ApplyFoodDrinkVisual(holder);
            spells.ObserveFoodDrinkHeartbeat(holder);
        }, Tick));

    private static void Tick(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        if (!target.IsAlive || target.PowerType != (PowerType)aura.MiscValue || target.PowerType != PowerType.Rage)
        {
            return;
        }

        long delta = aura.Amount * 3L / 5;
        uint current = SpellSystem.GetPower(target, PowerType.Rage);
        uint maximum = target.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage);
        SpellSystem.SetPower(target, PowerType.Rage, (uint)Math.Clamp(current + delta, 0L, maximum));
    }
}
