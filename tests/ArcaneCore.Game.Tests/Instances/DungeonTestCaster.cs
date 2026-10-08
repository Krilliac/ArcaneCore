using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>A creature spell caster whose every cast succeeds and is recorded (spell, target), for the boss scripts' timer and phase paths.</summary>
internal sealed class DungeonTestCaster : ICreatureSpellCaster
{
    public List<(uint Spell, Unit? Target)> Casts { get; } = [];

    public event Action<Unit, Unit, SpellInfo>? SpellHit { add { } remove { } }

    public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
    {
        Casts.Add((spellId, target));
        return CreatureCastResult.Ok;
    }

    public bool IsCasting(Creature caster) => false;

    public bool HasAura(Unit unit, uint spellId) => false;

    public void Interrupt(Creature caster)
    {
    }

    public void OnCreatureRemoved(Creature creature)
    {
    }
}
