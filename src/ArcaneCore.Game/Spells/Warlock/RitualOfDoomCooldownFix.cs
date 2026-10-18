using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Ritual of Doom client cooldown fix (vmangos Spell::finish, Spell.cpp:4335-4345): the 1.12 client greys out Ritual of Doom (18540) itself while
/// it is being cast and leaves it greyed when the cast fails or is cancelled. When a warlock's own (not triggered) Ritual of Doom ends without
/// completing and the spell has no cooldown on the server, SMSG_CLEAR_COOLDOWN tells the client it is usable again. Triggered casts (the ritual
/// helpers) are ignored.
/// </summary>
public sealed class RitualOfDoomCooldownFix : ISpellHandlerModule
{
    public const uint RitualOfDoom = 18540;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterObserver(new Observer(system));
    }

    private sealed class Observer(SpellSystem system) : ISpellCastObserver
    {
        public void OnFinished(SpellCast cast, bool completed)
        {
            if (completed || cast.IsTriggered || cast.Spell.Id != RitualOfDoom || cast.Caster is not Player { Class: Class.Warlock } player
                || !system.IsSpellReadyIgnoringGlobalCooldown(player, cast.Spell))
            {
                return;
            }

            player.Session.Send(WorldOpcode.SmsgClearCooldown, SpellPackets.BuildClearCooldown(RitualOfDoom, player.Guid));
        }
    }
}