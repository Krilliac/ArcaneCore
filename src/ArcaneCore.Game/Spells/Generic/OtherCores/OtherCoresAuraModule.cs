using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Generic.OtherCores;

/// <summary>
/// DUMMY aura cases that only mangos-classic has in Aura::HandleAuraDummy (SpellAuras.cpp): Frostwhisper's Lifeblood 17189 (:1414-1419, on removal the
/// caster is filled to full health), Anub'Rekhan's Aura 29104 (:1429-1434, a player who dies under it casts 29105 on itself), Submerge Visual 28819
/// (:1521-1531, custom stand state; an Eye Stalk 16236 despawns 700 ms later; stand again on removal) and Lordaeron's Blessing 30238 (:1346-1350, :1435-1439,
/// applies its effect aura 31906 and drops it on removal). World-thread only; the module holds no state.
/// </summary>
public sealed class OtherCoresAuraModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterDummyAuraHandler(17189, (_, holder, _, apply) =>
        {
            if (!apply && holder.Target.Map?.FindObject(holder.CasterGuid) is Unit caster && caster.IsAlive)
            {
                caster.Health = caster.MaxHealth;
            }
        });
        system.RegisterDummyAuraHandler(29104, (spells, holder, _, apply) =>
        {
            if (!apply && holder.RemoveMode == AuraRemoveMode.Death && holder.Target is Player player)
            {
                spells.CastSpell(player, 29105, SpellCastTargets.ForSelf(), triggered: true);
            }
        });
        system.RegisterDummyAuraHandler(28819, (_, holder, _, apply) =>
        {
            Unit target = holder.Target;
            if (apply)
            {
                target.StandState = StandState.Custom;
                if (target is Creature { Entry: 16236 } stalk)
                {
                    WorldDummyCasts.Despawn(stalk, 700);
                }
            }
            else
            {
                target.StandState = StandState.Stand;
            }
        });
        system.RegisterDummyAuraHandler(30238, (spells, holder, _, apply) =>
        {
            if (apply)
            {
                spells.CastSpell(holder.Target, 31906, SpellCastTargets.ForSelf(), triggered: true);
            }
            else
            {
                spells.RemoveAuras(holder.Target, 31906);
            }
        });
    }
}
