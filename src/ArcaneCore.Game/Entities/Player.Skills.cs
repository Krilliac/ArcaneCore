using ArcaneCore.Game.Skills;

namespace ArcaneCore.Game.Entities;

public sealed partial class Player
{
    /// <summary>
    /// The player's skills (null until the skills feature attaches them during login, see
    /// docs/areas/skills.md). World-thread owned once the player is in a map.
    /// </summary>
    public PlayerSkills? Skills { get; private set; }

    /// <summary>Skill aura handlers may be installed before the loading player has its skill state.</summary>
    internal event Action<PlayerSkills>? SkillsAttached;

    /// <summary>Attach the skill state once, before the player is handed to the world thread.</summary>
    public void AttachSkills(PlayerSkills skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        if (Skills is not null)
        {
            throw new InvalidOperationException("skills are already attached to this player");
        }

        Skills = skills;
        SkillsAttached?.Invoke(skills);
    }
}
