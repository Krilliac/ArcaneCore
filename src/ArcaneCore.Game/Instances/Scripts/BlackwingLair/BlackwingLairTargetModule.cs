using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// The Blackwing Lair spells whose implicit targets the built-in switch does not handle (mangos-classic Spell::SetTargetMap and
/// CheckScriptTargeting for the script targets, Spell::OnCheckTarget for Nefarian's class calls). Spell.dbc 5875 targets and the
/// ClassicDB z2815 spell_script_target rows used:
/// <list type="bullet">
/// <item>19832 Possess: effects 0 and 2 target A 38 (TARGET_UNIT_SCRIPT_NEAR_CASTER), range index 6 (100 yd); row (19832,1,12435).</item>
/// <item>19873 Destroy Egg: effect 0 ACTIVATE_OBJECT target A 40 (TARGET_GAMEOBJECT_SCRIPT_NEAR_CASTER), effect 1 DUMMY target A 46
/// (TARGET_LOCATION_SCRIPT_NEAR_CASTER), range index 7 (10 yd); row (19873,0,177807).</item>
/// <item>23642 Nefarius' Corruption: target A 22 / B 7 (TARGET_ENUM_UNITS_SCRIPT_AOE_AT_SRC_LOC), radius index 12 (100 yd); row (23642,1,13020).</item>
/// <item>23362 Raise Drakonids: target A 22 / B 51 (TARGET_ENUM_GAMEOBJECTS_SCRIPT_AOE_AT_SRC_LOC); row (23362,0,179804). Its only effect,
/// ACTIVATE_OBJECT, has no unit; the instance raises the recorded bones itself (<see cref="BlackwingLairInstance.RaiseBones"/>).</item>
/// <item>Nefarian's nine class calls: target A 22 / B 15 (every enemy in 100 yd, a built-in area); mangos-classic Spell::OnCheckTarget keeps
/// only the called class, here a <see cref="SpellSystem.RegisterSpellTargetFilter"/>. vmangos HandleClassCall applies them to players only, as here.</item>
/// </list>
/// </summary>
public sealed class BlackwingLairTargetModule : ISpellHandlerModule
{
    public const uint Possess = 19832, DestroyEgg = 19873, NefariusCorruption = 23642, RaiseDrakonids = 23362;
    public const uint Razorgore = 12435, Vaelastrasz = 13020, BlackDragonEgg = 177807;

    /// <summary>Nefarian's class calls (MC boss_nefarian.cpp spell enum) and the class each one affects.</summary>
    public static IReadOnlyDictionary<uint, Class> ClassCalls { get; } = new Dictionary<uint, Class>
    {
        [23397] = Class.Warrior, [23398] = Class.Druid, [23401] = Class.Priest, [23410] = Class.Mage, [23414] = Class.Rogue,
        [23418] = Class.Paladin, [23425] = Class.Shaman, [23427] = Class.Warlock, [23436] = Class.Hunter,
    };

    public void Register(SpellSystem system)
    {
        system.RegisterSpellTargetSelector(Possess, (SpellImplicitTarget)38, SelectRazorgore);
        system.RegisterSpellTargetSelector(DestroyEgg, (SpellImplicitTarget)40, SelectEgg);
        system.RegisterSpellTargetSelector(DestroyEgg, (SpellImplicitTarget)46, SelectEggLocation);
        system.RegisterSpellTargetSelector(NefariusCorruption, (SpellImplicitTarget)7, SelectVaelastrasz);
        system.RegisterSpellTargetSelector(RaiseDrakonids, (SpellImplicitTarget)51, static (_, _, _, _) => []);
        foreach ((uint spell, Class called) in ClassCalls)
            system.RegisterSpellTargetFilter(spell, (_, _, target) => target is Player player && player.Class == called);
    }

