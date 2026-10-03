using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Fishing;

/// <summary>
/// Registers the fishing spells' TRANS_DOOR effect (<see cref="SpellEffectName.TransDoor"/>, vmangos Spell::EffectTransmitted) on a spell system
/// and ends the bobber with its channel. The per-map state lives in <see cref="FishingService"/>; this class only routes by map. Install once
/// per <see cref="SpellSystem"/>.
/// </summary>
public sealed class FishingSpells(Func<IEnumerable<FishingService>> services) : ISpellCastObserver
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        // RegisterEffect keeps one handler per effect, and vmangos EffectTransmitted also summons rituals and traps through TRANS_DOOR: keep
        // whatever was installed first and hand it every transmitted object that is not a fishing node.
        SpellEffectHandler? previous = system.GetEffectHandler(SpellEffectName.TransDoor);
        system.RegisterEffect(SpellEffectName.TransDoor, context =>
        {
            if (context.Caster.Map is { } map && ServiceOf(map) is { } service && service.Transmit(context))
            {
                return;
            }

            previous?.Invoke(context);
        });
        system.RegisterObserver(this);
    }

    /// <summary>The cast ended (also when the caster already left its map): every map forgets that caster's bobber.</summary>
    public void OnFinished(SpellCast cast, bool completed)
    {
        foreach (FishingService service in services())
        {
            service.OnCastFinished(cast.Caster, cast.Spell.Id);
        }
    }

    private FishingService? ServiceOf(Map map) => services().FirstOrDefault(s => ReferenceEquals(s.Map, map));
}