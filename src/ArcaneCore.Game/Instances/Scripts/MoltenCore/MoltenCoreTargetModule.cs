using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.MoltenCore;

/// <summary>mangos-classic Spell::SetTargetMap (targets 7,38) and CheckScriptTargeting, using the ClassicDB z2815
/// spell_script_target rows for these encounters only. Separation Anxiety has no rows and therefore selects all units in its radius.</summary>
public sealed class MoltenCoreTargetModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        foreach (uint spell in new uint[] { 20553, 20619, 21075, 21086, 21087, 21094, 23487 })
            system.RegisterSpellTargetSelector(spell, (SpellImplicitTarget)7, Area);
        foreach (uint spell in new uint[] { 19515, 20482, 20553, 21090 })
            system.RegisterSpellTargetSelector(spell, (SpellImplicitTarget)38, Nearest);
        system.RegisterCastCheck(new RaidDatabaseDestination());
    }
    private static List<(Unit Unit, float Multiplier)> Area(SpellSystem system, SpellCast cast, SpellEffectInfo effect, Unit? explicitTarget)
        => Select(cast, effect, false);
    private static List<(Unit Unit, float Multiplier)> Nearest(SpellSystem system, SpellCast cast, SpellEffectInfo effect, Unit? explicitTarget)
        => Select(cast, effect, true);
    private static List<(Unit Unit, float Multiplier)> Select(SpellCast cast, SpellEffectInfo effect, bool nearest)
    {
        if (cast.Caster.Map?.FindUpdater<MoltenCoreInstance>() is not { } raid
            || raid.Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return [];
        uint[] entries = cast.Spell.Id switch
        {
            19515 => [12057],
            20482 => [12099],
            20553 => [11672],
            21087 => [11663],
            20619 or 21075 or 21086 or 21090 => [11663, 11664],
            _ => []
        };
        float radius = effect.Radius > 0 ? effect.Radius : cast.Spell.Range.Max;
        float Distance(Unit target)
        {
            float dx = target.X - cast.Caster.X, dy = target.Y - cast.Caster.Y, dz = target.Z - cast.Caster.Z;
            return dx * dx + dy * dy + dz * dz;
        }
        IEnumerable<Unit> targets = creatures.Creatures.Cast<Unit>().Concat(raid.Instance.Players)
            .Where(u => u.IsAlive && (entries.Length == 0 || u is Creature c && entries.Contains(c.Entry))
                && Distance(u) <= MathF.Pow(radius + u.BoundingRadius + cast.Caster.BoundingRadius, 2));
        if (nearest) targets = targets.OrderBy(Distance).ThenBy(u => u.Guid.Value).Take((int)Math.Max(1u, effect.ChainTarget));
        else if (cast.Spell.MaxAffectedTargets > 0) targets = targets.Take((int)cast.Spell.MaxAffectedTargets);
        return targets.Select(u => (u, 1f)).ToList();
    }
}

/// <summary>Spell::SetTargetMap TARGET_LOCATION_DATABASE: these raid summons and breath segments use the stored position,
/// and fail closed when it is absent. No coordinates are synthesized.</summary>
internal sealed class RaidDatabaseDestination : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Final;
    public int Order => 0;
    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (context.Caster is not Creature { Entry: 10184 or 11502 }
            || !context.Spell.Effects.Any(e => e.TargetA == SpellImplicitTarget.LocationDatabase || e.TargetB == SpellImplicitTarget.LocationDatabase))
            return SpellCastResult.CastOk;
        if (context.System.Store.GetTargetPosition(context.Spell.Id) is not { } position || position.MapId != context.Caster.MapId)
            return SpellCastResult.NotHere;
        context.Targets.Dest = (position.X, position.Y, position.Z);
        context.Targets.Mask |= SpellCastTargetFlags.DestLocation;
        return SpellCastResult.CastOk;
    }
}
