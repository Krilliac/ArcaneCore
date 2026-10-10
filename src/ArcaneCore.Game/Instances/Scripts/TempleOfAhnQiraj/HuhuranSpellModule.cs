using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// vmangos HuhuranWyvernStingScript / HuhuranPoisonBoltVolleyScript select the closest area targets.
/// mangos-classic HuhuranWyvernSting triggers spell 26233 for 500 damage on expiry or 3000 on dispel.
/// </summary>
public sealed class HuhuranSpellModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        system.RegisterClosestAreaTargets(26180);
        system.RegisterClosestAreaTargets(26052);
        system.HolderRemoved += holder =>
        {
            if (holder.Spell.Id != 26180 || !holder.Target.IsAlive
                || holder.RemoveMode is AuraRemoveMode.Death or AuraRemoveMode.Delete
                || holder.Target.Map?.FindObject(holder.CasterGuid) is not Unit caster)
                return;

            int damage = holder.RemoveMode == AuraRemoveMode.Dispel ? 3000 : 500;
            system.CastCustomSpell(caster, 26233, SpellCastTargets.ForUnit(holder.Target.Guid), damage);
        };
    }
}
