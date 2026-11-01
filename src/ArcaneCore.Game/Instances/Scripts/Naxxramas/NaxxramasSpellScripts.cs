using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// Corrupted Mind's 1.12 healer class follow-up spells: mangos-classic
/// ScriptDevAI/scripts/eastern_kingdoms/naxxramas/boss_loatheb.cpp
/// CorruptedMind::OnEffectExecute (vmangos boss_loatheb.cpp spell script differs
/// on the priest ID; the ScriptDev2 mapping is used here).
/// </summary>
[SpellScript(29201)]
public sealed class NaxxramasSpellScripts : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Target is not Player target) return;
        uint child = target.Class switch
        {
            Class.Priest => 29185,
            Class.Druid => 29194,
            Class.Paladin => 29196,
            Class.Shaman => 29198,
            _ => 0,
        };
        if (child != 0)
            context.System.CastSpell(context.Caster, child,
                SpellCastTargets.ForUnit(target.Guid, context.Cast), triggered: true);
    }
}

/// <summary>
/// Widow's Embrace (28732), cast onto Faerlina by a mind-controlled Naxxramas Worshipper: its script effect
/// kills the casting worshipper. vmangos Spell::EffectScriptEffect case 28732 (SpellEffects.cpp) kills the
/// caster; mangos-classic boss_faerlina.cpp WidowEmbrace::OnEffectExecute does the same through 28748. The
/// effect on Faerlina herself is boss_faerlinaAI::SpellHit, in <see cref="NaxxramasBossAI.OnSpellHit"/>.
/// </summary>
[SpellScript(28732)]
public sealed class WidowsEmbraceScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.Spell.Effects[context.EffectIndex].Effect != SpellEffectName.ScriptEffect) return;
        Unit caster = context.Caster;
        if (caster.IsAlive)
            context.System.Damage.DealSpellDamage(caster, caster, context.Spell, caster.Health, periodic: false);
    }
}

/// <summary>
/// Summon Blizzard (28560), Sapphiron's ground-phase SCRIPT_EFFECT (vmangos Spell::EffectScriptEffect case 28560, SpellEffects.cpp; mangos-classic
/// boss_sapphiron.cpp SummonBlizzard): each unit the area spell reaches casts Summon Blizzard (28561) on itself, which raises the Blizzard under it.
/// Limit: vmangos and mangos-classic pass the boss as the original caster so the Blizzard is Sapphiron's summon; <see cref="SpellSystem"/> has no
/// original-caster seam, so the summon belongs to the unit that stands in it. Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(SpellId)]
public sealed class SapphironSummonBlizzardScript : ISpellScript
{
    public const uint SpellId = 28560, SummonBlizzard = 28561;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0 || context.Effect.Effect != SpellEffectName.ScriptEffect) return;
        context.System.CastSpell(context.Target, SummonBlizzard, SpellCastTargets.ForSelf(), triggered: true);
    }
}

/// <summary>
/// Magnetic Pull (28338), the Stalagg half of the tank swap (mangos-classic boss_thaddius.cpp MagneticPullThaddius lists 28338 and 28339 together):
/// it does what <see cref="ThaddiusMagneticPullScript"/> does for 28339. A second attribute on that class is not possible (one id list per class), so
/// this class forwards to it.
/// </summary>
[SpellScript(SpellId)]
public sealed class ThaddiusMagneticPullAScript : ISpellScript
{
    public const uint SpellId = 28338;

    private readonly ThaddiusMagneticPullScript _pull = new();

    public void OnEffectExecute(SpellEffectContext context) => _pull.OnEffectExecute(context);
}

