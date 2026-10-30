using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// The Gothik anchor spells, ScriptName <c>spell_anchor</c> on the sub-boss trigger 16137 (vmangos naxxramas/boss_gothik.cpp
/// EffectDummyCreature_spell_anchor, eSpellDummy). Each is a DUMMY whose only behaviour is the next link of the chain that carries a dead
/// living-side add over to the spectral side (<see cref="NaxxramasInstance"/> GothikAnchors); the three letters are the trainee (A), the death
/// knight (B) and the rider (C). Reimplemented, no GPL source copied.
/// <list type="bullet">
/// <item>To Anchor 1 (27892, 27928, 27935): the hit anchor casts To Anchor 2 of the same letter at the nearest high anchor of the spectral side.</item>
/// <item>To Anchor 2 (27893, 27929, 27936): the hit anchor casts Anchor to Skulls of the same letter at a random low summon point of the spectral side.</item>
/// <item>Anchor to Skulls (27915, 27931, 27937): Gothik raises the spectral trainee (16127), death knight (16148) or rider (16150, with its
/// spectral horse 16149) at the hit summon point.</item>
/// </list>
/// vmangos acts only on effect 0 and only when the target is a sub-boss trigger in a Naxxramas instance.
/// </summary>
[SpellScript(
    TraineeToAnchor1, KnightToAnchor1, RiderToAnchor1,
    TraineeToAnchor2, KnightToAnchor2, RiderToAnchor2,
    TraineeToSkull, KnightToSkull, RiderToSkull)]
public sealed class GothikAnchorChainScript : ISpellScript
{
    public const uint TraineeToAnchor1 = 27892, KnightToAnchor1 = 27928, RiderToAnchor1 = 27935;
    public const uint TraineeToAnchor2 = 27893, KnightToAnchor2 = 27929, RiderToAnchor2 = 27936;
    public const uint TraineeToSkull = 27915, KnightToSkull = 27931, RiderToSkull = 27937;

    /// <summary>naxxramas.h NPC_SPECT_TRAINEE, NPC_SPECT_DEATH_KNIGTH, NPC_SPECT_RIDER, NPC_SPECT_HORSE.</summary>
    public const uint SpectralTrainee = 16127, SpectralKnight = 16148, SpectralRider = 16150, SpectralHorse = 16149;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0 || context.Target is not Creature anchor || anchor.Entry != NaxxramasInstance.GothikTrigger
            || anchor.Map?.FindUpdater<NaxxramasInstance>() is not { } naxx || anchor.System is not { } creatures) return;

        switch (context.Spell.Id)
        {
            case TraineeToAnchor1:
            case KnightToAnchor1:
            case RiderToAnchor1:
                if (naxx.ClosestGothikAnchor(anchor, livingSide: false) is { } next)
                {
                    uint spell = context.Spell.Id switch
                    {
                        KnightToAnchor1 => KnightToAnchor2,
                        RiderToAnchor1 => RiderToAnchor2,
                        _ => TraineeToAnchor2,
                    };
                    context.System.CastSpell(anchor, spell, SpellCastTargets.ForUnit(next.Guid, context.Cast), triggered: true);
                }

                break;
            case TraineeToAnchor2:
            case KnightToAnchor2:
            case RiderToAnchor2:
            {
                IReadOnlyList<Creature> points = naxx.GothikSummonPoints(livingSide: false);
                if (points.Count == 0) break;
                Creature point = points[creatures.RandomInt(0, points.Count - 1)];
                uint spell = context.Spell.Id switch
                {
                    KnightToAnchor2 => KnightToSkull,
                    RiderToAnchor2 => RiderToSkull,
                    _ => TraineeToSkull,
                };
                context.System.CastSpell(anchor, spell, SpellCastTargets.ForUnit(point.Guid, context.Cast), triggered: true);
                break;
            }
            case TraineeToSkull:
            case KnightToSkull:
            case RiderToSkull:
            {
                uint entry = context.Spell.Id switch
                {
                    KnightToSkull => SpectralKnight,
                    RiderToSkull => SpectralRider,
                    _ => SpectralTrainee,
                };
                naxx.SummonGothikSpectralAdd(entry, anchor.X, anchor.Y, anchor.Z, anchor.Orientation);
                if (entry == SpectralRider) naxx.SummonGothikSpectralAdd(SpectralHorse, anchor.X, anchor.Y, anchor.Z, anchor.Orientation);
                break;
            }
        }
    }
}
