using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// Tentacle Call 26391, the Vanquished Tentacle of C'Thun trinket (vmangos scripts/spells/spell_item.cpp:390-401, VanquishedTentacleofCthunScript):
/// the summoned tentacle stays where it spawned (at stay, command Stay, follow cleared, stay position saved) instead of following its owner.
/// Raised by the guardian summon (<see cref="ISpellScript.OnSummon"/>).
/// </summary>
[SpellScript(TentacleCall)]
public sealed class VanquishedTentacleScript : ISpellScript
{
    public const uint TentacleCall = 26391;

    public void OnSummon(SpellEffectContext context, Creature summon)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(summon);
        if (summon.Summon?.Charm is not { } charm)
        {
            return;
        }

        charm.IsAtStay = true;
        charm.CommandState = Pets.CommandState.Stay;
        charm.IsCommandFollow = false;
        charm.StayPosition = (summon.X, summon.Y, summon.Z);
    }
}