/// <summary>
/// Zombie Chow Search (28236), the three-second pulse Gluth's aura 28235 triggers: a Zombie Chow within 15 yards (2D, the spell's radius) is killed and
/// Gluth regains five percent of his maximum health. vmangos boss_gluth.cpp DoSearchZombieChow does the same by hand ("Spell 28236 could be used
/// instead") and mangos-classic boss_gluth.cpp ZombieChowSearch + ZombieChowSingleTargetInstakill (28239) + ZombieChowSearchHeal (28238) chain it.
/// Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(SpellId)]
public sealed class GluthZombieChowSearchScript : ISpellScript
{
    public const uint SpellId = 28236, ZombieChow = 16360;
    public const float Range = 15.0f;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0 || context.Target is not Creature { IsAlive: true, Entry: ZombieChow } zombie) return;
        Unit gluth = context.Caster;
        float dx = zombie.X - gluth.X, dy = zombie.Y - gluth.Y;
        if (!gluth.IsAlive || dx * dx + dy * dy >= Range * Range) return;
        context.System.Damage.DealSpellDamage(gluth, zombie, context.Spell, zombie.Health, periodic: false);
        gluth.Health = Math.Min(gluth.MaxHealth, gluth.Health + gluth.MaxHealth / 20);
    }
}

/// <summary>
/// Call All Zombie Chow (29682), the pulse of Gluth's aura 29681: every living creature it reaches follows the caster at melee reach (vmangos
/// boss_gluth.cpp mob_zombieChow::ChaseGluth: MoveFollow(Gluth, ATTACK_DISTANCE); mangos-classic CallAllZombies). Only Zombie Chow stand in the
/// spell's area in the encounter, so no entry filter is applied. Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(SpellId)]
public sealed class GluthCallAllZombieChowScript : ISpellScript
{
    public const uint SpellId = 29682;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0 || context.Target is not Creature { IsAlive: true } zombie || ReferenceEquals(zombie, context.Caster)) return;
        zombie.Motion.MoveFollow(context.Caster, CombatConstants.AttackDistance, 0f);
    }
}

/// <summary>
/// Check Spectral Side (28749, spectral adds) and Check Unrelenting Side (29875, living-side adds): a Gothik add's own side check (mangos-classic
/// boss_gothik.cpp CheckGothikSide). With the combat gate open (Gothik not <see cref="EncounterState.InProgress"/>) the add fights the whole
/// zone; while the gate is closed it attacks the nearest living player standing on its own side. The living side is the one at or below the gate's Y
/// (<see cref="NaxxramasInstance.IsOnGothikLivingSide"/>, vmangos instance_naxxramas IsInRightSideGothArea). Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(CheckSpectralSide, CheckUnrelentingSide)]
public sealed class GothikCheckSideScript : ISpellScript
{
    public const uint CheckSpectralSide = 28749, CheckUnrelentingSide = 29875;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0 || context.Caster is not Creature { IsAlive: true } add || add.System is not { } creatures
            || add.Map is not { } map || map.FindUpdater<NaxxramasInstance>() is not { } naxx) return;
        if (naxx.GetData(NaxxramasInstance.Gothik) != EncounterState.InProgress)
        {
            creatures.SetInCombatWithZone(add);
            return;
        }

        bool livingSide = context.Spell.Id == CheckUnrelentingSide;
        Player? nearest = null;
        float best = float.MaxValue;
        foreach (Player player in map.Players)
        {
            if (!player.IsAlive || naxx.IsOnGothikLivingSide(player) != livingSide) continue;
            float dx = player.X - add.X, dy = player.Y - add.Y, dz = player.Z - add.Z;
            float distance = dx * dx + dy * dy + dz * dz;
            if (distance < best) { best = distance; nearest = player; }
        }

        if (nearest is not null) add.AI?.AttackStart(nearest);
    }
}

/// <summary>
/// Spectral Side Assault (28781) and Unrelenting Side Assault (29874): a creature the spell reaches that is not yet fighting joins the zone-wide fight
/// (mangos-classic boss_gothik.cpp GothikSideAssault: "if one or more creatures are not in combat when the central gates open, set them in combat
/// with zone"). Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(SpectralSideAssault, UnrelentingSideAssault)]
public sealed class GothikSideAssaultScript : ISpellScript
{
    public const uint SpectralSideAssault = 28781, UnrelentingSideAssault = 29874;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0 || context.Target is not Creature { IsAlive: true } creature || creature.Combat.IsInCombat
            || creature.System is not { } creatures) return;
        creatures.SetInCombatWithZone(creature);
    }
}
