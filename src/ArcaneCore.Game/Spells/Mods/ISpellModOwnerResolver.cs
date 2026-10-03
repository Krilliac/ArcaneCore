using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// Which player's modifiers apply to a caster's spells (vmangos Unit::GetSpellModOwner, Unit.cpp:9008-9023). The default is
/// the unit itself when it is a player and nobody otherwise; pets and totems resolve to their owner player through a resolver
/// the pet area installs.
/// </summary>
public interface ISpellModOwnerResolver
{
    /// <summary>The player whose mods apply to <paramref name="caster"/>'s spells, or null for none.</summary>
    Player? GetModOwner(Unit caster);
}

/// <summary>Only players hold modifiers (vmangos HandleAddModifier returns for a non-player target, SpellAuras.cpp:1083).</summary>
public sealed class SelfModOwnerResolver : ISpellModOwnerResolver
{
    public Player? GetModOwner(Unit caster) => caster as Player;
}
