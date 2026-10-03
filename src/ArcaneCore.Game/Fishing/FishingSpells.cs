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
        system.RegisterEffect(SpellEffectName.TransDoor, context =>
        {
            if (context.Caster.Map is { } map)
            {
                ServiceOf(map)?.Transmit(context);
            }
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