    private static float DistanceSq(WorldObject a, float x, float y, float z)
    {
        float dx = a.X - x, dy = a.Y - y, dz = a.Z - z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>CheckScriptTargeting: the explicit unit when it is a live listed creature in range, else the nearest one.</summary>
    private static List<(Unit Unit, float Multiplier)> SelectRazorgore(SpellSystem system, SpellCast cast, SpellEffectInfo effect, Unit? explicitTarget)
    {
        if (cast.Caster.Map?.FindUpdater<CreatureMapSystem>() is not { } creatures) return [];
        Unit caster = cast.Caster;
        float range = cast.Spell.Range.Max;
        Creature[] inRange = [.. creatures.Creatures.Where(c => c.Entry == Razorgore && c.IsAlive
            && DistanceSq(c, caster.X, caster.Y, caster.Z) <= MathF.Pow(range + c.BoundingRadius + caster.BoundingRadius, 2))];
        Creature? chosen = explicitTarget is Creature explicitCreature && inRange.Contains(explicitCreature) ? explicitCreature
            : inRange.OrderBy(c => DistanceSq(c, caster.X, caster.Y, caster.Z)).ThenBy(c => c.Guid.Value).FirstOrDefault();
        return chosen is null ? [] : [(chosen, 1f)];
    }

    /// <summary>The live egg the cast names, or the nearest spawned egg within the spell's range of the caster.</summary>
    internal static GameObject? FindEgg(Unit caster, float range, ObjectGuid named)
    {
        if (caster.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects) return null;
        bool Eligible(GameObject go) => go.Entry == BlackDragonEgg && go.IsSpawned && go.LootState == GameObjectLootState.Ready
            && DistanceSq(go, caster.X, caster.Y, caster.Z) <= MathF.Pow(range + caster.BoundingRadius, 2);
        if (!named.IsEmpty && objects.Find(named) is { } explicitEgg && Eligible(explicitEgg)) return explicitEgg;
        return objects.GameObjects.Where(Eligible).OrderBy(go => DistanceSq(go, caster.X, caster.Y, caster.Z)).ThenBy(go => go.Guid.Value)
            .FirstOrDefault();
    }

    /// <summary>TARGET_GAMEOBJECT_SCRIPT_NEAR_CASTER: the egg becomes the cast's object target; the caster carries the effect.</summary>
    private static List<(Unit Unit, float Multiplier)> SelectEgg(SpellSystem system, SpellCast cast, SpellEffectInfo effect, Unit? explicitTarget)
    {
        if (FindEgg(cast.Caster, cast.Spell.Range.Max, cast.Targets.GameObject) is not { } egg) return [];
        cast.Targets.GameObject = egg.Guid;
        cast.Targets.Mask |= SpellCastTargetFlags.GameObject;
        return [(cast.Caster, 1f)];
    }

    /// <summary>TARGET_LOCATION_SCRIPT_NEAR_CASTER: the destination is the egg; the DUMMY runs once, on the caster.</summary>
    private static List<(Unit Unit, float Multiplier)> SelectEggLocation(SpellSystem system, SpellCast cast, SpellEffectInfo effect, Unit? explicitTarget)
    {
        if (FindEgg(cast.Caster, cast.Spell.Range.Max, cast.Targets.GameObject) is not { } egg) return [];
        cast.Targets.Dest = (egg.X, egg.Y, egg.Z);
        cast.Targets.Mask |= SpellCastTargetFlags.DestLocation;
        return [(cast.Caster, 1f)];
    }

    /// <summary>TARGET_ENUM_UNITS_SCRIPT_AOE_AT_SRC_LOC: every live Vaelastrasz within the effect radius of the caster.</summary>
    private static List<(Unit Unit, float Multiplier)> SelectVaelastrasz(SpellSystem system, SpellCast cast, SpellEffectInfo effect, Unit? explicitTarget)
    {
        if (cast.Caster.Map?.FindUpdater<CreatureMapSystem>() is not { } creatures) return [];
        Unit caster = cast.Caster;
        float radius = effect.Radius > 0 ? effect.Radius : cast.Spell.Range.Max;
        return [.. creatures.Creatures.Where(c => c.Entry == Vaelastrasz && c.IsAlive
                && DistanceSq(c, caster.X, caster.Y, caster.Z) <= MathF.Pow(radius + c.BoundingRadius, 2))
            .Select(c => ((Unit)c, 1f))];
    }
}
