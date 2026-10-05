using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Stats;

/// <summary>
/// The effective skills used by the character's crit and avoidance fields (vmangos
/// 0e3ff01e, StatSystem.cpp:514-640). Hosts without skill content retain the level defaults.
/// </summary>
public sealed class PlayerSkillStatSource : IPlayerSkillSource
{
    public static PlayerSkillStatSource Instance { get; } = new();

    public int WeaponSkill(Player player, WeaponAttackType attackType, bool hasWeapon)
        => player.Skills is { } skills
            ? PlayerCombatSkills.WeaponSkill(player, skills, attackType)
            : LevelMaximumSkills.Instance.WeaponSkill(player, attackType, hasWeapon);

    public int DefenseSkill(Player player)
        => player.Skills is { } skills
            ? skills.GetValue(SkillIds.Defense)
            : LevelMaximumSkills.Instance.DefenseSkill(player);
}
