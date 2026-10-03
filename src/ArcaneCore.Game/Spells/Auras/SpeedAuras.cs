using ArcaneCore.Game.Locomotion;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The speed auras, after vmangos <c>Aura::HandleAuraModIncreaseSpeed</c>, <c>HandleAuraModIncreaseMountedSpeed</c>,
/// <c>HandleAuraModIncreaseSwimSpeed</c>, <c>HandleAuraModDecreaseSpeed</c> and <c>HandleAuraModUseNormalSpeed</c>
/// (SpellAuras.cpp:3972-4040, table entries :96-98,:123,:194-195,:236-237,:256):
/// <list type="table">
/// <item><term>31 MOD_INCREASE_SPEED, 129 MOD_SPEED_ALWAYS, 171 MOD_SPEED_NOT_STACK</term><description>recompute the run speed (Sprint, Aspect of the Cheetah, Ghost Wolf, boots)</description></item>
/// <item><term>32 MOD_INCREASE_MOUNTED_SPEED, 130 MOD_MOUNTED_SPEED_ALWAYS, 172 MOD_MOUNTED_SPEED_NOT_STACK</term><description>recompute the run speed (mounts)</description></item>
/// <item><term>33 MOD_DECREASE_SPEED</term><description>recompute run, run-back and swim (every snare)</description></item>
/// <item><term>58 MOD_INCREASE_SWIM_SPEED</term><description>recompute swim</description></item>
/// <item><term>191 USE_NORMAL_MOVEMENT_SPEED</term><description>recompute run and swim</description></item>
/// </list>
/// Each aura records itself in the unit's <see cref="AuraLedger"/> first, so the formula sees it on apply and no longer on
/// removal. The speed formula and the handshake are <see cref="UnitSpeed"/>. The talent speed modifier (SPELLMOD_SPEED applied
/// to a caster's own speed auras, :3981-3987) belongs to the talents lane and is not applied.
/// </summary>
public sealed class SpeedAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        foreach (AuraType type in new[] { AuraType.ModIncreaseSpeed, AuraType.ModIncreaseMountedSpeed, AuraType.ModSpeedAlways, AuraType.ModMountedSpeedAlways, AuraType.ModSpeedNotStack, AuraType.ModMountedSpeedNotStack })
        {
            system.RegisterAura(type, Handler(MoveType.Run));
        }

        system.RegisterAura(AuraType.ModDecreaseSpeed, Handler(MoveType.Run, MoveType.RunBack, MoveType.Swim));
        system.RegisterAura(AuraType.ModIncreaseSwimSpeed, Handler(MoveType.Swim));
        system.RegisterAura(AuraType.UseNormalMovementSpeed, Handler(MoveType.Run, MoveType.Swim));
    }

    private static AuraHandler Handler(params MoveType[] updates) => new((system, holder, aura, apply) =>
    {
        if (apply)
        {
            holder.Target.Locomotion.Auras.Add(aura);
        }
        else
        {
            holder.Target.Locomotion.Auras.Remove(aura);
        }

        foreach (MoveType type in updates)
        {
            UnitSpeed.UpdateSpeed(holder.Target, type);
        }
    }, null);
}
