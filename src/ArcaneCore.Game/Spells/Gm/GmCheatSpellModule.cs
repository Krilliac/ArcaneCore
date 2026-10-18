using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Gm;

/// <summary>
/// The spell side of the GM cheats (<see cref="PlayerCheats"/>): no cast time (Spell::prepare, Spell.cpp:3434-3436) and no power cost
/// (Spell.cpp:3397-3399, 5056-5058) as value modifiers, and for no cooldown the client's own timer is cleared once a cast completes
/// (Spell::finish, Spell.cpp:4347-4348); the server side skips the cooldowns in <c>SpellSystem.AddCooldown</c> / <c>AddGlobalCooldown</c>.
/// </summary>
public sealed class GmCheatSpellModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterValueModifier(new Modifier());
        system.RegisterObserver(new Observer());
    }

    private sealed class Modifier : ISpellValueModifier
    {
        public int Modify(SpellValueKind kind, in SpellValueContext context, int value) => kind switch
        {
            SpellValueKind.CastTime when context.Caster is Player player && player.HasCheat(PlayerCheats.NoCastTime) => 0,
            SpellValueKind.PowerCost when context.Caster is Player player && player.HasCheat(PlayerCheats.NoPowerCost) => 0,
            _ => value,
        };
    }

    private sealed class Observer : ISpellCastObserver
    {
        public void OnFinished(SpellCast cast, bool completed)
        {
            if (completed && cast.Caster is Player player && player.HasCheat(PlayerCheats.NoCooldown))
            {
                player.Session.Send(WorldOpcode.SmsgClearCooldown, SpellPackets.BuildClearCooldown(cast.Spell.Id, player.Guid));
            }
        }
    }
}