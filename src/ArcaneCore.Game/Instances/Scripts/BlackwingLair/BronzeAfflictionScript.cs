using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// vmangos game/Spells/SpellAuras.cpp periodic trigger case 23170: Brood Affliction:
/// Bronze triggers Time Stop (23171) on one in four aura ticks.
/// </summary>
[SpellScript(23171)]
public sealed class BronzeAfflictionScript : ISpellScript
{
    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
        => context.TriggeringSpell?.Id == 23170 && context.System.Random.Next(4) != 0
            ? SpellCastResult.DontReport : SpellCastResult.CastOk;
}
